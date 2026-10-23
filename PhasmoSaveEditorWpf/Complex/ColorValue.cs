using System.Text;
using System.Text.RegularExpressions;

namespace PhasmoSaveEditor.Complex;

/// <summary>
/// Color 类型：{ "r","g","b","a" }，各分量为 0~1 的浮点。
///
/// 原文格式（TAB 缩进，字段名与冒号间有空格）：
/// <code>
/// {
/// 		"r" : 1,
/// 		"g" : 1,
/// 		"b" : 1,
/// 		"a" : 1
/// 	}
/// </code>
/// </summary>
public sealed class ColorValue : ComplexValueBase
{
    public ColorValue(string raw) : base(raw)
    {
        Parse(raw);
        CaptureSnapshot();   // 必须在 Parse 之后，见基类说明
    }

    public double R { get; set; } = 1;
    public double G { get; set; } = 1;
    public double B { get; set; } = 1;
    public double A { get; set; } = 1;

    private static readonly Regex CompRe = new(
        "\"(?<c>[rgba])\"\\s*:\\s*(?<v>-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?)",
        RegexOptions.Compiled);

    private void Parse(string raw)
    {
        var found = new Dictionary<char, double>();
        foreach (Match m in CompRe.Matches(raw))
        {
            if (Es3Format.TryParseNum(m.Groups["v"].Value, out double v))
                found[m.Groups["c"].Value[0]] = v;
        }
        if (found.Count != 4 || !found.ContainsKey('r') || !found.ContainsKey('g') ||
            !found.ContainsKey('b') || !found.ContainsKey('a'))
        {
            ParseError = $"Color 解析不完整，只找到 {found.Count}/4 个分量";
            return;
        }
        R = found['r']; G = found['g']; B = found['b']; A = found['a'];
    }

    protected override string BuildSnapshot() => $"{R:R}|{G:R}|{B:R}|{A:R}";

    protected override string BuildText()
    {
        string t2 = Es3Format.Tab + Es3Format.Tab;
        var sb = new StringBuilder();
        sb.Append('{').Append(Nl);
        sb.Append(t2).Append("\"r\" : ").Append(Es3Format.Num(R)).Append(',').Append(Nl);
        sb.Append(t2).Append("\"g\" : ").Append(Es3Format.Num(G)).Append(',').Append(Nl);
        sb.Append(t2).Append("\"b\" : ").Append(Es3Format.Num(B)).Append(',').Append(Nl);
        sb.Append(t2).Append("\"a\" : ").Append(Es3Format.Num(A)).Append(Nl);
        sb.Append(Es3Format.Tab).Append('}');
        return sb.ToString();
    }

    public override void Revert()
    {
        ParseError = null;
        Parse(OriginalRaw);
        CaptureSnapshot();   // 还原后以原文为新基准，否则仍会显示为「已修改」
    }

    /// <summary>分量取值范围。</summary>
    public const double MinComp = 0.0, MaxComp = 1.0;

    public static bool InRange(double v) => v >= MinComp && v <= MaxComp;

    /// <summary>转成 0~255 的 RGB，供取色器与预览使用。</summary>
    public (byte R, byte G, byte B) ToRgb255()
        => ((byte)Math.Clamp(Math.Round(R * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(G * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(B * 255), 0, 255));

    /// <summary>由 0~255 的 RGB 设置分量（保留原 alpha）。</summary>
    public void FromRgb255(byte r, byte g, byte b)
    {
        R = Math.Round(r / 255.0, 7);
        G = Math.Round(g / 255.0, 7);
        B = Math.Round(b / 255.0, 7);
    }
}
