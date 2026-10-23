using PhasmoSaveEditor.Schema;
using PhasmoSaveEditor.ViewModels.Fields;

namespace PhasmoSaveEditor.ViewModels;

/// <summary>一次预设操作的结果摘要。</summary>
public sealed record PresetResult(string Name, int Changed, IReadOnlyList<string> Notes)
{
    public override string ToString() => $"{Name}：修改 {Changed} 个字段";
}

/// <summary>
/// 四个一键预设。
///
/// **所有预设只改 ViewModel 中的值，不碰磁盘。**
/// 用户仍需点「应用修改」并确认，才会真正写盘 —— 避免误操作直接毁档。
///
/// 三条安全规则贯穿所有预设：
///   1. **只升不降**：绝不把已有值改小。实测存档里 lighthouseKeeperCompleted = 50
///      （用进度值表达完成，不是布尔 1），若盲目置 1 会把它降级成「未完成」。
///   2. **精确匹配装备名**：用 EquipmentDefs 白名单定位字段，不用字符串通配。
///      存档里 Bone0/daily0/weekly0 也以 0/1 结尾，通配会误伤。
///   3. **不碰 Progression**：各挑战的进度目标值未知（有的是 50，有的是 1），
///      乱填可能破坏挑战判定逻辑。这类字段留给用户手动编辑。
/// </summary>
public sealed class PresetService
{
    private readonly IReadOnlyDictionary<string, FieldViewModel> _byKey;

    public PresetService(IEnumerable<FieldViewModel> fields)
    {
        _byKey = fields.ToDictionary(f => f.Key, f => f, StringComparer.Ordinal);
    }

    /// <summary>库存拉满时使用的安全上限（不用 int.MaxValue，避免游戏内显示异常）。</summary>
    public long InventoryCap { get; set; } = FieldCatalog.MaxInventory;

    // ---- 预设 1：解锁全装备 + 全三阶 --------------------------------------

    public PresetResult UnlockAllEquipment()
    {
        int changed = 0;

        foreach (var def in EquipmentDefs.Upgradable)
        {
            string eq = def.Key;

            // 三阶的「已购买解锁资格」与「已解锁可用」
            foreach (string tier in new[] { "One", "Two", "Three" })
            {
                changed += SetBoolIf(eq + "Tier" + tier + "UnlockOwned", true) ? 1 : 0;
                changed += SetBoolIf(eq + "Tier" + tier + "Unlocked", true) ? 1 : 0;
            }

            // 当前阶级与各装备栏位的阶级拉到 3
            foreach (string key in new[]
                     {
                         eq + "Tier", eq + "0Tier", eq + "1Tier", eq + "-1Tier",
                     })
            {
                changed += RaiseIntTo(key, 3) ? 1 : 0;
            }
        }

        return new PresetResult("解锁全装备 + 全三阶", changed, new[]
        {
            "已把所有可升级装备的一二三阶「解锁已购买」与「已解锁」置为 true，阶级拉到 3。",
            "装备栏位（栏位 1 / 栏位 2 / 默认）的阶级也一并拉到 3。",
        });
    }

    // ---- 预设 2：金钱拉满 + 库存拉满 --------------------------------------

    public PresetResult MaxMoneyAndInventory(long moneyCap = FieldCatalog.MaxMoney)
    {
        int changed = 0;
        var notes = new List<string>();

        // 金钱直接设为上限（金钱是「当前持有量」，不是进度，允许提高）
        if (SetIntExact("PlayersMoney", moneyCap)) changed++;

        foreach (var def in EquipmentDefs.All)
        {
            string eq = def.Key;
            // 主库存 + 分装备栏库存 + Amount 形式
            foreach (string key in new[]
                     {
                         eq + "Inventory", eq + "0", eq + "1", eq + "-1",
                         eq + "0Amount", eq + "1Amount",
                     })
            {
                changed += RaiseIntTo(key, InventoryCap) ? 1 : 0;
            }
        }

        notes.Add($"金钱设为 {moneyCap:N0}，所有装备库存提到 {InventoryCap}。");
        notes.Add("库存遵循「只升不降」：已高于上限的字段保持原值。");
        notes.Add("库存上限可在界面上调整（默认 99，未用 int 上限以避免游戏内显示异常）。");

        return new PresetResult("金钱拉满 + 库存拉满", changed, notes);
    }

    // ---- 预设 3：设定等级 / 转生 ------------------------------------------

    /// <summary>
    /// 设定等级与转生。
    ///
    /// 依据实证结论（见设计文档第 8 节开放问题 1）：
    ///   NewLevel = 身份卡上显示的等级（身份卡印 V-075 ↔ 存档 75）
    ///   Prestige = 转生次数，即等级前的罗马数字（V = 5）
    ///   Experience = **当前等级内的进度经验**，不是累计总经验（身份卡印 177/4024）
    ///
    /// 所以改等级直接写 NewLevel，**不再走旧 README 的经验换算公式**（该公式已过时）。
    /// </summary>
    /// <param name="level">目标等级，1 ~ 9999</param>
    /// <param name="prestige">目标转生次数，0 ~ 3999</param>
    /// <param name="resetLevelXp">
    /// 是否把 Experience（本级进度）归零。
    /// 默认 false —— 保留原进度，避免玩家凭空丢失已积累的经验条。
    /// </param>
    public PresetResult SetLevel(long level, long prestige, bool resetLevelXp = false)
    {
        var notes = new List<string>();
        int changed = 0;

        if (level < 1 || level > FieldCatalog.MaxLevel)
            return new PresetResult("设定等级", 0,
                new[] { $"等级需在 1 ~ {FieldCatalog.MaxLevel} 之间。" });
        if (prestige < 0 || prestige > FieldCatalog.MaxPrestige)
            return new PresetResult("设定等级", 0,
                new[] { $"转生需在 0 ~ {FieldCatalog.MaxPrestige} 之间。" });

        if (SetIntExact("NewLevel", level)) changed++;
        if (SetIntExact("Prestige", prestige)) changed++;

        // PrestigeIndex 与 Prestige 同步（实测两者相等：5 / 5）
        if (SetIntExact("PrestigeIndex", prestige)) changed++;

        if (resetLevelXp && SetIntExact("Experience", 0)) changed++;

        notes.Add($"已设定：等级 {level}，转生 {prestige}（身份卡会显示为罗马数字 + {level:000}）。");
        notes.Add(resetLevelXp
            ? "本级进度经验已归零。"
            : "本级进度经验（Experience）保留原值，未归零。");
        notes.Add("注意：旧版遗留字段 Level 未改动 —— 实证表明它不参与当前版本的等级显示。");
        notes.Add("改完进游戏请核对身份卡；若等级不对，说明游戏版本又变了，请还原备份。");

        return new PresetResult("设定等级 / 转生", changed, notes);
    }

