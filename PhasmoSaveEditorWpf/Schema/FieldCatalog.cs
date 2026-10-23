using System.Text.RegularExpressions;
using PhasmoSaveEditor.Core;

namespace PhasmoSaveEditor.Schema;

/// <summary>
/// 字段目录：把存档里的 597 个 key 映射成带中文名、分类、控件类型的 FieldSchema。
///
/// 采用**规则式匹配**而非手写 597 条映射表，理由：
///   - 装备字段高度模式化（21 种装备 × 十几个后缀），手写必然出错且难维护
///   - 游戏大版本更新会新增字段，规则能自动归位，未命中的进「高级」兜底组
///   - 规则按特异性从高到低排列，先精确后模糊
///
/// 注意：不做「字符串通配批量替换」。存档里 Recieved（拼写错误）与 Received（正确）
/// 两种拼写并存，必须分别处理。
/// </summary>
public static class FieldCatalog
{
    /// <summary>金钱上限（沿用旧版修改器的取值）。</summary>
    public const long MaxMoney = 999_999_999;

    /// <summary>库存类的安全上限。不用 int.MaxValue，避免游戏内显示或逻辑异常。</summary>
    public const long MaxInventory = 99;

    /// <summary>等级上限（实测两个开源修改器均按 9999 校验）。</summary>
    public const long MaxLevel = 9999;

    /// <summary>转生上限（v0.17.0.0 更新日志：名声上限提升至 4000，即 0~3999）。</summary>
    public const long MaxPrestige = 3999;

    /// <summary>由存档字段列表构建完整目录。</summary>
    public static IReadOnlyList<FieldSchema> Build(IReadOnlyList<SaveEntry> entries)
    {
        var list = new List<FieldSchema>(entries.Count);
        int i = 0;
        foreach (var e in entries)
            list.Add(Classify(e, i++));
        return list;
    }

    /// <summary>对单个字段分类。</summary>
    public static FieldSchema Classify(SaveEntry e, int order)
    {
        string k = e.Key;
        string t = e.Type;

        // ---- 0. 精确覆盖表（最高优先级）------------------------------------
        if (Explicit.TryGetValue(k, out var ex))
            return ex with { Key = k, Es3Type = t, Order = ex.Order >= 0 ? ex.Order : order };

        // ---- 1. 复杂类型：按 __type 判定 ------------------------------------
        var complex = ClassifyByType(k, t, order);
        if (complex is not null) return complex;

        // ---- 2. 装备体系 ---------------------------------------------------
        var eq = ClassifyEquipment(k, t, order);
        if (eq is not null) return eq;

        // ---- 3. 塔罗牌 -----------------------------------------------------
        var tarot = TarotZh.TryGetValue(k, out string tz);
        if (tarot)
            return New(k, t, $"塔罗牌 · {tz}", Category.Tarot, IntKind(t),
                       hint: "已发现的该类塔罗牌数量", order: order, max: MaxInventory);

        // ---- 4. 每日 / 每周挑战 --------------------------------------------
        var ch = ClassifyChallenge(k, t, order);
        if (ch is not null) return ch;

        // ---- 5. 成就 -------------------------------------------------------
        if (k.StartsWith("ACH_", StringComparison.Ordinal))
            return New(k, t, "成就 · " + AchievementZh(k), Category.Achievements, IntKind(t),
                       hint: "成就进度计数", order: order);

        // ---- 6. 统计（float 一律归统计）------------------------------------
        if (t == "float")
            return New(k, t, StatZh.TryGetValue(k, out string fz) ? fz : k,
                       Category.Stats, FieldKind.Float,
                       hint: "累计统计量（浮点）", order: order);

        // ---- 7. 其余具名 int 字段 ------------------------------------------
        var named = ClassifyNamed(k, t, order);
        if (named is not null) return named;

        // ---- 8. 兜底：高级 -------------------------------------------------
        return New(k, t, k, Category.Advanced, DefaultKind(t),
                   hint: "未归类字段，请谨慎修改", order: order);
    }

