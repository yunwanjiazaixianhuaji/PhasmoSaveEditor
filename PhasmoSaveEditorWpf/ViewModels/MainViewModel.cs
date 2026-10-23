using System.Collections.ObjectModel;
using System.IO;
using PhasmoSaveEditor.Core;
using PhasmoSaveEditor.Schema;
using PhasmoSaveEditor.ViewModels.Fields;

namespace PhasmoSaveEditor.ViewModels;

/// <summary>左侧分类树的一个节点。</summary>
public sealed class CategoryNode : ObservableObject
{
    public Category Cat { get; init; }
    public string Title { get; init; }
    public string Icon { get; init; }
    /// <summary>该分类下的字段数（用于树上显示计数）。</summary>
    public int Count { get; set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public override string ToString() => $"{Icon} {Title} ({Count})";
}

/// <summary>写入前给用户的确认摘要。</summary>
public sealed record ApplySummary(int FieldCount, IReadOnlyList<string> Lines);

/// <summary>
/// 主视图模型：加载存档 → 建字段目录 → 分类/搜索/改动跟踪 → 应用写入。
///
/// 写入严格走 SafetyService 的三道闸（进程检测 → 备份 → 加密自检），
/// 任一失败即抛异常、不写盘。UI 负责把异常转成友好提示。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private SaveDocument _doc;
    private List<FieldViewModel> _all = new();
    private PresetService _presets;

    public MainViewModel()
    {
        SavePath = SafetyService.DefaultSavePath;
        LoadCommand = new RelayCommand(Load, () => File.Exists(SavePath));
        BrowseCommand = new RelayCommand(() => BrowseRequested?.Invoke());
        RevertAllCommand = new RelayCommand(RevertAll, () => IsLoaded && HasChanges);
        BuildCategoryNodes();
    }

    // ---- 状态 ----

    public string SavePath
    {
        get => _savePath;
        set
        {
            if (SetField(ref _savePath, value))
            {
                LoadCommand = new RelayCommand(Load, () => File.Exists(value));
                OnPropertyChanged(nameof(LoadCommand));
            }
        }
    }
    private string _savePath;

    public bool IsLoaded => _doc is not null;
    public bool HasChanges => _all.Any(f => f.IsModified);
    public int ModifiedCount => _all.Count(f => f.IsModified);

    /// <summary>存在校验不通过的已改字段时，禁止写入。</summary>
    public bool HasBlockingError => _all.Any(f => f.IsModified && !f.IsValid);

    /// <summary>是否真的可以写入（存在校验失败时为 false）。</summary>
    public bool CanApply => IsLoaded && HasChanges && !HasBlockingError;

