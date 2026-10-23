using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PhasmoSaveEditor.Core;

/// <summary>存档中一个顶层字段的定位信息。</summary>
public sealed class SaveEntry
{
    public string Key { get; init; }
    /// <summary>__type 的原始字符串，复杂类型会很长（如 Dictionary`2[[...]]）。</summary>
    public string Type { get; init; }
    /// <summary>value 在明文中的起始下标（含）。</summary>
    public int ValueStart { get; init; }
    /// <summary>value 在明文中的结束下标（不含）。</summary>
    public int ValueEnd { get; init; }
    /// <summary>value 的原始文本片段（标量不含引号外的空白；复杂类型为完整块）。</summary>
    public string RawValue { get; init; }
    /// <summary>true = int/float/string/bool；false = 需要括号配平的复杂块。</summary>
    public bool IsScalar { get; init; }

    /// <summary>是否为 int/float/string/bool 四种基础类型。</summary>
    public bool IsBasicType =>
        Type == "int" || Type == "float" || Type == "string" || Type == "bool";
}

/// <summary>
/// 存档明文文档。
///
/// 存档是 Easy Save 3 写出的**非标准 JSON**，有两处不符合 JSON 规范，
/// 因此不能用 System.Text.Json / Newtonsoft 整体反序列化：
///   1. 字典 key 是裸整数：  "playedMaps" 的 value 形如 {11:166,0:154}
///   2. 字典 key 是对象：    LocalPlayerOutfit 的 Items 形如 {{"low":1,"high":0}:{...}}
///
/// 所以这里不做树形解析，而是「定位 + 原地替换」：
///   - 基础类型（int/float/string/bool）：正则匹配到 value 的字符区间后直接替换
///   - 复杂类型：从 value 起点做括号配平扫描，取出整个块交给专用解析器
/// </summary>
public sealed class SaveDocument
{
    private string _text;
    private int[] _depths;
    private Dictionary<string, SaveEntry> _index;

    /// <summary>加载时的原始明文，用于「还原全部」。</summary>
    public string OriginalText { get; }

