using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LastRegret.App;

/// <summary>按操作类型给徽标上色。</summary>
public sealed class OpToneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "created" => "Added",
            "deleted" => "Deleted",
            "modified" => "Modified",
            "renamed" => "Renamed",
            "moved" => "Renamed",
            "transient" => "Transient",
            _ => "FgSecondary",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>按变化类型给状态对比的行上色。</summary>
public sealed class ChangeToneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "added" => "Added",
            "deleted" => "Deleted",
            "modified" => "Modified",
            "renamed" => "Renamed",
            "typechanged" => "Warn",
            _ => "FgSecondary",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 三步恢复流程的步骤指示：把"当前第几步"翻译成每一步的显示状态。
/// 参数是该步骤的编号（1/2/3/4），值绑定 RestoreFlowStep。
/// 返回 "active"（当前）/ "done"（已走过）/ "todo"（还没到）。
/// </summary>
public sealed class StepStateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int current) return "todo";
        if (!int.TryParse(parameter as string, out var step)) return "todo";
        if (step == current) return "active";
        return step < current ? "done" : "todo";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>布尔 → Visibility（参数 "invert" 取反）。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility vis && vis == Visibility.Visible;
}

/// <summary>非空字符串/非空对象 → Visibility。</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            _ => true,
        };
        if (parameter as string == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Diff 行背景/前景（+ 绿、- 红、空格默认）。</summary>
public sealed class DiffLineBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var kind = value as string;
        return kind switch
        {
            "added" => new SolidColorBrush(Color.FromArgb(0x33, 0x5B, 0xC1, 0x7F)),
            "removed" => new SolidColorBrush(Color.FromArgb(0x33, 0xE5, 0x48, 0x4D)),
            _ => Brushes.Transparent,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>把布尔取反（用于"未选中任何项"之类的界面状态）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;
}

/// <summary>
/// 导航按钮高亮：比较按钮的 CommandParameter（Tag）与当前页面。
/// 相等返回 "active"（触发 DataTrigger），否则返回空串。
/// </summary>
public sealed class NavStateConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return string.Empty;
        var tag = values[0] as string;
        var current = values[1] as string;
        return !string.IsNullOrEmpty(tag) && string.Equals(tag, current, StringComparison.Ordinal)
            ? "active"
            : string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
