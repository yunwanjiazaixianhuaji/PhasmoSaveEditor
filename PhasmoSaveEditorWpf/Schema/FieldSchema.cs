namespace PhasmoSaveEditor.Schema;

/// <summary>字段的元数据（不含值，值在 ViewModel 里）。</summary>
public sealed record FieldSchema
{
    /// <summary>存档里的原始 key。</summary>
    public string Key { get; init; }

    /// <summary>__type 原文。</summary>
    public string Es3Type { get; init; }

    /// <summary>中文显示名。</summary>
    public string Display { get; init; }

    /// <summary>所属分类。</summary>
    public Category Category { get; init; }

    /// <summary>编辑控件类型。</summary>
    public FieldKind Kind { get; init; }

    /// <summary>鼠标悬停提示 / 补充说明。</summary>
    public string Hint { get; init; }

    /// <summary>int/float 的允许范围（可空 = 不限制）。</summary>
    public long? Min { get; init; }
    public long? Max { get; init; }

    /// <summary>该字段是否只读（如 Difficulty 含引用 ID，改动可能失效）。</summary>
    public bool ReadOnly { get; init; }

    /// <summary>排序权重，同类内小者在前。</summary>
    public int Order { get; init; }
}

/// <summary>左侧分类树的分组。</summary>
public enum Category
{
    MoneyLevel,     // 金钱与等级
    Equipment,      // 装备
    Tarot,          // 塔罗牌
    Challenges,     // 挑战进度
    Stats,          // 统计数据
    Achievements,   // 成就
    Appearance,     // 外观与颜色
    Players,        // 玩家记录
    Advanced,       // 高级（未归类字段兜底）
}

/// <summary>用哪种编辑控件。</summary>
public enum FieldKind
{
    Int,
    Float,
    Bool,
    String,
    Color,          // {r,g,b,a}
    IntIntDict,     // {11:166,0:154} 裸整数 key
    StrIntDict,     // {"Wraith":25}
    StrList,        // ["a","b"]
    IntList,        // [1,2]
    Outfit,         // 对象作 key 的字典
    Wardrobe,       // OwnedItems guid 数组
    Difficulty,     // 含 _ES3Ref 的复合对象
    EnumInt,        // __type 非 int 但 value 是裸整数
    RawBlock,       // 兜底：直接编辑原始文本块
}

public static class CategoryInfo
{
    public static readonly (Category Cat, string Title, string Icon)[] Order =
    {
        (Category.MoneyLevel,   "金钱与等级", "💰"),
        (Category.Equipment,    "装备",       "🔦"),
        (Category.Tarot,        "塔罗牌",     "🃏"),
        (Category.Challenges,   "挑战进度",   "📅"),
        (Category.Stats,        "统计数据",   "📊"),
        (Category.Achievements, "成就",       "🏆"),
        (Category.Appearance,   "外观与颜色", "🎨"),
        (Category.Players,      "玩家记录",   "👥"),
        (Category.Advanced,     "高级",       "⚙"),
    };

    public static string TitleOf(Category c)
        => Order.FirstOrDefault(o => o.Cat == c).Title ?? c.ToString();

    public static string IconOf(Category c)
        => Order.FirstOrDefault(o => o.Cat == c).Icon ?? "";
}
