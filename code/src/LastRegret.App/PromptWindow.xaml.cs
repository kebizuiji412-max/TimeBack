using System.Windows;
using System.Windows.Input;

namespace LastRegret.App;

/// <summary>
/// 极简文本输入对话框（不依赖任何第三方控件库，也不用 WinForms 的 InputBox）。
/// 用于给恢复点加备注之类的轻量输入。
/// </summary>
public partial class PromptWindow : Window
{
    private PromptWindow(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    /// <summary>弹出输入框；用户取消时返回 null（与"输入空字符串"区分开）。</summary>
    public static string? Ask(Window? owner, string title, string prompt, string initial = "")
    {
        var window = new PromptWindow(title, prompt, initial);
        if (owner is not null && !ReferenceEquals(owner, window)) window.Owner = owner;
        return window.ShowDialog() == true ? window.InputBox.Text.Trim() : null;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>清空输入 = 删除备注（与"取消"区分：清空是有效的用户意图）。</summary>
    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        InputBox.Text = string.Empty;
        InputBox.Focus();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }
}
