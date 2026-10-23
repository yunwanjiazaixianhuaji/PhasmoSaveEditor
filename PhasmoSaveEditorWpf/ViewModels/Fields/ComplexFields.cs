using System.Collections.ObjectModel;
using System.Globalization;
using PhasmoSaveEditor.Complex;
using PhasmoSaveEditor.Core;
using PhasmoSaveEditor.Schema;

namespace PhasmoSaveEditor.ViewModels.Fields;

/// <summary>
/// 复杂类型字段的公共基类：包一个 IComplexValue，
/// 未修改时 CurrentRaw 直接返回原文（由 ComplexValueBase 保证）。
/// </summary>
public abstract class ComplexFieldViewModel<T> : FieldViewModel where T : IComplexValue
{
    protected ComplexFieldViewModel(FieldSchema schema, SaveEntry entry) : base(schema, entry)
    {
        Value = Parse(entry.RawValue);
        ValidationError = Value.ParseError;
    }

    public T Value { get; private set; }

    protected abstract T Parse(string raw);

    public override string CurrentRaw => Value.Serialize();

    public override bool IsModified => Value.ParsedOk && Value.IsModified;

    /// <summary>解析失败时禁止写回，避免生成损坏的存档。</summary>
    public override bool ApplyTo(SaveDocument doc)
    {
        if (IsReadOnly || !Value.ParsedOk || !IsModified) return false;
        doc.SetRaw(Key, Value.Serialize());
        return true;
    }

    public override void Revert()
    {
        Value.Revert();
        ValidationError = Value.ParseError;
        OnPropertyChangeds(nameof(IsModified), nameof(CurrentRaw));
    }

    /// <summary>子项编辑后调用，刷新状态。</summary>
    protected void MarkTouched()
    {
        ValidationError = Value.ParseError;
        OnPropertyChangeds(nameof(IsModified), nameof(CurrentRaw));
        NotifyValueChanged();
    }
}

/// <summary>Color 字段：RGBA 四个 0~1 的分量 + 取色器用的 0~255 视图。</summary>
public sealed class ColorFieldViewModel : ComplexFieldViewModel<ColorValue>
{
    public ColorFieldViewModel(FieldSchema schema, SaveEntry entry) : base(schema, entry) { }

    protected override ColorValue Parse(string raw) => new ColorValue(raw);

    public double R
    {
        get => Value.R;
        set { if (SetComp(Value.R, value)) { Value.R = value; MarkTouched(); } }
    }
    public double G
    {
        get => Value.G;
        set { if (SetComp(Value.G, value)) { Value.G = value; MarkTouched(); } }
    }
    public double B
    {
        get => Value.B;
        set { if (SetComp(Value.B, value)) { Value.B = value; MarkTouched(); } }
    }
    public double A
    {
        get => Value.A;
        set { if (SetComp(Value.A, value)) { Value.A = value; MarkTouched(); } }
    }

    private bool SetComp(double old, double nw)
    {
        if (Math.Abs(old - nw) < 1e-12) return false;
        // 超出 0~1 时夹紧并记录错误，避免写出游戏不接受的值
        ValidationError = ColorValue.InRange(nw) ? Value.ParseError : "分量必须在 0~1 之间";
        return true;
    }

