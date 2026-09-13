namespace LastRegret.App;

/// <summary>
/// 崩溃日志的**薄转发层**（WPF 壳的一部分，不属于 Runtime）。
///
/// 实现已经搬到 <see cref="LastRegret.Runtime.CrashLog"/>，这里只是保留
/// <c>App.WriteCrashLog(...)</c> / <c>App.CrashLogPath()</c> 这两个原有成员名，
/// 让壳内的既有调用点（App.xaml.cs、MainWindow.xaml.cs）不必改写。
///
/// 为什么不把这些调用点直接改成 CrashLog.Write：那要动多处调用点，
/// 而这次重构的目标是"移动位置、不改行为"。留一行转发是最小改动。
///
/// 注意：这里必须写完全限定名 —— 本类有个成员叫 <c>Runtime</c>
/// （指向 AppRuntime 实例），它会遮蔽同名 namespace。
/// </summary>
public partial class App
{
    /// <summary>崩溃日志路径（与数据目录同级，方便用户找到）。</summary>
    public static string CrashLogPath() => LastRegret.Runtime.CrashLog.Path();

    internal static void WriteCrashLog(string source, Exception ex) =>
        LastRegret.Runtime.CrashLog.Write(source, ex);
}
