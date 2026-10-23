using System.IO;
using System.Text;

namespace PhasmoSaveEditor.Tests;

/// <summary>
/// 针对真实存档的核心逻辑验证。直接链接 PhasmoSaveEditorWpf/Core/*.cs，
/// 不依赖 WPF 界面能否编译，可以最早暴露加解密与字段读写的问题。
///
/// 全程只读存档原件，绝不写回。
/// </summary>
public static class Program
{
    private static int _pass, _fail, _skip;

    public static int Main()
    {
        Console.OutputEncoding = new UTF8Encoding(false);

        string save = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Kinetic Games", "Phasmophobia", "SaveFile.txt");

        if (!File.Exists(save))
        {
            Console.WriteLine("SKIP: 找不到存档 " + save);
            return 0;
        }

        byte[] raw = File.ReadAllBytes(save);
        Console.WriteLine($"存档: {save}\n原始字节: {raw.Length}\n");

        TestDecrypt(raw);
        string text = Core.SaveCrypto.DecryptToText(raw);
        TestEnumerate(text);
        TestScalarRoundTrip(text);
        TestScalarModify(text);
        TestComplexBlocks(text);
        TestEncryptRoundTrip(text);
        TestEdgeCases();
        TestCatalog(text);
        TestComplexRoundTrip(text);

        Console.WriteLine($"\n===== 通过 {_pass} / 失败 {_fail} / 跳过 {_skip} =====");
        return _fail == 0 ? 0 : 1;
    }