    /// <summary>
    /// 「应用修改」按钮的 IsEnabled。
    ///
    /// **故意不等于 CanApply**：有字段校验失败时按钮仍必须可点，
    /// 点击后才能弹出「哪些字段不合法、该怎么改」的说明。
    /// 若直接拿 CanApply 置灰，WPF 的 Click 事件根本不会触发，
    /// 用户只会看到一个灰按钮 + 字段旁的小红字，完全不知道下一步该做什么
    /// —— 这正是「无法应用修改」最难排查的表现。
    /// </summary>
    public bool ApplyEnabled => IsLoaded && (HasChanges || HasBlockingError);

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }
    private string _statusText = "尚未加载存档";

    /// <summary>顶部概览：金钱 / 等级 / 转生。</summary>
    public string SummaryText
    {
        get => _summaryText;
        set => SetField(ref _summaryText, value);
    }
    private string _summaryText = "";

    public ObservableCollection<CategoryNode> Categories { get; } = new();

    /// <summary>当前显示的字段（受分类 + 搜索 + 仅看已改过滤）。</summary>
    public ObservableCollection<FieldViewModel> VisibleFields { get; } = new();

    private CategoryNode _selectedCategory;
    public CategoryNode SelectedCategory
    {
        get => _selectedCategory;
        set { if (SetField(ref _selectedCategory, value)) RefreshVisible(); }
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { if (SetField(ref _searchText, value)) RefreshVisible(); }
    }

    private bool _onlyModified;
    public bool OnlyModified
    {
        get => _onlyModified;
        set { if (SetField(ref _onlyModified, value)) RefreshVisible(); }
    }

    // ---- 命令 ----

    public RelayCommand LoadCommand { get; private set; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand RevertAllCommand { get; }

    /// <summary>请求弹出文件选择框（由 View 层实现具体对话框）。</summary>
    public event Action BrowseRequested;
    /// <summary>请求弹出确认框（由 View 层实现）。返回 true = 用户确认。</summary>
    public Func<ApplySummary, bool> ConfirmApply { get; set; }
    /// <summary>请求弹信息框。</summary>
    public Action<string, string> ShowMessage { get; set; }

    // ---- 加载 ----

    private void BuildCategoryNodes()
    {
        Categories.Clear();
        foreach (var (cat, title, icon) in CategoryInfo.Order)
            Categories.Add(new CategoryNode { Cat = cat, Title = title, Icon = icon });
    }

    public void Load()
    {
        if (!File.Exists(SavePath))
        {
            StatusText = "找不到存档文件：" + SavePath;
            return;
        }
        try
        {
            _doc = SaveDocument.Load(SavePath);
        }
        catch (Exception ex)
        {
            StatusText = "读取失败：" + ex.Message;
            _all.Clear();
            VisibleFields.Clear();
            return;
        }

        var entries = _doc.EnumerateEntries();
        var schemas = FieldCatalog.Build(entries);
        _all = new List<FieldViewModel>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var vm = FieldViewModel.Create(entries[i], schemas[i]);
            vm.Changed += OnFieldChanged;
            _all.Add(vm);
        }
        _presets = new PresetService(_all);

        // 统计各分类字段数
        foreach (var node in Categories)
            node.Count = _all.Count(f => f.Category == node.Cat);

        if (SelectedCategory is null)
            SelectedCategory = Categories.FirstOrDefault(c => c.Cat == Category.MoneyLevel)
                               ?? Categories.FirstOrDefault();
        else
            RefreshVisible();

        UpdateSummary();
        StatusText = $"已加载 {_all.Count} 个字段：{Path.GetFileName(SavePath)}";
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void OnFieldChanged(FieldViewModel vm)
    {
        NotifyState();
        if (vm.Key is "PlayersMoney" or "NewLevel" or "Prestige") UpdateSummary();
        if (_onlyModified) RefreshVisible();
        RelayCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 统一通知所有派生状态属性。
    ///
    /// **CanApply 必须显式通知**：它是 IsLoaded / HasChanges / HasBlockingError 的派生属性，
    /// 「应用修改」按钮的 IsEnabled 直接绑定它。WPF 不会因底层属性变化自动重算派生属性，
    /// 漏掉通知会导致按钮永远是灰的、点了没反应（此前正是这个 bug）。
    /// </summary>
    private void NotifyState()
        => OnPropertyChangeds(
            nameof(HasChanges), nameof(ModifiedCount),
            nameof(HasBlockingError), nameof(CanApply), nameof(ApplyEnabled));

    private void UpdateSummary()
    {
        string money = _all.FirstOrDefault(f => f.Key == "PlayersMoney") is IntFieldViewModel m
            ? m.Value.ToString("N0") : "?";
        string level = _all.FirstOrDefault(f => f.Key == "NewLevel") is IntFieldViewModel l
            ? l.Value.ToString() : "?";
        string prestige = _all.FirstOrDefault(f => f.Key == "Prestige") is IntFieldViewModel p
            ? p.Value.ToString() : "?";
        SummaryText = $"金钱 {money}　等级 {level}　转生 {prestige}";
    }

    private void RefreshVisible()
    {
        if (!IsLoaded) return;
        string q = (SearchText ?? "").Trim();
        IEnumerable<FieldViewModel> src = _all;
        if (SelectedCategory is not null)
            src = src.Where(f => f.Category == SelectedCategory.Cat);
        if (_onlyModified)
            src = src.Where(f => f.IsModified);
        if (q.Length > 0)
            src = src.Where(f =>
                f.Key.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (f.Display?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));

        VisibleFields.Clear();
        foreach (var f in src.OrderBy(f => f.Schema.Order).ThenBy(f => f.Display, StringComparer.Ordinal))
            VisibleFields.Add(f);
    }

    // ---- 预设（只改 ViewModel，不写盘）----

    // 预设输入一律用 string 属性而非 long：
    // long 直接绑 TextBox 时，用户输入空或字母会被 WPF 静默丢弃并保留旧值，
    // 界面上看不出任何异常，容易让用户以为输入生效了。
    // 用 string 承接原始输入，在预设执行时解析并明确报错。

    /// <summary>「设定等级」预设的目标等级输入。</summary>
    public string PresetLevelText { get; set; } = "100";
    /// <summary>「设定等级」预设的目标转生输入。</summary>
    public string PresetPrestigeText { get; set; } = "0";
    /// <summary>「设定等级」预设是否把本级进度经验归零。</summary>
    public bool PresetResetLevelXp { get; set; } = false;
    /// <summary>库存拉满的上限。</summary>
    public string PresetInventoryCapText { get; set; } =
        FieldCatalog.MaxInventory.ToString();

    public PresetResult PresetUnlockAll() => RunPreset(_presets.UnlockAllEquipment());

    public PresetResult PresetMaxMoney()
    {
        if (!TryParsePresetLong(PresetInventoryCapText, 1, int.MaxValue,
                "库存上限", out long cap))
            return new PresetResult("金钱拉满 + 库存拉满", 0, new[] { LastPresetError });
        _presets.InventoryCap = cap;
        return RunPreset(_presets.MaxMoneyAndInventory());
    }

    public PresetResult PresetSetLevel()
    {
        if (!TryParsePresetLong(PresetLevelText, 1, FieldCatalog.MaxLevel,
                "等级", out long level))
            return new PresetResult("设定等级 / 转生", 0, new[] { LastPresetError });
        if (!TryParsePresetLong(PresetPrestigeText, 0, FieldCatalog.MaxPrestige,
                "转生", out long prestige))
            return new PresetResult("设定等级 / 转生", 0, new[] { LastPresetError });
        return RunPreset(_presets.SetLevel(level, prestige, PresetResetLevelXp));
    }

    public PresetResult PresetCompleteAll() => RunPreset(_presets.CompleteAchievementsAndChallenges());

    /// <summary>最近一次预设输入解析的错误说明。</summary>
    public string LastPresetError { get; private set; }

    private bool TryParsePresetLong(string text, long min, long max, string label, out long value)
    {
        value = 0;
        if (!long.TryParse((text ?? "").Trim(), out value))
        {
            LastPresetError = $"{label}必须是整数（当前输入「{text}」）。";
            return false;
        }
        if (value < min || value > max)
        {
            LastPresetError = $"{label}需在 {min} ~ {max} 之间（当前输入 {value}）。";
            return false;
        }
        LastPresetError = null;
        return true;
    }

    private PresetResult RunPreset(PresetResult r)
    {
        // 预设可能改了当前分类外的字段，刷新计数与可见列表
        foreach (var node in Categories)
            node.Count = _all.Count(f => f.Category == node.Cat);
        NotifyState();
        UpdateSummary();
        RefreshVisible();
        RelayCommand.RaiseCanExecuteChanged();
        StatusText = $"{r.Name}：{r.Changed} 个字段被修改（尚未写入，请点「应用修改」）";
        return r;
    }

    // ---- 还原 ----

    public void RevertAll()
    {
        foreach (var f in _all) f.Revert();
        NotifyState();
        UpdateSummary();
        RefreshVisible();
        RelayCommand.RaiseCanExecuteChanged();
        StatusText = "已还原全部修改";
    }

    // ---- 应用写入 ----

    /// <summary>
    /// 生成写入确认摘要（原值 → 新值），供 UI 弹确认框。
    /// 只列出确实改动且合法的字段。
    /// </summary>
    public ApplySummary BuildApplySummary()
    {
        var modified = _all.Where(f => f.IsModified && f.IsValid).ToList();
        var lines = modified.Take(60).Select(f => f.DescribeChange()).ToList();
        if (modified.Count > lines.Count)
            lines.Add($"……以及另外 {modified.Count - lines.Count} 个字段");
        return new ApplySummary(modified.Count, lines);
    }

    /// <summary>
    /// 列出所有校验未通过的字段，供 UI 给出可操作的提示。
    /// 返回「显示名（key）：错误原因」，最多 max 条。
    /// </summary>
    public IReadOnlyList<string> ListBlockingFields(int max = 12)
    {
        var bad = _all.Where(f => f.IsModified && !f.IsValid).ToList();
        var lines = new List<string>(Math.Min(bad.Count, max));
        foreach (var f in bad.Take(max))
            lines.Add($"{f.Display}（{f.Key}）：{f.ValidationError}");
        if (bad.Count > lines.Count)
            lines.Add($"……以及另外 {bad.Count - lines.Count} 个字段");
        return lines;
    }

    /// <summary>
    /// 执行写入。返回备份路径；抛异常代表未写入（原存档未受影响）。
    /// 调用方（UI）应先弹 BuildApplySummary 的确认框。
    /// </summary>
    public string Apply()
    {
        if (!IsLoaded) throw new InvalidOperationException("尚未加载存档");
        if (HasBlockingError)
            throw new InvalidOperationException("存在校验未通过的字段，请先修正后再写入");

        var summary = BuildApplySummary();
        if (ConfirmApply is not null && !ConfirmApply(summary))
        {
            StatusText = "已取消写入";
            return null;
        }

        // 把改动写进明文副本
        var doc = new SaveDocument(_doc.Text);
        int written = 0;
        foreach (var f in _all)
            if (f.ApplyTo(doc)) written++;

        // 走完整保护流程：进程检测 → 备份 → 加密自检 → 原子写入
        var result = SafetyService.WriteSave(SavePath, doc.Text);

        // 写入成功后以新内容为基准重新加载，
        // 让每个字段的 OriginalRaw 对齐已写入的值，从而清空「已修改」标记。
        _doc = new SaveDocument(doc.Text);
        ReloadAfterWrite();

        StatusText = $"已写入 {written} 个字段，备份：{Path.GetFileName(result.BackupPath)}";
        return result.BackupPath;
    }

    /// <summary>写入成功后重新加载，使 OriginalRaw 对齐已写入的新内容。</summary>
    private void ReloadAfterWrite()
    {
        var entries = _doc.EnumerateEntries();
        var schemas = FieldCatalog.Build(entries);
        foreach (var f in _all) f.Changed -= OnFieldChanged;
        _all = new List<FieldViewModel>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var vm = FieldViewModel.Create(entries[i], schemas[i]);
            vm.Changed += OnFieldChanged;
            _all.Add(vm);
        }
        _presets = new PresetService(_all);
        foreach (var node in Categories)
            node.Count = _all.Count(f => f.Category == node.Cat);
        NotifyState();
        UpdateSummary();
        RefreshVisible();
        RelayCommand.RaiseCanExecuteChanged();
    }
}