    public SaveDocument(string text)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        OriginalText = text;
    }

    public static SaveDocument Load(string path) => new(SaveCrypto.ReadSaveText(path));

    public static SaveDocument FromBytes(byte[] data) => new(SaveCrypto.DecryptToText(data));

    /// <summary>当前明文（含尚未写盘的修改）。</summary>
    public string Text => _text;

    /// <summary>是否相对加载时有任何改动。</summary>
    public bool IsDirty => _text != OriginalText;

    public void RestoreAll()
    {
        _text = OriginalText;
        InvalidateCache();
    }

    private void InvalidateCache()
    {
        _depths = null;
        _index = null;
    }

    // ---- 顶层字段扫描 -------------------------------------------------------

    /// <summary>
    /// 匹配一个 ES3 条目的头部：  "key" : { "__type" : "TYPE" , "value" :
    /// 匹配结束位置正好是 value 的第一个字符。
    /// </summary>
    private static readonly Regex HeaderRe = new(
        "\"(?<key>(?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\\{\\s*\"__type\"\\s*:\\s*" +
        "\"(?<type>[^\"]*)\"\\s*,\\s*\"value\"\\s*:\\s*",
        RegexOptions.Compiled);

    /// <summary>标量 value：数字 / 字符串 / True / False / null。</summary>
    private static readonly Regex ScalarRe = new(
        "-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?" +
        "|\"(?:[^\"\\\\]|\\\\.)*\"" +
        "|True|False|true|false|null",
        RegexOptions.Compiled);

    /// <summary>
    /// 计算每个字符位置的括号深度（字符串内部的括号不计）。
    /// 顶层字段的 key 位于深度 1（根对象内），用它过滤掉复杂类型内部的嵌套结构。
    /// </summary>
    private int[] BuildDepths()
    {
        var d = new int[_text.Length + 1];
        int depth = 0;
        bool inStr = false;
        for (int i = 0; i < _text.Length; i++)
        {
            d[i] = depth;
            char c = _text[i];
            if (inStr)
            {
                if (c == '\\')
                {
                    i++;
                    if (i < _text.Length) d[i] = depth;
                    continue;
                }
                if (c == '"') inStr = false;
                continue;
            }
            switch (c)
            {
                case '"': inStr = true; break;
                case '{':
                case '[': depth++; break;
                case '}':
                case ']': depth--; break;
            }
        }
        d[_text.Length] = depth;
        return d;
    }

    /// <summary>枚举全部顶层字段（不含复杂类型内部的嵌套条目）。</summary>
    public IReadOnlyList<SaveEntry> EnumerateEntries()
    {
        var depths = _depths ??= BuildDepths();
        var list = new List<SaveEntry>();
        foreach (Match m in HeaderRe.Matches(_text))
        {
            if (depths[m.Index] != 1) continue;   // 只要根对象下的字段
            int vs = m.Index + m.Length;
            int ve = ScanValueEnd(vs, out bool isScalar);
            list.Add(new SaveEntry
            {
                Key = UnescapeJson(m.Groups["key"].Value),
                Type = m.Groups["type"].Value,
                ValueStart = vs,
                ValueEnd = ve,
                RawValue = _text.Substring(vs, ve - vs),
                IsScalar = isScalar,
            });
        }
        return list;
    }

    private Dictionary<string, SaveEntry> Index
    {
        get
        {
            if (_index is null)
            {
                var d = new Dictionary<string, SaveEntry>(StringComparer.Ordinal);
                foreach (var e in EnumerateEntries())
                    d[e.Key] = e;      // 同名以后者为准（存档中未观察到重名）
                _index = d;
            }
            return _index;
        }
    }

    /// <summary>
    /// 从 value 起点扫描到 value 结束。
    /// 复杂类型做括号配平（{} 与 [] 共用一个深度计数，JSON 嵌套必然配平）；
    /// 标量则匹配到数字/字符串/布尔字面量结束。
    /// </summary>
    private int ScanValueEnd(int start, out bool isScalar)
    {
        isScalar = false;
        if (start >= _text.Length)
            throw new InvalidDataException("存档在 value 处意外结束，文件可能已损坏。");

        char c = _text[start];
        if (c == '{' || c == '[')
        {
            int depth = 0;
            bool inStr = false;
            for (int i = start; i < _text.Length; i++)
            {
                char ch = _text[i];
                if (inStr)
                {
                    if (ch == '\\') { i++; continue; }
                    if (ch == '"') inStr = false;
                    continue;
                }
                if (ch == '"') { inStr = true; continue; }
                if (ch == '{' || ch == '[') depth++;
                else if (ch == '}' || ch == ']')
                {
                    depth--;
                    if (depth == 0) return i + 1;
                }
            }
            throw new InvalidDataException("括号不配平，存档可能已损坏或格式有变化。");
        }

        var m = ScalarRe.Match(_text, start);
        if (!m.Success || m.Index != start)
            throw new InvalidDataException($"无法解析 value（明文位置 {start}），存档格式可能有变化。");
        isScalar = true;
        return start + m.Length;
    }

    // ---- 定位与读取 ---------------------------------------------------------

    public bool HasKey(string key) => Index.ContainsKey(key);

    /// <summary>取出字段的原始定位信息（含类型与 value 原文）。</summary>
    public bool TryGetEntry(string key, out SaveEntry entry) => Index.TryGetValue(key, out entry);

    /// <summary>取出 value 原文。标量为字面量文本，复杂类型为完整块。</summary>
    public bool TryGetRaw(string key, out string type, out string raw)
    {
        if (Index.TryGetValue(key, out var e)) { type = e.Type; raw = e.RawValue; return true; }
        type = null; raw = null; return false;
    }

    public bool TryGetInt(string key, out int value)
    {
        value = 0;
        if (!TryGetRaw(key, out var type, out var raw) || type != "int") return false;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public bool TryGetBool(string key, out bool value)
    {
        value = false;
        if (!TryGetRaw(key, out var type, out var raw) || type != "bool") return false;
        return TryParseBoolLiteral(raw, out value);
    }

    /// <summary>
    /// 解析布尔字面量。
    ///
    /// 实测存档里是 JSON 标准的小写 true / false（不是 C# 风格的 True / False）。
    /// 顺带说明：旧 Python 版修改器的正则只匹配 True|False，因此实际读不到任何 bool 字段。
    /// 这里两种大小写都接受，以兼容不同版本的 Easy Save 输出。
    /// </summary>
    public static bool TryParseBoolLiteral(string raw, out bool value)
    {
        if (raw is not null)
        {
            if (raw.Equals("true", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
            if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
        }
        value = false;
        return false;
    }

    /// <summary>以 float 精度读取 ES3 的 "float" 字段（编辑控件用）。</summary>
    public bool TryGetFloat(string key, out float value)
    {
        value = 0f;
        if (!TryGetDouble(key, out double d)) return false;
        value = (float)d;
        return true;
    }

    /// <summary>
    /// 读取 ES3 的 "float" 字段。
    ///
    /// 必须用 double 承载：实测存档里存在 9 位有效数字的值（如 sanityLost = 6703.72168、
    /// timeSpentInGhostsRoom = 16867.4414），超出 float 的 7 位精度，
    /// 用 float 解析再写回会变成 6703.7217 —— 原值回写就损坏了存档。
    /// </summary>
    public bool TryGetDouble(string key, out double value)
    {
        value = 0d;
        if (!TryGetRaw(key, out var type, out var raw)) return false;
        if (type != "float" && type != "double") return false;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public bool TryGetString(string key, out string value)
    {
        value = null;
        if (!TryGetRaw(key, out var type, out var raw) || type != "string") return false;
        if (raw.Length < 2 || raw[0] != '"' || raw[^1] != '"') return false;
        value = UnescapeJson(raw.Substring(1, raw.Length - 2));
        return true;
    }

    /// <summary>
    /// 枚举类型字段（__type 不是 int/bool/float/string，但 value 是裸整数）。
    /// 实测有 currentSeasonalEvent 与 RoleType（类型名是马拉雅拉姆文混淆名）两处。
    /// </summary>
    public bool TryGetEnumInt(string key, out int value)
    {
        value = 0;
        if (!TryGetRaw(key, out var type, out var raw)) return false;
        if (type is "int" or "bool" or "float" or "string") return false;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    // ---- 写入 ---------------------------------------------------------------

    /// <summary>用原始文本替换 value 区间。所有 Set* 最终都走这里。</summary>
    private void ReplaceValue(string key, string newRaw)
    {
        if (!Index.TryGetValue(key, out var e))
            throw new KeyNotFoundException($"存档中不存在字段：{key}");
        _text = string.Concat(
            _text.AsSpan(0, e.ValueStart),
            newRaw.AsSpan(),
            _text.AsSpan(e.ValueEnd));
        InvalidateCache();
    }

    public void SetRaw(string key, string rawValue) => ReplaceValue(key, rawValue);

    public void SetInt(string key, int value)
        => ReplaceValue(key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// 写 bool。必须用小写 true / false —— 实测存档（Easy Save 3 输出）用的是
    /// JSON 标准小写，写成 C# 风格的 True / False 会改动全部 151 个布尔字段。
    /// </summary>
    public void SetBool(string key, bool value)
        => ReplaceValue(key, value ? "true" : "false");

    /// <summary>以 float 精度写入（编辑控件用）。</summary>
    public void SetFloat(string key, float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            throw new ArgumentException($"字段 {key} 不能写入 NaN/Infinity");
        ReplaceValue(key, FormatNumber(value));
    }

    /// <summary>
    /// 写入 ES3 的 "float" 字段，用 double 承载以保住原始精度。
    /// 存档里存在 9 位有效数字（6703.72168），float 装不下。
    /// </summary>
    public void SetDouble(string key, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException($"字段 {key} 不能写入 NaN/Infinity");
        ReplaceValue(key, FormatNumber(value));
    }

    /// <summary>
    /// 格式化数值为 ES3 风格：整数值不带小数点（15 而非 15.0），
    /// 小数用最短可往返表示（"R"），避免 99398.07001 这类精度尾巴。
    /// </summary>
    private static string FormatNumber(double value)
    {
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    public void SetString(string key, string value)
        => ReplaceValue(key, "\"" + EscapeJson(value ?? "") + "\"");

    /// <summary>替换复杂类型的整个 value 块（由 Complex/ 下的解析器生成）。</summary>
    public void SetBlock(string key, string blockText) => ReplaceValue(key, blockText);

    // ---- JSON 字符串转义 ----------------------------------------------------

    public static string EscapeJson(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    public static string UnescapeJson(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            char n = s[++i];
            switch (n)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (i + 4 < s.Length &&
                        ushort.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber,
                                        CultureInfo.InvariantCulture, out ushort cp))
                    {
                        sb.Append((char)cp);
                        i += 4;
                    }
                    else sb.Append(n);
                    break;
                default: sb.Append(n); break;
            }
        }
        return sb.ToString();
    }
}
