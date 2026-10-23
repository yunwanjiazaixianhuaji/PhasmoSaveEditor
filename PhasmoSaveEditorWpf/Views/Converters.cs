using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PhasmoSaveEditor.Views;

/// <summary>null / 空字符串 → Collapsed，否则 Visible。用于提示文本与错误文本。</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>#RRGGBB 字符串 → 画刷，用于颜色预览块。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly BrushConverter Conv = new();

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        try
        {
            return Conv.ConvertFromString(value as string ?? "#000000") ?? Brushes.Black;
        }
        catch
        {
            return Brushes.Black;
        }
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>把 double（0~1 的颜色分量）格式化为最多 7 位小数的字符串显示。</summary>
public sealed class ColorComponentConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is double d ? d.ToString("0.#######", CultureInfo.InvariantCulture) : "";

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
    {
        string s = (value as string ?? "").Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            return d;
        return Binding.DoNothing;   // 输入非法时不回写，避免把字段改坏
    }
}

/// <summary>bool 取反，用于「解析失败才显示原文」这类反向可见性。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is bool b ? !b : false;
}