    // ---- 复杂类型分类 -------------------------------------------------------

    private static FieldSchema ClassifyByType(string k, string t, int order)
    {
        if (t == "Color")
            return New(k, t, ColorZh.TryGetValue(k, out string cz) ? cz : k,
                       Category.Appearance, FieldKind.Color,
                       hint: "RGBA 颜色，各分量取值 0~1", order: order);

        if (t.StartsWith("PlayerOutfit", StringComparison.Ordinal))
            return New(k, t, "角色外观搭配", Category.Appearance, FieldKind.Outfit,
                       hint: "以 {low,high} 为 key 的字典，值为外观 guid 与材质选项。" +
                             "guid 对应游戏资源，具体物品名需挖游戏资源才能映射",
                       order: order);

        if (t.StartsWith("PlayerWardrobe", StringComparison.Ordinal))
            return New(k, t, "衣柜已拥有物品", Category.Appearance, FieldKind.Wardrobe,
                       hint: "OwnedItems 为已拥有的外观 guid 列表", order: order);

        if (t.StartsWith("Difficulty", StringComparison.Ordinal))
            return New(k, t, "上次使用的难度", Category.Advanced, FieldKind.Difficulty,
                       hint: "含 _ES3Ref 引用 ID 与多个子字段，改动可能与引用不一致而失效，" +
                             "建议只读查看", order: order, readOnly: true);

        if (t.Contains("Dictionary`2"))
        {
            // 类型串形如 Dictionary`2[[<KEYTYPE>],[<VALUETYPE>]],mscorlib
            // 只看 KEY 段：裸整数 key（playedMaps）与字符串 key（mostCommonGhosts）
            // 的 value 都是 Int32，所以不能用「是否含 Int32」判断。
            int keyStart = t.IndexOf("[[", StringComparison.Ordinal);
            int keyEnd = t.IndexOf("],[", StringComparison.Ordinal);
            string keyType = keyStart >= 0 && keyEnd > keyStart
                ? t.Substring(keyStart + 2, keyEnd - keyStart - 2)
                : "";
            bool intKey = keyType.StartsWith("System.Int32", StringComparison.Ordinal);

            var kind = intKey ? FieldKind.IntIntDict : FieldKind.StrIntDict;
            string disp = k switch
            {
                "playedMaps"       => "各地图游玩次数",
                "mostCommonGhosts" => "最常遇到的鬼种",
                "ghostKills"       => "各鬼种击杀次数",
                _                  => k,
            };
            string hint = k switch
            {
                "playedMaps" => "key 为地图 ID（数字），value 为游玩次数。" +
                                "地图 ID 与名称的对应关系未经验证，请谨慎修改 key",
                "mostCommonGhosts" => "key 为鬼种英文名，value 为遇到次数",
                "ghostKills" => "key 为鬼种英文名，value 为击杀次数",
                _ => "字典字段",
            };
            return New(k, t, disp, Category.Stats, kind, hint: hint, order: order);
        }

        if (t.Contains("List`1"))
        {
            bool isInt = t.Contains("System.Int32");
            var (disp, cat) = k switch
            {
                _ when k.StartsWith("recentPlayer", StringComparison.Ordinal)
                    => ("近期玩家 · " + PlayerListZh(k), Category.Players),
                _ when k.StartsWith("blockedPlayer", StringComparison.Ordinal)
                    => ("已拉黑玩家 · " + PlayerListZh(k), Category.Players),
                _ => (k, Category.Advanced),
            };
            return New(k, t, disp, cat, isInt ? FieldKind.IntList : FieldKind.StrList,
                       hint: "列表字段，可增删条目", order: order);
        }

        // 其余带命名空间的类型（含混淆名），value 是裸整数 -> 枚举
        if (t.Contains(',') || t.Contains('+'))
            return New(k, t, EnumZh.TryGetValue(k, out string ez) ? ez : k,
                       Category.Advanced, FieldKind.EnumInt,
                       hint: $"枚举字段，__type = {Trunc(t, 60)}。" +
                             "只能按整数修改，取值含义未完全确认",
                       order: order);

        return null;
    }

