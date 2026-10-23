using System.Text;
using System.Text.RegularExpressions;

namespace PhasmoSaveEditor.Complex;

/// <summary>外观 Items 字典中的一项：以 {low,high} 为 key，值为 guid + materialOption。</summary>
public sealed class OutfitItem
{
    public long Low { get; set; }
    public long High { get; set; }
    public string Guid { get; set; } = "";
    public int MaterialOption { get; set; }
}

/// <summary>
/// PlayerOutfit 类型。
///
/// 这是存档里最特殊的结构：**字典的 key 是一个对象**，不是合法的 JSON：
/// <code>
/// {
/// 		"Items" : {{
/// 				"low" : 1,
/// 				"high" : 0
/// 			}:{
/// 				"guid" : { "value" : "6928d191-..." },
/// 				"materialOption" : 0
/// 			},{ ... }
/// 		},
/// 		"legacyColour" : 0,
/// 		"legacyBackground" : 0,
/// 		"legacyAccent" : 0
/// 	}
/// </code>
///
/// guid 对应游戏内的具体服饰资源，但 guid→物品名的映射需要挖游戏资源，
/// 本编辑器只做 guid 级的查看与增删（见设计文档第 8 节开放问题 2）。
/// </summary>
public sealed class OutfitValue : ComplexValueBase
{
    public List<OutfitItem> Items { get; } = new();

    public int LegacyColour { get; set; }
    public int LegacyBackground { get; set; }
    public int LegacyAccent { get; set; }

    public OutfitValue(string raw) : base(raw)
    {
        Parse(raw);
        CaptureSnapshot();
    }

    private static readonly Regex ItemRe = new(
        "\\{\\s*\"low\"\\s*:\\s*(?<low>-?\\d+)\\s*,\\s*\"high\"\\s*:\\s*(?<high>-?\\d+)\\s*\\}" +
        "\\s*:\\s*\\{\\s*\"guid\"\\s*:\\s*\\{\\s*\"value\"\\s*:\\s*\"(?<guid>[^\"]*)\"\\s*\\}\\s*," +
        "\\s*\"materialOption\"\\s*:\\s*(?<mat>\\d+)\\s*\\}",
        RegexOptions.Compiled);

    private static readonly Regex LegacyRe = new(
        "\"(?<name>legacyColour|legacyBackground|legacyAccent)\"\\s*:\\s*(?<v>-?\\d+)",
        RegexOptions.Compiled);

    private void Parse(string raw)
    {
        Items.Clear();
        foreach (Match m in ItemRe.Matches(raw))
        {
            Items.Add(new OutfitItem
            {
                Low = long.TryParse(m.Groups["low"].Value, out long lo) ? lo : 0,
                High = long.TryParse(m.Groups["high"].Value, out long hi) ? hi : 0,
                Guid = m.Groups["guid"].Value,
                MaterialOption = int.TryParse(m.Groups["mat"].Value, out int mo) ? mo : 0,
            });
        }
        if (Items.Count == 0)
        {
            ParseError = "未能从 Items 中解析出任何条目";
            return;
        }

        foreach (Match m in LegacyRe.Matches(raw))
        {
            int v = int.TryParse(m.Groups["v"].Value, out int x) ? x : 0;
            switch (m.Groups["name"].Value)
            {
                case "legacyColour": LegacyColour = v; break;
                case "legacyBackground": LegacyBackground = v; break;
                case "legacyAccent": LegacyAccent = v; break;
            }
        }
    }

    protected override string BuildSnapshot()
    {
        var sb = new StringBuilder();
        foreach (var it in Items)
            sb.Append(it.Low).Append(',').Append(it.High).Append(',')
              .Append(it.Guid).Append(',').Append(it.MaterialOption).Append(';');
        sb.Append('|').Append(LegacyColour).Append(',').Append(LegacyBackground)
          .Append(',').Append(LegacyAccent);
        return sb.ToString();
    }

    protected override string BuildText()
    {
        // 复刻 ES3 排版：TAB 缩进 + CRLF 换行，key 对象紧跟 Items 的开括号
        var sb = new StringBuilder();
        sb.Append('{').Append(Nl);
        sb.Append(T2).Append("\"Items\" : {");
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            if (i > 0) sb.Append(',');
            // key 对象
            sb.Append('{').Append(Nl);
            sb.Append(T4).Append("\"low\" : ").Append(Es3Format.Num(it.Low)).Append(',').Append(Nl);
            sb.Append(T4).Append("\"high\" : ").Append(Es3Format.Num(it.High)).Append(Nl);
            sb.Append(T3).Append('}').Append(':');
            // value 对象
            sb.Append('{').Append(Nl);
            sb.Append(T4).Append("\"guid\" : {").Append(Nl);
            sb.Append(T5).Append("\"value\" : ").Append(Es3Format.Str(it.Guid)).Append(Nl);
            sb.Append(T4).Append("},").Append(Nl);
            sb.Append(T4).Append("\"materialOption\" : ").Append(Es3Format.Num(it.MaterialOption)).Append(Nl);
            sb.Append(T3).Append('}');
        }
        sb.Append(Nl).Append(T2).Append('}').Append(',').Append(Nl);
        sb.Append(T2).Append("\"legacyColour\" : ").Append(Es3Format.Num(LegacyColour)).Append(',').Append(Nl);
        sb.Append(T2).Append("\"legacyBackground\" : ").Append(Es3Format.Num(LegacyBackground)).Append(',').Append(Nl);
        sb.Append(T2).Append("\"legacyAccent\" : ").Append(Es3Format.Num(LegacyAccent)).Append(Nl);
        sb.Append(T1).Append('}');
        return sb.ToString();
    }

    private const string T1 = "\t", T2 = "\t\t", T3 = "\t\t\t", T4 = "\t\t\t\t", T5 = "\t\t\t\t\t";

    public override void Revert()
    {
        ParseError = null;
        Parse(OriginalRaw);
        CaptureSnapshot();
    }

    // ---- 编辑操作 ----

    public void UpdateAt(int index, string guid, int materialOption)
    {
        if (index < 0 || index >= Items.Count) return;
        Items[index].Guid = guid;
        Items[index].MaterialOption = materialOption;
    }

    public void Add(long low, long high, string guid, int materialOption)
        => Items.Add(new OutfitItem { Low = low, High = high, Guid = guid, MaterialOption = materialOption });

    public void RemoveAt(int index)
    {
        if (index >= 0 && index < Items.Count) Items.RemoveAt(index);
    }
}