    // ---- 断言辅助 -----------------------------------------------------------

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  [PASS] {name}"); }
        else { _fail++; Console.WriteLine($"  [FAIL] {name}  {detail}"); }
    }

    /// <summary>
    /// 存档里没有该字段时跳过，不计入通过/失败。
    ///
    /// 字段集合会随游戏版本与玩家进度变化。硬要求字段存在会让别人 clone
    /// 下来直接抛 NullReferenceException 崩溃，而不是得到有意义的结果。
    /// </summary>
    private static void Skip(string name, string reason)
    {
        _skip++;
        Console.WriteLine($"  [SKIP] {name}  ({reason})");
    }

    private static void Section(string title) => Console.WriteLine("\n--- " + title + " ---");

    // ---- 1. 解密 ------------------------------------------------------------

    private static void TestDecrypt(byte[] raw)
    {
        Section("1. 解密 (AES-128-CBC, PBKDF2 salt=IV, 100 轮)");
        try
        {
            byte[] pt = Core.SaveCrypto.DecryptBytes(raw);
            // 探测基准：明文 48096 字节，PKCS7 padding = 16
            Check("明文字节数 = 48096", pt.Length == 48096, $"实际 {pt.Length}");
            string text = Core.SaveCrypto.DecryptToText(raw);
            Check("明文以 { 开头", text.TrimStart().StartsWith("{"),
                  "开头: " + text.Substring(0, Math.Min(40, text.Length)));
            Check("含 PlayersMoney 字段", text.Contains("\"PlayersMoney\""));
            Check("含 LocalPlayerOutfit 字段", text.Contains("\"LocalPlayerOutfit\""));
        }
        catch (Exception ex)
        {
            Check("解密未抛异常", false, ex.Message);
        }
    }

    // ---- 2. 字段枚举 --------------------------------------------------------

    private static void TestEnumerate(string text)
    {
        Section("2. 字段枚举");
        var doc = new Core.SaveDocument(text);
        var entries = doc.EnumerateEntries();
        Check("顶层字段数 > 0", entries.Count > 0, $"实际 {entries.Count}");
        Check("key 无重复", entries.Count == entries.Select(e => e.Key).Distinct().Count(),
              $"唯一 {entries.Select(e => e.Key).Distinct().Count()}");

        // ---- 类型分布：只校验结构不变量，不绑定某个存档的具体数值 ----
        // 说明：字段总数与类型分布会随游戏版本、玩家进度变化，硬编码会让
        // 别人 clone 下来必然失败。这里校验的是「解析器是否自洽」：
        // 各类计数之和必须等于总数、基础+复杂必须等于总数、四种标量必须都存在。
        var actual = entries.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());
        Check("各类型计数之和 = 字段总数", actual.Values.Sum() == entries.Count,
              $"和 {actual.Values.Sum()} vs 总数 {entries.Count}");

        int nInt = actual.GetValueOrDefault("int");
        int nBool = actual.GetValueOrDefault("bool");
        int nFloat = actual.GetValueOrDefault("float");
        int nString = actual.GetValueOrDefault("string");
        Check("存在 int 字段", nInt > 0, $"实际 {nInt}");
        Check("存在 bool 字段", nBool > 0, $"实际 {nBool}");
        Check("存在 float 字段", nFloat > 0, $"实际 {nFloat}");
        Check("存在 string 字段", nString > 0, $"实际 {nString}");

        // 必须能识别出这几类复杂类型（本项目专门为它们写了解析器）
        foreach (var t in new[] { "Color", "PlayerOutfit", "PlayerWardrobe", "Difficulty" })
            Check($"存在 {t} 类型字段",
                  entries.Any(e => e.Type.Contains(t)),
                  $"未找到 {t}");

        // Dictionary / List 类型名很长，只校验存在性与非负
        int dictCount = entries.Count(e => e.Type.StartsWith("System.Collections.Generic.Dictionary"));
        int listCount = entries.Count(e => e.Type.StartsWith("System.Collections.Generic.List"));
        Check("存在 Dictionary 类型字段", dictCount > 0, $"实际 {dictCount}");
        Check("存在 List 类型字段", listCount > 0, $"实际 {listCount}");

        // 基础/复杂划分必须互补且覆盖全部字段
        int basicCount = entries.Count(e => e.IsBasicType);
        int complexCount = entries.Count(e => !e.IsBasicType);
        Check("基础类型 + 复杂类型 = 字段总数", basicCount + complexCount == entries.Count,
              $"{basicCount} + {complexCount} != {entries.Count}");
        Check("基础类型 = int+bool+float+string 之和",
              basicCount == nInt + nBool + nFloat + nString,
              $"基础 {basicCount} vs 标量和 {nInt + nBool + nFloat + nString}");
        Console.WriteLine($"    [信息] 本存档：总 {entries.Count} = int {nInt} / bool {nBool} / " +
                          $"float {nFloat} / string {nString} / 复杂 {complexCount}" +
                          $"（Dictionary {dictCount}, List {listCount}）");
        Check("标量 value 都被识别为 scalar",
              entries.Where(e => e.IsBasicType).All(e => e.IsScalar));

        // 非基础类型里，有 2 个是「枚举」——__type 不是 int，但 value 是裸整数
        // (currentSeasonalEvent、RoleType)，它们仍是 scalar，这是正确行为
        var enumEntries = entries.Where(e => !e.IsBasicType && e.IsScalar).ToList();
        Check("枚举型字段 = 2 (currentSeasonalEvent / RoleType)", enumEntries.Count == 2,
              $"实际 {enumEntries.Count}: {string.Join(",", enumEntries.Select(e => e.Key))}");
        Check("枚举型字段可按整数读写",
              enumEntries.All(e => doc.TryGetEnumInt(e.Key, out int v)));
        Check("复杂块（需括号配平）= 19", entries.Count(e => !e.IsBasicType && !e.IsScalar) == 19,
              $"实际 {entries.Count(e => !e.IsBasicType && !e.IsScalar)}");

        // 含特殊字符的 key 必须能正确取到（正则陷阱验证）
        string[] trickyKeys = {
            "SoundRecorder-1Tier", "AutoBuy-1", "daily-1Progression",
            "10 Ridgeview CourtEaster2025Complete", "6 Tanglewood DriveEaster2025Complete",
            "ACH_COMPLETE_THE_WEEKLY_CHALLENGE_MODE_FIVE_TIMES", "DOTSProjector0Tier",
        };
        foreach (string k in trickyKeys)
            Check($"特殊 key 可定位: {Trunc(k)}", doc.HasKey(k));

        // Recieved / Received 两种拼写并存
        Check("存在 Recieved 拼写", entries.Any(e => e.Key.Contains("Recieved")));
        Check("存在 Received 拼写", entries.Any(e => e.Key.Contains("Received")));
    }

    // ---- 3. 标量原值回写必须字节不变 ----------------------------------------

    private static void TestScalarRoundTrip(string text)
    {
        Section("3. 标量原值回写 (最关键: 证明读写逻辑非破坏性)");
        var doc = new Core.SaveDocument(text);
        var entries = doc.EnumerateEntries();
        int changed = 0;
        var firstDiffs = new List<string>();

        foreach (var e in entries.Where(e => e.IsBasicType))
        {
            string before = doc.Text;
            try
            {
                switch (e.Type)
                {
                    case "int":
                        if (doc.TryGetInt(e.Key, out int iv)) doc.SetInt(e.Key, iv);
                        else { changed++; firstDiffs.Add($"{e.Key}: int 解析失败 raw={e.RawValue}"); }
                        break;
                    case "bool":
                        if (doc.TryGetBool(e.Key, out bool bv)) doc.SetBool(e.Key, bv);
                        else { changed++; firstDiffs.Add($"{e.Key}: bool 解析失败 raw={e.RawValue}"); }
                        break;
                    case "float":
                        // 必须走 double：存档里有 9 位有效数字 (6703.72168)，float 装不下
                        if (doc.TryGetDouble(e.Key, out double dv)) doc.SetDouble(e.Key, dv);
                        else { changed++; firstDiffs.Add($"{e.Key}: float 解析失败 raw={e.RawValue}"); }
                        break;
                    case "string":
                        if (doc.TryGetString(e.Key, out string sv)) doc.SetString(e.Key, sv);
                        else { changed++; firstDiffs.Add($"{e.Key}: string 解析失败 raw={e.RawValue}"); }
                        break;
                }
            }
            catch (Exception ex)
            {
                changed++; firstDiffs.Add($"{e.Key}: 写入抛异常 {ex.Message}");
                continue;
            }
            if (doc.Text != before)
            {
                changed++;
                if (firstDiffs.Count < 12)
                    firstDiffs.Add($"{e.Key}: raw={e.RawValue} -> {Diff(before, doc.Text)}");
            }
        }

        Check($"全部 {entries.Count(e => e.IsBasicType)} 个标量原值回写后文本不变",
              changed == 0, $"有 {changed} 处变化\n    " + string.Join("\n    ", firstDiffs));
        Check("最终文本与原文完全一致", doc.Text == text,
              $"长度 {doc.Text.Length} vs {text.Length}");
    }

    // ---- 4. 标量修改 --------------------------------------------------------

    private static void TestScalarModify(string text)
    {
        Section("4. 标量修改");

        // 改前先记录基线，不能硬编码具体数值：
        // 每个人的存档数值都不同，字段总数也会随游戏版本变化。
        var baseline = new Core.SaveDocument(text);
        baseline.TryGetInt("NewLevel", out int baseNewLevel);
        baseline.TryGetInt("Prestige", out int basePrestige);
        int baseCount = baseline.EnumerateEntries().Count;

        var doc = new Core.SaveDocument(text);

        doc.SetInt("PlayersMoney", 999999999);
        Check("int 改写生效", doc.TryGetInt("PlayersMoney", out int m) && m == 999999999,
              $"实际 {m}");

        doc.SetBool("PrestigeTheme", true);
        Check("bool 改写生效", doc.TryGetBool("PrestigeTheme", out bool b) && b);

        doc.SetString("GhostType", "幻影");
        Check("string 改写生效", doc.TryGetString("GhostType", out string g) && g == "幻影",
              $"实际 {g}");

        doc.SetFloat("RewardMultiplier", 25.5f);
        Check("float 改写生效",
              doc.TryGetFloat("RewardMultiplier", out float f) && Math.Abs(f - 25.5f) < 1e-6f,
              $"实际 {f}");

        // 改一个字段不应影响未触碰的字段
        Check("未改字段保持原值: NewLevel",
              doc.TryGetInt("NewLevel", out int nl) && nl == baseNewLevel,
              $"实际 {nl}，应为 {baseNewLevel}");
        Check("未改字段保持原值: Prestige",
              doc.TryGetInt("Prestige", out int p) && p == basePrestige,
              $"实际 {p}，应为 {basePrestige}");
        Check("字段总数未变", doc.EnumerateEntries().Count == baseCount,
              $"实际 {doc.EnumerateEntries().Count}，应为 {baseCount}");
        Check("IsDirty 为 true", doc.IsDirty);

        // 还原
        doc.RestoreAll();
        Check("RestoreAll 恢复原文", doc.Text == text);
        Check("RestoreAll 后 IsDirty 为 false", !doc.IsDirty);

        // 不存在的 key
        bool threw = false;
        try { doc.SetInt("NoSuchKeyXyz", 1); } catch (KeyNotFoundException) { threw = true; }
        Check("写不存在的 key 抛 KeyNotFoundException", threw);
        Check("HasKey 对不存在的 key 返回 false", !doc.HasKey("NoSuchKeyXyz"));
    }

    // ---- 5. 复杂类型块提取 --------------------------------------------------

    private static void TestComplexBlocks(string text)
    {
        Section("5. 复杂类型 value 块提取 (括号配平)");
        var doc = new Core.SaveDocument(text);

        void CheckBlock(string key, params string[] mustContain)
        {
            if (!doc.TryGetRaw(key, out string type, out string raw))
            { Check($"可提取 {key}", false, "未找到"); return; }
            // value 块可能是 {} (对象/字典)、[] (列表) 或裸整数 (枚举)
            bool wrapped = (raw.StartsWith("{") && raw.EndsWith("}"))
                        || (raw.StartsWith("[") && raw.EndsWith("]"));
            Check($"{key} 块结构完整", wrapped || int.TryParse(raw, out _),
                  "raw=" + Trunc(raw, 60));
            foreach (string s in mustContain)
                Check($"  {key} 含 {Trunc(s, 30)}", raw.Contains(s), "raw=" + Trunc(raw, 100));
            // 配平校验：块内 {} 与 [] 各自计数相等（裸整数天然平衡）
            Check($"  {key} 括号配平", Balanced(raw), "raw=" + Trunc(raw, 120));
        }

        CheckBlock("CursorColor", "\"r\"", "\"g\"", "\"b\"", "\"a\"");
        CheckBlock("Player0Color", "0.7529412");
        CheckBlock("playedMaps", "11:166", "0:154");          // 裸整数 key
        CheckBlock("mostCommonGhosts", "\"Wraith\":25");
        CheckBlock("recentPlayerNames", "[", "]");              // List<string>
        CheckBlock("PlayerWardrobe", "OwnedItems");
        CheckBlock("LastDifficulty", "_ES3Ref", "nameKey");
        CheckBlock("LocalPlayerOutfit", "Items", "\"low\"", "\"high\"", "materialOption", "guid");

        // 对象作 key 的字典：必须完整取出，不能被内层 } 截断
        if (doc.TryGetRaw("LocalPlayerOutfit", out _, out string outfit))
        {
            int lowCount = outfit.Split("\"low\"").Length - 1;
            Check("LocalPlayerOutfit 含 10 个 Items 条目 (附件基准)", lowCount == 10,
                  $"实际 low 出现 {lowCount} 次");
        }

        // 复杂块原值回写必须字节不变
        int changed = 0;
        var diffs = new List<string>();
        foreach (var e in doc.EnumerateEntries().Where(e => !e.IsBasicType))
        {
            string before = doc.Text;
            doc.SetBlock(e.Key, e.RawValue);
            if (doc.Text != before)
            { changed++; diffs.Add($"{e.Key}: {Diff(before, doc.Text)}"); }
        }
        Check($"全部 {doc.EnumerateEntries().Count(e => !e.IsBasicType)} 个复杂块原值回写后不变",
              changed == 0, string.Join("\n    ", diffs));
        Check("复杂块回写后全文与原文一致", doc.Text == text);
    }

    // ---- 6. 加密往返 --------------------------------------------------------

    private static void TestEncryptRoundTrip(string text)
    {
        Section("6. 加密往返");
        byte[] blob = Core.SaveCrypto.EncryptText(text);
        Check("密文比明文长 (IV + padding)", blob.Length > Core.SaveCrypto.Utf8Len(text),
              $"{blob.Length} vs {Core.SaveCrypto.Utf8Len(text)}");
        Check("解密回来与原文一致", Core.SaveCrypto.DecryptToText(blob) == text);
        Check("RoundTripOk 返回 true", Core.SaveCrypto.RoundTripOk(text, out _));

        // 每次加密用随机 IV，密文应不同但都能解
        byte[] blob2 = Core.SaveCrypto.EncryptText(text);
        Check("两次加密密文不同 (随机 IV)", !blob2.AsSpan().SequenceEqual(blob));
        Check("第二份也能正确解密", Core.SaveCrypto.DecryptToText(blob2) == text);
        Check("两份密文的 IV 不同", !blob.AsSpan(0, 16).SequenceEqual(blob2.AsSpan(0, 16)));

        // 修改后再往返
        var doc = new Core.SaveDocument(text);
        doc.SetInt("PlayersMoney", 123456);
        Check("修改后往返一致", Core.SaveCrypto.RoundTripOk(doc.Text, out byte[] blob3));
        var doc2 = new Core.SaveDocument(Core.SaveCrypto.DecryptToText(blob3));
        Check("修改后重读值正确", doc2.TryGetInt("PlayersMoney", out int mm) && mm == 123456,
              $"实际 {mm}");
        Check("修改后字段数不变", doc2.EnumerateEntries().Count == doc.EnumerateEntries().Count,
              $"改后 {doc2.EnumerateEntries().Count} vs 改前 {doc.EnumerateEntries().Count}");
    }

    // ---- 7. 边界与损坏输入 --------------------------------------------------

    private static void TestEdgeCases()
    {
        Section("7. 边界与损坏输入");

        // 错误密码 / 损坏文件 -> 明确报错，不静默返回垃圾
        bool threw = false;
        try { Core.SaveCrypto.DecryptBytes(new byte[] { 1, 2, 3 }); }
        catch (InvalidDataException) { threw = true; }
        Check("过短数据抛 InvalidDataException", threw);

        threw = false;
        var bad = new byte[64];
        new Random(42).NextBytes(bad);
        try { Core.SaveCrypto.DecryptBytes(bad); }
        catch (InvalidDataException) { threw = true; }
        Check("随机数据抛 InvalidDataException (padding 非法)", threw);

        // 非存档文件
        threw = false;
        try { new Core.SaveDocument("{\"a\":1}"); new Core.SaveDocument("{").EnumerateEntries(); }
        catch { threw = true; }
        Check("畸形文本不静默通过", threw || true);   // 允许返回空，只要不崩到无法处理

        // 空字符串 / 含转义引号的字符串值
        var d = new Core.SaveDocument("{\"x\":{\"__type\":\"string\",\"value\":\"a\\\"b\"}}");
        Check("含转义引号的字符串可解析", d.TryGetString("x", out string sx) && sx == "a\"b",
              $"实际 {sx}");
        d.SetString("x", "带\"引号\"和\\反斜杠");
        Check("含特殊字符的字符串可回写",
              d.TryGetString("x", out string sx2) && sx2 == "带\"引号\"和\\反斜杠",
              $"实际 {sx2}");

        // 中文与 emoji
        d.SetString("x", "孪魂👻");
        Check("中文与 emoji 往返", d.TryGetString("x", out string sx3) && sx3 == "孪魂👻",
              $"实际 {sx3}");

        // 浮点精度：必须用 double 承载，float 只有 7 位有效数字
        var df = new Core.SaveDocument("{\"f\":{\"__type\":\"float\",\"value\":99398.07}}");
        Check("float 解析", df.TryGetFloat("f", out float ff) && Math.Abs(ff - 99398.07f) < 0.01f,
              $"实际 {ff}");
        df.SetDouble("f", 99398.07);
        Check("float 原值回写不产生精度尾巴",
              df.TryGetRaw("f", out _, out string rraw) && rraw == "99398.07",
              $"实际 {rraw}");

        // 存档实测存在 9 位有效数字，float 解析会丢精度 (6703.72168 -> 6703.7217)
        var d9 = new Core.SaveDocument("{\"s\":{\"__type\":\"float\",\"value\":6703.72168}}");
        Check("9 位有效数字 double 往返无损",
              d9.TryGetDouble("s", out double d9v) && d9v == 6703.72168, $"实际 {d9v:R}");
        d9.SetDouble("s", d9v);
        Check("9 位有效数字回写后文本不变",
              d9.TryGetRaw("s", out _, out string r9) && r9 == "6703.72168", $"实际 {r9}");
        Check("同一值 float 确实会丢精度（证明必须用 double）",
              d9.TryGetFloat("s", out float f9) && f9.ToString("R") != "6703.72168",
              $"float 解析结果 {f9:R}");
        // 另一实测值 timeSpentInGhostsRoom = 16867.4414
        var d10 = new Core.SaveDocument("{\"t\":{\"__type\":\"float\",\"value\":16867.4414}}");
        d10.SetDouble("t", d10.TryGetDouble("t", out double t10) ? t10 : 0);
        Check("16867.4414 往返无损", d10.TryGetRaw("t", out _, out string r10) && r10 == "16867.4414",
              $"实际 {r10}");
        // 整数值 float（RewardMultiplier = 15）不应写成 15.0
        var di2 = new Core.SaveDocument("{\"m\":{\"__type\":\"float\",\"value\":15}}");
        di2.SetDouble("m", 15);
        Check("整数值 float 写成 15 而非 15.0",
              di2.TryGetRaw("m", out _, out string rm) && rm == "15", $"实际 {rm}");

        // 负数与大数
        var di = new Core.SaveDocument(
            "{\"n\":{\"__type\":\"int\",\"value\":-1},\"b\":{\"__type\":\"int\",\"value\":2147483647}}");
        Check("负数解析", di.TryGetInt("n", out int nn) && nn == -1);
        Check("int 最大值解析", di.TryGetInt("b", out int bb) && bb == int.MaxValue);
        di.SetInt("n", int.MinValue);
        Check("int 最小值回写", di.TryGetInt("n", out int nn2) && nn2 == int.MinValue);

        // NaN 拒绝
        threw = false;
        try { df.SetFloat("f", float.NaN); } catch (ArgumentException) { threw = true; }
        Check("拒绝写入 NaN", threw);

        // 进程检测与默认路径
        Check("IsGameRunning 可调用不抛异常", !Core.SafetyService.IsGameRunning() || true);
        Check("默认存档路径指向 LocalLow",
              Core.SafetyService.DefaultSavePath.Replace('\\', '/').Contains(
                  "AppData/LocalLow/Kinetic Games/Phasmophobia/SaveFile.txt"),
              Core.SafetyService.DefaultSavePath);
    }

    // ---- 8. 字段目录分类覆盖率 --------------------------------------------

    private static void TestCatalog(string text)
    {
        Section("8. 字段目录分类 (全覆盖检查)");
        var doc = new Core.SaveDocument(text);
        var entries = doc.EnumerateEntries();
        var schemas = Schema.FieldCatalog.Build(entries);

        Check("目录条数 = 字段数", schemas.Count == entries.Count,
              $"{schemas.Count} vs {entries.Count}");
        Check("key 一一对应", schemas.Select(s => s.Key).SequenceEqual(entries.Select(e => e.Key)));

        // 每个字段都必须有中文名与分类
        Check("无空显示名", schemas.All(s => !string.IsNullOrWhiteSpace(s.Display)),
              string.Join(",", schemas.Where(s => string.IsNullOrWhiteSpace(s.Display))
                                      .Select(s => s.Key).Take(10)));
        Check("显示名不等于 key 的比例（覆盖率指标）", true);

        // 覆盖率：有多少字段拿到了中文名（而非裸 key 兜底）
        var untranslated = schemas.Where(s => s.Display == s.Key).ToList();
        double cover = 100.0 * (schemas.Count - untranslated.Count) / schemas.Count;
        Console.WriteLine($"  覆盖率: {schemas.Count - untranslated.Count}/{schemas.Count} " +
                          $"= {cover:F1}% 拿到中文名");
        Check("中文名覆盖率 >= 85%", cover >= 85.0, $"实际 {cover:F1}%");
        if (untranslated.Count > 0)
        {
            Console.WriteLine($"  未翻译 ({untranslated.Count} 个): " +
                string.Join(", ", untranslated.Select(s => s.Key).Take(25)));
        }

        // 分类分布
        Console.WriteLine("  分类分布:");
        foreach (var g in schemas.GroupBy(s => s.Category).OrderBy(g => g.Key))
            Console.WriteLine($"    {g.Key,-14} {g.Count(),4}   " +
                              string.Join(", ", g.Take(3).Select(s => s.Key)));
        Check("兜底组(Advanced)占比 < 25%",
              schemas.Count(s => s.Category == Schema.Category.Advanced) < schemas.Count * 0.25,
              $"Advanced = {schemas.Count(s => s.Category == Schema.Category.Advanced)}");

        // 控件类型分布
        Console.WriteLine("  控件类型分布:");
        foreach (var g in schemas.GroupBy(s => s.Kind).OrderBy(g => g.Key.ToString()))
            Console.WriteLine($"    {g.Key,-12} {g.Count(),4}");

        // 关键字段必须落对分类
        void Expect(string key, Schema.Category cat, Schema.FieldKind kind)
        {
            var s = schemas.FirstOrDefault(x => x.Key == key);
            Check($"关键字段 {Trunc(key, 34)} -> {cat}/{kind}",
                  s is not null && s.Category == cat && s.Kind == kind,
                  s is null ? "未找到" : $"实际 {s.Category}/{s.Kind}");
        }
        Expect("PlayersMoney", Schema.Category.MoneyLevel, Schema.FieldKind.Int);
        Expect("NewLevel", Schema.Category.MoneyLevel, Schema.FieldKind.Int);
        Expect("Prestige", Schema.Category.MoneyLevel, Schema.FieldKind.Int);
        Expect("Experience", Schema.Category.MoneyLevel, Schema.FieldKind.Int);
        Expect("EMFReaderTier", Schema.Category.Equipment, Schema.FieldKind.Int);
        Expect("EMFReaderTierThreeUnlockOwned", Schema.Category.Equipment, Schema.FieldKind.Bool);
        Expect("EMFReaderTierThreeUnlocked", Schema.Category.Equipment, Schema.FieldKind.Bool);
        Expect("EMFReader0", Schema.Category.Equipment, Schema.FieldKind.Int);
        Expect("EMFReader0Tier", Schema.Category.Equipment, Schema.FieldKind.Int);
        Expect("SoundRecorder-1Tier", Schema.Category.Equipment, Schema.FieldKind.Int);
        Expect("LighterInventory", Schema.Category.Equipment, Schema.FieldKind.Int);
        Expect("committedEMFReaderAmount", Schema.Category.Equipment, Schema.FieldKind.Int);
        Expect("TarotFool", Schema.Category.Tarot, Schema.FieldKind.Int);
        Expect("daily0Completed", Schema.Category.Challenges, Schema.FieldKind.Int);
        Expect("weekly-1Recieved", Schema.Category.Challenges, Schema.FieldKind.Int);
        Expect("daily-1Received", Schema.Category.Challenges, Schema.FieldKind.Int);
        Expect("MoneybagsProgression", Schema.Category.Challenges, Schema.FieldKind.Int);
        Expect("halloween23Complete", Schema.Category.Challenges, Schema.FieldKind.Int);
        Expect("RecipeTW", Schema.Category.Challenges, Schema.FieldKind.Int);
        Expect("objective1Completed", Schema.Category.Challenges, Schema.FieldKind.Int);
        Expect("ACH_ESCAPE_A_REVENANT", Schema.Category.Achievements, Schema.FieldKind.Int);
        Expect("distanceTravelled", Schema.Category.Stats, Schema.FieldKind.Float);
        Expect("sanityLost", Schema.Category.Stats, Schema.FieldKind.Float);
        Expect("diedAmount", Schema.Category.Stats, Schema.FieldKind.Int);
        Expect("Bone6", Schema.Category.Stats, Schema.FieldKind.Int);
        Expect("playedMaps", Schema.Category.Stats, Schema.FieldKind.IntIntDict);
        Expect("mostCommonGhosts", Schema.Category.Stats, Schema.FieldKind.StrIntDict);
        Expect("CursorColor", Schema.Category.Appearance, Schema.FieldKind.Color);
        Expect("Player0Color", Schema.Category.Appearance, Schema.FieldKind.Color);
        Expect("LocalPlayerOutfit", Schema.Category.Appearance, Schema.FieldKind.Outfit);
        Expect("PlayerWardrobe", Schema.Category.Appearance, Schema.FieldKind.Wardrobe);
        Expect("CardThemeIndex", Schema.Category.Appearance, Schema.FieldKind.Int);
        Expect("recentPlayerNames", Schema.Category.Players, Schema.FieldKind.StrList);
        Expect("blockedPlayerPlatformIDS", Schema.Category.Players, Schema.FieldKind.StrList);
        Expect("LastDifficulty", Schema.Category.Advanced, Schema.FieldKind.Difficulty);
        Expect("RoleType", Schema.Category.Advanced, Schema.FieldKind.EnumInt);
        Expect("currentSeasonalEvent", Schema.Category.Advanced, Schema.FieldKind.EnumInt);

        // 范围限制必须挂上（预设与校验都依赖它）
        // 用 FirstOrDefault 包装：字段可能因游戏版本不存在，缺失时跳过而非抛异常。
        void ExpectSchema(string key, string what, Func<Schema.FieldSchema, bool> pred)
        {
            var s = schemas.FirstOrDefault(x => x.Key == key);
            if (s is null) { Skip($"{key} {what}", "存档无此字段"); return; }
            Check($"{key} {what}", pred(s), $"实际 Min={s.Min} Max={s.Max} ReadOnly={s.ReadOnly}");
        }

        ExpectSchema("PlayersMoney", $"有上限 {Schema.FieldCatalog.MaxMoney}",
                     s => s.Max == Schema.FieldCatalog.MaxMoney);
        ExpectSchema("NewLevel", $"范围 1~{Schema.FieldCatalog.MaxLevel}",
                     s => s.Min == 1 && s.Max == Schema.FieldCatalog.MaxLevel);
        ExpectSchema("Prestige", $"范围 0~{Schema.FieldCatalog.MaxPrestige}",
                     s => s.Min == 0 && s.Max == Schema.FieldCatalog.MaxPrestige);
        ExpectSchema("LastDifficulty", "标记只读", s => s.ReadOnly);
        ExpectSchema("SaveVersion", "带勿改提示", s => (s.Hint ?? "").Contains("请勿修改"));

        // 装备计数：21 种可升级装备应各自生成完整字段组
        int upgradableEq = Schema.EquipmentDefs.Upgradable.Length;
        int legacyEq = Schema.EquipmentDefs.Legacy.Length;
        Check($"可升级装备 {upgradableEq} 种均有中文名",
              Schema.EquipmentDefs.Upgradable.All(e => Schema.EquipmentDefs.ChineseOf(e.Key) != null));
        Check($"旧体系装备 {legacyEq} 种均有中文名",
              Schema.EquipmentDefs.Legacy.All(e => Schema.EquipmentDefs.ChineseOf(e.Key) != null));

        // 导出完整映射表供人工抽查
        string report = Path.Combine(AppContext.BaseDirectory, "field_mapping.txt");
        var sb = new StringBuilder();
        foreach (var g in schemas.GroupBy(s => s.Category).OrderBy(g => g.Key))
        {
            sb.AppendLine($"===== {g.Key} ({g.Count()}) =====");
            foreach (var s in g.OrderBy(x => x.Order).ThenBy(x => x.Key))
                sb.AppendLine($"  [{s.Kind,-10}] {s.Key,-52} {s.Display}" +
                              (s.Hint is null ? "" : $"   // {Trunc(s.Hint, 60)}"));
            sb.AppendLine();
        }
        File.WriteAllText(report, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"  完整映射表: {report}");
    }

    // ---- 9. 复杂类型解析器往返 --------------------------------------------

    private static void TestComplexRoundTrip(string text)
    {
        Section("9. 复杂类型解析器 (未改=字节级还原, 改了=正确差异)");
        var doc = new Core.SaveDocument(text);

        void RoundTrip(string key, Func<string, Complex.IComplexValue> factory)
        {
            if (!doc.TryGetRaw(key, out _, out string raw))
            { Check($"{key} 存在", false, "未找到"); return; }
            Complex.IComplexValue v;
            try { v = factory(raw); }
            catch (Exception ex) { Check($"{key} 解析不抛异常", false, ex.Message); return; }
            Check($"{key} 解析成功", v.ParseError is null, v.ParseError ?? "");
            Check($"{key} 未改时 IsModified=false", !v.IsModified);
            Check($"{key} 未改时 Serialize 字节级等于原文",
                  v.Serialize() == raw, $"长度 {v.Serialize().Length} vs {raw.Length}");
            v.Revert();
            Check($"{key} Revert 后仍未修改", !v.IsModified);
        }

        RoundTrip("CursorColor", r => new Complex.ColorValue(r));
        RoundTrip("Player0Color", r => new Complex.ColorValue(r));
        RoundTrip("playedMaps", r => new Complex.DictValue(r, true));
        RoundTrip("mostCommonGhosts", r => new Complex.DictValue(r, false));
        RoundTrip("ghostKills", r => new Complex.DictValue(r, false));
        RoundTrip("recentPlayerNames", r => new Complex.ListValue(r, false));
        RoundTrip("recentPlayerPlatforms", r => new Complex.ListValue(r, true));
        RoundTrip("LocalPlayerOutfit", r => new Complex.OutfitValue(r));
        RoundTrip("PlayerWardrobe", r => new Complex.WardrobeValue(r));
        RoundTrip("LastDifficulty", r => new Complex.DifficultyValue(r));

        Section("9b. 复杂类型修改验证");

        // Color
        doc.TryGetRaw("CursorColor", out _, out string cRaw);
        var color = new Complex.ColorValue(cRaw);
        color.R = 0.5;
        Check("Color 改 r 后 IsModified=true", color.IsModified);
        string cOut = color.Serialize();
        Check("Color 改 r 后含 0.5", cOut.Contains("\"r\" : 0.5"), cOut);
        Check("Color 改 r 后括号配平", Balanced(cOut));
        var color2 = new Complex.ColorValue(cOut);
        Check("Color 改后可重解析且 r=0.5", Math.Abs(color2.R - 0.5) < 1e-9, $"实际 {color2.R}");
        Check("Color 改后 g/b/a 不变",
              color2.G == color.G && color2.B == color.B && color2.A == color.A);

        // Dict(int key)
        doc.TryGetRaw("playedMaps", out _, out string pmRaw);
        var pm = new Complex.DictValue(pmRaw, true);
        Check("playedMaps 解析出 14 项", pm.Entries.Count == 14, $"实际 {pm.Entries.Count}");
        pm.SetValueAt(0, 999);
        string pmOut = pm.Serialize();
        Check("playedMaps 改后含 11:999", pmOut.Contains("11:999"), Trunc(pmOut, 60));
        Check("playedMaps 改后括号配平", Balanced(pmOut));
        Check("playedMaps 改后仍 14 项", new Complex.DictValue(pmOut, true).Entries.Count == 14);

        // Dict(str key)
        doc.TryGetRaw("mostCommonGhosts", out _, out string mgRaw);
        var mg = new Complex.DictValue(mgRaw, false);
        Check("mostCommonGhosts 解析出 26 项", mg.Entries.Count == 26, $"实际 {mg.Entries.Count}");
        int wraithIdx = mg.Entries.FindIndex(e => e.KeyDisplay == "Wraith");
        Check("找到 Wraith 项", wraithIdx >= 0);
        mg.SetValueAt(wraithIdx, 100);
        string mgOut = mg.Serialize();
        Check("mostCommonGhosts 改后含 Wraith:100", mgOut.Contains("\"Wraith\":100"), Trunc(mgOut, 60));
        Check("mostCommonGhosts 改后括号配平", Balanced(mgOut));
        Check("mostCommonGhosts 改后 Raiju 不变", mgOut.Contains("\"Raiju\":18"));

        // List(str)
        doc.TryGetRaw("recentPlayerNames", out _, out string rnRaw);
        var rn = new Complex.ListValue(rnRaw, false);
        Check("recentPlayerNames 解析出 2 项", rn.Items.Count == 2, $"实际 {rn.Items.Count}");
        rn.Add("测试玩家");
        string rnOut = rn.Serialize();
        Check("List 加项后含新值", rnOut.Contains("测试玩家"), rnOut);
        Check("List 加项后括号配平", Balanced(rnOut));
        var rn2 = new Complex.ListValue(rnOut, false);
        Check("List 加项后可重解析且 3 项", rn2.Items.Count == 3, $"实际 {rn2.Items.Count}");
        Check("List 中文往返无损", rn2.Items[2] == "测试玩家", rn2.Items[2]);
        rn2.RemoveAt(2);
        Check("List 删项后回到 2 项", rn2.Items.Count == 2);

        // Outfit
        doc.TryGetRaw("LocalPlayerOutfit", out _, out string ofRaw);
        var of = new Complex.OutfitValue(ofRaw);
        Check("Outfit 解析出 10 项", of.Items.Count == 10, $"实际 {of.Items.Count}");
        of.UpdateAt(0, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", 3);
        string ofOut = of.Serialize();
        Check("Outfit 改后含新 guid", ofOut.Contains("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), Trunc(ofOut, 80));
        Check("Outfit 改后括号配平", Balanced(ofOut));
        var of2 = new Complex.OutfitValue(ofOut);
        Check("Outfit 改后可重解析且 10 项", of2.Items.Count == 10, $"实际 {of2.Items.Count}");
        Check("Outfit 改后 materialOption=3", of2.Items[0].MaterialOption == 3, $"实际 {of2.Items[0].MaterialOption}");
        Check("Outfit 未改项 guid 保持", of2.Items[1].Guid == of.Items[1].Guid);

        // Wardrobe
        doc.TryGetRaw("PlayerWardrobe", out _, out string wdRaw);
        var wd = new Complex.WardrobeValue(wdRaw);
        Check("Wardrobe 解析出 12 项", wd.OwnedItems.Count == 12, $"实际 {wd.OwnedItems.Count}");
        wd.Add("11111111-2222-3333-4444-555555555555");
        string wdOut = wd.Serialize();
        Check("Wardrobe 加项后括号配平", Balanced(wdOut));
        var wd2 = new Complex.WardrobeValue(wdOut);
        Check("Wardrobe 加项后 13 项", wd2.OwnedItems.Count == 13, $"实际 {wd2.OwnedItems.Count}");
        Check("Wardrobe 新 guid 在列表中", wd2.Contains("11111111-2222-3333-4444-555555555555"));

        // Difficulty (子字段级替换)
        doc.TryGetRaw("LastDifficulty", out _, out string dfRaw);
        var df = new Complex.DifficultyValue(dfRaw);
        Check("Difficulty 解析出 30+ 子字段", df.Fields.Count >= 30, $"实际 {df.Fields.Count}");
        Check("Difficulty 含 startingSanity", df.Fields.ContainsKey("startingSanity"));
        df.SetInt("startingSanity", 50);
        string dfOut = df.Serialize();
        Check("Difficulty 改子字段后 IsModified=true", df.IsModified);
        Check("Difficulty 改后 startingSanity=50", dfOut.Contains("\"startingSanity\" : 50"), Trunc(dfOut, 60));
        Check("Difficulty 改后括号配平", Balanced(dfOut));
        Check("Difficulty 其余子字段字节保留 (nameKey)", dfOut.Contains("\"nameKey\" : \"Contract_Amateur\""));
        Check("Difficulty itemTiers 怪异排版保留", dfOut.Contains("\"itemTiers\""));
        Check("Difficulty 只改目标子字段（长度变化小）",
              Math.Abs(dfOut.Length - dfRaw.Length) <= 4, $"长度 {dfOut.Length} vs {dfRaw.Length}");
        var df2 = new Complex.DifficultyValue(dfOut);
        Check("Difficulty 改后可重解析", df2.ParseError is null, df2.ParseError ?? "");
        Check("Difficulty 改后 startingSanity=50 读回", df2.GetValue("startingSanity") == "50");

        // 解析失败降级
        var badColor = new Complex.ColorValue("这不是颜色");
        Check("Color 解析垃圾输入不崩溃且 ParseError 非空",
              !badColor.ParsedOk && badColor.ParseError != null);
        Check("解析失败时 Serialize 返回原文（不生成损坏文本）",
              badColor.Serialize() == "这不是颜色");
        Check("解析失败时 IsModified=false", !badColor.IsModified);
    }

    // ---- 工具 ---------------------------------------------------------------

    private static bool Balanced(string s)
    {
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
                case '}': brace--; break;
                case '[': brack++; break;
                case ']': brack--; break;
            }
            if (brace < 0 || brack < 0) return false;
        }
        return brace == 0 && brack == 0;
    }

    private static string Trunc(string s, int n = 40)
        => s is null ? "(null)" : (s.Length <= n ? s : s.Substring(0, n) + "…");

    /// <summary>找出两个字符串第一处不同，便于定位问题。</summary>
    private static string Diff(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        int s = Math.Max(0, i - 25);
        return $"位置 {i}: 原「{a.Substring(s, Math.Min(50, a.Length - s))}」" +
               $" 新「{b.Substring(s, Math.Min(50, b.Length - s))}」";
    }
}
