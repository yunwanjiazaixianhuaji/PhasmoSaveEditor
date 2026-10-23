namespace PhasmoSaveEditor.Schema;

/// <summary>
/// 装备定义。
///
/// 存档里装备字段有两套并存的体系：
///   A. 新体系（可升级）：每种装备有 Tier / Inventory，外加一二三阶的
///      UnlockOwned（是否已购解锁）与 Unlocked（是否已解锁可用）
///   B. 旧体系（仅数量）：只有 XxxInventory，如 Lighter、Candle
///
/// 另外还有「分套路库存」：Xxx0 / Xxx1 表示装备栏 1/2 里该物品的数量，
/// 以及 Xxx0Tier / Xxx1Tier / Xxx-1Tier 表示对应位置的阶级。
/// </summary>
public sealed record EquipDef(string Key, string Chinese, bool Upgradable = true);

public static class EquipmentDefs
{
    /// <summary>新体系装备（有 Tier + 三阶解锁）。</summary>
    public static readonly EquipDef[] Upgradable =
    {
        new("EMFReader",           "EMF 读取仪"),
        new("SpiritBox",           "灵异对讲机"),
        new("UVLight",             "紫外线灯"),
        new("GhostWritingBook",    "通灵书"),
        new("Thermometer",         "温度计"),
        new("PhotoCamera",         "拍照相机"),
        new("VideoCamera",         "摄像机"),
        new("DOTSProjector",       "DOTS 投影仪"),
        new("Flashlight",          "手电筒"),
        new("MotionSensor",        "运动传感器"),
        new("SoundSensor",         "声音传感器"),
        new("ParabolicMicrophone", "抛物面麦克风"),
        new("Salt",                "盐"),
        new("SanityMedication",    "理智药剂"),
        new("Firelight",           "火焰棒"),
        new("Crucifix",            "十字架"),
        new("Tripod",              "三脚架"),
        new("Igniter",             "点火器"),
        new("HeadGear",            "头部装备"),
        new("Repellent",           "驱虫剂"),
        new("SoundRecorder",       "录音机"),
    };

    /// <summary>旧体系装备（仅库存数量）。</summary>
    public static readonly EquipDef[] Legacy =
    {
        new("SanityPills",        "理智药丸",    false),
        new("Glowstick",          "荧光棒",      false),
        new("Candle",             "蜡烛",        false),
        new("Camera",             "相机",        false),
        new("StrongFlashlight",   "强光手电",    false),
        new("UVFlashlight",       "紫外线手电",  false),
        new("DSLRCamera",         "单反相机",    false),
        new("HeadMountedCamera",  "头戴摄像机",  false),
        new("EVPRecorder",        "EVP 录音机",  false),
        new("Lighter",            "打火机",      false),
        new("Sage",               "鼠尾草",      false),
    };

    public static readonly EquipDef[] All =
        Upgradable.Concat(Legacy).ToArray();

    private static readonly Dictionary<string, string> ByKey =
        All.ToDictionary(e => e.Key, e => e.Chinese, StringComparer.Ordinal);

    /// <summary>装备的中文名；不是装备则返回 null。</summary>
    public static string ChineseOf(string key)
        => ByKey.TryGetValue(key, out string c) ? c : null;

    /// <summary>装备栏位置：0 = 栏位 1，1 = 栏位 2，-1 = 默认/全局。</summary>
    public static string SlotName(string slot) => slot switch
    {
        "0"  => "栏位 1",
        "1"  => "栏位 2",
        "-1" => "默认",
        _    => "栏位 " + slot,
    };
}
