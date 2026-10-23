using System.Text;
using System.Text.RegularExpressions;

namespace PhasmoSaveEditor.Complex;

/// <summary>
/// List 类型的两种形态：List&lt;string&gt; 与 List&lt;int&gt;。
///
/// 实测格式（单行，元素间逗号无空格）：
///   字符串:  ["邪王萌虎酱","石像鼠"]
///   整数:    [0,0]
///
/// 注意与 LastDifficulty 内嵌的 chosenCursedItems（多行、每行缩进）不同，
/// 顶层 List 字段都是单行紧凑格式。
/// </summary>
public sealed class ListValue : ComplexValueBase
{
    /// <summary>true = List&lt;int&gt;；false = List&lt;string&gt;。</summary>
    public bool IntList { get; }

    /// <summary>元素文本。字符串元素存的是**已去引号转义**后的值。</summary>
    public List<string> Items { get; } = new();

    public ListValue(string raw, bool intList) : base(raw)
    {
        IntList = intList;
        Parse(raw);
        CaptureSnapshot();
    }

    private static readonly Regex StrItemRe = new(
        "\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
    private static readonly Regex IntItemRe = new(
        @"-?\d+", RegexOptions.Compiled);

    private void Parse(string raw)
    {
        Items.Clear();
        int s = raw.IndexOf('[');
        int e = raw.LastIndexOf(']');
        if (s < 0 || e < s)
        {
            ParseError = "列表不以 [ ] 包裹";
            return;
        }
        string body = raw.Substring(s + 1, e - s - 1);

        if (IntList)
        {
            foreach (Match m in IntItemRe.Matches(body)) Items.Add(m.Value);
        }
        else
        {
            foreach (Match m in StrItemRe.Matches(body))
                Items.Add(Core.SaveDocument.UnescapeJson(m.Groups[1].Value));
        }
        // 空列表是合法的（如 recentPlayerIDS 可能为空），不算解析失败
    }

    protected override string BuildSnapshot() => string.Join("\u0001", Items);

    protected override string BuildText()
    {
        var rendered = IntList
            ? Items.Select(i => i)
            : Items.Select(i => "\"" + Core.SaveDocument.EscapeJson(i) + "\"");
        return Es3Format.ListInline(rendered);
    }

    public override void Revert()
    {
        ParseError = null;
        Parse(OriginalRaw);
        CaptureSnapshot();
    }

    // ---- 编辑操作 ----

    public void SetAt(int index, string value)
    {
        if (index >= 0 && index < Items.Count) Items[index] = value;
    }

    public void Add(string value) => Items.Add(value);

    public void RemoveAt(int index)
    {
        if (index >= 0 && index < Items.Count) Items.RemoveAt(index);
    }

    /// <summary>整数列表专用：读某项为 long。</summary>
    public bool TryGetIntAt(int index, out long value)
    {
        value = 0;
        return index >= 0 && index < Items.Count &&
               long.TryParse(Items[index], out value);
    }
}