    /// <summary>取色器绑定的 0~255 RGB（写回时换算为 0~1）。</summary>
    public string HexColor
    {
        get
        {
            var (r, g, b) = Value.ToRgb255();
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        set
        {
            if (TryParseHex(value, out byte r, out byte g, out byte b))
            {
                Value.FromRgb255(r, g, b);
                OnPropertyChangeds(nameof(R), nameof(G), nameof(B));
                MarkTouched();
            }
        }
    }

    private static bool TryParseHex(string s, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('#');
        if (s.Length == 3) s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
        if (s.Length != 6) return false;
        return byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    public override string DescribeChange() => $"{Display}：RGBA → ({Value.R}, {Value.G}, {Value.B}, {Value.A})";
}

/// <summary>字典字段：绑定到可编辑的 ObservableCollection。</summary>
public sealed class DictFieldViewModel : ComplexFieldViewModel<DictValue>
{
    public ObservableCollection<DictRow> Rows { get; } = new();

    public DictFieldViewModel(FieldSchema schema, SaveEntry entry, bool intKey) : base(schema, entry)
    {
        Reload();
        AddRowCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(NewKey)) return;
            AddRow(NewKey.Trim(), NewValue);
            NewKey = ""; NewValue = 0;
            OnPropertyChangeds(nameof(NewKey), nameof(NewValue));
        });
        RemoveRowCommand = new RelayCommand(
            () => { if (SelectedRow is not null) RemoveRow(SelectedRow); },
            () => SelectedRow is not null);
    }

    /// <summary>
    /// 用 Schema.Kind 判定 key 类型，不用构造函数参数。
    /// 基类构造函数会调用 Parse()，那时派生类字段还没赋值，
    /// 读 _intKey 会拿到默认值 false，把裸整数 key 字典当成字符串 key 解析。
    /// Schema 在 FieldViewModel 构造时就已设置，可安全读取。
    /// </summary>
    protected override DictValue Parse(string raw) => new DictValue(raw, Kind == FieldKind.IntIntDict);

    private void Reload()
    {
        Rows.Clear();
        for (int i = 0; i < Value.Entries.Count; i++)
        {
            var en = Value.Entries[i];
            int idx = i;
            var row = new DictRow(en.KeyDisplay, en.ValueInt ?? 0);
            row.KeyChanged += nk => { Value.SetKeyAt(idx, nk); MarkTouched(); };
            row.ValueChanged += nv => { Value.SetValueAt(idx, nv); MarkTouched(); };
            Rows.Add(row);
        }
    }

    public bool IntKey => Kind == FieldKind.IntIntDict;

    /// <summary>DataGrid 当前选中行。</summary>
    public DictRow SelectedRow { get; set; }

    /// <summary>新增行的输入缓冲（绑定到界面底部的两个输入框）。</summary>
    public string NewKey { get; set; } = "";
    public long NewValue { get; set; }

    public RelayCommand AddRowCommand { get; }
    public RelayCommand RemoveRowCommand { get; }

    public void AddRow(string key, long value)
    {
        if (Value.ContainsKey(key))
        {
            ValidationError = $"键 {key} 已存在";
            OnPropertyChanged(nameof(ValidationError));
            return;
        }
        ValidationError = null;
        Value.Add(key, value);
        Reload();
        MarkTouched();
    }

    public void RemoveRow(DictRow row)
    {
        int idx = Rows.IndexOf(row);
        if (idx < 0) return;
        Value.RemoveAt(idx);
        Reload();
        MarkTouched();
    }

    public override void Revert()
    {
        base.Revert();
        Reload();
    }

    public override string DescribeChange() => $"{Display}：字典已修改（{Value.Entries.Count} 项）";
}

/// <summary>字典行：可编辑的 key/value。</summary>
public sealed class DictRow : ObservableObject
{
    private string _key;
    private long _value;

    public DictRow(string key, long value) { _key = key; _value = value; }

    public string Key
    {
        get => _key;
        set { if (SetField(ref _key, value)) KeyChanged?.Invoke(value); }
    }

    public long Value
    {
        get => _value;
        set { if (SetField(ref _value, value)) ValueChanged?.Invoke(value); }
    }

    public event Action<string> KeyChanged;
    public event Action<long> ValueChanged;
}

/// <summary>列表字段（List&lt;string&gt; / List&lt;int&gt;）。</summary>
public sealed class ListFieldViewModel : ComplexFieldViewModel<ListValue>
{
    public ObservableCollection<string> Items { get; } = new();