    // ---- 装备 ---------------------------------------------------------------

    /// <summary>阶级后缀：TierOne/Two/Three。</summary>
    private static readonly Regex EquipTierUnlockRe = new(
        @"^(?<eq>[A-Z][A-Za-z]+)Tier(?<n>One|Two|Three)(?<suf>UnlockOwned|Unlocked)$",
        RegexOptions.Compiled);

    /// <summary>分套路：{Equip}{slot}Tier / {Equip}{slot}Amount / {Equip}{slot}。</summary>
    private static readonly Regex EquipSlotRe = new(
        @"^(?<eq>[A-Z][A-Za-z]+)(?<slot>-1|0|1)(?<suf>Tier|Amount)$",
        RegexOptions.Compiled);

    /// <summary>{Equip}{slot}（无后缀）= 分套路库存数量。</summary>
    private static readonly Regex EquipSlotBareRe = new(
        @"^(?<eq>[A-Z][A-Za-z]+)(?<slot>-1|0|1)$",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, string> TierNumZh = new()
    {
        ["One"] = "一阶", ["Two"] = "二阶", ["Three"] = "三阶",
    };

    private static FieldSchema ClassifyEquipment(string k, string t, int order)
    {
        // committed{Equip}Amount —— 出战配置里预提交的数量
        if (k.StartsWith("committed", StringComparison.Ordinal) &&
            k.EndsWith("Amount", StringComparison.Ordinal))
        {
            string mid = k.Substring("committed".Length, k.Length - "committed".Length - "Amount".Length);
            if (EquipmentDefs.ChineseOf(mid) is string cz)
                return New(k, t, $"{cz} · 出战预提交数量", Category.Equipment, IntKind(t),
                           hint: "上次出战配置中该装备预提交的数量",
                           order: order, min: 0, max: MaxInventory);
            return null;
        }

        // {Equip}Tier / {Equip}Inventory
        foreach (var def in EquipmentDefs.All)
        {
            string eq = def.Key;
            if (k == eq + "Tier")
                return New(k, t, $"{def.Chinese} · 当前阶级", Category.Equipment, IntKind(t),
                           hint: "1~3 阶。改阶级前请确认对应阶级的解锁已打开",
                           order: order, min: 0, max: 3);
            if (k == eq + "Inventory")
                return New(k, t, $"{def.Chinese} · 库存数量", Category.Equipment, IntKind(t),
                           order: order, min: 0, max: MaxInventory);
        }

        // {Equip}TierOne/Two/ThreeUnlockOwned / Unlocked
        var m = EquipTierUnlockRe.Match(k);
        if (m.Success && EquipmentDefs.ChineseOf(m.Groups["eq"].Value) is string ceq)
        {
            string num = TierNumZh[m.Groups["n"].Value];
            bool owned = m.Groups["suf"].Value == "UnlockOwned";
            return New(k, t,
                       $"{ceq} · {num}{(owned ? "解锁已购买" : "已解锁")}",
                       Category.Equipment, FieldKind.Bool,
                       hint: owned
                           ? "是否已花钱买下该阶级的解锁资格"
                           : "该阶级是否已解锁可用。通常需同时打开「解锁已购买」",
                       order: order);
        }

        // {Equip}{slot}Tier / {Equip}{slot}Amount
        m = EquipSlotRe.Match(k);
        if (m.Success && EquipmentDefs.ChineseOf(m.Groups["eq"].Value) is string seq)
        {
            string slot = EquipmentDefs.SlotName(m.Groups["slot"].Value);
            bool isTier = m.Groups["suf"].Value == "Tier";
            return New(k, t,
                       $"{seq} · {slot}{(isTier ? "阶级" : "数量")}",
                       Category.Equipment, IntKind(t),
                       hint: $"装备{slot}中该物品的{(isTier ? "阶级" : "数量")}",
                       order: order, min: 0, max: isTier ? 3 : MaxInventory);
        }

        // {Equip}{slot} 裸形式 = 分套路库存
        m = EquipSlotBareRe.Match(k);
        if (m.Success && EquipmentDefs.ChineseOf(m.Groups["eq"].Value) is string beq)
        {
            string slot = EquipmentDefs.SlotName(m.Groups["slot"].Value);
            return New(k, t, $"{beq} · {slot}库存", Category.Equipment, IntKind(t),
                       hint: $"装备{slot}中该物品的数量（旧字段体系）",
                       order: order, min: 0, max: MaxInventory);
        }

        return null;
    }

    // ---- 挑战 ---------------------------------------------------------------

    private static readonly Regex DailyWeeklyRe = new(
        @"^(?<p>daily|weekly)(?<idx>-?\d+)(?<suf>Variant|Recieved|Received|Completed|Progression)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static FieldSchema ClassifyChallenge(string k, string t, int order)
    {
        var m = DailyWeeklyRe.Match(k);
        if (m.Success)
        {
            string prefix = m.Groups["p"].Value.ToLowerInvariant();
            string idx = m.Groups["idx"].Value;
            string zhPrefix = prefix == "daily" ? "每日挑战" : "每周挑战";
            string tag = idx == "-1" ? "上期" : "#" + idx;
            string suf = m.Groups["suf"].Success ? m.Groups["suf"].Value : "";

            var (zhSuf, hint) = suf.ToLowerInvariant() switch
            {
                "variant"     => ("变体", "该期挑战的变体编号"),
                "recieved"    => ("已领取", "奖励是否已领取（注意此 key 拼写为 Recieved）"),
                "received"    => ("已领取", "奖励是否已领取"),
                "completed"   => ("已完成", "该期挑战是否已完成"),
                "progression" => ("进度", "该期挑战的进度计数"),
                _             => ("挑战编号", "该期对应的挑战内容编号"),
            };
            string disp = suf.Length == 0 ? $"{zhPrefix} {tag} · 挑战编号"
                                          : $"{zhPrefix} {tag} · {zhSuf}";
            return New(k, t, disp, Category.Challenges, IntKind(t), hint: hint, order: order);
        }

        // 种子与难度挑战
        if (k == "dailyChallengeSeed")
            return New(k, t, "每日挑战种子", Category.Challenges, IntKind(t),
                       hint: "决定每日挑战内容的随机种子", order: order);
        if (k == "weeklyChallengeSeed")
            return New(k, t, "每周挑战种子", Category.Challenges, IntKind(t),
                       hint: "决定每周挑战内容的随机种子", order: order);
        if (k.StartsWith("weeklyDifficulty", StringComparison.Ordinal))
        {
            string rest = k.Substring("weeklyDifficulty".Length);
            return New(k, t, "每周难度挑战 · " + (rest.Length == 0 ? "种子" : rest),
                       Category.Challenges, IntKind(t), order: order);
        }

        // 具名挑战：XxxProgression / XxxCompleted / XxxReceived / XxxRecieved
        foreach (var suf in new[] { "Progression", "Completed", "Received", "Recieved" })
        {
            if (!k.EndsWith(suf, StringComparison.Ordinal) || k.Length <= suf.Length) continue;
            string baseName = k.Substring(0, k.Length - suf.Length);
            if (NamedChallengeZh.TryGetValue(baseName, out string ncz))
            {
                string zhSuf = suf switch
                {
                    "Progression" => "进度",
                    "Completed"   => "已完成",
                    _             => "已领取",
                };
                return New(k, t, $"{ncz} · {zhSuf}", Category.Challenges, IntKind(t),
                           hint: suf.StartsWith("Received", StringComparison.OrdinalIgnoreCase) ||
                                 suf == "Recieved"
                               ? "奖励是否已领取" : null,
                           order: order);
            }
        }

        // objective0Completed / objective1Completed / objective2Completed
        if (Regex.IsMatch(k, @"^objective\d+Completed$"))
            return New(k, t, $"周常目标 {k.Substring(9, k.Length - 18)} · 已完成",
                       Category.Challenges, IntKind(t), order: order);

        // 节日 / 季节活动
        if (SeasonalZh.TryGetValue(k, out string sz))
            return New(k, t, sz, Category.Challenges, IntKind(t), order: order);

        // 地图相关（Easter2025、配方）
        if (k.EndsWith("Easter2025Complete", StringComparison.Ordinal))
            return New(k, t, $"2025 复活节活动 · {k.Replace("Easter2025Complete", "")} · 已完成",
                       Category.Challenges, IntKind(t), order: order);

        if (k.StartsWith("Recipe", StringComparison.Ordinal))
        {
            string mapCode = k.Substring("Recipe".Length);
            return New(k, t, $"自定义难度预设 · {MapCodeZh(mapCode)}", Category.Challenges,
                       IntKind(t), hint: "地图相关的自定义难度预设解锁状态", order: order);
        }

        return null;
    }

    // ---- 具名 int 字段 ------------------------------------------------------

    private static FieldSchema ClassifyNamed(string k, string t, int order)
    {
        if (StatZh.TryGetValue(k, out string sz))
            return New(k, t, sz, Category.Stats, IntKind(t),
                       hint: "累计统计量", order: order);

        if (CursedZh.TryGetValue(k, out string czz))
            return New(k, t, czz, Category.Stats, IntKind(t),
                       hint: "诅咒道具相关统计", order: order, min: 0, max: MaxInventory);

        if (k.StartsWith("Bone", StringComparison.Ordinal) &&
            int.TryParse(k.AsSpan(4), out int boneIdx))
            return New(k, t, $"骨头收集 · 地图 {boneIdx}", Category.Stats, IntKind(t),
                       hint: "该地图上已发现的骨头数量。地图 ID 对应关系未经验证",
                       order: order, min: 0, max: MaxInventory);

        if (k.StartsWith("AutoBuy", StringComparison.Ordinal))
            return New(k, t, $"自动购买 · 槽位 {k.Substring(7)}", Category.Advanced, IntKind(t),
                       hint: "商店自动购买的槽位开关", order: order);
        if (k.StartsWith("AutoAdd", StringComparison.Ordinal))
            return New(k, t, $"自动添加 · 槽位 {k.Substring(7)}", Category.Advanced, IntKind(t),
                       hint: "自动添加到装备栏的槽位开关", order: order);

        if (k.EndsWith("TutorialComplete", StringComparison.Ordinal))
            return New(k, t, "教程 · " + k.Replace("TutorialComplete", "") + " 已完成",
                       Category.Advanced, IntKind(t), order: order);

        return null;
    }

    // ---- 精确覆盖表 ---------------------------------------------------------

    /// <summary>
    /// 需要特殊处理的字段（含范围限制、只读、易误解语义的说明）。
    /// Order 用负数表示在分类内置顶。
    /// </summary>
    private static readonly Dictionary<string, FieldSchema> Explicit = new(StringComparer.Ordinal)
    {
        ["PlayersMoney"] = T("金钱", Category.MoneyLevel, FieldKind.Int, order: -100,
            min: 0, max: MaxMoney, hint: "当前持有的金钱"),

        // 等级语义已实证确认（见设计文档第 8 节）
        ["NewLevel"] = T("等级（当前显示等级）", Category.MoneyLevel, FieldKind.Int, order: -99,
            min: 1, max: MaxLevel,
            hint: "身份卡上显示的等级。实证：身份卡印 V-075 ↔ 存档 75。改等级写这里"),
        ["Prestige"] = T("转生等级（名声）", Category.MoneyLevel, FieldKind.Int, order: -98,
            min: 0, max: MaxPrestige,
            hint: "身份卡等级前的罗马数字。实证：V = 5 ↔ 存档 Prestige = 5"),
        ["PrestigeIndex"] = T("转生索引", Category.MoneyLevel, FieldKind.Int, order: -97,
            hint: "与 Prestige 同步的内部索引，通常无需单独修改"),
        ["PrestigeTheme"] = T("启用转生主题界面", Category.MoneyLevel, FieldKind.Bool, order: -96,
            hint: "界面是否使用转生主题配色"),
        ["Experience"] = T("本级进度经验", Category.MoneyLevel, FieldKind.Int, order: -95,
            min: 0,
            hint: "**当前等级内的进度经验，不是累计总经验**。" +
                  "实证：身份卡印 177/4024 ↔ 存档 177。改等级请写 NewLevel"),
        ["Level"] = T("旧版等级（遗留字段）", Category.MoneyLevel, FieldKind.Int, order: -94,
            hint: "转生系统引入前的旧字段，当前版本不用于显示等级（对应日志里的 Legacy Level）。" +
                  "改等级请写 NewLevel"),
        ["myTotalExp"] = T("累计经验（用途未确认）", Category.MoneyLevel, FieldKind.Int, order: -93,
            hint: "含义未经实证确认，两个开源修改器均不读写此字段。谨慎修改"),
        ["RewardMultiplier"] = T("奖励倍率", Category.MoneyLevel, FieldKind.Float, order: -92,
            hint: "结算奖励倍率"),

        ["LastMode"] = T("上次的游戏模式", Category.Advanced, FieldKind.Int, order: -10),
        ["LastLoadout"] = T("上次使用的装备配置", Category.Advanced, FieldKind.Int, order: -9),
        ["EquippedLoadout"] = T("当前装备配置", Category.Advanced, FieldKind.Int, order: -8),
        ["LastCustomize"] = T("上次自定义页签", Category.Advanced, FieldKind.Int, order: -7),
        ["SaveVersion"] = T("存档格式版本", Category.Advanced, FieldKind.Int, order: -6,
            hint: "请勿修改。改动可能导致游戏拒绝读取存档"),
        ["LegacyColor"] = T("旧版配色方案", Category.Appearance, FieldKind.Int, order: -5),
        ["CardThemeIndex"] = T("身份卡主题编号", Category.Appearance, FieldKind.Int, order: -4),
        ["BadgeIndex"] = T("徽章编号", Category.Appearance, FieldKind.Int, order: -3),
        ["RoleIndex"] = T("角色编号", Category.Appearance, FieldKind.Int, order: -2),

        ["GhostType"] = T("上次遇到的鬼种", Category.Stats, FieldKind.String, order: -1,
            hint: "上次对局识别出的鬼种名称（中文）"),

        // 以下两个以 "Complete"（无 d）结尾，不匹配常规的 Completed 后缀规则
        ["halloween23Complete"] = T("2023 万圣节活动 · 已完成", Category.Challenges,
            FieldKind.Int, order: 0),
        ["GunComplete"] = T("枪械挑战 · 已完成", Category.Challenges, FieldKind.Int, order: 0),

        ["lastSmallSunnyMeadowsMap"] = T("上次游玩的小向阳草甸地图", Category.Stats,
            FieldKind.Int, order: 0,
            hint: "向阳草甸有大小两种布局，此字段记录上次用的是哪个"),
    };

    private static FieldSchema T(string display, Category cat, FieldKind kind,
                                 int order = 0, long? min = null, long? max = null,
                                 string hint = null, bool readOnly = false)
        => new()
        {
            Key = "", Es3Type = "", Display = display, Category = cat, Kind = kind,
            Hint = hint, Min = min, Max = max, ReadOnly = readOnly, Order = order,
        };

    // ---- 名称字典 -----------------------------------------------------------

    private static readonly Dictionary<string, string> ColorZh = new()
    {
        ["CursorColor"] = "光标颜色",
        ["Player0Color"] = "玩家 1 颜色",
        ["Player1Color"] = "玩家 2 颜色",
        ["Player2Color"] = "玩家 3 颜色",
        ["Player3Color"] = "玩家 4 颜色",
    };

    private static readonly Dictionary<string, string> EnumZh = new()
    {
        ["currentSeasonalEvent"] = "当前季节活动类型",
        ["RoleType"] = "角色类型",
    };

    private static readonly Dictionary<string, string> TarotZh = new()
    {
        ["TarotFool"] = "愚者", ["TarotMagician"] = "魔术师", ["TarotPriestess"] = "女祭司",
        ["TarotEmpress"] = "皇后", ["TarotEmperor"] = "皇帝", ["TarotHierophant"] = "教皇",
        ["TarotLovers"] = "恋人", ["TarotChariot"] = "战车", ["TarotStrength"] = "力量",
        ["TarotHermit"] = "隐士", ["TarotWheel"] = "命运之轮", ["TarotJustice"] = "正义",
        ["TarotHangedMan"] = "倒吊人", ["TarotDeath"] = "死神", ["TarotTemperance"] = "节制",
        ["TarotDevil"] = "恶魔", ["TarotTower"] = "高塔", ["TarotStar"] = "星星",
        ["TarotMoon"] = "月亮", ["TarotSun"] = "太阳", ["TarotJudgement"] = "审判",
        ["TarotWorld"] = "世界",
    };

    private static readonly Dictionary<string, string> CursedZh = new()
    {
        ["MirrorsFound"] = "诅咒道具 · 已发现镜子",
        ["MusicBoxesFound"] = "诅咒道具 · 已发现八音盒",
        ["OuijasFound"] = "诅咒道具 · 已发现通灵板",
        ["VoodoosFound"] = "诅咒道具 · 已发现巫毒娃娃",
        ["MonkeyPawFound"] = "诅咒道具 · 已发现猴爪",
        ["SummoningCirclesUsed"] = "诅咒道具 · 召唤阵使用次数",
        ["amountOfCursedHuntsTriggered"] = "诅咒猎杀触发次数",
        ["amountOfCursedPossessionsUsed"] = "诅咒道具使用次数",
    };

    /// <summary>float 与部分 int 统计量的中文名。</summary>
    private static readonly Dictionary<string, string> StatZh = new()
    {
        // ---- float 统计（13 个）----
        ["distanceTravelled"] = "行走总距离",
        ["ghostDistanceTravelled"] = "鬼魂移动总距离",
        ["timeSpentInvestigating"] = "调查总时长",
        ["timeSpentInLight"] = "处于光亮中的时长",
        ["timeSpentInDark"] = "处于黑暗中的时长",
        ["timeSpentInTruck"] = "待在车内的时长",
        ["timeSpentInGhostsRoom"] = "待在鬼魂房间的时长",
        ["timeInFavouriteRoom"] = "待在鬼魂最爱房间的时长",
        ["totalHuntTime"] = "猎杀总时长",
        ["timeSpentBeingChased"] = "被追逐的时长",
        ["sanityGained"] = "累计恢复的理智",
        ["sanityLost"] = "累计损失的理智",

        // ---- int 统计 ----
        ["amountOfGhostHunts"] = "遭遇猎杀次数",
        ["amountOfGhostEvents"] = "鬼魂显形次数",
        ["amountOfGhostInteractions"] = "鬼魂互动次数",
        ["abilitiesUsed"] = "鬼魂技能使用次数",
        ["ghostsIdentifiedAmount"] = "正确识别鬼种次数",
        ["ghostsMisidentifiedAmount"] = "错误识别鬼种次数",
        ["ghostsRepelled"] = "成功驱鬼次数（十字架）",
        ["objectivesCompleted"] = "完成目标数",
        ["photosTaken"] = "拍照次数",
        ["videosTaken"] = "录像次数",
        ["soundsTaken"] = "录音次数",
        ["phrasesRecognized"] = "识别到的语音次数",
        ["amountOfBonesCollected"] = "收集骨头总数",
        ["doorsMoved"] = "门被移动次数",
        ["lightsSwitched"] = "开关灯次数",
        ["fuseboxToggles"] = "电闸操作次数",
        ["objectsUsed"] = "使用物品次数",
        ["roomChanged"] = "鬼魂换房次数",
        ["itemsBought"] = "购买物品次数",
        ["itemsLost"] = "丢失物品次数",
        ["moneySpent"] = "累计花费金钱",
        ["moneyEarned"] = "累计赚取金钱",
        ["diedAmount"] = "死亡次数",
        ["revivedAmount"] = "被复活次数",
        ["GoldMultiplierReward"] = "金牌倍率奖励",
        ["SilverMultiplierReward"] = "银牌倍率奖励",
        ["BronzeMultiplierReward"] = "铜牌倍率奖励",
    };

    private static readonly Dictionary<string, string> NamedChallengeZh = new()
    {
        ["Moneybags"] = "挑战 · 钱袋子",
        ["ApocalypseGold"] = "末日挑战 · 金",
        ["ApocalypseSilver"] = "末日挑战 · 银",
        ["ApocalypseBronze"] = "末日挑战 · 铜",
        ["lighthouseKeeper"] = "挑战 · 灯塔守望者",
        ["lighthouseFerrymen"] = "挑战 · 灯塔摆渡人",
        ["rangerChallenge"] = "挑战 · 巡林员",
        ["sunnyMeadowsSurvival"] = "挑战 · 向阳草甸生存",
        ["NellsDiner"] = "挑战 · Nell 餐厅",
        ["AchievementHunter"] = "成就猎人",
    };

    private static readonly Dictionary<string, string> SeasonalZh = new()
    {
        ["Halloween2023Seen"] = "已看过 2023 万圣节活动",
        ["Holiday2022Seen"] = "已看过 2022 节日活动",
        ["currentSeasonalEvent"] = "当前季节活动类型",
    };

    private static string MapCodeZh(string code) => code switch
    {
        "TW" => "Tanglewood（坦格伍德）",
        "EF" => "Edgefield（埃奇菲尔德）",
        "RV" => "Ridgeview（岭景）",
        "GF" => "Grafton（格拉夫顿）",
        "Willow" => "Willow（柳街）",
        "BD" => "Bleasdale（布莱斯代尔）",
        "Prison" => "Sunny Meadows（向阳草甸）",
        "CWW" => "Camp Woodwind（伍德温德营地）",
        "Maple" => "Maple Lodge（梅普尔小屋）",
        _ => code,
    };

    private static string AchievementZh(string k)
        => k.Substring(4).Replace('_', ' ').ToLowerInvariant();

    private static string PlayerListZh(string k)
    {
        if (k.EndsWith("PlatformIDS", StringComparison.Ordinal)) return "平台 ID";
        if (k.EndsWith("Platforms", StringComparison.Ordinal)) return "平台";
        if (k.EndsWith("IDS", StringComparison.Ordinal)) return "ID";
        if (k.EndsWith("Names", StringComparison.Ordinal)) return "名称";
        return k;
    }

    // ---- 工具 ---------------------------------------------------------------

    private static FieldKind IntKind(string t) => t == "bool" ? FieldKind.Bool
                                                : t == "float" ? FieldKind.Float
                                                : FieldKind.Int;

    private static FieldKind DefaultKind(string t) => t switch
    {
        "int" => FieldKind.Int,
        "float" => FieldKind.Float,
        "bool" => FieldKind.Bool,
        "string" => FieldKind.String,
        _ => FieldKind.RawBlock,
    };

    private static FieldSchema New(string key, string type, string display, Category cat,
                                   FieldKind kind, string hint = null, int order = 0,
                                   long? min = null, long? max = null, bool readOnly = false)
        => new()
        {
            Key = key, Es3Type = type, Display = display, Category = cat, Kind = kind,
            Hint = hint, Order = order, Min = min, Max = max, ReadOnly = readOnly,
        };

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
}
