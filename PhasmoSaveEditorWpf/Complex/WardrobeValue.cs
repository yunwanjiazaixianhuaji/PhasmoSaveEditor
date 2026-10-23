using System.Text;
using System.Text.RegularExpressions;

namespace PhasmoSaveEditor.Complex;

/// <summary>
/// PlayerWardrobe 类型：OwnedItems 是已拥有外观的 guid 数组。
///
/// 实测格式（每项是 {"value":"guid"} 对象，项之间用 },{ 分隔）：
/// <code>
/// {
/// 		"OwnedItems" : [
/// 			{
/// 				"value" : "1500c997-..."
/// 			},{
/// 				"value" : "6b7c0561-..."
/// 			}
/// 		]
/// 	}
/// </code>
/// </summary>
public sealed class WardrobeValue : ComplexValueBase
{
    /// <summary>已拥有的外观 guid 列表。</summary>
    public List<string> OwnedItems { get; } = new();

    public WardrobeValue(string raw) : base(raw)
    {
        Parse(raw);
        CaptureSnapshot();
    }

    private static readonly Regex ItemRe = new(
        "\\{\\s*\"value\"\\s*:\\s*\"(?<guid>[^\"]*)\"\\s*\\}", RegexOptions.Compiled);

    private void Parse(string raw)
    {
        OwnedItems.Clear();
        foreach (Match m in ItemRe.Matches(raw))
            OwnedItems.Add(m.Groups["guid"].Value);
        // 空衣柜是合法的，不算解析失败
    }

    protected override string BuildSnapshot() => string.Join("\u0001", OwnedItems);

    protected override string BuildText()
    {
        // 复刻 ES3 排版。要点：
        //   1. 换行用原文风格（CRLF）
        //   2. 首项前有 T3 缩进，后续项是 "},{" —— 逗号后紧跟开括号，无缩进无换行
        var sb = new StringBuilder();
        sb.Append('{').Append(Nl);
        sb.Append(T2).Append("\"OwnedItems\" : [").Append(Nl);
        for (int i = 0; i < OwnedItems.Count; i++)
        {
            if (i > 0) sb.Append(',');
            else sb.Append(T3);
            sb.Append('{').Append(Nl);
            sb.Append(T4).Append("\"value\" : ").Append(Es3Format.Str(OwnedItems[i])).Append(Nl);
            sb.Append(T3).Append('}');
        }
        if (OwnedItems.Count > 0) sb.Append(Nl);
        sb.Append(T2).Append(']').Append(Nl);
        sb.Append(T1).Append('}');
        return sb.ToString();
    }

    private const string T1 = "\t", T2 = "\t\t", T3 = "\t\t\t", T4 = "\t\t\t\t";

    public override void Revert()
    {
        ParseError = null;
        Parse(OriginalRaw);
        CaptureSnapshot();
    }

    // ---- 编辑操作 ----

    public void SetAt(int index, string guid)
    {
        if (index >= 0 && index < OwnedItems.Count) OwnedItems[index] = guid;
    }

    public void Add(string guid) => OwnedItems.Add(guid);

    public void RemoveAt(int index)
    {
        if (index >= 0 && index < OwnedItems.Count) OwnedItems.RemoveAt(index);
    }

    public bool Contains(string guid)
        => OwnedItems.Contains(guid, StringComparer.OrdinalIgnoreCase);
}
