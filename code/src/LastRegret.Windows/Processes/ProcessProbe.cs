using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LastRegret.Core.Abstractions;
using LastRegret.Core.Model;
using LastRegret.Windows.Native;

namespace LastRegret.Windows.Processes;

/// <summary>
/// 进程探测：为每一次文件变化提供「关联进程（可能来源）」。
///
/// ══════════════════════════════════════════════════════════════════════
/// 能力边界（本机实测，2026-09-11，绝不含糊其辞）：
///
///  ✅ 可用：枚举全系统内核句柄（NtQuerySystemInformation），
///          并能把一个**自己进程持有的**文件句柄解析成设备路径；
///  ❌ 不可用：跨进程 DuplicateHandle —— 实测 281 个进程中只有 2 个能成功
///          （PROCESS_DUP_HANDLE 被拒绝），因此**无法**可靠地判断
///          "这个文件此刻被哪个进程打开着"。
///
/// 结论：本系统**不声称**能确定"文件是被哪个进程修改的"，
///       只提供"该时刻附近有哪些进程在活动"这一**关联事实**，
///       并按置信度如实标注（Nearby / Likely）。
///       这直接落实产品原则：记录事实，不伪造因果。
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class ProcessProbe : IProcessProbe
{
    /// <summary>能够主动产生文件变化的常见进程（用于"可能来源"排序，仅作提示）。</summary>
    private static readonly Dictionary<string, string> KnownActors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code.exe"] = "VS Code",
        ["code - insiders.exe"] = "VS Code Insiders",
        ["devenv.exe"] = "Visual Studio",
        ["rider64.exe"] = "JetBrains Rider",
        ["idea64.exe"] = "IntelliJ IDEA",
        ["pycharm64.exe"] = "PyCharm",
        ["webstorm64.exe"] = "WebStorm",
        ["goland64.exe"] = "GoLand",
        ["explorer.exe"] = "文件资源管理器",
        ["cmd.exe"] = "命令提示符",
        ["powershell.exe"] = "Windows PowerShell",
        ["pwsh.exe"] = "PowerShell 7",
        ["windowsterminal.exe"] = "Windows Terminal",
        ["wt.exe"] = "Windows Terminal",
        ["conhost.exe"] = "控制台宿主",
        ["git.exe"] = "Git",
        ["node.exe"] = "Node.js",
        ["python.exe"] = "Python",
        ["python3.exe"] = "Python",
        ["dotnet.exe"] = "dotnet CLI",
        ["msbuild.exe"] = "MSBuild",
        ["winword.exe"] = "Microsoft Word",
        ["excel.exe"] = "Microsoft Excel",
        ["powerpnt.exe"] = "Microsoft PowerPoint",
        ["notepad.exe"] = "记事本",
        ["notepad++.exe"] = "Notepad++",
        ["sublime_text.exe"] = "Sublime Text",
        ["vim.exe"] = "Vim",
        ["nvim.exe"] = "Neovim",
        ["7zfm.exe"] = "7-Zip",
        ["winrar.exe"] = "WinRAR",
        ["robocopy.exe"] = "Robocopy",
        ["curl.exe"] = "curl",
    };

    private readonly object _gate = new();
    private IReadOnlyList<ProcessRecord> _cache = Array.Empty<ProcessRecord>();
    private DateTime _cacheUtc = DateTime.MinValue;
    private AttributionCapability? _capability;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(2);

    public IReadOnlyList<ProcessRecord> Snapshot()
    {
        lock (_gate)
        {
            if ((DateTime.UtcNow - _cacheUtc) < _cacheTtl && _cache.Count > 0) return _cache;

            var list = new List<ProcessRecord>();
            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch (Exception)
            {
                return _cache;
            }

            foreach (var p in processes)
            {
                try
                {
                    var record = new ProcessRecord
                    {
                        Pid = p.Id,
                        ProcessName = SafeName(p),
                        LastSeenUtc = DateTime.UtcNow,
                    };

                    // 启动时间只能通过受权限控制的路径取得：取不到就留空（不猜）
                    try
                    {
                        record.StartTimeUtc = p.StartTime.ToUniversalTime();
                    }
                    catch (Exception)
                    {
                        record.StartTimeUtc = null; // 权限不足：如实留空
                    }

                    try
                    {
                        record.ExecutablePath = p.MainModule?.FileName;
                    }
                    catch (Exception)
                    {
                        record.ExecutablePath = null;
                    }

                    list.Add(record);
                }
                catch (Exception)
                {
                    // 单个进程读取失败不影响整体
                }
                finally
                {
                    p.Dispose();
                }
            }

            _cache = list;
            _cacheUtc = DateTime.UtcNow;
            return list;
        }
    }

    private static string SafeName(Process p)
    {
        try
        {
            return p.ProcessName + ".exe";
        }
        catch (Exception)
        {
            return $"pid-{p.Id}";
        }
    }

    /// <summary>前台窗口所属进程：这是**最强但有限**的证据。</summary>
    public ProcessAttribution? GetForegroundProcess()
    {
        try
        {
            var hwnd = Win32.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;

            string title = string.Empty;
            int len = Win32.GetWindowTextLengthW(hwnd);
            if (len > 0)
            {
                var sb = new StringBuilder(len + 2);
                Win32.GetWindowTextW(hwnd, sb, sb.Capacity);
                title = sb.ToString();
            }

            string name = string.Empty;
            string? exePath = null;
            DateTime? startUtc = null;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                name = SafeName(p);
                try { exePath = p.MainModule?.FileName; } catch (Exception) { }
                try { startUtc = p.StartTime.ToUniversalTime(); } catch (Exception) { }
            }
            catch (ArgumentException)
            {
                // 进程已退出：仍然保留 PID 事实，但不编造名字
            }

            return new ProcessAttribution
            {
                Pid = (int)pid,
                ProcessName = name,
                ExecutablePath = exePath,
                StartTimeUtc = startUtc,
                WindowTitle = title,
                Confidence = AttributionConfidence.Likely,
                Basis = "事件时刻的前台窗口进程",
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 尽力而为的句柄归属探测。
    ///
    /// 本机实测几乎总是返回 null（跨进程 DuplicateHandle 被系统拒绝）。
    /// 保留这个能力是为了：在**权限允许**的环境下自动升级到内核级证据，
    /// 而不是为了让日志看起来好看。
    /// </summary>
    public ProcessAttribution? TryFindHandleOwner(string absolutePath, IReadOnlyList<ProcessAttribution> candidates)
    {
        // ── 先看本机能力自检的结论（启动时实测并缓存，不额外开销）──
        // 如果这台机器上**一个**进程都不允许打开句柄，那无论枚举多少次都拿不到内核级证据
        // —— 直接跳过，不再为每个事件做一次全系统句柄枚举（这不是降低能力，
        // 而是不做注定失败的工作；只要还有进程可能被打开，就照常尝试）。
        var capability = GetCapability();
        if (!capability.HandleEnumerationAvailable || capability.ProcessesOpenable == 0) return null;

        var fileTypeIndex = SystemHandleEnumerator.FindFileTypeIndex();
        if (fileTypeIndex < 0) return null;
        _ = SystemHandleEnumerator.TryCapture(out _);   // 预热缓存（下面按进程号取句柄时直接用）

        var normalizedTarget = absolutePath.TrimEnd('\\');

        // 每次调用最多尝试解析几个句柄：解析对象名最坏 1.2 秒，而这里本来就在
        // "尽力而为"地找内核级证据。不设上限的话，一次批量事件里每个事件都可能赔上好几秒。
        const int maxNameQueries = 3;
        var nameQueries = 0;

        foreach (var candidate in candidates)
        {
            // 只看**这个候选进程自己**的文件句柄（旧写法是"候选 × 全部系统句柄"的双层循环，
            // 二十多万条句柄 × 每个事件都要扫一遍，是本次性能事故的主要开销之一）。
            foreach (var entry in SystemHandleEnumerator.FileHandlesOf(candidate.Pid, (ushort)fileTypeIndex))
            {
                if (entry.ProcessId != candidate.Pid || entry.ObjectTypeIndex != fileTypeIndex) continue;

                if (nameQueries++ >= maxNameQueries) return null;

                var processHandle = Win32.OpenProcess(Win32.PROCESS_DUP_HANDLE, false, candidate.Pid);
                if (processHandle == IntPtr.Zero) return null; // 权限不足：立刻放弃，不做无意义重试

                try
                {
                    if (!Win32.DuplicateHandle(processHandle, entry.HandleValue, Win32.GetCurrentProcess(),
                            out var local, 0, false, Win32.DUPLICATE_SAME_ACCESS))
                    {
                        return null;
                    }

                    try
                    {
                        var name = SystemHandleEnumerator.QueryObjectName(local);
                        if (name is not null &&
                            name.EndsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            return new ProcessAttribution
                            {
                                Pid = candidate.Pid,
                                ProcessName = candidate.ProcessName,
                                ExecutablePath = candidate.ExecutablePath,
                                StartTimeUtc = candidate.StartTimeUtc,
                                Confidence = AttributionConfidence.Handler,
                                Basis = "内核文件句柄持有者",
                            };
                        }
                    }
                    finally
                    {
                        Win32.CloseHandle(local);
                    }
                }
                finally
                {
                    Win32.CloseHandle(processHandle);
                }
            }
        }

        return null;
    }

    /// <summary>为本机能力做一次实测并缓存结论（UI 会如实展示）。</summary>
    public AttributionCapability GetCapability()
    {
        lock (_gate)
        {
            if (_capability is not null) return _capability;

            bool handleEnum = SystemHandleEnumerator.TryCapture(out var entries);
            int probed = 0, openable = 0;

            if (handleEnum)
            {
                var pids = new HashSet<int>();
                foreach (var e in entries)
                {
                    if (e.ProcessId > 4) pids.Add((int)e.ProcessId);
                }
                probed = pids.Count;

                foreach (var pid in pids)
                {
                    var h = Win32.OpenProcess(Win32.PROCESS_DUP_HANDLE, false, pid);
                    if (h != IntPtr.Zero)
                    {
                        openable++;
                        Win32.CloseHandle(h);
                    }
                }
            }

            _capability = new AttributionCapability
            {
                HandleEnumerationAvailable = handleEnum,
                CrossProcessDupAvailable = openable > 0 && openable >= probed / 4,
                ProcessesProbed = probed,
                ProcessesOpenable = openable,
                Summary = BuildCapabilitySummary(handleEnum, probed, openable),
            };
            return _capability;
        }
    }

    private static string BuildCapabilitySummary(bool handleEnum, int probed, int openable)
    {
        if (!handleEnum)
            return "无法枚举系统句柄：本次只能提供「关联进程」信息。";

        if (openable == 0)
            return $"已枚举系统句柄，但 {probed} 个进程中 0 个允许打开句柄（权限限制）。" +
                   "因此本程序只能提供「关联进程 / 可能来源」，无法确定具体是哪个进程。";

        return $"已枚举系统句柄，{probed} 个进程中有 {openable} 个允许打开句柄。" +
               "对允许打开的进程会尝试给出内核级证据，其余仍只提供「关联进程」。";
    }

    // ─────────────────────────────────────────────────────────────────────
    // 归属推断（只做"关联"，不做"因果"）
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 为一个事件时刻挑选"附近活跃的候选进程"。
    /// 返回的每一条都带 <see cref="ProcessAttribution.Basis"/>，说明它凭什么被列为候选。
    /// </summary>
    public IReadOnlyList<ProcessAttribution> FindNearbyCandidates(DateTime eventUtc, int windowMs, int maxResults = 5)
    {
        var result = new List<ProcessAttribution>();
        var foreground = GetForegroundProcess();
        if (foreground is not null) result.Add(foreground);

        var processes = Snapshot();
        var window = TimeSpan.FromMilliseconds(Math.Max(200, windowMs));

        var others = new List<ProcessAttribution>();
        foreach (var p in processes)
        {
            if (foreground is not null && p.Pid == foreground.Pid) continue;

            // 只把"在事件时间窗口内启动或仍在运行"的已知操作者列为候选
            bool startedRecently = p.StartTimeUtc is not null &&
                                   (eventUtc - p.StartTimeUtc.Value) <= window &&
                                   p.StartTimeUtc.Value <= eventUtc.Add(window);

            bool known = KnownActors.ContainsKey(p.ProcessName);
            if (!known && !startedRecently) continue;

            others.Add(new ProcessAttribution
            {
                Pid = p.Pid,
                ProcessName = p.ProcessName,
                ExecutablePath = p.ExecutablePath,
                StartTimeUtc = p.StartTimeUtc,
                Confidence = AttributionConfidence.Nearby,
                Basis = startedRecently
                    ? "事件时刻前后启动的进程"
                    : "常见的文件操作类进程（当前正在运行）",
            });
        }

        // 已知操作者优先
        others.Sort((a, b) => ActorRank(a.ProcessName).CompareTo(ActorRank(b.ProcessName)));
        result.AddRange(others);

        return result.Take(maxResults).ToList();
    }

    private static int ActorRank(string processName)
    {
        if (KnownActors.TryGetValue(processName, out _)) return 0;
        return 1;
    }

    /// <summary>把进程名翻译成用户熟悉的名称（例如 Code.exe → VS Code）。</summary>
    public static string DescribeProcess(string processName) =>
        KnownActors.TryGetValue(processName, out var friendly) ? $"{friendly}" : processName;
}
