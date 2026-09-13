using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace LastRegret.App;

/// <summary>极简 MVVM 基础设施（不引入任何第三方库）。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>简单委托命令（比 RelayCommand 少一层概念）。</summary>
public sealed class DelegateCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public DelegateCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public DelegateCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        if (canExecute is not null)
        {
            // 按钮的可用状态来自这里。CommandManager.RequerySuggested 是 WPF 里
            // 唯一会让绑定到 Command 的控件重新询问 CanExecute 的信号，
            // 不接上它，业务里调 InvalidateRequerySuggested 就等于没调，
            // 按钮会一直停在最初算出来的可用/不可用状态。
            CommandManager.RequerySuggested += (_, _) => RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>线程亲和性保护：后台线程更新集合时自动切回 UI 线程。</summary>
public static class UiDispatch
{
    private static System.Windows.Threading.Dispatcher? _dispatcher;

    public static void Initialize(System.Windows.Threading.Dispatcher dispatcher) => _dispatcher = dispatcher;

    public static void Invoke(Action action)
    {
        var d = _dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }
}
