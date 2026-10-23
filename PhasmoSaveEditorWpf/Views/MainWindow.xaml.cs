using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using PhasmoSaveEditor.Core;
using PhasmoSaveEditor.ViewModels;

namespace PhasmoSaveEditor.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // 把需要对话框的交互从 VM 回调到 View 层实现
        _vm.BrowseRequested += Browse;
        _vm.ConfirmApply = ConfirmApply;
        _vm.ShowMessage = (msg, title) => MessageBox.Show(this, msg, title,
            MessageBoxButton.OK, MessageBoxImage.Information);

        Loaded += (_, _) => _vm.Load();
        Closing += OnClosing;
    }

    private void Browse()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 SaveFile.txt",
            Filter = "存档文件 (*.txt)|*.txt|全部文件 (*.*)|*.*",
        };
        string dir = Path.GetDirectoryName(_vm.SavePath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            dlg.InitialDirectory = dir;

        if (dlg.ShowDialog(this) == true)
        {
            _vm.SavePath = dlg.FileName;
            _vm.Load();
        }
    }

    /// <summary>写入前弹出变更确认框，列出将要写入的字段。</summary>
    private bool ConfirmApply(ApplySummary summary)
    {
        if (summary.FieldCount == 0)
        {
            MessageBox.Show(this, "没有任何改动需要写入。", "恐鬼症存档修改器",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var sb = new StringBuilder();
        sb.Append("即将写入以下改动：\n\n");
        foreach (string line in summary.Lines) sb.Append("  • ").Append(line).Append('\n');
        sb.Append($"\n共 {summary.FieldCount} 个字段。\n\n");
        sb.Append("原存档会自动备份为带时间戳的 .bak 文件。\n");
        sb.Append("确定继续吗？");

        return MessageBox.Show(this, sb.ToString(), "确认写入",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    // ---- 预设按钮 ----

    private void OnPresetUnlockAll(object sender, RoutedEventArgs e)
        => ShowPreset(_vm.PresetUnlockAll());

    private void OnPresetMaxMoney(object sender, RoutedEventArgs e)
        => ShowPreset(_vm.PresetMaxMoney());

    private void OnPresetCompleteAll(object sender, RoutedEventArgs e)
        => ShowPreset(_vm.PresetCompleteAll());

    private void OnPresetSetLevel(object sender, RoutedEventArgs e)
        => ShowPreset(_vm.PresetSetLevel());

    /// <summary>展示预设结果：成功列出说明，失败给出错误。预设只改内存不写盘。</summary>
    private void ShowPreset(PresetResult r)
    {
        var sb = new StringBuilder();
        sb.Append(r.Name).Append("：").Append(r.Changed).Append(" 个字段被修改。\n\n");
        foreach (string n in r.Notes) sb.Append("  • ").Append(n).Append('\n');
        sb.Append("\n这些改动尚未写入存档。请核对后点「应用修改」写盘，或点「还原全部」放弃。");

        // Changed=0 且首条 note 像是错误信息时，用警告图标
        bool isWarn = r.Changed == 0;
        MessageBox.Show(this, sb.ToString(), r.Name,
            MessageBoxButton.OK, isWarn ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    // ---- 应用写入 ----

    private void OnApply(object sender, RoutedEventArgs e)
    {
        // 校验未通过：按钮此时仍是可点的（ApplyEnabled），
        // 目的就是让用户能点开这个说明，而不是面对一个灰按钮无从下手。
        if (_vm.HasBlockingError)
        {
            var sb = new StringBuilder();
            sb.Append("以下字段校验未通过，已阻止写入（原存档未受影响）：\n\n");
            foreach (string line in _vm.ListBlockingFields())
                sb.Append("  • ").Append(line).Append('\n');
            sb.Append("\n请修正为合法值，或点「还原全部」放弃这些输入后重试。");

            MessageBox.Show(this, sb.ToString(), "无法写入：存在非法字段",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_vm.CanApply)
        {
            MessageBox.Show(this,
                _vm.IsLoaded
                    ? "当前没有可写入的改动。"
                    : "尚未加载存档。请先点「读取存档」，或用「浏览」手动选择 SaveFile.txt。",
                "恐鬼症存档修改器", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            string backup = _vm.Apply();
            if (backup is null) return;   // 用户在确认框取消了

            MessageBox.Show(this,
                "修改完成。\n\n备份文件：\n" + backup + "\n\n" +
                "注意：本作接入了 Unity 云存档与 Steam 云同步。\n" +
                "如果进游戏发现进度被回滚，说明云端覆盖了本地存档，\n" +
                "需要在 Steam 里临时关闭该游戏的云同步后重试。",
                "写入成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (InvalidOperationException ex)
        {
            // 游戏在运行 / 加密自检失败等可预期错误
            MessageBox.Show(this, ex.Message, "未写入", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this,
                "没有写入存档的权限：\n\n" + ex.Message +
                "\n\n存档目录可能被设为只读，或被其他程序占用。\n" +
                "可以试试以管理员身份运行本程序。",
                "未写入", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "写入失败，原存档未受影响：\n\n" + ex,
                "恐鬼症存档修改器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    /// <summary>有未写入的改动时，关闭前提醒，避免用户以为已经保存。</summary>
    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_vm.HasChanges) return;
        var r = MessageBox.Show(this,
            "有尚未写入的改动。\n\n「是」= 放弃改动并关闭；「否」= 返回继续编辑。",
            "未保存的改动", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r == MessageBoxResult.No) e.Cancel = true;
    }
}
