using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using LastRegret.Runtime;

namespace LastRegret.App;

/// <summary>
/// 主窗口。界面本身不做任何业务判断 —— 全部事实都来自 <see cref="MainViewModel"/>，
/// 这样"界面显示的内容"与"引擎实际做的事"不会出现两套说法。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;

    public MainWindow(AppRuntime runtime)
    {
        InitializeComponent();
        _vm = new MainViewModel(runtime);
        DataContext = _vm;

        // 状态栏与时间线每 2 秒刷新一次（引擎在后台持续记录）
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _timer.Tick += (_, _) => _vm.OnTick();
        _timer.Start();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>供对话框 Owner 使用。</summary>
    public MainViewModel ViewModel => _vm;

    /// <summary>
    /// 迷你文件浏览器：双击文件夹进入（或双击".."返回上一级）。
    ///
    /// 用事件而不是 Command，是因为双击的目标取决于"鼠标点在哪一行"，
    /// 而 WPF 的 ListBox 没有现成的"双击行"命令；这里取 SelectedItem 交给 VM 处理。
    /// </summary>
    private void OnRestoreBrowseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox list) return;
        if (list.SelectedItem is not RestoreBrowseRow row) return;
        _vm.OpenBrowseRowCommand.Execute(row);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 无论有没有受保护目录，首次打开都落在「首页」：
        //   · 没有目录时，首页就是"选一个文件夹"这一屏（不再把用户丢进设置后台）
        //   · 有目录时，首页显示"正在保护什么 + 最近发生了什么 + 出问题了怎么办"
        _vm.NavigateCommand.Execute("home");

        // 上次扫描没做完的目录，这次启动直接接着扫完 —— 不让用户去找按钮。
        _vm.AutoFinishPendingScans();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        try
        {
            // 只停掉"本窗口自己的"计时器（它属于窗口，窗口关掉就该停）。
            //
            // ⚠ 这里**不释放 Runtime**：Runtime 的所有者是 App（OnStartup 创建 / OnExit 释放）。
            //   原因有二：
            //     ① Closing 会被取消（用户点了"不关"、或将来加了"还有任务在进行"的确认），
            //        而此时数据库/内容库已经被关掉的话，窗口还活着却用不了 —— 那是更糟的状态；
            //     ② 以前这里也 Dispose 一次，OnExit 又 Dispose 一次，属于双重释放。
            //   注意顺序：Closing 只 stop 计时器，真正的释放发生在之后的 Exit。
            _timer.Stop();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("Closing", ex);
        }
    }
}
