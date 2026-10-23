using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace PhasmoSaveEditor.ViewModels;

/// <summary>
/// INotifyPropertyChanged 基类。
/// 手写而非引入 CommunityToolkit.Mvvm，保证项目离线可构建（无任何 NuGet 依赖）。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>字段变化时赋值并通知；值相同则不通知，避免无谓刷新。</summary>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>批量通知多个属性（用于一次编辑影响多个显示时）。</summary>
    protected void OnPropertyChangeds(params string[] names)
    {
        foreach (string n in names) OnPropertyChanged(n);
    }
}

/// <summary>标准 ICommand 实现。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object> _execute;
    private readonly Predicate<object> _canExecute;

    public RelayCommand(Action execute, Func<bool> canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object parameter) => _execute(parameter);

    public event EventHandler CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <summary>让按钮重新评估可用状态。</summary>
    public static void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