    // ---- 预设 4：全成就 / 挑战完成 ----------------------------------------

    /// <summary>成就名里的数字词 -> 目标值。用于把进度补到「刚好完成」。</summary>
    private static readonly (string Word, long Target)[] AchievementTargets =
    {
        ("FIFTY", 50), ("THIRTY", 30), ("TEN", 10), ("FIVE", 5), ("THREE", 3),
        ("_50_", 50), ("_30_", 30), ("_10_", 10), ("_5_", 5),
    };

    public PresetResult CompleteAchievementsAndChallenges()
    {
        int changed = 0;
        var notes = new List<string>();
        var skipped = new List<string>();

        // ---- 成就计数：补到名称中隐含的目标值 ----
        foreach (var kv in _byKey)
        {
            string key = kv.Key;
            if (!key.StartsWith("ACH_", StringComparison.Ordinal)) continue;
            if (kv.Value is not IntFieldViewModel) continue;

            long? target = ParseAchievementTarget(key);
            if (target is null)
            {
                skipped.Add(key);
                continue;
            }
            if (RaiseIntTo(key, target.Value)) changed++;
        }

        // ---- 挑战：Completed / Received / Recieved ----
        // 只把「当前为 0」的置 1；已有非零值（如 lighthouseKeeperCompleted = 50）保持不动。
        foreach (var suffix in new[] { "Completed", "Received", "Recieved" })
        {
            foreach (var kv in _byKey)
            {
                string key = kv.Key;
                if (!key.EndsWith(suffix, StringComparison.Ordinal)) continue;
                if (key.Length <= suffix.Length) continue;
                if (kv.Value is not IntFieldViewModel vm) continue;

                // 只把「当前为 0」的置 1；非 0 一律不动 —— 只升不降
                if (vm.Value == 0)
                {
                    vm.SetValue(1);
                    changed++;
                }
            }
        }

        notes.Add("成就计数已补到名称中隐含的目标值（如 FIFTY→50、_30_→30）。");
        notes.Add("挑战的 Completed / Received 已从 0 置为 1；原本非 0 的保持不动（只升不降）。");
        notes.Add("各挑战的 Progression（进度）**未改动** —— 目标值因挑战而异（实测有 50 也有 1），" +
                  "乱填可能破坏判定逻辑，需要时请手动编辑。");
        if (skipped.Count > 0)
            notes.Add($"以下成就名中没有可识别的目标数字，未改动：{string.Join("、", skipped)}");
        notes.Add("重要：Steam 成就由 Steam 侧记录，改存档**不一定**能让 Steam 成就弹窗解锁。");

        return new PresetResult("全成就 / 挑战完成", changed, notes);
    }

    /// <summary>从成就名解析目标值；解析不出返回 null（宁可不改，也不乱填）。</summary>
    private static long? ParseAchievementTarget(string key)
    {
        // 优先匹配 "COMPLETE_50_" / "COMPLETE_30_" 这类带下划线的阿拉伯数字
        foreach (var (word, target) in AchievementTargets)
        {
            if (word.StartsWith("_") && key.Contains(word, StringComparison.Ordinal))
                return target;
        }
        foreach (var (word, target) in AchievementTargets)
        {
            if (!word.StartsWith("_") && key.Contains("_" + word + "_", StringComparison.Ordinal))
                return target;
        }
        return null;
    }

    // ---- 共用小工具 ---------------------------------------------------------

    private bool SetBoolIf(string key, bool value)
    {
        if (_byKey.TryGetValue(key, out var vm) && vm is BoolFieldViewModel b)
        {
            if (b.Value == value) return false;
            b.SetValue(value);
            return true;
        }
        return false;
    }

    /// <summary>精确设为某值（用于金钱、等级这类「当前量」而非「进度」的字段）。</summary>
    private bool SetIntExact(string key, long value)
    {
        if (_byKey.TryGetValue(key, out var vm) && vm is IntFieldViewModel i)
        {
            if (i.Value == value) return false;
            i.SetValue(value);
            return true;
        }
        return false;
    }

    /// <summary>
    /// **只升不降**地把整数字段提到 target。
    /// 当前值已 >= target 时不做任何事，避免把 lighthouseKeeperCompleted=50 这类
    /// 用进度值表达完成的字段降级成 1。
    /// </summary>
    private bool RaiseIntTo(string key, long target)
    {
        if (_byKey.TryGetValue(key, out var vm) && vm is IntFieldViewModel i)
        {
            if (i.Value >= target) return false;
            i.SetValue(target);
            return true;
        }
        return false;
    }
}
