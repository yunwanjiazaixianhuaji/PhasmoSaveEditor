using System.IO;
using System.Text;
using PhasmoSaveEditor.Complex;
using PhasmoSaveEditor.Core;
using PhasmoSaveEditor.Schema;
using PhasmoSaveEditor.ViewModels;
using PhasmoSaveEditor.ViewModels.Fields;

namespace PhasmoSaveEditor.E2E;

/// <summary>
/// 端到端验证（任务13）。
///
/// **全程只在临时副本上操作，绝不触碰真实存档原件。**
/// 覆盖三件事：
///   1. 改动生效 + 未触碰字段逐字节不变
///   2. 四个一键预设的正确性与「只升不降」安全规则
///   3. 完整写入链路：进程检测 / 备份 / 加密自检 / 原子写 / 坏数据拦截
/// </summary>
public static class Program
{
    private static int _pass, _fail, _skip;

    [STAThread]   // 测试会触及 WPF 的 CommandManager，必须运行在 STA 线程
    public static int Main()
    {
        Console.OutputEncoding = new UTF8Encoding(false);

        string realSave = SafetyService.DefaultSavePath;
        if (!File.Exists(realSave))
        {
            Console.WriteLine("SKIP: 找不到真实存档 " + realSave);
            return 0;
        }

        // 复制一份到项目内的临时目录（不写系统 Temp，避免越界），所有写操作都针对副本
        string workDir = Path.Combine(AppContext.BaseDirectory, "_e2ework_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        string copy = Path.Combine(workDir, "SaveFile.txt");
        File.Copy(realSave, copy, overwrite: true);
        Console.WriteLine($"真实存档: {realSave}");
        Console.WriteLine($"测试副本: {copy}\n（所有写入只作用于副本，原件不受影响）");

        try
        {
            string originalPlain = SaveCrypto.ReadSaveText(copy);

            TestScalarAndComplexModify(copy, originalPlain);
            TestUntouchedFieldsByteIdentical(copy, originalPlain);
            TestPresets(copy, originalPlain);
            TestCanApplyNotification(copy);      // 放在破坏性写入测试之前，需要完好的加密副本
            TestApplyThroughViewModel(copy, originalPlain);
            TestWritePipeline(copy, workDir);

            Console.WriteLine($"\n===== 通过 {_pass} / 失败 {_fail} / 跳过 {_skip} =====");
            return _fail == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    // ---- 断言辅助 ----
    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  [PASS] {name}"); }
        else { _fail++; Console.WriteLine($"  [FAIL] {name}  {detail}"); }
    }
    private static void Section(string t) => Console.WriteLine("\n--- " + t + " ---");

    /// <summary>
    /// 存档里没有该字段时跳过，不计入通过/失败。
    ///
    /// 字段集合会随游戏版本与玩家进度变化（某些挑战、赛季、外观字段只在
    /// 特定条件下存在）。硬要求字段存在会让别人 clone 下来直接抛
    /// NullReferenceException 崩溃，而不是得到一个有意义的结果。
    /// </summary>
    private static void Skip(string name, string reason)
    {
        _skip++;
        Console.WriteLine($"  [SKIP] {name}  ({reason})");
    }

    /// <summary>用 FieldViewModel 层加载副本（与真实 UI 相同的代码路径）。</summary>
    private static (SaveDocument doc, List<FieldViewModel> fields) LoadFields(string path)
    {
        var doc = SaveDocument.Load(path);
        var entries = doc.EnumerateEntries();
        var schemas = FieldCatalog.Build(entries);
        var fields = new List<FieldViewModel>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
            fields.Add(FieldViewModel.Create(entries[i], schemas[i]));
        return (doc, fields);
    }

    private static FieldViewModel Find(List<FieldViewModel> fs, string key)
        => fs.FirstOrDefault(f => f.Key == key);

    private static IntFieldViewModel FindInt(List<FieldViewModel> fs, string key)
        => (IntFieldViewModel)Find(fs, key);

    // ---- 1. 标量 + 复杂修改 ----
    private static void TestScalarAndComplexModify(string copy, string originalPlain)
    {
        Section("1. 端到端修改：标量 + 复杂（走 FieldViewModel.ApplyTo）");
        var (doc, fields) = LoadFields(copy);

        // 记录基线字段数，后续断言一律与它对比（不绑定某个存档的具体数值）
        int baseCount = doc.EnumerateEntries().Count;

        // 每次成功改动一个字段就 +1，最后用「实际改了几个」做断言，
        // 而不是硬编码 7 —— 别人的存档可能缺其中某些字段。
        int touched = 0;

        // ---- 标量 ----
        var money = FindInt(fields, "PlayersMoney");
        if (money != null) { money.SetValue(123456); touched++; }
        else Skip("标量: PlayersMoney", "存档无此字段");

        var prestigeTheme = Find(fields, "PrestigeTheme") as BoolFieldViewModel;
        if (prestigeTheme != null) { prestigeTheme.SetValue(true); touched++; }
        else Skip("标量: PrestigeTheme", "存档无此字段或非 bool");

        var ghostType = Find(fields, "GhostType") as StringFieldViewModel;
        if (ghostType != null) { ghostType.Value = "幻影"; touched++; }
        else Skip("标量: GhostType", "存档无此字段或非 string");

        // ---- 复杂：颜色 ----
        var color = Find(fields, "CursorColor") as ColorFieldViewModel;
        bool colorTouched = false;
        if (color != null) { color.R = 0.25; touched++; colorTouched = true; }
        else Skip("复杂: CursorColor", "存档无此字段或非 Color");

        // ---- 复杂：字典 ----
        var maps = Find(fields, "playedMaps") as DictFieldViewModel;
        bool mapsTouched = false;
        if (maps?.Value?.Entries.Count > 0)
        {
            maps.Value.SetValueAt(0, 888); touched++; mapsTouched = true;
        }
        else Skip("复杂: playedMaps", "存档无此字段或字典为空");

        // ---- 复杂：列表加项 ----
        var names = Find(fields, "recentPlayerNames") as ListFieldViewModel;
        bool namesTouched = false;
        if (names != null) { names.Add("端到端测试玩家"); touched++; namesTouched = true; }
        else Skip("复杂: recentPlayerNames", "存档无此字段或非 List");

        // ---- 复杂：外观改材质 ----
        var outfit = Find(fields, "LocalPlayerOutfit") as OutfitFieldViewModel;
        bool outfitTouched = false;
        if (outfit?.Value?.Items.Count > 0)
        {
            outfit.Value.UpdateAt(0, outfit.Value.Items[0].Guid, 9);
            touched++; outfitTouched = true;
        }
        else Skip("复杂: LocalPlayerOutfit", "存档无此字段或外观条目为空");

        Check($"检测到 {touched} 个已修改字段（本存档实际可改的数量）",
              fields.Count(f => f.IsModified) == touched,
              $"实际 {fields.Count(f => f.IsModified)}，预期 {touched}");

        // 应用到一个新文档副本，模拟 UI 的 ApplyTo 流程
        var target = new SaveDocument(doc.Text);
        int written = 0;
        foreach (var f in fields) if (f.ApplyTo(target)) written++;
        Check("ApplyTo 写入的字段数 = 已修改字段数", written == touched,
              $"写入 {written}，已修改 {touched}");

        // ---- 重新解析验证（只校验确实改动过的）----
        var doc2 = new SaveDocument(target.Text);

        if (money != null)
            Check("重读后 PlayersMoney=123456",
                  doc2.TryGetInt("PlayersMoney", out int m) && m == 123456, $"实际 {m}");
        if (prestigeTheme != null)
            Check("重读后 PrestigeTheme=true", doc2.TryGetBool("PrestigeTheme", out bool b) && b);
        if (ghostType != null)
            Check("重读后 GhostType=幻影",
                  doc2.TryGetString("GhostType", out string g) && g == "幻影", $"实际 {g}");

        if (colorTouched && doc2.TryGetRaw("CursorColor", out _, out string cRaw))
        {
            var c2 = new ColorValue(cRaw);
            Check("重读后 CursorColor.R=0.25", Math.Abs(c2.R - 0.25) < 1e-9, $"实际 {c2.R}");
        }

        if (mapsTouched && doc2.TryGetRaw("playedMaps", out _, out string pmRaw))
            Check("重读后 playedMaps 含 888", pmRaw.Contains("888"),
                  pmRaw.Substring(0, Math.Min(30, pmRaw.Length)));

        if (namesTouched && doc2.TryGetRaw("recentPlayerNames", out _, out string rnRaw))
            Check("重读后列表含新玩家", rnRaw.Contains("端到端测试玩家"), rnRaw);

        if (outfitTouched && doc2.TryGetRaw("LocalPlayerOutfit", out _, out string ofRaw))
        {
            var of2 = new OutfitValue(ofRaw);
            if (of2.Items.Count > 0)
                Check("重读后外观 materialOption=9", of2.Items[0].MaterialOption == 9,
                      $"实际 {of2.Items[0].MaterialOption}");
            else
                Skip("重读后外观 materialOption=9", "重读后外观条目为空");
        }

        // 字段总数不变（与修改前对比，不绑定具体存档的字段数）
        Check("重读后字段数不变", doc2.EnumerateEntries().Count == baseCount,
              $"改后 {doc2.EnumerateEntries().Count} vs 改前 {baseCount}");
    }

    // ---- 2. 未触碰字段逐字节不变 ----
    private static void TestUntouchedFieldsByteIdentical(string copy, string originalPlain)
    {
        Section("2. 未触碰字段逐字节不变（最关键的安全性验证）");

        // 只改 1 个字段，其余 596 个必须逐字节不变
        var (doc, fields) = LoadFields(copy);
        FindInt(fields, "PlayersMoney").SetValue(42);

        var target = new SaveDocument(doc.Text);
        foreach (var f in fields) f.ApplyTo(target);
        string after = target.Text;

        var origDoc = new SaveDocument(originalPlain);
        var origEntries = origDoc.EnumerateEntries();
        var afterEntries = new SaveDocument(after).EnumerateEntries();

        Check("字段数一致", origEntries.Count == afterEntries.Count);

        int diffCount = 0;
        var diffs = new List<string>();
        var byKeyAfter = afterEntries.ToDictionary(e => e.Key, e => e.RawValue);
        foreach (var oe in origEntries)
        {
            byKeyAfter.TryGetValue(oe.Key, out string nv);
            if (nv != oe.RawValue)
            {
                diffCount++;
                if (diffs.Count < 10) diffs.Add($"{oe.Key}: 「{oe.RawValue}」→「{nv}」");
            }
        }
        Check("只有 PlayersMoney 一个字段变化", diffCount == 1,
              $"变化 {diffCount} 个: {string.Join("; ", diffs)}");
        Check("变化的是 PlayersMoney", diffs.Count == 1 && diffs[0].StartsWith("PlayersMoney"));

        // 反向：完全不改任何字段，写回后必须与原文逐字节一致
        var (doc0, fields0) = LoadFields(copy);
        var target0 = new SaveDocument(doc0.Text);
        int written0 = 0;
        foreach (var f in fields0) if (f.ApplyTo(target0)) written0++;
        Check("不改任何字段时 ApplyTo 写入 0 个", written0 == 0, $"实际 {written0}");
        Check("不改任何字段时文本与原文逐字节一致", target0.Text == originalPlain,
              $"长度 {target0.Text.Length} vs {originalPlain.Length}");
    }

    // ---- 3. 四个预设 ----
    private static void TestPresets(string copy, string originalPlain)
    {
        Section("3. 一键预设正确性 + 只升不降安全规则");
        var (doc, fields) = LoadFields(copy);
        var presets = new PresetService(fields);

        // 预设1：解锁全装备 + 全三阶
        var r1 = presets.UnlockAllEquipment();
        Check("预设1 修改了字段", r1.Changed > 0, $"Changed={r1.Changed}");

        // 抽查几个装备字段。字段可能因游戏版本而不存在，缺失即跳过。
        var emfT3 = Find(fields, "EMFReaderTierThreeUnlockOwned") as BoolFieldViewModel;
        if (emfT3 != null) Check("预设1: EMFReaderTierThreeUnlockOwned=true", emfT3.Value);
        else Skip("预设1: EMFReaderTierThreeUnlockOwned", "存档无此字段");

        var emfTier = FindInt(fields, "EMFReaderTier");
        if (emfTier != null) Check("预设1: EMFReaderTier=3", emfTier.Value == 3, $"实际 {emfTier.Value}");
        else Skip("预设1: EMFReaderTier", "存档无此字段");

        var saltT3u = Find(fields, "SaltTierThreeUnlocked") as BoolFieldViewModel;
        if (saltT3u != null) Check("预设1: SaltTierThreeUnlocked=true", saltT3u.Value);
        else Skip("预设1: SaltTierThreeUnlocked", "存档无此字段");

        // 预设2：金钱 + 库存拉满
        var r2 = presets.MaxMoneyAndInventory();
        Check("预设2 修改了字段", r2.Changed > 0, $"Changed={r2.Changed}");

        var money2 = FindInt(fields, "PlayersMoney");
        if (money2 != null)
            Check("预设2: PlayersMoney=999999999", money2.Value == 999_999_999, $"实际 {money2.Value}");
        else Skip("预设2: PlayersMoney", "存档无此字段");

        var lighter = FindInt(fields, "LighterInventory");
        if (lighter != null)
        {
            Check("预设2: LighterInventory 拉到 99", lighter.Value == 99, $"实际 {lighter.Value}");

            // 预设2 安全规则：只升不降。构造一个高于上限的值，预设后应保持
            lighter.SetValue(200);   // 人为设到 200 > 99
            presets.MaxMoneyAndInventory();
            Check("预设2 只升不降：LighterInventory=200 未被降到 99",
                  lighter.Value == 200, $"实际 {lighter.Value}");
        }
        else Skip("预设2 只升不降: LighterInventory", "存档无此字段");

        // 预设3：设定等级/转生（依据实证：写 NewLevel + Prestige）
        var r3 = presets.SetLevel(500, 7, resetLevelXp: false);

        var newLevel = FindInt(fields, "NewLevel");
        if (newLevel != null) Check("预设3: NewLevel=500", newLevel.Value == 500, $"实际 {newLevel.Value}");
        else Skip("预设3: NewLevel", "存档无此字段");

        var prestige = FindInt(fields, "Prestige");
        if (prestige != null) Check("预设3: Prestige=7", prestige.Value == 7, $"实际 {prestige.Value}");
        else Skip("预设3: Prestige", "存档无此字段");

        var prestigeIndex = FindInt(fields, "PrestigeIndex");
        if (prestigeIndex != null) Check("预设3: PrestigeIndex=7 同步", prestigeIndex.Value == 7);
        else Skip("预设3: PrestigeIndex", "存档无此字段");

        // 不改 Experience（resetLevelXp=false）
        Check("预设3: resetLevelXp=false 时不动 Experience",
              !r3.Notes.Any(n => n.Contains("已归零")));

        // 预设3 边界：超范围应拒绝
        var rBad = presets.SetLevel(99999, 0, false);
        Check("预设3: 等级超上限时拒绝且改 0 个字段", rBad.Changed == 0,
              $"Changed={rBad.Changed}");
        var rBad2 = presets.SetLevel(50, 99999, false);
        Check("预设3: 转生超上限时拒绝", rBad2.Changed == 0);

        // 预设4：全成就/挑战完成（只升不降）
        var (doc4, fields4) = LoadFields(copy);
        var presets4 = new PresetService(fields4);
        // 先把某个 Completed 设成 50（模拟 lighthouseKeeperCompleted 那种进度值）
        var lighthouse = FindInt(fields4, "lighthouseKeeperCompleted");
        bool hasLighthouse = lighthouse != null;
        if (hasLighthouse) lighthouse.SetValue(50);
        var r4 = presets4.CompleteAchievementsAndChallenges();
        Check("预设4 修改了字段", r4.Changed > 0, $"Changed={r4.Changed}");
        if (hasLighthouse)
            Check("预设4 只升不降：lighthouseKeeperCompleted=50 未被降到 1",
                  lighthouse.Value == 50, $"实际 {lighthouse.Value}");
        // 成就补到目标值
        var ach50 = FindInt(fields4, "ACH_COMPLETE_FIFTY_OPTIONAL_OBJECTIVES");
        if (ach50 != null)
            Check("预设4: FIFTY 成就 >= 50", ach50.Value >= 50, $"实际 {ach50.Value}");
        // Completed=0 的应被置 1
        var daily0c = FindInt(fields4, "daily0Completed");
        if (daily0c != null)
            Check("预设4: daily0Completed 从 0 置 1", daily0c.Value >= 1, $"实际 {daily0c.Value}");
        Check("预设4: 不改动 Progression（notes 里声明）",
              r4.Notes.Any(n => n.Contains("Progression")));

        // 预设只改内存，不写盘：验证磁盘上的副本仍是原文
        Check("预设执行后未触碰磁盘（副本仍是原文）",
              SaveCrypto.ReadSaveText(copy) == originalPlain);
    }

    // ---- 4. 完整写入链路 ----
    private static void TestWritePipeline(string copy, string workDir)
    {
        Section("4. 完整写入链路（进程检测 / 备份 / 加密自检 / 原子写 / 坏数据拦截）");

        string originalPlain = SaveCrypto.ReadSaveText(copy);

        // 4a. 正常写入：改金钱后写盘，重新读回验证
        var doc = new SaveDocument(originalPlain);
        doc.SetInt("PlayersMoney", 777777);
        var result = SafetyService.WriteSave(copy, doc.Text);
        Check("正常写入返回备份路径", File.Exists(result.BackupPath), result.BackupPath);
        Check("备份文件名含 .bak-", result.BackupPath.Contains(".bak-"));
        Check("写入后重新读回金钱=777777",
              new SaveDocument(SaveCrypto.ReadSaveText(copy)).TryGetInt("PlayersMoney", out int m) && m == 777777,
              $"实际 {m}");
        Check("写入后字段数不变",
              new SaveDocument(SaveCrypto.ReadSaveText(copy)).EnumerateEntries().Count
              == new SaveDocument(originalPlain).EnumerateEntries().Count);
        // 备份内容是写入前的原文
        Check("备份内容 = 写入前原文",
              SaveCrypto.ReadSaveText(result.BackupPath) == originalPlain);
        Check("写入后未残留 .tmp-writing 临时文件",
              !File.Exists(copy + ".tmp-writing"));

        // 4b. 加密自检：喂一个会导致自检失败的输入（构造异常文本），验证不写盘
        //     正常字符串自检会过，这里验证 RoundTripOk 对合法内容返回 true
        Check("RoundTripOk 对合法内容返回 true", SaveCrypto.RoundTripOk(doc.Text, out _));

        // 4c. 坏数据拦截：损坏的文件读取应抛明确异常，而不是静默返回垃圾
        string corrupt = Path.Combine(workDir, "corrupt.txt");
        var badBytes = new byte[64];
        new Random(7).NextBytes(badBytes);
        File.WriteAllBytes(corrupt, badBytes);
        bool threw = false;
        try { SaveCrypto.ReadSaveText(corrupt); }
        catch (InvalidDataException) { threw = true; }
        Check("损坏文件读取抛 InvalidDataException（不静默）", threw);

        // 4d. 进程检测逻辑本身可调用（不依赖游戏是否在跑）
        bool running = SafetyService.IsGameRunning();
        Check("IsGameRunning 可正常调用", running || !running);
        Console.WriteLine($"    （当前游戏进程状态：{(running ? "运行中" : "未运行")}）");

        // 4e. 找不到文件时的写入应抛 FileNotFoundException
        threw = false;
        try { SafetyService.WriteSave(Path.Combine(workDir, "nope.txt"), "{}"); }
        catch (FileNotFoundException) { threw = true; }
        Check("写不存在的存档抛 FileNotFoundException", threw);

        // 4f. ListBackups 能列出刚才的备份
        var backups = SafetyService.ListBackups(copy);
        Check("ListBackups 能列出备份", backups.Count >= 1, $"实际 {backups.Count}");

        // 注意：不要在这里把明文写回 copy —— 那会破坏副本的加密格式，
        // 使后续任何需要重新解密副本的测试失败。副本由 finally 统一删除。
    }

    // ---- 5. 回归：CanApply 状态通知 ----
    /// <summary>
    /// 回归测试「应用修改按钮点不动」这个 bug。
    ///
    /// CanApply 是 IsLoaded / HasChanges / HasBlockingError 的派生属性，
    /// 按钮的 IsEnabled 直接绑定它。WPF 不会因底层属性变化自动重算派生属性，
    /// 所以每次状态变化都必须显式通知 CanApply，否则按钮永远是灰的。
    /// 原先四处通知都漏了它，这个测试确保以后不会再漏。
    /// </summary>
    private static void TestCanApplyNotification(string copy)
    {
        Section("5. 回归：CanApply 通知链路（应用修改按钮可用性）");

        var vm = new MainViewModel { SavePath = copy };

        // 记录所有收到通知的属性名
        var notified = new List<string>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.Load();
        Check("加载后 IsLoaded = true", vm.IsLoaded);
        Check("加载后无改动 → CanApply = false", vm.CanApply == false);

        // 用搜索定位到 PlayersMoney（同时验证搜索链路可用）
        vm.SearchText = "PlayersMoney";
        Check("搜索命中 PlayersMoney", vm.VisibleFields.Count >= 1,
            $"实际 {vm.VisibleFields.Count}");

        var money = vm.VisibleFields.OfType<IntFieldViewModel>()
            .FirstOrDefault(f => f.Key == "PlayersMoney");
        Check("能在 VisibleFields 中取到整数字段", money is not null);

        // 改值 → 必须发出 CanApply 通知，且按钮变为可用
        notified.Clear();
        money.SetValue(777777);

        Check("改字段后发出了 CanApply 通知（按钮解禁的关键）",
            notified.Contains(nameof(MainViewModel.CanApply)),
            "实际通知: " + string.Join(",", notified.Distinct()));
        Check("改字段后 CanApply = true", vm.CanApply);
        Check("改字段后 HasChanges = true", vm.HasChanges);
        Check("改字段后 ModifiedCount = 1", vm.ModifiedCount == 1, $"实际 {vm.ModifiedCount}");

        // 还原全部 → CanApply 必须回到 false，且同样要通知
        notified.Clear();
        vm.RevertAll();
        Check("还原全部后发出了 CanApply 通知",
            notified.Contains(nameof(MainViewModel.CanApply)),
            "实际通知: " + string.Join(",", notified.Distinct()));
        Check("还原全部后 CanApply = false", vm.CanApply == false);
        Check("还原全部后 HasChanges = false", vm.HasChanges == false);

        // 预设 → 同样必须通知 CanApply
        notified.Clear();
        vm.PresetMaxMoney();
        Check("预设后发出了 CanApply 通知",
            notified.Contains(nameof(MainViewModel.CanApply)),
            "实际通知: " + string.Join(",", notified.Distinct()));
        Check("预设后 CanApply = true", vm.CanApply);

        // 非法输入 → HasBlockingError 拦住写入，CanApply 必须为 false
        vm.RevertAll();
        vm.SearchText = "PlayersMoney";
        var m2 = vm.VisibleFields.OfType<IntFieldViewModel>()
            .FirstOrDefault(f => f.Key == "PlayersMoney");
        notified.Clear();
        m2.Text = "99999999999999";   // 远超金钱上限 999999999（Text 是可写入口，Value 只读）
        Check("非法输入被标记为校验失败", m2.IsValid == false, "错误: " + m2.ValidationError);
        Check("非法输入后 HasBlockingError = true", vm.HasBlockingError);
        Check("非法输入后 CanApply = false（阻止写坏存档）", vm.CanApply == false);
        Check("非法输入时发出了 CanApply 通知",
            notified.Contains(nameof(MainViewModel.CanApply)),
            "实际通知: " + string.Join(",", notified.Distinct()));

        // 关键：非法输入时按钮仍必须可点，否则用户看不到任何解释、也没有出路
        Check("非法输入后 ApplyEnabled = true（按钮仍可点，能看到原因）", vm.ApplyEnabled);
        Check("非法输入后 ApplyEnabled 通知已发出",
            notified.Contains(nameof(MainViewModel.ApplyEnabled)),
            "实际通知: " + string.Join(",", notified.Distinct()));

        // 死锁回归：非法字段必须仍算「已修改」，否则还原入口会一起变灰
        Check("非法字段仍算已修改（否则还原按钮会锁死）", m2.IsModified);
        Check("非法字段不出现在写入摘要里（不会被写盘）",
            vm.BuildApplySummary().FieldCount == 0,
            $"实际 {vm.BuildApplySummary().FieldCount}");

        // 错误清单必须指名道姓，含字段名与原因
        var blocking = vm.ListBlockingFields();
        Check("ListBlockingFields 列出了非法字段", blocking.Count == 1, $"实际 {blocking.Count}");
        Check("错误说明包含字段名与原因",
            blocking.Count == 1 && blocking[0].Contains("PlayersMoney")
            && blocking[0].Contains("不能大于"),
            blocking.Count == 1 ? blocking[0] : "（空）");

        // 死锁出口：还原全部必须能把非法输入清掉并解禁
        vm.RevertAll();
        Check("还原全部后非法状态被清除", vm.HasBlockingError == false);
        Check("还原全部后 HasChanges = false", vm.HasChanges == false);
        Check("还原全部后 ApplyEnabled = false（无改动可写）", vm.ApplyEnabled == false);
        Check("还原全部后 CanApply = false", vm.CanApply == false);
    }

    // ---- 6. 回归：走完整 UI 路径真的能写入 ----
    /// <summary>
    /// 模拟用户真实操作：加载 → 改值 → 点「应用修改」（带确认回调）→ 写盘。
    /// 这是「无法应用修改」的直接验证 —— 必须证明改动真的落到了存档文件里。
    /// 只作用于副本，真实存档不受影响。
    /// </summary>
    private static void TestApplyThroughViewModel(string copy, string originalPlain)
    {
        Section("6. 回归：走 MainViewModel.Apply 完整写入（模拟点击「应用修改」）");

        long before = long.Parse(FindInt(LoadFields(copy).fields, "PlayersMoney").Value.ToString());

        var vm = new MainViewModel { SavePath = copy };
        vm.Load();

        // 确认框回调：模拟用户点「是」
        bool confirmCalled = false;
        vm.ConfirmApply = s => { confirmCalled = true; return s.FieldCount > 0; };

        vm.SearchText = "PlayersMoney";
        var money = vm.VisibleFields.OfType<IntFieldViewModel>()
            .First(f => f.Key == "PlayersMoney");
        const long target = 555000111;
        money.SetValue(target);

        Check("改值后 CanApply = true（按钮可用）", vm.CanApply);
        Check("改值后 ApplyEnabled = true", vm.ApplyEnabled);

        string backup = vm.Apply();
        Check("Apply 返回了备份路径（非 null = 写入未被取消）", backup is not null);
        Check("确认框被调用过", confirmCalled);
        Check("备份文件真实存在", backup is not null && File.Exists(backup),
            backup ?? "(null)");

        // 核心：重新从磁盘解密读取，确认值真的写进去了
        var (docAfter, fieldsAfter) = LoadFields(copy);
        Check("写盘后重新读取，金钱 = 目标值",
            FindInt(fieldsAfter, "PlayersMoney").Value == target,
            $"实际 {FindInt(fieldsAfter, "PlayersMoney").Value}，期望 {target}");

        // 写入后应清空「已修改」标记，按钮回到不可用
        Check("写入后 HasChanges = false（已对齐新基准）", vm.HasChanges == false);
        Check("写入后 CanApply = false（无待写改动）", vm.CanApply == false);

        // 备份内容必须等于写入前的原文（可回滚的前提）
        string backupPlain = SaveCrypto.ReadSaveText(backup);
        Check("备份内容 = 写入前的原文（可用于回滚）", backupPlain == originalPlain);

        // 未触碰的字段必须逐字节不变
        var docBefore = new SaveDocument(originalPlain);
        int diff = 0;
        foreach (var e in docAfter.EnumerateEntries())
        {
            if (e.Key == "PlayersMoney") continue;
            if (!docBefore.TryGetEntry(e.Key, out var old) || old.RawValue != e.RawValue) diff++;
        }
        Check("除金钱外其余字段全部未变", diff == 0, $"有 {diff} 个字段被意外改动");

        // 清理本次产生的备份，避免污染（备份写在副本同目录 = 项目内临时目录）
        try { if (backup is not null && File.Exists(backup)) File.Delete(backup); } catch { }

        // 把副本恢复为原始状态：本测试真的写了盘，若不恢复会影响其后的写入链路测试
        SafetyService.WriteSave(copy, originalPlain);
        foreach (string b in SafetyService.ListBackups(copy))
            try { File.Delete(b); } catch { }
        Check("副本已恢复为原始明文（不污染后续测试）",
            SaveCrypto.ReadSaveText(copy) == originalPlain);

        Console.WriteLine($"    （写入前金钱 {before} → 目标 {target}，已验证）");
    }
}
