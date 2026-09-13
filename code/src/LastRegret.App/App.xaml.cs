using System.IO;
using System.Windows;
using System.Windows.Threading;
using LastRegret.Runtime;

namespace LastRegret.App;

/// <summary>
/// WPF 应用壳（入口 + WPF 生命周期）。
///
/// 它**不负责组装服务** —— 那件事在 <see cref="LastRegret.Runtime.AppRuntime"/> 里，
/// 与 UI 无关、可被 CLI / MCP 宿主复用。这里只做三件事：
///   ① 建立运行上下文（<see cref="AppRuntime.Create"/>）；
///   ② 把启动过程中的失败翻译成用户看得懂的中文弹窗；
///   ③ 打开主窗口、退出时释放运行上下文。
///
/// 关于弹窗与日志的分工：<see cref="LastRegret.Runtime.CrashLog"/> 负责"记录事实"，
/// 本类负责"用 WPF 的方式告诉用户"。这样 Runtime 不需要认识 MessageBox。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 本次运行的运行上下文（窗口与 ViewModel 都从这里取依赖）。
    ///
    /// 生命周期所有权在**本类**：<see cref="OnStartup"/> 创建、<see cref="OnExit"/> 释放。
    /// 别处（例如窗口的 Closing）不要释放它 —— 见 <see cref="DisposeRuntime"/> 的说明。
    /// </summary>
    public static AppRuntime Runtime { get; private set; } = null!;

    private static bool _runtimeDisposed;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 未捕获异常一律给用户一个明确交代（并写日志），不允许无声崩溃
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) WriteCrashLog("AppDomain", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("Task", args.Exception);
            args.SetObserved();
        };

        try
        {
            Runtime = AppRuntime.Create();
            _runtimeDisposed = false;
        }
        catch (Exception ex)
        {
            WriteCrashLog("Startup", ex);
            MessageBox.Show(
                "启动失败：" + ex.Message + Environment.NewLine + Environment.NewLine +
                "详细信息已写入日志文件（见下方路径）。" + Environment.NewLine +
                "日志里的本机路径已自动打码（只保留盘符与文件名）。" + Environment.NewLine + CrashLogPath(),
                "回溯",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow(Runtime);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeRuntime("Shutdown");
        base.OnExit(e);
    }

    /// <summary>
    /// 释放运行上下文。**这是 Runtime 唯一的释放点**。
    ///
    /// 为什么要做成幂等的一次性操作：
    ///   · WPF 里 <c>Shutdown()</c> 与正常关窗都会走到 <see cref="OnExit"/>；
    ///   · 启动失败时 <c>Shutdown(1)</c> 也会触发 <see cref="OnExit"/>，而此时 Runtime 尚未创建；
    ///   · 窗口的 Closing 会被取消（用户选择不关），但 Exit 不会 ——
    ///     所以释放必须挂在 Exit 上，而不是 Closing 上。
    ///
    /// 这里用"只执行一次"把重复调用堵住，而不是去依赖底层资源各自是否幂等 ——
    /// 目前 WatchService / LastRegretDatabase / SqliteConnection 自带 _disposed 保护，
    /// 但 ContentStore / DirectoryWatcher 没有，将来还可能有新的非幂等资源。
    /// </summary>
    internal static void DisposeRuntime(string source)
    {
        if (_runtimeDisposed) return;
        _runtimeDisposed = true;

        try
        {
            Runtime?.Dispose();
        }
        catch (Exception ex)
        {
            WriteCrashLog(source, ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("Dispatcher", e.Exception);
        MessageBox.Show(
            "界面出现未处理的异常，已记录到日志。" + Environment.NewLine +
            e.Exception.Message + Environment.NewLine + Environment.NewLine +
            "你可以继续使用；若反复出现请把日志发给开发者。" + Environment.NewLine +
            "日志里的本机路径已自动打码（只保留盘符与文件名）。" + Environment.NewLine + CrashLogPath(),
            "回溯",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
