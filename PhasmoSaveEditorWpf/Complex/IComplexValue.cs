using System.Globalization;
using System.Text;

namespace PhasmoSaveEditor.Complex;

/// <summary>
/// 复杂类型值的统一接口。
///
/// **架构决策：未修改时保留原文。**
/// Easy Save 3 的排版细节很多（TAB 缩进、字典条目无空格、结尾 "\n\t}"、
/// 空数组里仍有空白），任何一处对不上就等于改动了存档结构。
/// 因此每个实现都保存 OriginalRaw，IsModified 为 false 时 Serialize() 原样返回，
/// 只有用户真的改了内容才重新生成文本。
/// 这样「未触碰字段的字节级往返」是天然保证的，不依赖格式复刻的完备性。
/// </summary>
public interface IComplexValue
{
    /// <summary>生成写回存档的文本（未修改时 = 原文）。</summary>
    string Serialize();

    bool IsModified { get; }

    /// <summary>解析是否成功。失败时该字段应降级为只读原文展示，且禁止写回。</summary>
    bool ParsedOk { get; }

    /// <summary>放弃修改，回到原文状态。</summary>
    void Revert();

    /// <summary>解析失败时的原因（null = 成功）。</summary>
    string ParseError { get; }
}

/// <summary>复杂值基类：持有原文、提供修改标记与还原。</summary>
public abstract class ComplexValueBase : IComplexValue
{
    protected ComplexValueBase(string originalRaw)
    {
        OriginalRaw = originalRaw ?? "";
        // 从原文探测换行符风格：实测存档用 CRLF，生成 LF 会破坏行尾一致性。
        Nl = OriginalRaw.Contains("\r\n") ? "\r\n" : "\n";
        // 注意：此处**不能**立即调用 BuildSnapshot()。
        // 基类构造函数执行时，派生类的 Parse() 还没跑，成员仍是默认值
        // （例如 Color 的 R/G/B/A 默认 1），快照会记成默认值而非真实值，
        // 导致任何复杂字段一加载就被误判为「已修改」。
        // 快照改为首次访问 IsModified 时惰性计算，那时派生构造已完成。
    }

    /// <summary>存档中的原始文本。</summary>
    public string OriginalRaw { get; }

    /// <summary>解析失败原因；解析成功则为 null。</summary>
    public string ParseError { get; protected set; }

    /// <summary>解析是否成功。失败时该字段应降级为只读原文展示。</summary>
    public bool ParsedOk => ParseError is null;

    private string _snapshot;

    /// <summary>
    /// 原文使用的换行符。
    ///
    /// 实测存档用 CRLF（\r\n），若生成 LF 会导致每个换行少 1 字节、
    /// 破坏整个存档的行尾风格。从原文探测，探测不到则用 CRLF。
    /// </summary>
    protected string Nl { get; }

    /// <summary>
    /// 捕获「原始状态」指纹，用于后续判断是否被修改。
    ///
    /// **派生类必须在构造函数末尾、Parse() 之后调用一次。**
    /// 不能放在基类构造函数里：那时派生类的 Parse() 还没跑，
    /// 成员仍是默认值（例如 Color 的 R/G/B/A 默认 1），快照会记错，
    /// 导致字段一加载就被误判为「已修改」。
    /// 也不能靠惰性计算：若用户在首次读 IsModified 之前就改了值，
    /// 快照会记成修改后的状态，改动就检测不到了。
    /// </summary>
    protected void CaptureSnapshot() => _snapshot = BuildSnapshot();

    /// <summary>当前状态的指纹。</summary>
    protected abstract string BuildSnapshot();

    public bool IsModified => ParsedOk && _snapshot is not null && BuildSnapshot() != _snapshot;

    /// <summary>
    /// 以当前状态作为新的「原始状态」基准。
    /// 用于 Revert() 后，或用户确认某次修改后重置基准。
    /// </summary>
    public abstract void Revert();

    /// <summary>解析失败时返回原文，绝不生成可能损坏存档的文本。</summary>
    public virtual string Serialize() => ParsedOk ? BuildText() : OriginalRaw;

    protected abstract string BuildText();
}

/// <summary>ES3 排版与数值格式化的共用约定。</summary>
public static class Es3Format
{
    public const string Tab = "\t";

    /// <summary>
    /// ES3 的数值格式：整数值不带小数点（1 而非 1.0），
    /// 小数用最短可往返表示（"R"）。
    ///
    /// 必须用 double 承载：存档里存在 9 位有效数字（6703.72168），float 装不下。
    /// </summary>
    public static string Num(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new FormatException("不能写入 NaN / Infinity");
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    public static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>解析 ES3 数值（可能是整数或小数）。</summary>
    public static bool TryParseNum(string s, out double value)
        => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static string Str(string s) => "\"" + Core.SaveDocument.EscapeJson(s ?? "") + "\"";

    /// <summary>拼接 "{a,b,c" + nl + Tab + "}" 这种字典结尾形式。nl 需传原文的换行风格。</summary>
    public static string DictClose(IEnumerable<string> items, string nl)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append(string.Join(",", items));
        sb.Append(nl).Append(Tab).Append('}');
        return sb.ToString();
    }

    /// <summary>拼接 "[a,b,c]" 单行列表形式。</summary>
    public static string ListInline(IEnumerable<string> items)
        => "[" + string.Join(",", items) + "]";
}
