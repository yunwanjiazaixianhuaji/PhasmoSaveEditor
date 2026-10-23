using System.IO;
using System.Windows;
using PhasmoSaveEditor.ViewModels;
using PhasmoSaveEditor.Views;

namespace PhasmoSaveEditor;

public partial class App : Application
{
    /// <summary>诊断日志路径（仅 --diag 模式使用，写在 exe 同级目录）。</summary>
    private static readonly string DiagLog =
        Path.Combine(AppContext.BaseDirectory, "diag_progress.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterExceptionHandlers();

        if (e.Args.Contains("--diag"))
        {
            RunDiagnostics();
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }

    private void RegisterExceptionHandlers()
    {
        // 任何未捕获异常都弹窗展示，避免程序静默崩溃后用户不知道存档是否已写坏。
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("程序出错了：\n\n" + args.Exception, "恐鬼症存档修改器",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                MessageBox.Show("程序出错了：\n\n" + ex, "恐鬼症存档修改器",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }

    // ---- 无头诊断：定位启动期崩溃（含栈溢出）----

    private static void Diag(string step)
    {
        try { File.AppendAllText(DiagLog, $"[{DateTime.Now:HH:mm:ss.fff}] {step}\n"); }
        catch { /* 诊断本身不能再抛 */ }
    }

    /// <summary>
    /// 分步执行启动流程，每步落盘 flush。
    /// 栈溢出会直接终止进程、无法 catch，所以靠「日志里最后一条 = 崩溃点」定位。
    /// 此方法运行在 App 实例内部（this 已初始化），不再 new App()。
    /// </summary>
    private void RunDiagnostics()
    {
        try { File.Delete(DiagLog); } catch { }
        Diag("=== 诊断开始 ===");

        // 步骤 1：资源字典（App.xaml 已在自动生成的入口点里 InitializeComponent）
        Diag($"1. MergedDictionaries 数量 = {Resources.MergedDictionaries.Count}");
        foreach (string k in new[] { "FieldCard", "ErrBrush", "BoolToVis", "HexToBrush",
                                     "IntFieldTemplate", "ColorFieldTemplate" })
            Diag($"1. 资源 {k} {(Resources.Contains(k) ? "存在" : "缺失!!")}");

        // 步骤 2：构造 MainWindow（触发 XAML 解析 + 模板实例化 + DataContext）
        Diag("2. 构造 MainWindow 之前");
        MainWindow win;
        try { win = new MainWindow(); }
        catch (Exception ex) { Diag("2. !! MainWindow 构造抛异常: " + ex); return; }
        Diag("2. MainWindow 已构造");

        // 步骤 3：强制布局（DataTemplate 真正实例化在这里）
        Diag("3. Measure 之前");
        win.Measure(new Size(1180, 780));
        Diag("3. Measure 完成");
        win.Arrange(new Rect(0, 0, 1180, 780));
        Diag("3. Arrange 完成");

        // 步骤 4：加载存档 + 填充可见字段（触发字段模板对全部字段的实例化）
        Diag("4. 触发 Load 之前");
        try
        {
            if (win.DataContext is MainViewModel vm)
            {
                vm.Load();
                Diag($"4. Load 完成，IsLoaded={vm.IsLoaded}，可见字段数={vm.VisibleFields.Count}");
                win.Measure(new Size(1180, 780));
                win.Arrange(new Rect(0, 0, 1180, 780));
                Diag("4. 加载后重新布局完成");
            }
            else Diag("4. DataContext 不是 MainViewModel");
        }
        catch (Exception ex) { Diag("4. !! Load 抛异常: " + ex); }

        Diag("=== 诊断全部通过，无崩溃 ===");
    }
}
