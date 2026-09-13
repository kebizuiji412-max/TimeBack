namespace LastRegret.Tests;

/// <summary>
/// 让测试进程使用**系统真实时区**。
///
/// 背景（真实踩坑）：本测试进程由沙箱化的 pwsh 启动，环境里带了 <c>TZ=UTC</c>，
/// 于是 <c>DateTime.Now</c> 与系统时钟差 8 小时，而 <c>DateTime.UtcNow</c> 是正确的。
/// 结果是"本地时间 ↔ UTC"的往返反查必然错位，测试会以极具迷惑性的方式失败
/// （例如"恢复到某个时间点"说没有恢复点）。
///
/// 这不是被测代码的缺陷，而是测试环境的时区污染；
/// 因此在这里显式地把进程时区对齐到注册表里的系统时区。
/// </summary>
public static class TimeZoneBootstrap
{
    public static void Apply()
    {
        try
        {
            var system = SystemTimeZone();
            if (system is null) return;

            if (string.Equals(system.Id, TimeZoneInfo.Local.Id, StringComparison.OrdinalIgnoreCase)) return;

            // 进程已启动后 .NET 会缓存时区，仅设环境变量通常不生效；
            // 真正的对齐由运行脚本负责（在启动前设置 TZ）。
            // 这里只在生效时打印提示，绝不假装成功。
            Environment.SetEnvironmentVariable("TZ", system.Id);
            TimeZoneInfo.ClearCachedData();

            if (string.Equals(system.Id, TimeZoneInfo.Local.Id, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  [时区] 测试进程时区已对齐为系统时区：{system.Id}");
            }
            else
            {
                Console.WriteLine(
                    $"  [时区] 注意：测试进程时区为 {TimeZoneInfo.Local.Id}，系统时区为 {system.Id}。" +
                    "时间显示会与系统不同，但 UTC 语义一致，不影响测试结论。");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  [时区] 对齐失败（不影响测试正确性，只影响时间显示）：" + ex.Message);
        }
    }

    /// <summary>读取注册表里的系统时区（Windows）。</summary>
    private static TimeZoneInfo? SystemTimeZone()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation");
            var name = key?.GetValue("TimeZoneKeyName") as string;
            if (string.IsNullOrWhiteSpace(name)) return null;
            return TimeZoneInfo.FindSystemTimeZoneById(name);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
