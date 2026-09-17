using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LastRegret.App;

/// <summary>
/// 页面级滚轮裁决。
///
/// 为什么需要它：WPF 里内层可滚动控件（ListBox / TextBox 内部的 ScrollViewer）会先吃掉
/// MouseWheel，即使已经滚到边界也不再往外传 —— 结果是"鼠标停在列表上，页面就滚不动"，
/// 而用户根本分不清自己停在哪儿。
///
/// 这里在 <b>Preview（隧道）阶段</b>统一裁决，规则只有两条：
///   · 内层还能朝这个方向滚 → 不插手，让内层自己滚（避免页面与列表抢滚轮 / 双重滚动）；
///   · 内层已到边界、或鼠标下根本没有内层 → 页面自己滚一档（并标记已处理，避免重复滚）。
///
/// 页面本身必须使用 <c>CanContentScroll="False"</c>（按像素滚动）：
/// 若按条目滚动，一个"比视口还高的单一条目"等于完全没有可滚区间，滚轮会毫无反应。
/// </summary>
internal static class PageScroll
{
    private const double Epsilon = 0.5;

    /// <summary>滚一档的量：直接用 WPF 的 Delta（通常 120），不做放大，避免"滚一下跳太远"。</summary>
    public static void Handle(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not ScrollViewer page) return;

        // 页面本来就装得下 → 完全不插手（不给短页面强加滚动行为）
        if (page.ScrollableHeight <= Epsilon) return;

        var inner = FindInnerScrollable(e.OriginalSource as DependencyObject, page);
        if (inner is not null && CanScrollFurther(inner, e.Delta)) return;

        // e.Delta > 0 = 向上滚 → 偏移减小；两端都夹紧，保证能回到顶、也能到底
        var target = page.VerticalOffset - e.Delta;
        target = Math.Max(0, Math.Min(page.ScrollableHeight, target));

        // 已经在顶/底：不吞事件（把机会留给外层窗口或系统）
        if (Math.Abs(target - page.VerticalOffset) < Epsilon) return;

        page.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    private static bool CanScrollFurther(ScrollViewer inner, int delta)
    {
        if (inner.ScrollableHeight <= Epsilon) return false;
        return delta > 0
            ? inner.VerticalOffset > Epsilon                            // 向上：上面还有内容
            : inner.VerticalOffset < inner.ScrollableHeight - Epsilon;  // 向下：下面还有内容
    }

    /// <summary>从事件源向上找第一个"内层"滚动容器（不含页面自己）。</summary>
    private static ScrollViewer? FindInnerScrollable(DependencyObject? source, ScrollViewer page)
    {
        var node = source;
        while (node is not null && !ReferenceEquals(node, page))
        {
            if (node is ScrollViewer sv) return sv;
            node = ParentOf(node);
        }
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
