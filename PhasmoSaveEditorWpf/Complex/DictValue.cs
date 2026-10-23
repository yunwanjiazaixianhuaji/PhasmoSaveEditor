using System.Text;
using System.Text.RegularExpressions;

namespace PhasmoSaveEditor.Complex;

/// <summary>字典中的一项。</summary>
public sealed class DictEntry
{
    public string Key { get; set; }     // 原始 key 文本（整数不带引号，字符串带引号）
    public string Value { get; set; }   // 原始 value 文本
    /// <summary>key 的显示值（字符串 key 已去引号转义）。</summary>
    public string KeyDisplay { get; set; }
    /// <summary>value 的整数形式（解析失败为 null）。</summary>
    public long? ValueInt { get; set; }
}

/// <summary>
/// Dictionary 类型的两种形态。
///
/// 实测格式（注意结尾是 换行 + TAB + }，条目之间无空格）：
///   裸整数 key:  {11:166,0:154,5:22\n\t}
///   字符串 key:  {"Wraith":25,"Raiju":18\n\t}
///
/// key 是裸整数 —— 这不是合法 JSON，也是不能整体反序列化的原因之一。
/// </summary>
public sealed class DictValue : ComplexValueBase
{
    /// <summary>true = key 是裸整数；false = key 是带引号字符串。</summary>
    public bool IntKey { get; }

    public List<DictEntry> Entries { get; } = new();

    public DictValue(string raw, bool intKey) : base(raw)
    {
        IntKey = intKey;
        Parse(raw);
        CaptureSnapshot();
    }

    private static readonly Regex IntEntryRe = new(
        @"(?<k>-?\d+)\s*:\s*(?<v>-?\d+)", RegexOptions.Compiled);

    private static readonly Regex StrEntryRe = new(
        "\"(?<k>(?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*(?<v>-?\\d+)", RegexOptions.Compiled);

    private void Parse(string raw)
    {
        Entries.Clear();
        var re = IntKey ? IntEntryRe : StrEntryRe;
        // 只在最外层 {} 内找条目：去掉首尾大括号后再匹配，避免嵌套干扰
        string body = TrimBraces(raw);
        if (body is null)
        {
            ParseError = "字典不以 { 开头或 } 结尾";
            return;
        }

        foreach (Match m in re.Matches(body))
        {
            string k = m.Groups["k"].Value;
            string v = m.Groups["v"].Value;
            Entries.Add(new DictEntry
            {
                Key = k,
                Value = v,
                KeyDisplay = IntKey ? k : Core.SaveDocument.UnescapeJson(k),
                ValueInt = long.TryParse(v, out long lv) ? lv : null,
            });
        }

        if (Entries.Count == 0 && body.Trim().Length > 0)
        {
            ParseError = $"未能解析出任何字典条目（body={Trunc(body, 60)}）";
            return;
        }
    }

    /// <summary>去掉最外层的 { }（保留内部原文，含结尾的 换行+TAB）。</summary>
    private static string TrimBraces(string raw)
    {
        int s = raw.IndexOf('{');
        int e = raw.LastIndexOf('}');
        if (s < 0 || e <= s) return null;
        return raw.Substring(s + 1, e - s - 1);
    }

    protected override string BuildSnapshot()
    {
        var sb = new StringBuilder();
        foreach (var en in Entries) sb.Append(en.Key).Append('=').Append(en.Value).Append(';');
        return sb.ToString();
    }

    protected override string BuildText()
    {
        // 复刻 ES3 格式：条目无空格，结尾为 换行 + TAB + }
        var items = Entries.Select(en =>
            (IntKey ? en.Key : "\"" + Core.SaveDocument.EscapeJson(en.KeyDisplay) + "\"")
            + ":" + en.Value);
        return Es3Format.DictClose(items, Nl);
    }

    public override void Revert()
    {
        ParseError = null;
        Parse(OriginalRaw);
        CaptureSnapshot();
    }

    // ---- 编辑操作 ----

    public void SetValueAt(int index, long value)
    {
        if (index < 0 || index >= Entries.Count) return;
        Entries[index].Value = Es3Format.Num(value);
        Entries[index].ValueInt = value;
    }

    public void SetKeyAt(int index, string newKey)
    {
        if (index < 0 || index >= Entries.Count) return;
        Entries[index].Key = newKey;
        Entries[index].KeyDisplay = newKey;
    }

    public void Add(string key, long value)
        => Entries.Add(new DictEntry
        {
            Key = key,
            Value = Es3Format.Num(value),
            KeyDisplay = key,
            ValueInt = value,
        });

    public void RemoveAt(int index)
    {
        if (index >= 0 && index < Entries.Count) Entries.RemoveAt(index);
    }

    /// <summary>key 是否已存在（新增时防重复）。</summary>
    public bool ContainsKey(string key)
        => Entries.Any(e => string.Equals(e.KeyDisplay, key, StringComparison.Ordinal));

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
}