    public ListFieldViewModel(FieldSchema schema, SaveEntry entry, bool intList) : base(schema, entry)
    {
        Reload();
        AddCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(NewItem)) return;
            Add(NewItem);
            NewItem = "";
            OnPropertyChanged(nameof(NewItem));
        });
        RemoveCommand = new RelayCommand(
            () => { if (SelectedItem is not null) Remove(SelectedItem); },
            () => SelectedItem is not null);
    }

    /// <summary>列表中当前选中的项。</summary>
    public string SelectedItem { get; set; }

    /// <summary>新增项的输入缓冲。</summary>
    public string NewItem { get; set; } = "";

    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }

    /// <summary>
    /// 用 Schema.Kind 判定元素类型，不靠文本猜测。
    /// 注意基类构造函数会调用 Parse()，此时派生类字段尚未初始化，
    /// 所以只能依赖已在构造前设置好的 Schema。
    /// </summary>
    protected override ListValue Parse(string raw) => new ListValue(raw, Kind == FieldKind.IntList);

    private void Reload()
    {
        Items.Clear();
        foreach (string s in Value.Items) Items.Add(s);
    }

    public void Add(string item)
    {
        Value.Add(item ?? "");
        Reload();
        MarkTouched();
    }

    public void Remove(string item)
    {
        int idx = Items.IndexOf(item);
        if (idx < 0) return;
        Value.RemoveAt(idx);
        Reload();
        MarkTouched();
    }

    public void UpdateAt(int index, string newItem)
    {
        Value.SetAt(index, newItem);
        MarkTouched();
    }

    public override void Revert()
    {
        base.Revert();
        Reload();
    }

    public override string DescribeChange() => $"{Display}：列表已修改（{Value.Items.Count} 项）";
}

/// <summary>外观字段（对象作 key 的字典）。</summary>
public sealed class OutfitFieldViewModel : ComplexFieldViewModel<OutfitValue>
{
    public ObservableCollection<OutfitRow> Rows { get; } = new();

    public OutfitFieldViewModel(FieldSchema schema, SaveEntry entry) : base(schema, entry) { Reload(); }

    protected override OutfitValue Parse(string raw) => new OutfitValue(raw);

    private void Reload()
    {
        Rows.Clear();
        for (int i = 0; i < Value.Items.Count; i++)
        {
            var it = Value.Items[i];
            int idx = i;
            var row = new OutfitRow(it.Low, it.High, it.Guid, it.MaterialOption);
            row.GuidChanged += g => { Value.UpdateAt(idx, g, Value.Items[idx].MaterialOption); MarkTouched(); };
            row.MaterialChanged += mo => { Value.UpdateAt(idx, Value.Items[idx].Guid, mo); MarkTouched(); };
            Rows.Add(row);
        }
    }

    public void RemoveRow(OutfitRow row)
    {
        int idx = Rows.IndexOf(row);
        if (idx < 0) return;
        Value.RemoveAt(idx);
        Reload();
        MarkTouched();
    }

    public void AddRow(long low, long high, string guid, int material)
    {
        Value.Add(low, high, guid, material);
        Reload();
        MarkTouched();
    }

    public override void Revert()
    {
        base.Revert();
        Reload();
    }

    public override string DescribeChange() => $"{Display}：外观搭配已修改（{Value.Items.Count} 项）";
}

/// <summary>外观行。</summary>
public sealed class OutfitRow : ObservableObject
{
    private string _guid;
    private int _material;

    public OutfitRow(long low, long high, string guid, int material)
    {
        Low = low; High = high; _guid = guid; _material = material;
    }

    /// <summary>key 的一部分，只读展示（改动它等于换装备槽位，风险高）。</summary>
    public long Low { get; }
    public long High { get; }

    /// <summary>槽位含义提示：low/high 是位标志，可提示对应哪一类外观。</summary>
    public string SlotHint => OutfitSlotHints.Of(Low, High);

    public string Guid
    {
        get => _guid;
        set { if (SetField(ref _guid, value)) GuidChanged?.Invoke(value); }
    }

    public int MaterialOption
    {
        get => _material;
        set { if (SetField(ref _material, value)) MaterialChanged?.Invoke(value); }
    }

    public event Action<string> GuidChanged;
    public event Action<int> MaterialChanged;
}

