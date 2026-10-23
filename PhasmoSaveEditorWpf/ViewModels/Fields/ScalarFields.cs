using System.Globalization;
using PhasmoSaveEditor.Complex;
using PhasmoSaveEditor.Core;
using PhasmoSaveEditor.Schema;

namespace PhasmoSaveEditor.ViewModels.Fields;

/// <summary>
/// 整数字段。
///
/// 文本框绑定到 <see cref="Text"/>（string）而非直接绑 long：
/// 这样用户输入非法内容（空、字母、超范围）时不会产生 WPF 绑定异常，
/// 而是落到 <see cref="ValidationError"/>，由 UI 红字提示，ApplyTo 会跳过它。
/// </summary>
public class IntFieldViewModel : FieldViewModel
{
    protected long _parsed;
    protected string _text;

    public IntFieldViewModel(FieldSchema schema, SaveEntry entry) : base(schema, entry)
    {
        if (long.TryParse(entry.RawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
            _parsed = v;
        _text = entry.RawValue;
        Revalidate();
    }

    public long Value => _parsed;

    public string Text
    {
        get => _text;
        set
        {
            if (SetField(ref _text, value))
            {
                if (long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                    _parsed = v;
                Touched = true;
                Revalidate();
                OnPropertyChanged(nameof(Value));
                NotifyValueChanged();
            }
        }
    }

    /// <summary>供预设等程序化赋值使用。</summary>
    public virtual void SetValue(long v)
    {
        _parsed = v;
        _text = v.ToString(CultureInfo.InvariantCulture);
        Touched = true;
        Revalidate();
        OnPropertyChangeds(nameof(Text), nameof(Value));
        NotifyValueChanged();
    }

    protected virtual void Revalidate()
    {
        if (!long.TryParse(_text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            ValidationError = "必须是整数";
            return;
        }
        if (Schema.Min is long lo && _parsed < lo) { ValidationError = $"不能小于 {lo}"; return; }
        if (Schema.Max is long hi && _parsed > hi) { ValidationError = $"不能大于 {hi}"; return; }
        ValidationError = null;
    }

    public override string CurrentRaw =>
        Touched && IsValid ? _parsed.ToString(CultureInfo.InvariantCulture) : OriginalRaw;

    /// <summary>
    /// 校验失败时也必须算「已修改」。
    ///
    /// CurrentRaw 在非法时故意回退成原文（防止把坏值写进存档），
    /// 若 IsModified 沿用「CurrentRaw != OriginalRaw」判定，非法字段就会被判成
    /// 「没有改动」，进而使 HasBlockingError 恒为 false、还原按钮与「还原全部」
    /// 同时变灰 —— 用户输入一个超范围数字就会把界面锁死且看不到任何解释。
    /// </summary>
    public override bool IsModified => Touched && (!IsValid || CurrentRaw != OriginalRaw);

    public override void Revert()
    {
        if (long.TryParse(OriginalRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
            _parsed = v;
        _text = OriginalRaw;
        Touched = false;
        ValidationError = null;
        Revalidate();
        OnPropertyChangeds(nameof(Text), nameof(Value), nameof(IsModified));
    }

    public override string DescribeChange() => $"{Display}：{OriginalRaw} → {_parsed}";
}

/// <summary>
/// 枚举字段（__type 非 int，但 value 是裸整数，如 currentSeasonalEvent / RoleType）。
/// 行为与整数完全一致，只是分类与提示不同，故直接复用。
/// </summary>
public sealed class EnumIntFieldViewModel : IntFieldViewModel
{
    public EnumIntFieldViewModel(FieldSchema schema, SaveEntry entry) : base(schema, entry) { }
}

/// <summary>
/// 浮点字段。用 double 承载 —— 存档里有 9 位有效数字（6703.72168），float 会丢精度。
/// </summary>
public sealed class FloatFieldViewModel : FieldViewModel
{
    private double _parsed;
    private string _text;

    public FloatFieldViewModel(FieldSchema schema, SaveEntry entry) : base(schema, entry)
    {
        if (Es3Format.TryParseNum(entry.RawValue, out double v)) _parsed = v;
        _text = entry.RawValue;
        Revalidate();
    }

    public double Value => _parsed;

    public string Text
    {
        get => _text;
        set
        {
            if (SetField(ref _text, value))
            {
                if (Es3Format.TryParseNum(value, out double v)) _parsed = v;
                Touched = true;
                Revalidate();
                OnPropertyChanged(nameof(Value));
                NotifyValueChanged();
            }
        }
    }

    public void SetValue(double v)
    {
        _parsed = v;
        _text = Es3Format.Num(v);
        Touched = true;
        Revalidate();
        OnPropertyChangeds(nameof(Text), nameof(Value));
        NotifyValueChanged();
    }

    private void Revalidate()
    {
        if (!Es3Format.TryParseNum(_text, out _)) { ValidationError = "必须是数字"; return; }
        if (double.IsNaN(_parsed) || double.IsInfinity(_parsed)) { ValidationError = "不能是 NaN/无穷"; return; }
        ValidationError = null;
    }

    public override string CurrentRaw =>
        Touched && IsValid ? Es3Format.Num(_parsed) : OriginalRaw;

    /// <summary>同 IntFieldViewModel：非法时也要算已修改，否则界面会被锁死。</summary>
    public override bool IsModified => Touched && (!IsValid || CurrentRaw != OriginalRaw);

    public override void Revert()
    {
        if (Es3Format.TryParseNum(OriginalRaw, out double v)) _parsed = v;
        _text = OriginalRaw;
        Touched = false;
        ValidationError = null;
        OnPropertyChangeds(nameof(Text), nameof(Value), nameof(IsModified));
    }

    public override string DescribeChange() => $"{Display}：{OriginalRaw} → {Es3Format.Num(_parsed)}";
}

/// <summary>布尔字段（CheckBox / 开关）。</summary>
public sealed class BoolFieldViewModel : FieldViewModel
{
    private bool _value;

    public BoolFieldViewModel(FieldSchema schema, SaveEntry entry) : base(schema, entry)
    {
        _value = SaveDocument.TryParseBoolLiteral(entry.RawValue, out bool b) && b;
    }

    public bool Value
    {
        get => _value;
        set
        {
            if (SetField(ref _value, value))
            {
                Touched = true;
                ValidationError = null;
                NotifyValueChanged();
            }
        }
    }

    public void SetValue(bool v) => Value = v;

    /// <summary>ES3 用小写 true/false（见 SaveDocument.SetBool 注释）。</summary>
    public override string CurrentRaw => Touched ? (_value ? "true" : "false") : OriginalRaw;

    public override void Revert()
    {
        _value = SaveDocument.TryParseBoolLiteral(OriginalRaw, out bool b) && b;
        Touched = false;
        OnPropertyChangeds(nameof(Value), nameof(IsModified));
    }

    public override string DescribeChange() => $"{Display}：{OriginalRaw} → {(_value ? "true" : "false")}";
}

/// <summary>字符串字段（如 GhostType）。</summary>
public sealed class StringFieldViewModel : FieldViewModel
{
    private string _value;

    public StringFieldViewModel(FieldSchema schema, SaveEntry entry) : base(schema, entry)
    {
        // entry.RawValue 是带引号的原文 "孪魂"，这里存去引号后的值
        _value = entry.RawValue.Length >= 2 && entry.RawValue[0] == '"'
            ? SaveDocument.UnescapeJson(entry.RawValue.Substring(1, entry.RawValue.Length - 2))
            : entry.RawValue;
    }

    public string Value
    {
        get => _value;
        set
        {
            if (SetField(ref _value, value))
            {
                Touched = true;
                ValidationError = null;
                NotifyValueChanged();
            }
        }
    }

    public void SetValue(string v) => Value = v;

    public override string CurrentRaw =>
        Touched ? "\"" + SaveDocument.EscapeJson(_value ?? "") + "\"" : OriginalRaw;

    public override void Revert()
    {
        _value = OriginalRaw.Length >= 2 && OriginalRaw[0] == '"'
            ? SaveDocument.UnescapeJson(OriginalRaw.Substring(1, OriginalRaw.Length - 2))
            : OriginalRaw;
        Touched = false;
        OnPropertyChangeds(nameof(Value), nameof(IsModified));
    }

    public override string DescribeChange() => $"{Display}：「{OriginalRaw}」→「{CurrentRaw}」";
}

/// <summary>
/// 原始块兜底编辑器：直接编辑 value 的原始文本（多行）。
/// 用于无法结构化解析的复杂类型。校验括号是否配平，避免写出损坏结构。
/// </summary>
public sealed class RawBlockFieldViewModel : FieldViewModel
{
    private string _text;

    public RawBlockFieldViewModel(FieldSchema schema, SaveEntry entry) : base(schema, entry)
    {
        _text = entry.RawValue;
    }

    public string Text
    {
        get => _text;
        set
        {
            if (SetField(ref _text, value))
            {
                Touched = true;
                Revalidate();
                NotifyValueChanged();
            }
        }
    }

    private void Revalidate()
    {
        ValidationError = Balanced(_text) ? null : "括号不配平，写回会损坏存档结构";
    }

    public override string CurrentRaw => Touched && IsValid ? _text : OriginalRaw;

    /// <summary>同上：括号不配平时也要算已修改，否则还原入口会一起失效。</summary>
    public override bool IsModified => Touched && (!IsValid || CurrentRaw != OriginalRaw);

    public override void Revert()
    {
        _text = OriginalRaw;
        Touched = false;
        ValidationError = null;
        OnPropertyChangeds(nameof(Text), nameof(IsModified));
    }

    public override string DescribeChange() => $"{Display}：原始块已修改";

    private static bool Balanced(string s)
    {
        if (s is null) return false;
        int brace = 0, brack = 0;
        bool inStr = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inStr = false;
                continue;
            }
            switch (c)
            {
                case '"': inStr = true; break;
                case '{': brace++; break;
                case '}': if (--brace < 0) return false; break;
                case '[': brack++; break;
                case ']': if (--brack < 0) return false; break;
            }
        }
        return brace == 0 && brack == 0;
    }
}
