using System.Text;
using System.Text.RegularExpressions;

namespace PhasmoSaveEditor.Complex;

/// <summary>
/// Difficulty 类型（LastDifficulty）。
///
/// 这是一个含 30+ 子字段的复合对象，且带 `_ES3Ref` 引用 ID。
/// 与其他复杂类型不同，这里**不整体重建文本**，而是采用「子字段级替换」：
/// 只对被修改的标量子字段做定点替换，其余字节原样保留。
///
/// 理由：
///   1. 对象里有空数组但含空白的怪异排版（itemTiers = "[\n\t\t\t\n\t\t]"），
///      整体复刻极易出错。
///   2. 该字段默认只读（见设计文档），改动 _ES3Ref/nameKey 可能与引用不一致而失效。
///      子字段替换把「误伤未改字段」的风险降到最低。
///
/// 对外仍以 key-value 形式暴露所有子字段，方便 UI 展示。
/// </summary>
public sealed class DifficultyValue : ComplexValueBase
{
    /// <summary>子字段：key -> 原始 value 文本。</summary>
    public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);

    /// <summary>用户修改过的子字段：key -> 新 value 文本。</summary>
    private readonly Dictionary<string, string> _edits = new(StringComparer.Ordinal);

    /// <summary>子字段在原文中的出现顺序（用于稳定展示）。</summary>
    public List<string> Order { get; } = new();

    private string _workingText;

    public DifficultyValue(string raw) : base(raw)
    {
        Parse(raw);
        CaptureSnapshot();
    }

    private static readonly Regex FieldRe = new(
        "\"(?<k>[A-Za-z_][A-Za-z0-9_]*)\"\\s*:\\s*(?<v>-?\\d+(?:\\.\\d+)?|true|false|\"(?:[^\"\\\\]|\\\\.)*\")",
        RegexOptions.Compiled);

    private void Parse(string raw)
    {
        Fields.Clear();
        Order.Clear();
        _workingText = raw;

        foreach (Match m in FieldRe.Matches(raw))
        {
            string k = m.Groups["k"].Value;
            // 跳过 guid 内部的 "value" 键（本对象无此结构，但稳妥起见）
            if (!Fields.ContainsKey(k))
            {
                Fields[k] = m.Groups["v"].Value;
                Order.Add(k);
            }
        }
        if (Fields.Count == 0)
            ParseError = "未能解析出任何子字段";
    }

    protected override string BuildSnapshot()
    {
        var sb = new StringBuilder();
        foreach (var kv in _edits.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
        return sb.ToString();
    }

    /// <summary>只对被编辑的子字段做定点替换，其余字节原样保留。</summary>
    protected override string BuildText()
    {
        string result = OriginalRaw;
        foreach (var kv in _edits)
        {
            string key = kv.Key;
            string newVal = kv.Value;
            // 精确定位 "key" : <oldvalue> 并替换 oldvalue 部分
            var re = new Regex(
                "(\"" + Regex.Escape(key) + "\"\\s*:\\s*)(" +
                "-?\\d+(?:\\.\\d+)?|true|false|\"(?:[^\"\\\\]|\\\\.)*\")");
            result = re.Replace(result, m => m.Groups[1].Value + newVal, 1);
        }
        return result;
    }

    public override void Revert()
    {
        _edits.Clear();
        ParseError = null;
        Parse(OriginalRaw);
        CaptureSnapshot();
    }

    // ---- 编辑操作 ----

    /// <summary>读取子字段的当前值（含未提交的编辑）。</summary>
    public string GetValue(string key)
        => _edits.TryGetValue(key, out string e) ? e
         : Fields.TryGetValue(key, out string f) ? f : null;

    /// <summary>设置一个整数子字段。</summary>
    public void SetInt(string key, long value)
        => _edits[key] = Es3Format.Num(value);

    /// <summary>设置一个浮点子字段。</summary>
    public void SetDouble(string key, double value)
        => _edits[key] = Es3Format.Num(value);

    /// <summary>设置一个布尔子字段。</summary>
    public void SetBool(string key, bool value)
        => _edits[key] = value ? "true" : "false";

    /// <summary>
    /// 数组/对象类子字段（chosenCursedItems、actualCursedItems、itemTiers）
    /// 不做结构化编辑，UI 上以只读原文展示，避免误改排版。
    /// </summary>
    public static readonly HashSet<string> NonEditable = new(StringComparer.Ordinal)
    {
        "chosenCursedItems", "actualCursedItems", "itemTiers", "_ES3Ref",
    };

    /// <summary>子字段的中文名（用于展示）。</summary>
    public static string FieldZh(string key) => key switch
    {
        "_ES3Ref" => "内部引用 ID（勿改）",
        "difficulty" => "难度档位",
        "nameKey" => "难度名称键",
        "descriptionKey" => "难度描述键",
        "sanityPillRestorationAmount" => "理智药丸恢复量",
        "startingSanity" => "初始理智",
        "sanityDrain" => "理智流失速率",
        "sprinting2" => "冲刺设置",
        "flashlights" => "允许手电筒",
        "loseItemsOnDeath" => "死亡掉落物品",
        "playerSpeed" => "玩家速度",
        "evidenceGiven" => "给出的证据数",
        "changingFavouriteRoom" => "更换最爱房间",
        "interactionAmount" => "互动频率",
        "eventFrequency" => "显形频率",
        "huntDuration" => "猎杀时长",
        "gracePeriod" => "猎杀保护期",
        "fingerprintChance" => "指纹出现概率",
        "fingerprintDuration" => "指纹持续时间",
        "friendlyGhost" => "友善鬼魂",
        "killsExtendHuntDuration" => "击杀延长猎杀",
        "roamingFrequency" => "游荡频率",
        "ghostSpeed" => "鬼魂速度",
        "ghostTypeOverride" => "鬼种覆盖",
        "setupTime" => "准备时间",
        "selectedWeather" => "选择的天气",
        "amountOfDoorsOpen" => "开启的门数",
        "amountOfHidingPlaces" => "藏身处数量",
        "sanityMonitor" => "理智监测仪",
        "activityMonitor" => "活动监测仪",
        "fuseBoxVisibleOnMap" => "电闸显示在地图",
        "fuseBoxStartPower" => "电闸初始电量",
        "cursedPossessionsQuantity" => "诅咒道具数量",
        "chosenCursedItems" => "选中的诅咒道具（数组）",
        "overrideMultiplier" => "倍率覆盖",
        "actualWeather" => "实际天气",
        "actualCursedItems" => "实际诅咒道具（数组）",
        "itemTiers" => "物品阶级（数组）",
        _ => key,
    };
}