/// <summary>
/// 外观槽位（low/high 位标志）的含义提示。
///
/// 数值来自实测存档观察：各槽位是 2 的幂（1、4、2048、16384、33554432、67108864、
/// 134217728、268435456、-2147483648），符合位标志特征。
/// **具体对应哪一类外观未经游戏资源验证**，故只做保守提示，不写死名称。
/// </summary>
public static class OutfitSlotHints
{
    public static string Of(long low, long high)
    {
        if (high != 0) return $"槽位标志 low={low}, high={high}";
        return $"槽位标志 {low}";
    }
}

/// <summary>衣柜字段（guid 列表）。</summary>
public sealed class WardrobeFieldViewModel : ComplexFieldViewModel<WardrobeValue>
{
    public ObservableCollection<string> Guids { get; } = new();

    public WardrobeFieldViewModel(FieldSchema schema, SaveEntry entry) : base(schema, entry)
    {
        Reload();
        AddCommand = new RelayCommand(() =>
        {
            Add(NewGuid);
            NewGuid = "";
            OnPropertyChanged(nameof(NewGuid));
        });
        RemoveCommand = new RelayCommand(
            () => { if (SelectedGuid is not null) Remove(SelectedGuid); },
            () => SelectedGuid is not null);
    }

    /// <summary>当前选中的 guid。</summary>
    public string SelectedGuid { get; set; }

    /// <summary>新增 guid 的输入缓冲。</summary>
    public string NewGuid { get; set; } = "";

    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }

    protected override WardrobeValue Parse(string raw) => new WardrobeValue(raw);

    private void Reload()
    {
        Guids.Clear();
        foreach (string g in Value.OwnedItems) Guids.Add(g);
    }

    public void Add(string guid)
    {
        if (string.IsNullOrWhiteSpace(guid)) return;
        guid = guid.Trim();
        if (Value.Contains(guid))
        {
            ValidationError = "该 guid 已在衣柜中";
            OnPropertyChanged(nameof(ValidationError));
            return;
        }
        ValidationError = null;
        Value.Add(guid);
        Reload();
        MarkTouched();
    }

    public void Remove(string guid)
    {
        int idx = Guids.IndexOf(guid);
        if (idx < 0) return;
        Value.RemoveAt(idx);
        Reload();
        MarkTouched();
    }

    public override void Revert()
    {
        base.Revert();
        Reload();
    }

    public override string DescribeChange() => $"{Display}：衣柜已修改（{Value.OwnedItems.Count} 项）";
}

/// <summary>
/// 难度字段（LastDifficulty）。默认只读展示，见设计文档第 8 节开放问题 3。
/// </summary>
public sealed class DifficultyFieldViewModel : ComplexFieldViewModel<DifficultyValue>
{
    public DifficultyFieldViewModel(FieldSchema schema, SaveEntry entry) : base(schema, entry) { }

    protected override DifficultyValue Parse(string raw) => new DifficultyValue(raw);

    /// <summary>供 UI 展示的子字段清单。用类而非元组，XAML 绑定才可靠。</summary>
    public IEnumerable<DifficultyFieldRow> Fields
        => Value.Order.Select(k => new DifficultyFieldRow(
            k,
            DifficultyValue.FieldZh(k),
            Value.GetValue(k) ?? "",
            !DifficultyValue.NonEditable.Contains(k)));

    public void SetIntField(string key, long v) { Value.SetInt(key, v); MarkTouched(); }
    public void SetBoolField(string key, bool v) { Value.SetBool(key, v); MarkTouched(); }

    public override string DescribeChange() => $"{Display}：难度参数已修改";
}

/// <summary>难度对象的一个子字段行（只读展示用）。</summary>
public sealed class DifficultyFieldRow
{
    public DifficultyFieldRow(string key, string zh, string value, bool editable)
    {
        Key = key; Zh = zh; Value = value; Editable = editable;
    }

    public string Key { get; }
    public string Zh { get; }
    public string Value { get; }
    public bool Editable { get; }
}
