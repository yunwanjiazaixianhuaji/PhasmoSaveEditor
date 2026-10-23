using PhasmoSaveEditor.Complex;
using PhasmoSaveEditor.Core;
using PhasmoSaveEditor.Schema;

namespace PhasmoSaveEditor.ViewModels.Fields;

/// <summary>
/// 字段视图模型基类。
///
/// **贯穿全层的设计原则：未触碰的字段返回原文。**
/// 只有用户真正改过（Touched）才生成新文本；未改时 CurrentRaw 恒等于 OriginalRaw。
/// 这样「没动的字段绝对不变」是结构性保证，不依赖每种类型格式复刻的完备性。
/// 复杂类型天然满足（IComplexValue.Serialize 在未修改时返回原文）。
/// </summary>
public abstract class FieldViewModel : ObservableObject
{
    protected FieldViewModel(FieldSchema schema, SaveEntry entry)
    {
        Schema = schema;
        Entry = entry;
        OriginalRaw = entry.RawValue;
        RevertCommand = new RelayCommand(
            () => { Revert(); NotifyValueChanged(); },
            () => IsModified);
    }

    public FieldSchema Schema { get; }
    public SaveEntry Entry { get; }

    public string Key => Schema.Key;
    public string Display => Schema.Display;
    public Category Category => Schema.Category;
    public FieldKind Kind => Schema.Kind;
    public string Hint => Schema.Hint;
    public bool IsReadOnly => Schema.ReadOnly;

    /// <summary>加载时的原始文本。</summary>
    public string OriginalRaw { get; }

    /// <summary>用户是否触碰过该字段。</summary>
    protected bool Touched { get; set; }

    /// <summary>当前应写回存档的文本。未触碰时 = 原文。</summary>
    public abstract string CurrentRaw { get; }

    /// <summary>是否相对加载时有改动。复杂字段会重写为基于解析器的判定。</summary>
    public virtual bool IsModified => Touched && CurrentRaw != OriginalRaw;

    /// <summary>校验错误（null = 合法）。非法时 ApplyTo 会跳过该字段。</summary>
    public string ValidationError { get; protected set; }

    /// <summary>是否可安全写回。</summary>
    public bool IsValid => ValidationError is null;

    /// <summary>把当前值写入文档。返回是否真的写了。</summary>
    public virtual bool ApplyTo(SaveDocument doc)
    {
        if (IsReadOnly) return false;
        if (!IsValid) return false;
        if (!IsModified) return false;
        doc.SetRaw(Key, CurrentRaw);
        return true;
    }

    /// <summary>放弃修改。</summary>
    public abstract void Revert();

    /// <summary>
    /// 单项还原命令（模板里的「还原」按钮绑定它）。
    /// 命令体是延迟执行的，所以在基类构造里初始化即可 ——
    /// 真正调用时派生类已完全构造好。
    /// </summary>
    public RelayCommand RevertCommand { get; }

    /// <summary>值变化后统一调用：刷新 IsModified 并通知外部（主窗口靠它统计改动数）。</summary>
    protected void NotifyValueChanged()
    {
        OnPropertyChangeds(nameof(IsModified), nameof(CurrentRaw));
        Changed?.Invoke(this);
    }

    /// <summary>任一字段变化时触发，供 MainViewModel 汇总状态。</summary>
    public event Action<FieldViewModel> Changed;

    /// <summary>按 Schema.Kind 建立对应的视图模型。</summary>
    public static FieldViewModel Create(SaveEntry entry, FieldSchema schema) => schema.Kind switch
    {
        FieldKind.Int       => new IntFieldViewModel(schema, entry),
        FieldKind.Float     => new FloatFieldViewModel(schema, entry),
        FieldKind.Bool      => new BoolFieldViewModel(schema, entry),
        FieldKind.String    => new StringFieldViewModel(schema, entry),
        FieldKind.EnumInt   => new EnumIntFieldViewModel(schema, entry),
        FieldKind.Color     => new ColorFieldViewModel(schema, entry),
        FieldKind.IntIntDict => new DictFieldViewModel(schema, entry, intKey: true),
        FieldKind.StrIntDict => new DictFieldViewModel(schema, entry, intKey: false),
        FieldKind.IntList   => new ListFieldViewModel(schema, entry, intList: true),
        FieldKind.StrList   => new ListFieldViewModel(schema, entry, intList: false),
        FieldKind.Outfit    => new OutfitFieldViewModel(schema, entry),
        FieldKind.Wardrobe  => new WardrobeFieldViewModel(schema, entry),
        FieldKind.Difficulty => new DifficultyFieldViewModel(schema, entry),
        _                   => new RawBlockFieldViewModel(schema, entry),
    };

    /// <summary>生成一段人类可读的「原值 → 新值」摘要，用于写入确认对话框。</summary>
    public abstract string DescribeChange();
}
