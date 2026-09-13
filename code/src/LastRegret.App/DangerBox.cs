using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LastRegret.App;

/// <summary>
/// 危险操作确认框：**默认焦点永远落在"不做那件事"的按钮上**。
///
/// 为什么必须自己调 Win32（真实缺陷，Release 黑盒压测发现）：
///   WPF 的 <c>MessageBox.Show</c> 没有"默认按钮"参数。用 YesNo / YesNoCancel 时，
///   系统把默认焦点给第一个按钮 —— 也就是"是"。于是「移除保护 → 连历史一起删除」
///   这种不可逆操作，用户随手一个 Enter 就执行了。
///
///   Win32 的 <c>MessageBoxW</c> 本来就有 <c>MB_DEFBUTTON2/3</c>，只是 WPF 没往外暴露。
///   所以这里直接调它，把默认按钮固定成安全的那一个：
///     是 / 否      → 默认「否」
///     是 / 否 / 取消 → 默认「取消」
///     确定 / 取消   → 默认「取消」
///     Esc          → 系统默认映射到「取消 / 否」
///
/// 返回值与 <see cref="MessageBox.Show(string, string, MessageBoxButton, MessageBoxImage)"/> 完全一致，
/// 调用方原来的判断逻辑一行都不用改。
/// </summary>
internal static class DangerBox
{
    private const uint MB_OKCANCEL = 0x00000001;
    private const uint MB_YESNOCANCEL = 0x00000003;
    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_DEFBUTTON2 = 0x00000100;
    private const uint MB_DEFBUTTON3 = 0x00000200;
    private const uint MB_SETFOREGROUND = 0x00010000;

    private const int IDOK = 1;
    private const int IDCANCEL = 2;
    private const int IDYES = 6;
    private const int IDNO = 7;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>弹出带"安全默认按钮"的确认框。</summary>
    public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons)
    {
        uint type;
        switch (buttons)
        {
            case MessageBoxButton.YesNo:
                // 默认「否」
                type = MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2;
                break;

            case MessageBoxButton.YesNoCancel:
                // 默认「取消」
                type = MB_YESNOCANCEL | MB_ICONWARNING | MB_DEFBUTTON3;
                break;

            default:
                // OKCancel 以及一切"…继续吗？"型确认：默认「取消」
                type = MB_OKCANCEL | MB_ICONWARNING | MB_DEFBUTTON2;
                break;
        }

        var result = MessageBoxW(ActiveOwnerHandle(), message, title, type | MB_SETFOREGROUND);
        return result switch
        {
            IDOK => MessageBoxResult.OK,
            IDYES => MessageBoxResult.Yes,
            IDNO => MessageBoxResult.No,
            IDCANCEL => MessageBoxResult.Cancel,
            // 关掉窗口、点右上角 × 等一切"非明确同意"的情况一律按取消处理。
            // 危险操作不能因为"用户没回答"就当成同意。
            _ => MessageBoxResult.Cancel,
        };
    }

    /// <summary>
    /// 把确认框挂到当前活动窗口上，避免它跑到主窗口后面（用户以为"点了没反应"）。
    /// 不直接用 <c>Application.Current.MainWindow</c>：它不保证是当前活动的那一个。
    /// </summary>
    private static IntPtr ActiveOwnerHandle()
    {
        try
        {
            var app = Application.Current;
            if (app is null) return IntPtr.Zero;

            Window? owner = null;
            foreach (Window w in app.Windows)
            {
                if (w.IsActive) { owner = w; break; }
            }
            owner ??= app.MainWindow;
            return owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
        }
        catch
        {
            // 取不到宿主就用桌面作为属主，弹窗本身不能因此失败
            return IntPtr.Zero;
        }
    }
}
