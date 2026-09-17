using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using LastRegret.Core;
using LastRegret.Core.Abstractions;
using LastRegret.Core.Compare;
using LastRegret.Core.Config;
using LastRegret.Core.Model;
using LastRegret.Core.Restore;
using LastRegret.Core.Util;
using LastRegret.Engine;
using LastRegret.Runtime;
using LastRegret.Windows.Io;
using LastRetretApp = LastRegret.App;

namespace LastRegret.App;

/// <summary>主视图模型：把引擎的事实翻译成界面能展示的行。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppRuntime _rt;

    /// <summary>
    /// 恢复流程协调器：预览 / 自定义恢复集合合并 / 执行 / 撤销 / 恢复记录都在它里面。
    /// 本视图模型不再直接调用 RestoreEngine —— 只负责把结果变成界面能显示的东西。
    /// </summary>
    private readonly RestoreCoordinator _restoreFlow;

    /// <summary>
    /// 保护范围协调器：登记/移除/暂停/重新扫描/基线扫描与扫描作用域都在它里面。
    /// 本视图模型不再直接调用 WatchService（含它的引擎事件订阅）。
    /// </summary>
    private readonly ProtectionCoordinator _protection;

    /// <summary>
    /// 历史协调器：变化记录 / 历史版本 / 内容差异的读取都在它里面。
    /// 本视图模型只负责把这些模型变成界面行。
    /// </summary>
    private readonly HistoryCoordinator _history;

    private string _activePage = "home";
    private string _statusText = "正在初始化…";

    /// <summary>设置页里"高级设置"是否展开（默认折叠 —— 普通用户不该先看到它）。</summary>
    private bool _advancedSettingsOpen;

    /// <summary>恢复流程当前走到第几步：1 选时间点 / 2 看预览 / 3 确认执行 / 4 已完成。</summary>
    private long _restoreStepSnapshotId;
    private string _lastRestoreDoneText = string.Empty;
    private bool _lastRestoreCanUndo;

    /// <summary>
    /// 要删除的路径集合（相对路径）—— **自定义删除**。
    ///
    /// 为什么必须单独一套：勾选表达的是"从这个时间点把它恢复回来"，
    /// 而"我要删掉它"是另一种意图。尤其当这个文件在他选的时间点里**存在**时，
    /// "恢复到那个时间点"根本表达不出删除（恢复到那时只会把它还原成那时的内容）。
    /// </summary>
    private readonly HashSet<string> _deletionPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>被标记删除的数量（底部与确认框都会显示）。</summary>
    public int DeletionCount => _deletionPaths.Count;

    public bool HasDeletions => _deletionPaths.Count > 0;

    private RootRow? _selectedRoot;

    private EventRow? _selectedEvent;
    private FileRow? _selectedFile;
    private VersionRow? _selectedVersionA;
    private VersionRow? _selectedVersionB;

    private string _timelineFilter = string.Empty;
    private bool _showTransient;
    private int _timelineDays = 1;
    private bool _timelineDirty = true;

    private string _fileSearch = string.Empty;
    private string _diffSummary = string.Empty;
    private string _diffMeta = string.Empty;
    private bool _diffReversed;

    private string _storageSummary = string.Empty;
    private string _cleanupPreview = string.Empty;

    public MainViewModel(AppRuntime runtime)
    {
        _rt = runtime;
        _restoreFlow = new RestoreCoordinator(runtime.Application, runtime.Restore, runtime.SnapshotService);
        _protection = new ProtectionCoordinator(runtime.Watch, runtime.Roots);
        _history = new HistoryCoordinator(runtime.Application, runtime.Events, runtime.Versions, runtime.Compare);
        UiDispatch.Initialize(System.Windows.Application.Current.Dispatcher);

        NavigateCommand = new DelegateCommand(p => Navigate(p as string ?? "timeline"));
        RefreshCommand = new DelegateCommand(() => RefreshAll());
        CreateSnapshotCommand = new DelegateCommand(CreateSnapshotNow, () => _selectedRoot is not null);
        UndoLastRestoreCommand = new DelegateCommand(UndoLastRestore);
        OpenFileDialogCommand = new DelegateCommand(AddRootViaDialog);
        AddRootCommand = new DelegateCommand(p => AddRootFromPath(p as string), p => !string.IsNullOrWhiteSpace(p as string));
        RemoveRootCommand = new DelegateCommand(ToggleOrRemoveRoot);
        RemoveRootRowCommand = new DelegateCommand(p => RemoveRootRow(p as RootRow), p => p is RootRow);
        PauseRootCommand = new DelegateCommand(TogglePauseRoot, () => _selectedRoot is not null);
        RescanRootCommand = new DelegateCommand(RescanRoot, () => _selectedRoot is not null);
        CancelScanCommand = new DelegateCommand(CancelScan, () => _selectedRoot is not null && _protection.IsScanning(_selectedRoot.Id));
        RefreshHistoryCommand = new DelegateCommand(() => { RefreshRestoreHistory(); ReloadPoints(); });
        SaveSettingsCommand = new DelegateCommand(SaveSettings);
        PlanCleanupCommand = new DelegateCommand(PlanCleanup);
        ApplyCleanupCommand = new DelegateCommand(ApplyCleanup, () => _cleanupPlan is not null);
        CheckIntegrityCommand = new DelegateCommand(CheckIntegrity);
        ShowFileCommand = new DelegateCommand(p => ShowFile(p as FileRow), p => p is FileRow);
        SwapDiffCommand = new DelegateCommand(() => { _diffReversed = !_diffReversed; UpdateDiff(); });
        DismissErrorCommand = new DelegateCommand(() => { LastErrorBanner = null; });

        // 首页动作
        GoHistoryCommand = new DelegateCommand(() => Navigate("history"));
        GoRestoreCommand = new DelegateCommand(() => Navigate("restore"));
        GoHomeCommand = new DelegateCommand(() => Navigate("home"));
        ShowAdvancedSettingsCommand = new DelegateCommand(() => AdvancedSettingsOpen = true);
        HideAdvancedSettingsCommand = new DelegateCommand(() => AdvancedSettingsOpen = false);
        PickFolderCommand = new DelegateCommand(AddRootViaDialog);

        // 恢复面板（紧凑控制面板）
        SelectAllRestoreFilesCommand = new DelegateCommand(SelectAllRestoreFiles);
        ClearRestoreSelectionCommand = new DelegateCommand(ClearRestoreSelection);
        InvertRestoreSelectionCommand = new DelegateCommand(InvertRestoreSelection);
        SwapRestoreEndsCommand = new DelegateCommand(SwapRestoreEnds);
        UseCurrentStateAsTargetCommand = new DelegateCommand(UseCurrentStateAsTarget);
        ConfirmRestoreFromPanelCommand = new DelegateCommand(ConfirmRestoreFromPanel, () => CanConfirmRestore);
        // 迷你文件浏览器
        OpenBrowseRowCommand = new DelegateCommand(p => OpenBrowseRow(p as RestoreBrowseRow), p => p is RestoreBrowseRow);
        GoUpDirectoryCommand = new DelegateCommand(GoUpDirectory, () => CanGoUp);
        GoRootDirectoryCommand = new DelegateCommand(GoRootDirectory);
        TogglePickedListCommand = new DelegateCommand(TogglePickedList);
        RemovePickedCommand = new DelegateCommand(p => RemovePicked(p as RestorePickedRow), p => p is RestorePickedRow);
        ClearAllPickedCommand = new DelegateCommand(ClearAllPicked);
        // 自定义删除
        MarkDeletionCommand = new DelegateCommand(MarkSelectedForDeletion);
        UnmarkDeletionCommand = new DelegateCommand(UnmarkDeletion);
        ClearAllDeletionsCommand = new DelegateCommand(ClearAllDeletions);
        // 历史记录清理 + 存储位置
        DeleteSelectedEventCommand = new DelegateCommand(DeleteSelectedEvent, () => SelectedEvent is not null);
        ClearAllEventsCommand = new DelegateCommand(ClearAllEvents);
        PurgeAllHistoryCommand = new DelegateCommand(PurgeAllHistory);
        PickStorageFolderCommand = new DelegateCommand(PickStorageFolder);
        ResetStorageFolderCommand = new DelegateCommand(ResetStorageFolder);
        OpenStorageFolderCommand = new DelegateCommand(OpenStorageFolder);
        OpenLogFolderCommand = new DelegateCommand(OpenLogFolder);
        ClearAllDataCommand = new DelegateCommand(ClearAllData);
        ShowLogCommand = new DelegateCommand(() => Navigate("log"));

        _protection.TimelineChanged += () => { _timelineDirty = true; Raise(nameof(StatusText)); };
        _protection.Logged += entry => UiDispatch.Invoke(() =>
        {
            Logs.Insert(0, new LogRow { Utc = entry.Utc, Level = entry.Level, Message = entry.Message });
            while (Logs.Count > 200) Logs.RemoveAt(Logs.Count - 1);
            LastErrorBanner = entry.Level == "error" ? entry.Message : LastErrorBanner;
        });

        LoadRoots();
        LoadSettingsIntoForm();
        ReloadTimeline();

        StatusText = BuildStatus();
        TimelineIsEmpty = Events.Count == 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 集合
    // ─────────────────────────────────────────────────────────────────────

    public ObservableCollection<RootRow> Roots { get; } = new();
    public ObservableCollection<EventRow> Events { get; } = new();
    public ObservableCollection<FileRow> Files { get; } = new();
    public ObservableCollection<VersionRow> Versions { get; } = new();
    public ObservableCollection<DiffRow> DiffRows { get; } = new();
    public ObservableCollection<LogRow> Logs { get; } = new();
    public ObservableCollection<RestoreRow> RestoreHistory { get; } = new();
    public ObservableCollection<string> StartupNotes { get; } = new();

    // ─────────────────────────────────────────────────────────────────────
    // 页面与状态
    // ─────────────────────────────────────────────────────────────────────

    public string ActivePage
    {
        get => _activePage;
        private set
        {
            if (!Set(ref _activePage, value)) return;
            Raise(nameof(IsHomePage));
            Raise(nameof(IsHistoryPage));
            Raise(nameof(IsTimelinePage));
            Raise(nameof(IsFilesPage));
            Raise(nameof(IsRestorePage));
            Raise(nameof(IsSettingsPage));
            Raise(nameof(IsLogPage));
            Raise(nameof(ShowSidebarFolderPrompt));
            Raise(nameof(StatusBarLeftText));
        }
    }

    /// <summary>
    /// 左侧栏底部那个"还没有保护任何文件夹"的小卡片。
    /// 首页本身就在引导选文件夹，这时不必再重复一遍。
    /// </summary>
    public bool ShowSidebarFolderPrompt => !HasRoots && !IsHomePage;

    /// <summary>状态栏左边那句话：没有目录时不要重复第③遍"还没有保护任何文件夹"。</summary>
    public string StatusBarLeftText => Roots.Count == 0
        ? "先在首页选择一个文件夹，之后它的变化才会被记录。"
        : StatusText;

    /// <summary>"首页"：现在正在保护什么 + 最近发生了什么 + 出问题怎么办。</summary>
    public bool IsHomePage => _activePage == "home";

    /// <summary>"历史"：原来的时间线（普通用户叫它"历史"）。</summary>
    public bool IsHistoryPage => _activePage == "history";

    /// <summary>兼容旧名：历史页就是原来的时间线页。</summary>
    public bool IsTimelinePage => _activePage == "history";

    /// <summary>单个文件的历史与内容对比。**不是主导航入口**，只从历史里点进来。</summary>
    public bool IsFilesPage => _activePage == "fileDetail";

    public bool IsRestorePage => _activePage == "restore";
    public bool IsSettingsPage => _activePage == "settings";

    /// <summary>运行日志：只在「设置 → 高级设置」里进入，不占主导航。</summary>
    public bool IsLogPage => _activePage == "log";

    /// <summary>高级设置是否展开（默认折叠）。</summary>
    public bool AdvancedSettingsOpen
    {
        get => _advancedSettingsOpen;
        set
        {
            if (!Set(ref _advancedSettingsOpen, value)) return;
            Raise(nameof(BasicSettingsOnly));
        }
    }

    public bool BasicSettingsOnly => !_advancedSettingsOpen;

    // ── 普通设置：保留多久 / 最多占多少，只给常用档位，不让用户填数字 ──

    private const int DefaultRetentionDays = 30;

    /// <summary>保留 7 天？</summary>
    public bool RetentionIs7
    {
        get => SettingRetentionDays == 7;
        set { if (value) SetRetentionDays(7); }
    }

    /// <summary>保留 30 天（默认）？</summary>
    public bool RetentionIs30
    {
        get => SettingRetentionDays == DefaultRetentionDays;
        set { if (value) SetRetentionDays(DefaultRetentionDays); }
    }

    /// <summary>保留 90 天？</summary>
    public bool RetentionIs90
    {
        get => SettingRetentionDays == 90;
        set { if (value) SetRetentionDays(90); }
    }

    /// <summary>当前档位以外的手填值（高级设置里改过）——普通设置里要如实说一句。</summary>
    public bool RetentionIsCustom => !RetentionIs7 && !RetentionIs30 && !RetentionIs90;

    public string RetentionCustomText => $"当前是自定义的 {SettingRetentionDays} 天（在高级设置里改的）。";

    private void SetRetentionDays(int days)
    {
        if (SettingRetentionDays == days) return;
        SettingRetentionDays = days;
        RaiseRetentionFlags();
    }

    private void RaiseRetentionFlags()
    {
        Raise(nameof(RetentionIs7));
        Raise(nameof(RetentionIs30));
        Raise(nameof(RetentionIs90));
        Raise(nameof(RetentionIsCustom));
        Raise(nameof(RetentionCustomText));
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            // 状态栏那一行绑的是 StatusBarLeftText（它没有目录时会换一句引导语），
            // 所以 StatusText 变了必须把它一起通知出去。
            //
            // 为什么这是必须的（真实缺陷，Release 黑盒压测发现）：
            //   StatusBarLeftText 是派生属性，原来只在 LoadRoots() 里 Raise 一次，
            //   而且那一次 Raise 发生在 StatusText 被赋值**之前**。于是状态栏永远比
            //   真实状态慢一步 —— 点"暂停"后状态栏还写"正在保护"，点"恢复"后反而
            //   写"已暂停保护"，而且再也不会自己纠正（每 2 秒的定时刷新只改 StatusText，
            //   通知不到状态栏）。
            if (!Set(ref _statusText, value)) return;
            Raise(nameof(StatusBarLeftText));
        }
    }

    private string? _lastErrorBanner;

    public string? LastErrorBanner
    {
        get => _lastErrorBanner;
        private set { if (Set(ref _lastErrorBanner, value)) Raise(nameof(HasErrorBanner)); }
    }

    public bool HasErrorBanner => !string.IsNullOrWhiteSpace(_lastErrorBanner);

    public bool TimelineIsEmpty { get; private set; }

    // ─────────────────────────────────────────────────────────────────────
    // 受保护范围
    // ─────────────────────────────────────────────────────────────────────

    public RootRow? SelectedRoot
    {
        get => _selectedRoot;
        set
        {
            if (!Set(ref _selectedRoot, value)) return;
            _timelineDirty = true;
            ReloadTimeline();
            ReloadPoints();
            LoadFiles();
            RefreshRestoreHistory();
            Raise(nameof(SelectedRootPath));
        }
    }

    public string SelectedRootPath => _selectedRoot?.Path ?? "（未选择受保护范围）";

    private void LoadRoots()
    {
        Roots.Clear();
        foreach (var root in _protection.ListRoots())
        {
            Roots.Add(new RootRow
            {
                Root = root,
                Enabled = root.Enabled,
                Watching = _protection.GetState(root.Id).Watching,
                LastEventUtc = root.LastEventUtc,
            });
        }

        RefreshRootStats();

        if (_selectedRoot is null && Roots.Count > 0) SelectedRoot = Roots[0];
        else if (Roots.Count == 0) SelectedRoot = null;

        Raise(nameof(HasRoots));
        Raise(nameof(HasMultipleRoots));
        Raise(nameof(SelectedRootStateText));
        Raise(nameof(ShowSidebarFolderPrompt));
        Raise(nameof(StatusBarLeftText));
        ReloadHome();
    }

    public bool HasRoots => Roots.Count > 0;

    /// <summary>只有一个受保护目录时不必给用户一个下拉框。</summary>
    public bool HasMultipleRoots => Roots.Count > 1;

    /// <summary>左侧栏里"这个文件夹现在是什么状态"。</summary>
    public string SelectedRootStateText => _selectedRoot?.ProtectStateText ?? string.Empty;

    private void RefreshRootStats()
    {
        foreach (var row in Roots)
        {
            try
            {
                var (_, _, last24) = _history.GetEventStatistics(row.Id);
                var root = _protection.GetRoot(row.Id);
                var state = _protection.GetState(row.Id);
                var health = _protection.DescribeRootHealth(row.Id);

                row.RefreshStats(last24, root?.LastEventUtc);
                row.Watching = state.Watching;
                row.Scanning = state.Scanning;
                row.ScanNote = state.ScanNote;
                row.LastError = state.LastError;
                row.Enabled = root?.Enabled ?? false;
                row.HasBaseline = health.HasBaseline;

                if (!row.HasBaseline && row.Scanning)
                {
                    row.ScanNote = state.ScanNote ?? "正在准备保护…";
                }
            }
            catch (Exception ex)
            {
                row.LastError = ex.Message;
            }
        }
        Raise(nameof(SelectedRootStateText));
        Raise(nameof(HomeRootPath));
        Raise(nameof(HomeProtectState));
        Raise(nameof(HomeProtectHealthy));
        Raise(nameof(HomePreparingText));
        Raise(nameof(HomeIsPreparing));
        Raise(nameof(HomeIsReady));
    }

    private void AddRootViaDialog()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择要保护的文件夹",
                Multiselect = false,
            };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                AddRootFromPath(dialog.FolderName);
            }
        }
        catch (Exception ex)
        {
            LastErrorBanner = "打开文件夹选择框失败：" + ex.Message;
        }
    }

    private void AddRootFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // 第一步：登记 + 立即开始监听（很快，不阻塞界面）
        var (ok, rootId, message) = _protection.RegisterRoot(path);
        if (!ok)
        {
            LastErrorBanner = message;
            return;
        }

        LoadRoots();
        SelectedRoot = Roots.FirstOrDefault(r => r.Id == rootId) ?? _selectedRoot;
        LastErrorBanner = null;
        SetStatusNote(message, 12);

        // 第二步：先评估目录规模（只枚举、不读内容，很快），再让用户决定是否开始扫描。
        // ⚠ 关键修复（真实缺陷 PIT-076/PIT-077）：
        //   ① 以前整段扫描跑在 UI 线程上，界面会被彻底堵死并显示"未响应"；
        //   ② 即使放到后台，对一个 39 万文件的目录也要接近一小时——
        //      这种事必须先告诉用户规模、预估耗时与占用，让他自己决定。
        EstimateThenStartBaseline(rootId);
    }

    /// <summary>先评估规模并征询用户，然后（可选地）开始后台基线扫描。</summary>
    private void EstimateThenStartBaseline(long rootId)
    {
        var row = Roots.FirstOrDefault(r => r.Id == rootId);
        if (row is not null) row.ScanNote = "正在评估目录规模…";
        SetStatusNote("正在评估目录规模…", 30);

        Task.Run(() =>
        {
            Rescanner.ScanEstimate? estimate = null;
            string? error = null;
            try
            {
                estimate = _protection.EstimateScanScope(rootId);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            UiDispatch.Invoke(() =>
            {
                if (row is not null) row.ScanNote = null;

                if (estimate is null)
                {
                    LastErrorBanner = "评估目录规模失败：" + (error ?? "未知原因") + "；将直接开始扫描。";
                    StartBaselineScan(rootId);
                    return;
                }

                if (estimate.Files == 0)
                {
                    SetStatusNote("该目录下（按当前排除规则）没有需要记录的文件。", 12);
                    StartBaselineScan(rootId, estimatedTotal: 0, skipConfirm: true);
                    return;
                }

                // 把它到底要花多久、占多少空间、哪些文件能恢复，原原本本告诉用户
                var cannotRestore = estimate.Mode == ProtectionMode.TrackOnly;
                var message =
                    "开始保护前的规模评估\n\n" +
                    estimate.Describe() + "\n\n" +
                    (cannotRestore
                        ? "⚠ 当前是「只记录变化」模式：能告诉你发生了什么变化，但不能把文件内容还原回去。\n" +
                          "　如需要内容恢复能力，请先到「设置」页改为「智能留存」或「完整内容」。\n\n"
                        : "扫描在后台进行，界面可正常使用，随时可点「取消扫描」。\n" +
                          "取消后已扫描的部分有效，点「重新扫描补齐」可继续（已扫描过的文件会跳过，更快）。\n\n") +
                    "现在开始准备保护吗？（选「否」= 目录已加入保护范围但暂不扫描，之后可手动补齐）";

                var answer = DangerBox.Show( message, "开始保护前的规模评估",
                    System.Windows.MessageBoxButton.YesNo);

                if (answer == System.Windows.MessageBoxResult.Yes)
                {
                    StartBaselineScan(rootId, estimate.Files + estimate.Directories);
                }
                else
                {
                    SetStatusNote("已加入保护范围但还没有准备保护：请在设置页点「重新扫描补齐」，否则这段时间的变化不会被记录。", 15);
                    RefreshAll();
                }
            });
        });
    }

    /// <summary>
    /// 启动时自动把"上次没扫完"的目录接着扫完。
    ///
    /// 为什么必须自动做：普通用户根本不知道"扫描没做完"意味着什么，
    /// 更不会想到要去设置页点一个按钮。让他面对一个"正在保护但其实什么都没记录"
    /// 的界面，等于软件在他眼里坏了。
    /// </summary>
    public void AutoFinishPendingScans()
    {
        IReadOnlyList<LastRegret.Core.Model.WatchedRoot> missing;
        try
        {
            missing = _protection.FindRootsMissingBaseline();
        }
        catch (Exception)
        {
            return;
        }

        foreach (var root in missing)
        {
            if (_protection.IsScanning(root.Id)) continue;
            StartBaselineScan(root.Id, 0, skipConfirm: true);
        }
    }

    /// <summary>启动（或重新启动）某个根的后台基线扫描。</summary>
    private void StartBaselineScan(long rootId, int estimatedTotal = 0, bool skipConfirm = false)
    {
        _ = skipConfirm;
        // 扫描作用域（含取消源）由保护协调器托管：已经开始就直接返回，不重复启动。
        if (!_protection.TryBeginScan(rootId, out var scanToken))
        {
            SetStatusNote("该目录正在准备保护，请等待完成，或点击「取消扫描」。", 30);
            return;
        }

        var row = Roots.FirstOrDefault(r => r.Id == rootId);
        if (row is not null)
        {
            row.Scanning = true;
            row.ScanNote = estimatedTotal > 0 ? $"正在扫描磁盘…共约 {estimatedTotal:N0} 项" : "正在扫描磁盘…";
        }

        SetStatusNote("正在准备保护（后台扫描，界面可继续操作，可随时取消）…", 30);
        CommandManager.InvalidateRequerySuggested();

        Task.Run(() =>
        {
            try
            {
                var (files, dirs) = _protection.RunBaseline(rootId, estimatedTotal, p =>
                {
                    UiDispatch.Invoke(() =>
                    {
                        if (row is null) return;
                        row.ScanNote = p.EstimatedTotal > 0 && p.EstimatedTotal >= p.Processed
                            ? $"正在扫描：{p.Processed:N0}/{p.EstimatedTotal:N0}（{p.Percent:0.#}%）"
                            : $"正在扫描：已处理 {p.Processed:N0} 项";
                        row.ScanTotal = p.EstimatedTotal;
                        row.ScanProgress = p.Processed;
                    });
                }, scanToken);

                UiDispatch.Invoke(() =>
                {
                    var r = Roots.FirstOrDefault(x => x.Id == rootId);
                    if (r is not null) { r.Scanning = false; r.ScanNote = null; r.ScanTotal = 0; r.ScanProgress = 0; }
                    LastErrorBanner = null;
                    SetStatusNote($"已准备好保护：{files} 个文件 / {dirs} 个目录。从现在起的变化都会被记录。", 10);
                    _timelineDirty = true;
                    RefreshAll();
                });
            }
            catch (OperationCanceledException)
            {
                UiDispatch.Invoke(() =>
                {
                    var r = Roots.FirstOrDefault(x => x.Id == rootId);
                    if (r is not null) { r.Scanning = false; r.ScanNote = null; r.ScanTotal = 0; r.ScanProgress = 0; }
                    SetStatusNote("准备保护已取消：已扫描的部分已保存，可随时点「重新扫描补齐」继续。", 12);
                    RefreshAll();
                });
            }
            catch (Exception ex)
            {
                UiDispatch.Invoke(() =>
                {
                    var r = Roots.FirstOrDefault(x => x.Id == rootId);
                    if (r is not null) { r.Scanning = false; r.ScanNote = null; r.ScanTotal = 0; r.ScanProgress = 0; r.LastError = ex.Message; }
                    LastErrorBanner = "准备保护失败：" + ex.Message;
                    RefreshAll();
                });
            }
            finally
            {
                _protection.EndScan(rootId);
                UiDispatch.Invoke(() => CommandManager.InvalidateRequerySuggested());
            }
        });
    }

    private void CancelScan()
    {
        if (_selectedRoot is null) return;
        _protection.CancelScan(_selectedRoot.Id);
        SetStatusNote("正在取消扫描…已扫描的部分会保存下来。", 8);
    }

    private void TogglePauseRoot()
    {
        if (_selectedRoot is null) return;
        try
        {
            if (_selectedRoot.Enabled)
            {
                _protection.SetEnabled(_selectedRoot.Id, false);
            }
            else
            {
                _protection.SetEnabled(_selectedRoot.Id, true);
            }
            LoadRoots();
            StatusText = BuildStatus();
        }
        catch (Exception ex)
        {
            LastErrorBanner = ex.Message;
        }
    }

    private void ToggleOrRemoveRoot() => RemoveRootCore(_selectedRoot, "移除受保护目录");

    /// <summary>
    /// 从"受保护文件夹"列表里直接移除某一个（不必先选中它）。
    ///
    /// 用户反馈过"只能添加不能删除" —— 原来的删除入口只藏在「高级设置」里，
    /// 而且只作用于"当前选中的那个"。所以这里接受具体的行，谁旁边点 ✕ 就删谁。
    /// </summary>
    private void RemoveRootRow(RootRow? row) => RemoveRootCore(row, "移除这个保护文件夹");

    private void RemoveRootCore(RootRow? target, string title)
    {
        if (target is null) return;
        var root = target;

        // 问清历史记录怎么处理：这是两个后果完全不同的选择，不能替用户决定
        var choice = DangerBox.Show(
            $"把下面这个文件夹移出保护范围？\n\n{root.Path}\n\n" +
            "· 磁盘上的文件不会被删除或修改。\n" +
            "· 选「是」= 连同它的历史记录一起删除（腾出空间，但以后回不到过去了）。\n" +
            "· 选「否」= 只移出保护范围，历史记录保留。\n" +
            "· 选「取消」= 什么都不做。",
            title,
            System.Windows.MessageBoxButton.YesNoCancel);

        if (choice == System.Windows.MessageBoxResult.Cancel) return;

        try
        {
            _protection.RemoveRoot(root.Id, deleteHistory: choice == System.Windows.MessageBoxResult.Yes);
            if (_selectedRoot?.Id == root.Id) _selectedRoot = null;
            ClearBrowseCaches();
            LoadRoots();
            ReloadTimeline();
            ReloadPoints();
            StatusText = BuildStatus();
        }
        catch (Exception ex)
        {
            LastErrorBanner = ex.Message;
        }
    }

    private void RescanRoot()
    {
        if (_selectedRoot is null) return;

        // 重新扫描 = 后台重扫 + 重新建立基线快照（补齐缺失的基线）。
        // 这是"首次扫描被中断"之后的自愈入口，因此必须真的把基线补上，
        // 而不只是请求一次对齐（旧实现只请求对齐，基线永远补不回来）。
        SetStatusNote("已开始重新扫描（后台执行，界面可继续操作）…", 30);
        StartBaselineScan(_selectedRoot.Id);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 时间线
    // ─────────────────────────────────────────────────────────────────────

    public string TimelineFilter
    {
        get => _timelineFilter;
        set { if (Set(ref _timelineFilter, value)) ReloadTimeline(); }
    }

    public bool ShowTransient
    {
        get => _showTransient;
        set { if (Set(ref _showTransient, value)) ReloadTimeline(); }
    }

    public int TimelineDays
    {
        get => _timelineDays;
        set { if (Set(ref _timelineDays, value <= 0 ? 1 : value)) ReloadTimeline(); }
    }

    public EventRow? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (!Set(ref _selectedEvent, value)) return;
            Raise(nameof(SelectedEventDetail));
            Raise(nameof(HasSelectedEvent));
        }
    }

    public bool HasSelectedEvent => _selectedEvent is not null;

    /// <summary>
    /// 这一条变化的详细情况。
    /// 先说人话（什么时候、干了什么、哪个文件、能不能恢复），
    /// 末尾才是给排查问题用的技术细节 —— 普通用户不看也不影响使用。
    /// </summary>
    public string SelectedEventDetail
    {
        get
        {
            if (_selectedEvent is null) return "在左边点一条变化，这里会告诉你它到底发生了什么。";
            var e = _selectedEvent.Event;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{e.TimestampLocal:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"{e.Operation.ToChinese()}{(e.Kind == EntryKind.Directory ? "文件夹" : "文件")}：{e.RelativePath}");
            if (e.OldRelativePath is { Length: > 0 }) sb.AppendLine($"它原来的位置：{e.OldRelativePath}");
            sb.AppendLine();
            if (e.SizeBefore is not null && e.SizeAfter is not null)
                sb.AppendLine($"大小：{PathUtil.FormatBytes(e.SizeBefore.Value)} → {PathUtil.FormatBytes(e.SizeAfter.Value)}");
            else if (e.SizeAfter is not null) sb.AppendLine($"大小：{PathUtil.FormatBytes(e.SizeAfter.Value)}");
            else if (e.SizeBefore is not null) sb.AppendLine($"原来的大小：{PathUtil.FormatBytes(e.SizeBefore.Value)}");
            sb.AppendLine($"能不能回到它变化前的样子：{_selectedEvent.RestoreHint}");
            if (e.Note is { Length: > 0 }) sb.AppendLine($"说明：{e.Note}");

            // ↓ 以下是排查问题时才有用的技术细节
            sb.AppendLine();
            sb.AppendLine("— 技术详情 —");
            if (e.AttributedProcess is not null)
                sb.AppendLine($"可能相关的程序：{_selectedEvent.ProcessText} [PID {e.AttributedPid}]");
            if (e.Attribution?.Basis is { Length: > 0 } basis) sb.AppendLine($"判断依据：{basis}");
            if (e.HashBefore is not null) sb.AppendLine($"变化前内容指纹：{Short(e.HashBefore)}");
            if (e.HashAfter is not null) sb.AppendLine($"变化后内容指纹：{Short(e.HashAfter)}");
            sb.AppendLine($"记录方式：{e.Source}");
            if (e.SuppressedCount > 0) sb.AppendLine($"已合并重复通知：{e.SuppressedCount} 次");
            if (e.AffectedDescendantCount > 0) sb.AppendLine($"同时影响的子项：{e.AffectedDescendantCount} 个");
            return sb.ToString().TrimEnd();
        }
    }

    private static string Short(string hash) => hash.Length >= 16 ? hash[..16] + "…" : hash;

    public void ReloadTimeline()
    {
        if (!_timelineDirty && Events.Count > 0) return;
        _timelineDirty = false;

        var rootId = _selectedRoot?.Id;
        var from = DateTime.UtcNow.AddDays(-_timelineDays);
        var query = new EventQuery
        {
            RootId = rootId,
            FromUtc = from,
            IncludeTransient = _showTransient,
            Limit = 600,
            Descending = true,
        };

        List<FileEvent> list;
        try
        {
            list = _history.QueryEvents(query).ToList();
        }
        catch (Exception ex)
        {
            LastErrorBanner = "读取时间线失败：" + ex.Message;
            list = new List<FileEvent>();
        }

        if (!string.IsNullOrWhiteSpace(_timelineFilter))
        {
            var f = _timelineFilter.Trim();
            list = list.Where(e =>
                e.RelativePath.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                (e.OldRelativePath?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.AttributedProcess?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false) ||
                e.Operation.ToChinese().Contains(f, StringComparison.Ordinal)).ToList();
        }

        var keepId = _selectedEvent?.Event.Id;
        Events.Clear();

        string? lastGroup = null;
        foreach (var e in list)
        {
            var title = DescribeDay(e.TimestampLocal);
            bool isGroupStart = title != lastGroup;
            lastGroup = title;
            Events.Add(new EventRow { Event = e, GroupTitle = title, IsGroupStart = isGroupStart });
        }

        TimelineIsEmpty = Events.Count == 0;
        Raise(nameof(TimelineIsEmpty));

        if (keepId is not null)
        {
            SelectedEvent = Events.FirstOrDefault(r => r.Event.Id == keepId);
        }

        RefreshRootStats();
        ReloadHome();
    }

    /// <summary>
    /// 把事件按"相邻 5 分钟内"合并成一段，给历史页顶部的概览用。
    /// 只用于展示，不改变任何底层事实。

    /// <summary>把首页所有派生显示（保护状态、最近变化、准备进度）一次性刷新。</summary>
    public void ReloadHome()
    {
        HomeRecentChanges.Clear();
        foreach (var row in Events.Take(6)) HomeRecentChanges.Add(row);
        Raise(nameof(HomeHasRecent));
        Raise(nameof(HomeRecentListHeight));
        Raise(nameof(HomeRootPath));
        Raise(nameof(HomeProtectState));
        Raise(nameof(HomeProtectHealthy));
        Raise(nameof(HomePreparingText));
        Raise(nameof(HomeIsPreparing));
        Raise(nameof(HomeIsReady));
    }

    public static string DescribeDay(DateTime local)
    {
        var today = DateTime.Today;
        if (local.Date == today) return "今天 · " + local.ToString("M月d日", CultureInfo.InvariantCulture);
        if (local.Date == today.AddDays(-1)) return "昨天 · " + local.ToString("M月d日", CultureInfo.InvariantCulture);
        return local.ToString("yyyy年M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // 恢复点与对比
    // ─────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────
    // 时间点选中状态（唯一来源）
    //
    // 只有 **一个** 可写状态：`_selectedTimePointChoice`（由恢复页的「时间点」下拉框写入，
    // 也由 ReloadRestoreChoices 按快照 Id 复位）。其余全部是**只读计算结果**：
    //   · Points            —— 与 TimePointChoices 同源、反序（见下）
    //   · SelectedPoint     —— 就是 _selectedTimePointChoice
    //   · SelectedPointIndex—— 只在 TimePointChoices 里查一次
    //   · HasSelectedPoint / SelectedPointText / PointsIsEmpty —— 由上面几个推出来
    //
    // 曾经这里有两套可写状态（`_selectedPointIndex` 给历史页、`_selectedTimePointChoice`
    // 给恢复页），两者从不互相同步。用户的症状就是"在恢复页选了时间点、切到别的页再回来，
    // 选择看起来没生效"。历史页现在已经没有任何时间点列表控件，index 那一条链路
    // 既没有界面读者、也没有第二个消费者，所以整条删掉，只留一个事实来源。
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 时间点列表，**最旧在前**。
    ///
    /// 它是 <see cref="TimePointChoices"/> 的反序视图，本身不保存任何状态 ——
    /// 这样"两个列表"不可能各自被改一份而互相矛盾。
    /// </summary>
    public IReadOnlyList<PointRow> Points => TimePointChoices.Reverse().ToList();

    /// <summary>
    /// 当前选中的时间点。**只读**：改它请改 <see cref="SelectedTimePointChoice"/>。
    ///
    /// 历史 / 恢复 / 对比 / 预览都从这里取"用户选的是哪个时间点"，
    /// 所以它们看到的一定是同一个事实。
    /// </summary>
    public PointRow? SelectedPoint => _selectedTimePointChoice;

    /// <summary>选中项在 <see cref="TimePointChoices"/> 中的位置；没有选中时为 -1。</summary>
    public int SelectedPointIndex => _selectedTimePointChoice is null
        ? -1
        : TimePointChoices.IndexOf(_selectedTimePointChoice);

    public string SelectedPointText
    {
        get
        {
            if (_selectedRoot is null) return "请先添加要保护的文件夹。";
            if (SelectedPoint is null) return "该时间范围内没有时间点。";
            return $"选中：{SelectedPoint.TimeText} · {SelectedPoint.KindText} · {SelectedPoint.DetailText}";
        }
    }

    public bool PointsIsEmpty => TimePointChoices.Count == 0;

    /// <summary>是否有选中的时间点（决定"能不能对它做恢复/对比"）。</summary>
    public bool HasSelectedPoint => SelectedPoint is not null;

    public void ReloadPoints() => ReloadPoints(0);

    /// <summary>
    /// 重新读取恢复点。<paramref name="keepSelectedSnapshotId"/> 不为 0 时，
    /// 读完仍把选中项放回**同一条快照**（按稳定 Id 匹配，不依赖列表下标）。
    /// </summary>
    public void ReloadPoints(long keepSelectedSnapshotId)
    {
        // 列表与选中状态的落地全权交给 ReloadRestoreChoices：
        // 它负责重建 TimePointChoices，并按快照 Id 保住当前选中项（保不住时回退到最新点）。
        _pendingKeepSnapshotId = keepSelectedSnapshotId;
        try
        {
            ReloadRestoreChoices();
        }
        finally
        {
            _pendingKeepSnapshotId = 0;
        }

        Raise(nameof(Points));
        Raise(nameof(SelectedPoint));
        Raise(nameof(SelectedPointIndex));
        Raise(nameof(SelectedPointText));
        Raise(nameof(PointsIsEmpty));
        Raise(nameof(HasSelectedPoint));
    }

    /// <summary>
    /// 在重建列表时"希望保住"的快照 Id（0 = 没有特别要求）。
    /// 只在 <see cref="ReloadRestoreChoices"/> 内部使用，属于过渡参数而非状态。
    /// </summary>
    private long _pendingKeepSnapshotId;


    private void CreateSnapshotNow()
    {
        if (_selectedRoot is null) return;
        try
        {
            SetStatusNote("正在保存时间点…", 30);
            var snapshot = _restoreFlow.CreateSnapshot(_selectedRoot.Id, SnapshotKind.Manual,
                $"用户手动创建（{DateTime.Now:HH:mm:ss}）");
            // 明确选中"刚建的这个"：用户按下"记下现在的状态"，
            // 紧接着打开恢复页时当然是想看刚刚这一刻，而不是最老的基线。
            ReloadPoints(snapshot.Id);
            LastErrorBanner = null;
            SetStatusNote($"已保存时间点 #{snapshot.Id}（{snapshot.FileCount} 个文件 / {snapshot.DirectoryCount} 个目录）", 10);
        }
        catch (Exception ex)
        {
            LastErrorBanner = "保存时间点失败：" + ex.Message;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 恢复预览与执行
    // ─────────────────────────────────────────────────────────────────────


    /// <summary>
    /// 对比结果与恢复预览都渲染在「恢复」页。用户在「历史」页按下相关按钮时
    /// 必须自动切过去 —— 否则按钮"看起来毫无反应"，而结果其实在看不见的另一页。
    /// </summary>
    private void GoToRestorePage()
    {
        ActivePage = "restore";
        ReloadPoints(SelectedPoint?.Point.SnapshotId ?? 0);
        RefreshRestoreHistory();
    }

    private void UndoLastRestore()
    {
        var last = LastUndoableOperationId;
        if (last is null) return;

        var (plan, error) = _restoreFlow.PreviewUndo(last.Value);
        if (plan is null)
        {
            LastErrorBanner = error;
            return;
        }

        var confirm = DangerBox.Show(
            $"撤销上一次恢复（操作 #{last}）？\n\n" +
            $"将把范围恢复到那次恢复**之前**的状态：{plan.TargetTimeLocal:yyyy-MM-dd HH:mm:ss}\n" +
            $"预计影响 {plan.Steps.Count} 项。\n\n继续吗？",
            "撤销恢复",
            System.Windows.MessageBoxButton.OKCancel);
        if (confirm != System.Windows.MessageBoxResult.OK) return;

        RunRestore(() => _restoreFlow.ExecuteUndo(last.Value, plan.Fingerprint));
    }

    private void RunRestore(Func<RestoreOutcome> action) => RunRestore(action, null);

    /// <param name="doneLabel">
    /// 完成页显示的目标说明。为空时用当前选中的时间点；
    /// 自定义恢复（文件来自多个时间点）会显式传一个"自定义恢复（…等）"进来。
    /// </param>
    private void RunRestore(Func<RestoreOutcome> action, string? doneLabel)
    {
        SetStatusNote("正在执行恢复…", 300);
        
        var targetLabel = doneLabel
                          ?? SelectedPoint?.IdentifiedTime
                          ?? string.Empty;

        Task.Run(() =>
        {
            try
            {
                var outcome = action();
                UiDispatch.Invoke(() =>
                {
                    _timelineDirty = true;
                    if (outcome.Ok)
                    {
                        // 这一次的恢复/删除已经执行完了，待执行集合必须跟着清空。
                        // 不清的话底部会一直挂着"标记删除 N 个"，用户会以为任务还没做
                        // （真实缺陷：执行完成后集合残留）。
                        // 同时这也保证了"再点一次确认恢复"不会重放已经完成的动作 ——
                        // 集合空了，确认按钮就不可用。
                        ClearAllPicked();
                    }

                    // 一次性刷新所有派生显示：时间线、恢复点、恢复记录、文件列表、根状态。
                    // 以前这里逐个调用，漏掉过"撤销可用性"，导致恢复成功后撤销按钮仍是灰的。
                    RefreshAll();
                    if (outcome.Ok)
                    {
                        var label = string.IsNullOrWhiteSpace(outcome.TargetLabel) ? targetLabel : outcome.TargetLabel!;
                        _lastRestoreDoneText = string.IsNullOrWhiteSpace(label) ? "✓ 已恢复" : $"✓ 已恢复到 {label}";
                        _lastRestoreCanUndo = outcome.CanUndo;
                        IsRestoreDone = true;
                        Raise(nameof(RestoreDoneHeadline));
                        Raise(nameof(RestoreDoneCanUndo));
                    }
                    else
                    {
                        // 失败的细节必须让用户看得见：送到「设置 → 运行日志」那套日志里，
                        // 并在顶部横幅把结论摆出来（不静默失败）。
                        var detail = outcome.Failures.Count > 0
                            ? "；" + string.Join("；", outcome.Failures.Take(5))
                            : string.Empty;
                        Logs.Insert(0, new LogRow
                        {
                            Utc = DateTime.UtcNow,
                            Level = "error",
                            Message = "恢复未完全成功：" + outcome.Message + detail,
                        });
                        LastErrorBanner = "恢复未完全成功：" + outcome.Message;
                    }
                    // ⚠ 必须放在 RefreshAll() 之后：RefreshAll 会用 BuildStatus() 覆盖状态文字，
                    //    放在前面的话状态栏会一直停在"正在执行恢复…"。
                    SetStatusNote(outcome.Ok ? "恢复完成。" : "恢复完成，但有部分文件未处理（见上方提示）。", 12);
                    CommandManager.InvalidateRequerySuggested();
                });
            }
            catch (Exception ex)
            {
                UiDispatch.Invoke(() =>
                {
                    // 同上：状态栏必须离开"正在执行恢复…"，并明确说出失败。
                    LastErrorBanner = "恢复执行失败：" + ex.Message;
                    SetStatusNote("恢复失败（见上方提示）。", 12);
                });
            }
        });
    }

    public long? LastUndoableOperationId => _restoreFlow.GetLastUndoable(_selectedRoot?.Id)?.Id;

    private void RefreshRestoreHistory()
    {
        RestoreHistory.Clear();
        try
        {
            foreach (var op in _restoreFlow.ListOperations(_selectedRoot?.Id, 20))
            {
                RestoreHistory.Add(new RestoreRow(op, OnUndoRequested));
            }
        }
        catch (Exception ex)
        {
            LastErrorBanner = "读取恢复记录失败：" + ex.Message;
        }
        Raise(nameof(LastUndoableOperationId));
        Raise(nameof(HasRestoreHistory));
        Raise(nameof(HasUndoable));
    }

    public bool HasRestoreHistory => RestoreHistory.Count > 0;

    public bool HasUndoable => LastUndoableOperationId is not null;

    /// <summary>供行内"撤销"按钮回调。</summary>
    private void OnUndoRequested(RestoreRow row) => UndoOperation(row.Operation.Id);

    /// <summary>为什么"没有可撤销的恢复"——给出可执行的原因，而不是让用户猜。</summary>
    private string DescribeWhyNoUndo()
    {
        var any = _restoreFlow.ListOperations(_selectedRoot?.Id, 50).ToList();
        if (any.Count == 0)
        {
            return "本保护范围还没有执行过任何恢复（「恢复」页的列表是空的）。";
        }
        if (any.All(o => o.UndoneByOperationId is not null))
        {
            return "所有恢复操作都已经撤销过了。";
        }
        if (any.All(o => o.PreRestoreSnapshotId is null))
        {
            return "这些恢复操作都没有「恢复前安全点」，因此无法撤销。";
        }
        return "最近一次恢复没有成功完成（状态：" +
               string.Join("、", any.Take(3).Select(o => o.Status.ToChinese())) + "）。";
    }

    private void UndoOperation(long operationId)
    {
        var (plan, error) = _restoreFlow.PreviewUndo(operationId);
        if (plan is null)
        {
            LastErrorBanner = error;
            return;
        }

        var confirm = DangerBox.Show(
            $"撤销恢复操作 #{operationId}？\n\n" +
            "将把这个保护范围恢复到那次恢复**之前**的状态：" +
            $"{plan.TargetTimeLocal:yyyy-MM-dd HH:mm:ss}\n" +
            $"预计影响 {plan.Steps.Count} 项。\n\n" +
            "· 撤销本身也会先创建安全点，因此同样可以再次撤销。\n继续吗？",
            "撤销恢复",
            System.Windows.MessageBoxButton.OKCancel);
        if (confirm != System.Windows.MessageBoxResult.OK) return;

        RunRestore(() => _restoreFlow.ExecuteUndo(operationId, plan.Fingerprint));
    }



    // ─────────────────────────────────────────────────────────────────────
    // 文件页
    // ─────────────────────────────────────────────────────────────────────

    public string FileSearch
    {
        get => _fileSearch;
        set { if (Set(ref _fileSearch, value)) LoadFiles(); }
    }

    public FileRow? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (!Set(ref _selectedFile, value)) return;
            ShowVersions(_selectedFile);
        }
    }

    public VersionRow? SelectedVersionA
    {
        get => _selectedVersionA;
        set { if (Set(ref _selectedVersionA, value)) UpdateDiff(); }
    }

    public VersionRow? SelectedVersionB
    {
        get => _selectedVersionB;
        set { if (Set(ref _selectedVersionB, value)) UpdateDiff(); }
    }

    public string DiffSummary
    {
        get => _diffSummary;
        private set => Set(ref _diffSummary, value);
    }

    public string DiffMeta
    {
        get => _diffMeta;
        private set => Set(ref _diffMeta, value);
    }

    private void LoadFiles()
    {
        Files.Clear();
        if (_selectedRoot is null) return;

        try
        {
            var (_, _, _) = _history.GetEventStatistics(_selectedRoot.Id);
            var events = _history.QueryEvents(new EventQuery
            {
                RootId = _selectedRoot.Id,
                Limit = 4000,
                Descending = true,
                IncludeTransient = false,
            });

            var grouped = new Dictionary<string, (int Count, DateTime Last, long Size, bool Exists)>(PathUtil.Comparer);
            foreach (var e in events)
            {
                var key = e.RelativePath;
                var exists = e.Operation != OperationType.Deleted;
                var size = e.SizeAfter ?? e.SizeBefore ?? 0;
                if (grouped.TryGetValue(key, out var cur))
                {
                    grouped[key] = (cur.Count + 1, cur.Last > e.TimestampUtc ? cur.Last : e.TimestampUtc, cur.Size != 0 ? cur.Size : size, exists);
                }
                else
                {
                    grouped[key] = (1, e.TimestampUtc, size, exists);
                }
            }

            var rows = grouped
                .Select(kv => new FileRow
                {
                    RootId = _selectedRoot.Id,
                    RelativePath = kv.Key,
                    ChangeCount = kv.Value.Count,
                    LastChangeUtc = kv.Value.Last,
                    SizeBytes = kv.Value.Size,
                    ExistsNow = kv.Value.Exists,
                });

            if (!string.IsNullOrWhiteSpace(_fileSearch))
            {
                var f = _fileSearch.Trim();
                rows = rows.Where(r => r.RelativePath.Contains(f, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var r in rows.OrderByDescending(r => r.LastChangeUtc).Take(500)) Files.Add(r);
        }
        catch (Exception ex)
        {
            LastErrorBanner = "读取文件列表失败：" + ex.Message;
        }
    }

    private void ShowFile(FileRow? row)
    {
        if (row is null) return;
        SelectedFile = row;
    }

    private void ShowVersions(FileRow? row)
    {
        Versions.Clear();
        DiffRows.Clear();
        DiffSummary = string.Empty;
        DiffMeta = string.Empty;
        _selectedVersionA = null;
        _selectedVersionB = null;
        Raise(nameof(SelectedVersionA));
        Raise(nameof(SelectedVersionB));
        if (row is null) return;

        try
        {
            var list = _history.ListVersions(row.RootId, row.RelativePath, 100);
            foreach (var v in list)
            {
                Versions.Add(new VersionRow
                {
                    Hash = v.Hash,
                    RecordedUtc = v.RecordedUtc,
                    Size = v.Size,
                    ObjectId = v.ObjectId,
                    ContentAvailable = v.ObjectId is not null && _rt.Store.Exists(v.ObjectId.Value),
                    Note = v.Note,
                    IsCurrent = false,
                });
            }

            // 默认比较"最近两个版本"
            if (Versions.Count >= 2)
            {
                SelectedVersionA = Versions[1];
                SelectedVersionB = Versions[0];
                Raise(nameof(SelectedVersionA));
                Raise(nameof(SelectedVersionB));
                UpdateDiff();
            }
            else if (Versions.Count == 1)
            {
                SelectedVersionB = Versions[0];
                Raise(nameof(SelectedVersionB));
                UpdateDiff();
            }
            else
            {
                DiffSummary = "该路径还没有留存任何历史版本。";
            }
        }
        catch (Exception ex)
        {
            LastErrorBanner = "读取文件历史失败：" + ex.Message;
        }
    }

    private void UpdateDiff()
    {
        DiffRows.Clear();
        if (_selectedFile is null) return;

        var a = _diffReversed ? _selectedVersionB : _selectedVersionA;
        var b = _diffReversed ? _selectedVersionA : _selectedVersionB;

        try
        {
            var result = _history.CompareContent(a?.ObjectId, b?.ObjectId, _selectedFile.RelativePath);

            if (result.IsBinary)
            {
                DiffSummary = "二进制文件：不做文本比较。";
                DiffMeta =
                    $"旧版本：{(result.SizeA is null ? "—" : PathUtil.FormatBytes(result.SizeA.Value))} / {(a?.HashShort ?? "—")}\n" +
                    $"新版本：{(result.SizeB is null ? "—" : PathUtil.FormatBytes(result.SizeB.Value))} / {(b?.HashShort ?? "—")}\n" +
                    (result.Note ?? string.Empty);
                return;
            }

            var diff = result.Diff;
            if (diff is null)
            {
                DiffSummary = "没有可比较的内容。";
                DiffMeta = string.Join("\n", new[] { result.ErrorA, result.ErrorB }.Where(x => !string.IsNullOrEmpty(x)));
                return;
            }

            foreach (var line in diff.Lines) DiffRows.Add(DiffRow.From(line));

            DiffSummary = diff.AreIdentical
                ? "两个版本内容完全相同。"
                : $"+{diff.AddedCount} 行  -{diff.RemovedCount} 行" + (diff.Truncated ? "（内容过大已截断）" : string.Empty);

            DiffMeta =
                $"旧：{(a is null ? "（未选择）" : a.TimeText + " · " + a.HashShort)}  [{result.EncodingA ?? "—"}]\n" +
                $"新：{(b is null ? "（未选择）" : b.TimeText + " · " + b.HashShort)}  [{result.EncodingB ?? "—"}]" +
                (result.Note is { Length: > 0 } ? "\n" + result.Note : string.Empty);
        }
        catch (Exception ex)
        {
            DiffSummary = "比较失败：" + ex.Message;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 设置页
    // ─────────────────────────────────────────────────────────────────────

    public AppSettings EditSettings { get; private set; } = new();

    public bool SettingAttribution
    {
        get => EditSettings.EnableProcessAttribution;
        set { EditSettings.EnableProcessAttribution = value; Raise(); }
    }

    public bool SettingCompression
    {
        get => EditSettings.EnableCompression;
        set { EditSettings.EnableCompression = value; Raise(); }
    }

    public bool SettingCollapseTransient
    {
        get => EditSettings.CollapseTransientInTimeline;
        set { EditSettings.CollapseTransientInTimeline = value; Raise(); }
    }

    public int SettingRetentionDays
    {
        get => EditSettings.RetentionDays;
        set
        {
            EditSettings.RetentionDays = Math.Max(0, value);
            Raise();
            RaiseRetentionFlags();
        }
    }

    public int SettingMaxHistoryGb
    {
        get => (int)Math.Round(EditSettings.MaxHistoryBytes / 1024.0 / 1024 / 1024);
        set
        {
            var gb = Math.Max(1, value);
            EditSettings.MaxHistoryBytes = gb * 1024L * 1024 * 1024;
            Raise();
        }
    }

    public int SettingMaxFileSizeMb
    {
        get => (int)Math.Round(EditSettings.MaxStoreFileSizeBytes / 1024.0 / 1024);
        set
        {
            var mb = Math.Clamp(value, 1, 4096);
            EditSettings.MaxStoreFileSizeBytes = mb * 1024L * 1024;
            Raise();
        }
    }

    public int SettingAutoSnapshotMinutes
    {
        get => EditSettings.AutoSnapshotIntervalMinutes;
        set { EditSettings.AutoSnapshotIntervalMinutes = Math.Max(0, value); Raise(); }
    }

    public int SettingMergeWindowMs
    {
        get => EditSettings.MergeWindowMs;
        set { EditSettings.MergeWindowMs = Math.Clamp(value, 100, 20000); Raise(); }
    }

    // ── 保护模式 ─────────────────────────────────────────────────────────
    // 三选一：决定"要不要留存文件内容"。实测（39.6 万条 / 43.6GB）：
    //   只记录变化 ≈ 3500 条/秒、占用 0；完整内容 ≈ 110 文件/秒、占用可接近源目录。
    // 所以必须让用户明确选，而不是替他决定。

    public bool IsModeFull
    {
        get => EditSettings.Protection == ProtectionMode.FullContent;
        set { if (value) SetProtectionMode(ProtectionMode.FullContent); }
    }

    public bool IsModeSmart
    {
        get => EditSettings.Protection == ProtectionMode.SmartContent;
        set { if (value) SetProtectionMode(ProtectionMode.SmartContent); }
    }

    public bool IsModeTrack
    {
        get => EditSettings.Protection == ProtectionMode.TrackOnly;
        set { if (value) SetProtectionMode(ProtectionMode.TrackOnly); }
    }

    private void SetProtectionMode(ProtectionMode mode)
    {
        EditSettings.Protection = mode;
        Raise(nameof(IsModeFull));
        Raise(nameof(IsModeSmart));
        Raise(nameof(IsModeTrack));
        Raise(nameof(ProtectionModeHint));
        Raise(nameof(IsTrackOnlyMode));
    }

    public string ProtectionModeHint => EditSettings.Protection.Describe();

    /// <summary>当前是"只记录变化"模式（界面需要显著提示"无法恢复内容"）。</summary>
    public bool IsTrackOnlyMode => EditSettings.Protection == ProtectionMode.TrackOnly;

    public int SettingSmartMaxMb
    {
        get => (int)Math.Round(EditSettings.SmartContentMaxBytes / 1024.0 / 1024);
        set
        {
            var mb = Math.Clamp(value, 1, 4096);
            EditSettings.SmartContentMaxBytes = mb * 1024L * 1024;
            Raise();
        }
    }

    public int SettingSettleDelayMs
    {
        get => EditSettings.SettleDelayMs;
        set { EditSettings.SettleDelayMs = Math.Clamp(value, 50, 10000); Raise(); }
    }

    public string ExcludePatternsText
    {
        get => string.Join(Environment.NewLine, EditSettings.ExcludePatterns);
        set
        {
            EditSettings.ExcludePatterns = value
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Raise();
        }
    }

    public string ExcludeDirectoriesText
    {
        get => string.Join(Environment.NewLine, EditSettings.ExcludeDirectoryNames);
        set
        {
            EditSettings.ExcludeDirectoryNames = value
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Raise();
        }
    }

    private void LoadSettingsIntoForm()
    {
        EditSettings = _rt.Settings.Clone();
        Raise(nameof(EditSettings));
        Raise(nameof(SettingAttribution));
        Raise(nameof(SettingCompression));
        Raise(nameof(SettingCollapseTransient));
        Raise(nameof(SettingRetentionDays));
        Raise(nameof(SettingMaxHistoryGb));
        Raise(nameof(SettingMaxFileSizeMb));
        Raise(nameof(SettingAutoSnapshotMinutes));
        Raise(nameof(SettingMergeWindowMs));
        Raise(nameof(SettingSettleDelayMs));
        Raise(nameof(ExcludePatternsText));
        Raise(nameof(ExcludeDirectoriesText));
        RefreshStorage();
    }

    private void SaveSettings()
    {
        try
        {
            _rt.ApplySettings(EditSettings);
            SetStatusNote("✓ 设置已保存", 6);
            LastErrorBanner = null;
        }
        catch (Exception ex)
        {
            LastErrorBanner = "保存设置失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 数据目录与日志目录的实际位置 —— 直接展示给用户。
    ///
    /// 为什么必须显式展示：本程序把历史数据放在 %LOCALAPPDATA%，用户既不知道它在哪，
    /// 卸载时也不会自动清除。上线前体检把"数据不可发现"列为高风险项，
    /// 所以这里把真实路径写给用户看，并提供一键打开。
    /// </summary>
    public string DataDirectoryText
    {
        get
        {
            var dir = _rt.DataDirectory;
            var logDir = Path.Combine(dir, "logs");
            var fallback = _rt.DataDirectoryIsFallback
                ? "\n（默认位置不可写，已自动改用此位置）"
                : string.Empty;
            return $"数据目录：{dir}{fallback}\n日志目录：{logDir}";
        }
    }

    public string StorageSummary
    {
        get => _storageSummary;
        private set => Set(ref _storageSummary, value);
    }

    private CleanupPlan? _cleanupPlan;

    public string CleanupPreview
    {
        get => _cleanupPreview;
        private set => Set(ref _cleanupPreview, value);
    }

    private void RefreshStorage()
    {
        try
        {
            var report = _rt.Maintenance.GetReport();
            StorageSummary =
                $"历史数据：{PathUtil.FormatBytes(report.TotalHistoryBytes)}" +
                $"（内容 {PathUtil.FormatBytes(report.ObjectsStoredBytes)} + 数据库 {PathUtil.FormatBytes(report.DatabaseBytes)}）\n" +
                $"保护范围：{PathUtil.FormatBytes(report.ProtectedBytes)} · {report.ProtectedFileCount} 个文件 / {report.ProtectedDirectoryCount} 个目录\n" +
                $"历史版本：{report.VersionCount} 条 · 文件变化：{report.EventCount} 条 · 时间点：{report.SnapshotCount} 个\n" +
                $"内容去重节省：{PathUtil.FormatBytes(report.DeduplicatedBytes)}" +
                (report.ObjectsLogicalBytes > 0 ? $"（实际占用 {report.DedupRatio:P1}）" : string.Empty) + "\n" +
                $"上限：{PathUtil.FormatBytes(report.QuotaBytes)} · 保留 {report.RetentionDays} 天 · " +
                $"所在磁盘剩余 {PathUtil.FormatBytes(report.DiskFreeBytes)}";
        }
        catch (Exception ex)
        {
            StorageSummary = "统计失败：" + ex.Message;
        }
    }

    private void PlanCleanup()
    {
        try
        {
            _cleanupPlan = _rt.Maintenance.PlanCleanup();
            CleanupPreview = _cleanupPlan.Describe() +
                             (_cleanupPlan.Reasons.Count > 0 ? "\n" + string.Join("\n", _cleanupPlan.Reasons) : string.Empty);
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            CleanupPreview = "生成清理计划失败：" + ex.Message;
        }
    }

    private void ApplyCleanup()
    {
        if (_cleanupPlan is null) return;
        if (_cleanupPlan.IsEmpty)
        {
            CleanupPreview = "没有需要清理的内容。";
            return;
        }

        var confirm = DangerBox.Show(
            _cleanupPlan.Describe() + "\n\n" +
            "· 所有时间点（含保护开始时留的、你手动留的、恢复前自动留的）都会保留，仍然可以恢复到过去。\n" +
            "· 只删除没有被任何时间点引用的历史内容。\n\n继续吗？",
            "确认清理历史",
            System.Windows.MessageBoxButton.OKCancel);
        if (confirm != System.Windows.MessageBoxResult.OK) return;

        try
        {
            var log = new List<string>();
            var applied = _rt.Maintenance.ApplyCleanup(_cleanupPlan, alsoDeleteEvents: false, log.Add);
            CleanupPreview = applied.Describe() + "\n" + string.Join("\n", log.Take(20));
            _cleanupPlan = null;
            RefreshStorage();
            ReloadTimeline();
            ReloadPoints();
        }
        catch (Exception ex)
        {
            CleanupPreview = "清理失败：" + ex.Message;
        }
    }

    private void CheckIntegrity()
    {
        try
        {
            var (ok, report) = _rt.Maintenance.CheckIntegrity();
            var (contentOk, contentReport) = _rt.Maintenance.VerifyContent();
            CleanupPreview = "数据库检查：" + (ok ? "通过" : "发现问题") + "\n" + report +
                             "\n内容校验：" + (contentOk ? "通过" : "发现问题") + "\n" + contentReport;
        }
        catch (Exception ex)
        {
            CleanupPreview = "检查失败：" + ex.Message;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 导航与命令
    // ─────────────────────────────────────────────────────────────────────

    public ICommand NavigateCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CreateSnapshotCommand { get; }
    public ICommand UndoLastRestoreCommand { get; }
    public ICommand OpenFileDialogCommand { get; }
    public ICommand AddRootCommand { get; }
    public ICommand RemoveRootCommand { get; }
    public ICommand RemoveRootRowCommand { get; }
    public ICommand PauseRootCommand { get; }
    public ICommand RescanRootCommand { get; }
    public ICommand CancelScanCommand { get; }
    public ICommand RefreshHistoryCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand PlanCleanupCommand { get; }
    public ICommand ApplyCleanupCommand { get; }
    public ICommand CheckIntegrityCommand { get; }
    public ICommand ShowFileCommand { get; }
    public ICommand SwapDiffCommand { get; }
    public ICommand DismissErrorCommand { get; }

    // 首页 / 三步恢复流程
    public ICommand GoHistoryCommand { get; }
    public ICommand GoRestoreCommand { get; }
    public ICommand GoHomeCommand { get; }
    public ICommand ShowAdvancedSettingsCommand { get; }
    public ICommand HideAdvancedSettingsCommand { get; }
    public ICommand PickFolderCommand { get; }
    public ICommand SelectAllRestoreFilesCommand { get; }
    public ICommand ClearRestoreSelectionCommand { get; }
    public ICommand InvertRestoreSelectionCommand { get; }
    public ICommand SwapRestoreEndsCommand { get; }
    public ICommand UseCurrentStateAsTargetCommand { get; }
    public ICommand ConfirmRestoreFromPanelCommand { get; }
    public ICommand OpenBrowseRowCommand { get; }
    public ICommand GoUpDirectoryCommand { get; }
    public ICommand GoRootDirectoryCommand { get; }
    public ICommand TogglePickedListCommand { get; }
    public ICommand RemovePickedCommand { get; }
    public ICommand ClearAllPickedCommand { get; }
    public ICommand MarkDeletionCommand { get; }
    public ICommand UnmarkDeletionCommand { get; }
    public ICommand ClearAllDeletionsCommand { get; }
    public ICommand DeleteSelectedEventCommand { get; }
    public ICommand ClearAllEventsCommand { get; }
    public ICommand PurgeAllHistoryCommand { get; }
    public ICommand PickStorageFolderCommand { get; }
    public ICommand ResetStorageFolderCommand { get; }
    public ICommand OpenStorageFolderCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    /// <summary>把全部保护范围连同历史与内容一起清掉，并把软件恢复成"从没用过"的状态。</summary>
    public ICommand ClearAllDataCommand { get; }
    public ICommand ShowLogCommand { get; }

    private void Navigate(string page)
    {
        // 「首页 / 历史 / 恢复」三个页面共用同一份事实，切页时统一刷新，
        // 避免出现"这页显示有变化、那页显示没有"的自相矛盾。
        ActivePage = page;
        switch (page)
        {
            case "home":
                _timelineDirty = true;
                ReloadTimeline();
                ReloadPoints();
                RefreshRestoreHistory();
                break;
            case "history":
                _timelineDirty = true;
                ReloadTimeline();
                ReloadPoints();
                break;
            case "fileDetail":
                LoadFiles();
                break;
            case "restore":
                ReloadPoints();
                RefreshRestoreHistory();
                break;
            case "settings":
                LoadRoots();
                LoadSettingsIntoForm();
                StartupNotes.Clear();
                foreach (var note in _rt.StartupNotes) StartupNotes.Add(note);
                foreach (var note in _rt.InterruptedOperations) StartupNotes.Add("⚠ " + note);
                break;
            case "log":
                break;
        }
        StatusText = BuildStatus();
    }

    public void RefreshAll()
    {
        _timelineDirty = true;
        LoadRoots();
        ReloadTimeline();
        ReloadPoints();
        LoadFiles();
        RefreshRestoreHistory();
        RefreshStorage();
        StatusText = BuildStatus();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  状态栏：常驻状态 + 一次性操作提示
    //
    //  为什么要把这两者分开（真实缺陷，Release 黑盒压测发现）：
    //    状态栏只有一个 StatusText，而 OnTick() 每 2 秒用 BuildStatus() 覆盖它。
    //    于是"设置已保存。""恢复完成。""正在执行恢复…"这些一次性提示会被无声冲掉 ——
    //    用户看到的是"点了保存完全没反应"，以及"恢复早就完成了，左下角却一直停在
    //    「正在执行恢复…」"。
    //  修法：一次性提示进 StatusNote（带过期时间），常驻状态永远由 BuildStatus() 算。
    //  提示只在有效期内显示，过期后自动回到常驻状态，绝不会成为陈旧状态。
    // ─────────────────────────────────────────────────────────────────────

    private string? _statusNote;
    private DateTime _statusNoteUntilUtc = DateTime.MinValue;

    /// <summary>显示一条一次性操作提示（几秒后自动回到常驻状态栏）。</summary>
    private void SetStatusNote(string note, int seconds = 6)
    {
        _statusNote = note;
        _statusNoteUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
        StatusText = note;
    }

    /// <summary>当前应显示的状态栏文字：提示未过期就用提示，否则用常驻状态。</summary>
    private string CurrentStatusText() =>
        _statusNote is not null && DateTime.UtcNow < _statusNoteUntilUtc ? _statusNote : BuildStatus();

    private string BuildStatus()
    {
        var stats = _protection.Statistics;
        var watching = Roots.Count(r => r.Watching);
        var paused = Roots.Count(r => !r.Enabled);
        var sb = new System.Text.StringBuilder();
        if (Roots.Count == 0)
        {
            sb.Append("还没有保护任何文件夹");
        }
        else
        {
            // 普通用户只关心"它在不在工作、记录了些什么"。
            // 原始通知数、合并率、缓冲区溢出这些是排查用的，放进「设置 → 高级 → 运行日志」。
            //
            // 暂停状态必须排在第一位说清楚：用户点了暂停之后，文件确实不再被记录，
            // 如果状态栏还写"正在保护"，他会以为记录还在进行（真实缺陷）。
            if (paused > 0 && paused == Roots.Count)
            {
                sb.Append(CultureInfo.InvariantCulture, $"已暂停保护（{Roots.Count} 个文件夹）");
            }
            else
            {
                sb.Append(CultureInfo.InvariantCulture, $"正在保护 {Roots.Count} 个文件夹");
                if (paused > 0)
                    sb.Append(CultureInfo.InvariantCulture, $"（{paused} 个已暂停）");
                else if (watching < Roots.Count)
                    sb.Append(CultureInfo.InvariantCulture, $"（{watching} 个在监听）");
            }
            sb.Append(CultureInfo.InvariantCulture, $" · 已记录 {stats.EventsPersisted} 条变化");
            if (_protection.IsAnyScanning) sb.Append(" · 正在准备保护");
            if (stats.LastEventUtc is not null)
                sb.Append(CultureInfo.InvariantCulture, $" · 最近一次 {stats.LastEventUtc.Value.ToLocalTime():HH:mm:ss}");
        }
        return sb.ToString();
    }

    /// <summary>定时刷新（由窗口的 DispatcherTimer 调用）。</summary>
    public void OnTick()
    {
        RefreshRootStats();
        StatusText = CurrentStatusText();
        if (_timelineDirty && (IsTimelinePage || IsHomePage)) ReloadTimeline();
        RefreshStorageIfVisible();
        CommandManager.InvalidateRequerySuggested();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  设置页的存储统计：只要这一页开着，就跟着刷新
    //
    //  为什么必须补这一下（真实缺陷）：
    //    那些数字（历史数据/事件/恢复点/占用空间）原来只在"进入设置页"的那一刻
    //    算一次。用户把设置页开着，然后在别的页面产生变化、创建恢复点、恢复、删除、
    //    撤销 —— 回到设置页（或重启）才会重算，中间一直显示旧值。
    //  为什么不直接每 2 秒算一次：
    //    GetReport() 要跑好几条聚合查询 + 读磁盘剩余空间，没必要这么勤。
    //    5 秒一次既能让用户看到数字在动，又不给运行时加负担。
    // ─────────────────────────────────────────────────────────────────────

    private static readonly TimeSpan StorageRefreshInterval = TimeSpan.FromSeconds(5);
    private DateTime _storageRefreshedUtc = DateTime.MinValue;

    private void RefreshStorageIfVisible()
    {
        if (!string.Equals(ActivePage, "settings", StringComparison.Ordinal)) return;
        var now = DateTime.UtcNow;
        if (now - _storageRefreshedUtc < StorageRefreshInterval) return;
        _storageRefreshedUtc = now;
        RefreshStorage();
    }

    // ─────────────────────────────────────────────────────────────────────
    // 首页：只回答三个问题
    //   ① 我在保护什么？ ② 最近发生了什么？ ③ 出问题了怎么办？
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>首页顶部：当前保护的文件夹路径。</summary>
    public string HomeRootPath => _selectedRoot?.Path ?? "还没有保护任何文件夹";

    /// <summary>首页顶部的保护状态（"● 正在保护" / "已暂停" / "正在准备保护…"）。</summary>
    public string HomeProtectState
    {
        get
        {
            if (_selectedRoot is null) return string.Empty;
            if (_protection.IsScanning(_selectedRoot.Id)) return "正在准备保护…";
            if (!_selectedRoot.Enabled) return "已暂停保护";
            return _selectedRoot.Watching ? "● 正在保护" : "● 已开始保护";
        }
    }

    /// <summary>首页的状态点是否显示为"正常"（用于配色）。</summary>
    public bool HomeProtectHealthy =>
        _selectedRoot is not null && _selectedRoot.Enabled && !_protection.IsScanning(_selectedRoot.Id) &&
        _selectedRoot.Watching;

    /// <summary>最近的变化（首页只显示最近几条，完整清单在「历史」）。</summary>
    public ObservableCollection<EventRow> HomeRecentChanges { get; } = new();

    public bool HomeHasRecent => HomeRecentChanges.Count > 0;

    /// <summary>
    /// 首页"最近变化"列表的高度：按条数收缩，避免只有两条时还撑满整屏留一大片空白。
    /// 最多显示 8 行左右，再多就交给滚动条。
    /// </summary>
    public double HomeRecentListHeight => Math.Min(HomeRecentChanges.Count, 8) * 21 + 6;

    /// <summary>首页"最近发生的变化"为空时的说明：状态 + 原因 + 下一步。</summary>
    public string HomeNoChangeText =>
        "现在还没有记录到变化。\n\n你正常修改、创建或删除文件后，变化会自动出现在这里。";

    /// <summary>首页正在准备保护时的文案（普通用户不需要知道"基线"这个词）。</summary>
    public string HomePreparingText
    {
        get
        {
            var root = _selectedRoot;
            if (root is null) return string.Empty;
            var lines = new List<string> { "正在准备保护你的文件夹……" };
            lines.Add(string.Empty);
            if (root.HasScanProgress) lines.Add(root.ScanUserText);
            else lines.Add("正在检查文件夹里已有的文件……");
            lines.Add(string.Empty);
            lines.Add("扫描期间你仍然可以正常使用电脑。");
            return string.Join("\n", lines);
        }
    }

    /// <summary>准备完成后的那句话。</summary>
    public string HomeReadyText => "从现在开始，这个文件夹里的变化会被记录。\n\n你可以正常使用电脑，不需要一直打开这个软件。";

    /// <summary>首页是否需要显示"正在准备保护"这一屏。</summary>
    public bool HomeIsPreparing => _selectedRoot is not null && _protection.IsScanning(_selectedRoot.Id);

    /// <summary>首页是否需要显示"已开始保护"这一屏（有目录、已扫描完、且首页还没有变化）。</summary>
    public bool HomeIsReady => _selectedRoot is not null && !HomeIsPreparing && !HomeHasRecent;

    /// <summary>扫描失败时给用户的说法：先保证"你的文件没有被改动"。</summary>
    public string HomeScanFailedText =>
        "没关系，你的文件没有被修改。\n\n可以点「重新扫描补齐」再试一次。";

    /// <summary>首次运行（没有任何受保护目录）时的首页说明。</summary>
    public string FirstRunExamples =>
        "例如：\n    D:\\我的项目\n    D:\\工作文件\n    D:\\游戏资料";

    // ─────────────────────────────────────────────────────────────────────
    // 恢复：三步流程（选时间 → 看会改什么 → 确认），完成后突出"撤销"
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 刚刚完成过一次恢复（界面据此显示"✓ 已恢复到 …"和「撤销」）。
    ///
    /// 曾经这里是个 4 态流程机（选时间/看预览/确认/完成），但恢复页改成
    /// **单页紧凑面板**之后，只有"完成"这一个状态还需要界面知道 —— 所以降成一个 bool。
    /// </summary>
    public bool IsRestoreDone
    {
        get => _isRestoreDone;
        private set => Set(ref _isRestoreDone, value);
    }

    private bool _isRestoreDone;

    /// <summary>恢复完成后的一句话。</summary>
    public string RestoreDoneHeadline => _lastRestoreDoneText;

    /// <summary>恢复完成后能不能撤销（决定"撤销"按钮是否醒目可用）。</summary>
    public bool RestoreDoneCanUndo => _lastRestoreCanUndo;

    // ─────────────────────────────────────────────────────────────────────
    // 恢复 = 「来源状态 → 目标状态」+「用户自己挑出来的恢复集合」
    //
    // 这个面板不是"某个时间点的变化清单"，而是一个**迷你文件浏览器**：
    //   · 时间点[▼] 决定下面浏览的是哪个历史状态的文件结构
    //   · 双击文件夹进去，勾选要恢复的文件
    //   · 换个时间点继续勾，**之前勾的不会丢**（浏览状态与恢复集合分开管理）
    //   · 最终把来自不同时间点的文件合起来，交给现有的 Restore Engine 一次性执行
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>时间点下拉框的备选（只放真实时间点）。</summary>
    public ObservableCollection<PointRow> TimePointChoices { get; } = new();

    /// <summary>右侧"恢复到"下拉框的备选。</summary>
    public ObservableCollection<PointRow> TargetPointChoices { get; } = new();

    /// <summary>迷你文件浏览器当前显示的行（那个时间点里，当前目录下的目录与文件）。</summary>
    public ObservableCollection<RestoreBrowseRow> RestoreBrowse { get; } = new();

    /// <summary>恢复集合：用户从各个时间点挑出来的文件（与浏览位置无关，切换时间点不会丢）。</summary>
    public ObservableCollection<RestorePickedRow> RestorePicked { get; } = new();

    /// <summary>是否展开"恢复集合"清单（默认收起，保持面板紧凑）。</summary>
    private bool _pickedListOpen;

    public bool PickedListOpen
    {
        get => _pickedListOpen;
        set { if (Set(ref _pickedListOpen, value)) Raise(nameof(PickedListClosed)); }
    }

    public bool PickedListClosed => !_pickedListOpen;

    private PointRow? _selectedTimePointChoice;
    private PointRow? _selectedTargetPoint;
    private bool _targetIsCurrentState = true;

    /// <summary>当前浏览的目录（相对路径，空字符串表示根）。</summary>
    private string _browsePath = string.Empty;

    /// <summary>浏览用到的清单缓存：快照 Id → 该时间点的全部条目。</summary>
    private readonly Dictionary<long, IReadOnlyList<ManifestEntry>> _manifestCache = new();

    /// <summary>
    /// 目录清单缓存：(时间点, 目录) → 已排好序的条目。
    ///
    /// 为什么需要：来回切目录/时间点时，每次都要读快照清单 + 过滤 + 排序。
    /// 目录里文件多的时候这一步会让人感觉"顿一下"，而内容其实不会变（快照是不可变的）。
    /// </summary>
    private readonly Dictionary<string, List<RestoreBrowseRow>> _dirCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>快照内容不可变，缓存可以放心保留；只在切换受保护范围时清掉。</summary>
    private void ClearBrowseCaches()
    {
        _manifestCache.Clear();
        _dirCache.Clear();
    }

    private string? _infoBanner;

    /// <summary>面板顶部的一条提示（如实说明，不弹窗打断操作）。</summary>
    public string? InfoBanner
    {
        get => _infoBanner;
        private set { if (Set(ref _infoBanner, value)) Raise(nameof(HasInfoBanner)); }
    }

    public bool HasInfoBanner => !string.IsNullOrWhiteSpace(_infoBanner);

    /// <summary>左侧：浏览哪个历史时间点。</summary>
    public PointRow? SelectedTimePointChoice
    {
        get => _selectedTimePointChoice;
        set
        {
            if (!Set(ref _selectedTimePointChoice, value)) return;
            // 换时间点：**不清空恢复集合**，只是把浏览器切到新时间点的状态
            _browsePath = string.Empty;
            LoadBrowseDirectory();
            RaiseEnds();
        }
    }

    /// <summary>右侧：恢复到哪个状态（默认"当前状态"）。</summary>
    public PointRow? SelectedTargetPoint
    {
        get => _selectedTargetPoint;
        set
        {
            if (!Set(ref _selectedTargetPoint, value)) return;
            RaiseEnds();
        }
    }

    public bool IsTargetCurrentState => _targetIsCurrentState;

    public string TargetArrowLabel => _targetIsCurrentState ? "恢复到 →" : "恢复回 →";

    /// <summary>左端选中的时间点（下拉框旁边再明确写一次：时间 + 身份）。</summary>
    public string FromEndText => _selectedTimePointChoice?.IdentifiedTime ?? "（未选）";

    /// <summary>右端的目标（默认"现在"）。</summary>
    public string TargetEndText => _targetIsCurrentState
        ? "现在"
        : _selectedTargetPoint?.IdentifiedTime ?? "（未选）";

    public string RestoreDirectionText => _targetIsCurrentState
        ? $"正在从「{FromEndText}」恢复到「当前状态」"
        : $"正在从「{FromEndText}」恢复到「{TargetEndText}」";

    /// <summary>浏览器标题栏：现在是哪个时间点 + 当前目录（面包屑）。</summary>
    public string BrowseHeader => _selectedTimePointChoice is null
        ? "（还没有选择时间点）"
        : $"{_selectedTimePointChoice.IdentifiedTime}　\\{_browsePath}";

    public string BrowsePathText => string.IsNullOrEmpty(_browsePath) ? "\\（根目录）" : "\\" + _browsePath;

    public bool CanGoUp => !string.IsNullOrEmpty(_browsePath);

    public bool BrowseIsEmpty => RestoreBrowse.Count == 0;

    /// <summary>
    /// 恢复集合的说明文字（底部那行）。
    ///
    /// 措辞为什么改成"将恢复 N 个 · 将删除 N 个"（真实缺陷）：
    ///   原文是"已选 N 个文件 · 标记删除 M 个"。用户刚全选 2 个文件、点了"标记删除"，
    ///   这行就变成"已选 0 个文件 · 标记删除 2 个" —— 小白会读成"我的选择消失了"。
    ///   内部语义没错，但"已选"这个词把两件不同的事混在一起了。
    ///   现在只回答一件事：这次执行会做什么。
    /// </summary>
    public string PickedSummary
    {
        get
        {
            var restore = RestorePicked.Count(p => !p.IsDeletion);
            var delete = _deletionPaths.Count;
            var timePoints = RestorePicked.Where(p => !p.IsDeletion).Select(p => p.SnapshotLabel).Distinct().Count();
            // 跨时间点勾选是刻意允许的（切时间点不会丢选择），所以这里必须**说清楚**它来自多个时间点，
            // 不能让用户以为"只恢复当前看到的那一个时间点"。
            var fromTimePoints = timePoints > 1 ? $"（来自 {timePoints} 个时间点）" : string.Empty;
            if (restore == 0 && delete == 0) return "还没有选择任何文件";
            if (delete == 0) return $"将恢复 {restore} 个文件{fromTimePoints}";
            if (restore == 0) return $"将删除 {delete} 个文件";
            return $"将恢复 {restore} 个文件 · 将删除 {delete} 个文件{fromTimePoints}";
        }
    }

    /// <summary>「确认恢复」是否可用：有要恢复的，或有要删除的。</summary>
    public bool CanConfirmRestore => RestorePicked.Count > 0 || _deletionPaths.Count > 0;

    public string BrowseEmptyText
    {
        get
        {
            if (_selectedRoot is null) return "还没有保护任何文件夹。\n\n先在「首页」选择一个文件夹，之后才会有可恢复的时间点。";
            if (TimePointChoices.Count == 0) return "还没有可以浏览的时间点。\n\n时间点会在你点「记下现在的状态」，或文件发生变化后自动记录。";
            if (_selectedTimePointChoice is null) return "请先在上面选择一个时间点。";
            return "这个目录在这个时间点里是空的。";
        }
    }

    /// <summary>浏览器行上的勾选状态变化 → 更新恢复集合。</summary>
    private void OnBrowseRowToggled(RestoreBrowseRow row)
    {
        var key = KeyOf(row.SnapshotId, row.RelativePath);
        if (row.IsSelected && row.IsMarkedForDeletion)
        {
            // 用户又把这一行勾上了 → 他要的是"恢复它"，不是"删掉它"
            row.IsMarkedForDeletion = false;
            _deletionPaths.Remove(row.RelativePath);
        }
        if (row.IsSelected)
        {
            if (_pickedKeys.Add(key))
            {
                RestorePicked.Add(new RestorePickedRow
                {
                    RelativePath = row.RelativePath,
                    SnapshotId = row.SnapshotId,
                    SnapshotLabel = row.SnapshotLabel,
                });
            }
        }
        else if (_pickedKeys.Remove(key))
        {
            for (int i = 0; i < RestorePicked.Count; i++)
            {
                if (RestorePicked[i].SnapshotId == row.SnapshotId &&
                    PathUtil.Comparer.Equals(RestorePicked[i].RelativePath, row.RelativePath))
                {
                    RestorePicked.RemoveAt(i);
                    break;
                }
            }
        }
        OnRestoreSelectionChanged();
    }

    /// <summary>恢复集合的成员索引：O(1) 判断"这一行是不是已经在集合里"。</summary>
    private readonly HashSet<string> _pickedKeys = new(StringComparer.OrdinalIgnoreCase);

    private static string KeyOf(long snapshotId, string relativePath) => snapshotId + "|" + relativePath;

    /// <summary>
    /// 请求"稍后重算命令可用性"，而不是每一次勾选都立刻重算。
    ///
    /// 为什么必须合并：勾一个文件就会走一次命令重查，而命令重查会被排到
    /// Background 优先级 —— 用户连续点几下、或"全选"200 个文件时，
    /// 会堆积上百次重查，界面就明显发卡。
    /// </summary>
    private bool _requeryQueued;

    private void QueueRequery()
    {
        if (_requeryQueued) return;
        _requeryQueued = true;
        UiDispatch.Invoke(() =>
        {
            _requeryQueued = false;
            CommandManager.InvalidateRequerySuggested();
        });
    }

    /// <summary>
    /// 行属性变化统一入口：**只对勾选状态变化做处理**。
    ///
    /// 如果对所有 PropertyChanged 都当"勾选变了"，绑定刷新过程中的无关通知
    /// 也会触发一次恢复集合查找 + 命令重查，勾选多的时候就是明显的卡顿来源。
    /// </summary>
    private void OnBrowseRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(RestoreBrowseRow.IsSelected) or nameof(RestoreBrowseRow.CheckMark)))
            return;
        if (sender is RestoreBrowseRow row && row.IsSelected != _pickedKeys.Contains(KeyOf(row.SnapshotId, row.RelativePath)))
        {
            OnBrowseRowToggled(row);
        }
    }

    /// <summary>
    /// 重建时间点列表并确定选中项。**这是时间点状态的唯一落地处**：
    ///   · 列表来源：<c>RestoreCoordinator.ListTimePoints</c>（引擎已按时间倒序返回）
    ///   · 选中项规则（按优先级）：
    ///       ① <see cref="_pendingKeepSnapshotId"/> 指定的快照（调用方明确要求保住它）
    ///       ② 当前已选中的那条（按快照 Id 匹配 —— 列表重建后旧对象已不是同一个实例，
    ///          所以必须按 Id 找，不能按引用找）
    ///       ③ 列表里 <c>IsCurrent</c> 的那条（最新）
    ///       ④ 列表第一条
    ///     都找不到时选中项为 null —— 不允许出现"index 指向不存在的时间点"。
    /// </summary>
    public void ReloadRestoreChoices()
    {
        // ① 记住"希望保住哪一个"：调用方指定的优先，其次是当前选中的
        var keepId = _pendingKeepSnapshotId != 0
            ? _pendingKeepSnapshotId
            : _selectedTimePointChoice?.Point.SnapshotId ?? 0;
        var keepToId = _selectedTargetPoint?.Point.SnapshotId ?? 0;

        TimePointChoices.Clear();
        try
        {
            if (_selectedRoot is not null)
            {
                // ⚠ 真实缺陷（P1-2A 修复）：这里原本是
                //     var from = DateTime.UtcNow.AddDays(-Math.Max(_timelineDays, 1));
                //   而 _timelineDays **默认就是 1 天** —— 于是只要恢复点超过一天，
                //   GUI 就再也看不到它：数据库里有、CLI 的 timeline 能列出来、
                //   指纹也算得出来，用户却在界面上永远够不到。
                //   （验收实测：GUI 最旧只到 snapshot 59，而 snapshot 6 在 CLI 里
                //     hasEffect=true、steps=1，完全可用。）
                //
                //   历史工具的铁律：**只要恢复点仍然存在（没有被 retention 正常删除），
                //   用户就必须有办法在界面上选到它。** "最近 N 天"只是浏览习惯，
                //   不能当成可达性边界 —— 恢复点不是日志，日志可以只显示今天。
                //
                //   改为不限时间窗，只保留引擎自己的上限（最近 300 个恢复点，列表仍
                //   按时间倒序）。用 2000-01-01 作"无下界"哨兵：早于任何可能存在的
                //   恢复点，又避开 DateTime.MinValue 换算 Unix 时间的边界问题。
                //   历史页的"最近 N 天"过滤保持不变：那里是变化日志的浏览视图。
                var from = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var points = _restoreFlow.ListTimePoints(_selectedRoot.Id, from);
                var latest = points.FirstOrDefault();
                var rows = new List<PointRow>();

                foreach (var p in points)
                {
                    // 让引擎判断"这个恢复点是否明显不完整"，界面只负责如实展示
                    var row = new PointRow
                    {
                        Point = p,
                        IsCurrent = latest is not null && p.SnapshotId == latest.SnapshotId,
                    };
                    try
                    {
                        var info = _restoreFlow.DescribePoint(p.SnapshotId);
                        if (info is not null)
                        {
                            row.IsSuspect = info.Value.IsSuspect;
                            row.SuspectReason = info.Value.SuspectReason;
                            row.CanDelete = info.Value.CanDelete;
                            row.DeleteBlockReason = info.Value.DeleteBlockReason;
                        }
                    }
                    catch (Exception)
                    {
                        // 健康检查失败不应该阻止列表显示
                    }

                    // 引擎按时间倒序返回 → 先加进来就是"新的在前"。
                    // （这里曾经写成 Points.Reverse()，注释说"新的排前面"而实际把最旧的排到了第一位。）
                    TimePointChoices.Add(row);
                    rows.Add(row);
                }
            }
        }
        catch (Exception ex)
        {
            LastErrorBanner = "读取时间点失败：" + ex.Message;
        }

        TargetPointChoices.Clear();
        foreach (var row in TimePointChoices) TargetPointChoices.Add(row);

        // ② 按优先级确定选中项；找不到就是"没有选中"，绝不留下越界下标
        _selectedTimePointChoice =
            (keepId != 0 ? TimePointChoices.FirstOrDefault(p => p.Point.SnapshotId == keepId) : null)
            ?? TimePointChoices.FirstOrDefault(p => p.IsCurrent)
            ?? TimePointChoices.FirstOrDefault();

        _selectedTargetPoint = keepToId == 0
            ? null
            : TimePointChoices.FirstOrDefault(p => p.Point.SnapshotId == keepToId);

        Raise(nameof(SelectedTimePointChoice));
        Raise(nameof(SelectedTargetPoint));
        Raise(nameof(Points));
        Raise(nameof(SelectedPoint));
        Raise(nameof(SelectedPointIndex));
        Raise(nameof(SelectedPointText));
        Raise(nameof(PointsIsEmpty));
        Raise(nameof(HasSelectedPoint));
        RaiseEnds();
        LoadBrowseDirectory();
    }

    private void RaiseEnds()
    {
        Raise(nameof(IsTargetCurrentState));
        Raise(nameof(TargetArrowLabel));
        Raise(nameof(FromEndText));
        Raise(nameof(TargetEndText));
        Raise(nameof(RestoreDirectionText));
        Raise(nameof(SelectedTimePointChoice));
        Raise(nameof(SelectedTargetPoint));
        // SelectedPoint 系列都是 _selectedTimePointChoice 的派生结果：
        // 这里显式广播一次，保证"交换两端"之后它们不会停留在旧值上。
        Raise(nameof(SelectedPoint));
        Raise(nameof(SelectedPointIndex));
        Raise(nameof(SelectedPointText));
        Raise(nameof(HasSelectedPoint));
        Raise(nameof(BrowseHeader));
        Raise(nameof(BrowsePathText));
        Raise(nameof(CanGoUp));
        OnRestoreSelectionChanged();
    }

    /// <summary>把某个时间点的清单装进缓存（同一个快照只读一次）。</summary>
    private IReadOnlyList<ManifestEntry>? LoadManifestEntries(long snapshotId)
    {
        try
        {
            if (_manifestCache.TryGetValue(snapshotId, out var cached)) return cached;

            var list = _restoreFlow.LoadManifestEntries(snapshotId);
            if (list is null) return null;
            _manifestCache[snapshotId] = list;
            return list;
        }
        catch (Exception ex)
        {
            InfoBanner = "读取这个时间点的文件列表失败：" + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 把浏览器切到"当前时间点的当前目录"。
    /// 只列出这个时间点里存在的条目 —— 用户看到的是**那个时刻的文件结构**，不是"变化清单"。
    /// </summary>
    private void LoadBrowseDirectory()
    {
        RestoreBrowse.Clear();
        var snapRow = _selectedTimePointChoice;
        if (snapRow is null) { OnRestoreSelectionChanged(); return; }

        // 缓存命中（同一个时间点 + 同一个目录）：直接用上次算好的行，不再查库、不再排序
        var cacheKey = snapRow.Point.SnapshotId + "|" + _browsePath;
        if (!_dirCache.TryGetValue(cacheKey, out var rows))
        {
            rows = BuildBrowseRows(snapRow);
            if (rows is null) { OnRestoreSelectionChanged(); return; }
            _dirCache[cacheKey] = rows;
        }

        foreach (var row in rows)
        {
            // 勾选与删除标记每次都按当前集合重算（缓存的是"目录内容"，不是"用户选了什么"）
            row.IsSelected = _pickedKeys.Contains(KeyOf(row.SnapshotId, row.RelativePath));
            row.IsMarkedForDeletion = _deletionPaths.Contains(row.RelativePath);
            RestoreBrowse.Add(row);
        }

        Raise(nameof(BrowseIsEmpty));
        Raise(nameof(BrowseEmptyText));
        OnRestoreSelectionChanged();
    }

    /// <summary>构建某个时间点、当前目录下的行（已排序）。返回 null 表示读取失败。</summary>
    private List<RestoreBrowseRow>? BuildBrowseRows(PointRow snapRow)
    {
        var entries = LoadManifestEntries(snapRow.Point.SnapshotId);
        if (entries is null) return null;

        var prefix = string.IsNullOrEmpty(_browsePath) ? string.Empty : _browsePath + "/";
        var rows = new List<RestoreBrowseRow>();

        foreach (var e in entries)
        {
            if (e.RelativePath.Length == 0) continue;
            if (prefix.Length == 0)
            {
                if (e.RelativePath.Contains('/')) continue;   // 只显示根下的直接子项
            }
            else
            {
                if (!e.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = e.RelativePath[prefix.Length..];
                if (rest.Length == 0 || rest.Contains('/')) continue;   // 只显示当前目录的直接子项
            }

            var row = new RestoreBrowseRow
            {
                RelativePath = e.RelativePath,
                Kind = e.Kind,
                Size = e.Size,
                MtimeUtc = e.MtimeUtc,
                SnapshotId = snapRow.Point.SnapshotId,
                SnapshotLabel = snapRow.IdentifiedTime,
            };
            row.PropertyChanged += OnBrowseRowPropertyChanged;
            rows.Add(row);
        }

        // 目录排在文件前面，同类按名字排
        return rows.OrderByDescending(r => r.IsDirectory)
                   .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    /// <summary>双击文件夹进入（或双击".."返回上一级）。</summary>
    private void OpenBrowseRow(RestoreBrowseRow? row)
    {
        if (row is null) return;
        if (!row.IsDirectory) return;
        _browsePath = row.RelativePath;
        LoadBrowseDirectory();
    }

    /// <summary>返回上一级目录。</summary>
    private void GoUpDirectory()
    {
        if (string.IsNullOrEmpty(_browsePath)) return;
        var idx = _browsePath.LastIndexOf('/');
        _browsePath = idx < 0 ? string.Empty : _browsePath[..idx];
        LoadBrowseDirectory();
    }

    /// <summary>回到根目录。</summary>
    private void GoRootDirectory()
    {
        _browsePath = string.Empty;
        LoadBrowseDirectory();
    }

    /// <summary>全选：只针对**当前浏览列表中还没勾选的文件**（目录不参与）。</summary>
    private void SelectAllRestoreFiles()
        => BulkSelection(() =>
        {
            foreach (var row in RestoreBrowse.Where(r => r.CanSelect && !r.IsSelected).ToList())
                row.IsSelected = true;
        });

    /// <summary>取消全选：只针对当前浏览列表。</summary>
    private void ClearRestoreSelection()
        => BulkSelection(() =>
        {
            foreach (var row in RestoreBrowse.Where(r => r.CanSelect && r.IsSelected).ToList())
                row.IsSelected = false;
        });

    /// <summary>反选：把当前浏览列表里每个文件的勾选状态取反（只影响当前列表）。</summary>
    private void InvertRestoreSelection()
        => BulkSelection(() =>
        {
            foreach (var row in RestoreBrowse.Where(r => r.CanSelect).ToList())
                row.IsSelected = !row.IsSelected;
        });

    /// <summary>从恢复集合（或删除标记）里移除一项。</summary>
    private void RemovePicked(RestorePickedRow? row)
    {
        if (row is null) return;
        if (row.IsDeletion)
        {
            _deletionPaths.Remove(row.RelativePath);
            RestorePicked.Remove(row);
            var hitPath = RestoreBrowse.FirstOrDefault(r =>
                PathUtil.Comparer.Equals(r.RelativePath, row.RelativePath));
            if (hitPath is not null) hitPath.IsMarkedForDeletion = false;
        }
        else
        {
            RestorePicked.Remove(row);
            _pickedKeys.Remove(KeyOf(row.SnapshotId, row.RelativePath));
            // 浏览器里对应的那一行也要同步取消勾选
            var hit = RestoreBrowse.FirstOrDefault(r => r.SnapshotId == row.SnapshotId &&
                                                       PathUtil.Comparer.Equals(r.RelativePath, row.RelativePath));
            if (hit is not null) hit.IsSelected = false;
        }
        OnRestoreSelectionChanged();
    }

    private void ClearAllPicked()
        => BulkSelection(() =>
        {
            RestorePicked.Clear();
            _pickedKeys.Clear();
            _deletionPaths.Clear();
            foreach (var row in RestoreBrowse)
            {
                row.IsSelected = false;
                row.IsMarkedForDeletion = false;
            }
        });

    // ── 自定义删除 ────────────────────────────────────────────────────────
    //  勾选 = "从这个时间点把它恢复回来"
    //  标记删除 = "执行恢复后把它删掉"
    //  两者互斥；删除同样先留安全点，所以删错了可以整体撤销。

    /// <summary>把当前浏览列表里勾选的文件标记为"要删除"。</summary>
    private void MarkSelectedForDeletion()
    {
        var targets = RestoreBrowse.Where(r => r.CanSelect && r.IsSelected).ToList();
        if (targets.Count == 0)
        {
            InfoBanner = "先在列表里勾选要删除的文件，再点「标记删除」。";
            return;
        }

        BulkSelection(() =>
        {
            foreach (var row in targets)
            {
                row.IsSelected = false;          // 同一行不能既恢复又删除
                row.IsMarkedForDeletion = true;
                _deletionPaths.Add(row.RelativePath);
            }
        });
        InfoBanner = $"已标记删除 {_deletionPaths.Count} 个文件。执行前会自动留安全点，删错了可以撤销。";
    }

    /// <summary>取消当前浏览列表里的删除标记。</summary>
    private void UnmarkDeletion()
    {
        var targets = RestoreBrowse.Where(r => r.IsMarkedForDeletion).ToList();
        if (targets.Count == 0)
        {
            InfoBanner = "当前目录里没有标记删除的文件。";
            return;
        }
        BulkSelection(() =>
        {
            foreach (var row in targets)
            {
                row.IsMarkedForDeletion = false;
                _deletionPaths.Remove(row.RelativePath);
            }
        });
        InfoBanner = "已取消当前目录的删除标记。";
    }

    private void ClearAllDeletions() => BulkSelection(() =>
    {
        _deletionPaths.Clear();
        foreach (var row in RestoreBrowse) row.IsMarkedForDeletion = false;
    });

    private void TogglePickedList()
    {
        PickedListOpen = !PickedListOpen;
    }

    // ── 历史记录清理 ──────────────────────────────────────────────────────
    //  用户要有权把自己不想留的记录删掉（"这些文件变化是我自己弄的，不想留在里面"）。
    //  ⚠ 删事件是不可撤销的，所以必须二次确认，并把删了多少条如实告诉他。

    /// <summary>删掉列表里选中的那一条变化记录。</summary>
    private void DeleteSelectedEvent()
    {
        var row = SelectedEvent;
        if (row is null)
        {
            InfoBanner = "先在历史列表里选一条记录。";
            return;
        }

        var confirm = DangerBox.Show(
            $"从历史里删除这一条记录？\n\n{row.TimeText}　{row.OpText}　{row.PathText}\n\n" +
            "· 只删除这条「变化记录」，磁盘上的文件不会被改动。\n" +
            "· 已经保存的时间点不受影响。\n" +
            "· 删除后无法恢复这条记录本身。\n\n继续吗？",
            "删除这条历史记录",
            System.Windows.MessageBoxButton.OKCancel);
        if (confirm != System.Windows.MessageBoxResult.OK) return;

        try
        {
            var ok = _rt.Events.DeleteById(row.Event.Id);
            ReloadTimeline();
            ReloadHome();
            SetStatusNote(ok ? "已删除这条历史记录。" : "这条记录已经不在了。", 8);
        }
        catch (Exception ex)
        {
            LastErrorBanner = "删除记录失败：" + ex.Message;
        }
    }

    /// <summary>清空当前受保护范围的全部变化记录。</summary>
    private void ClearAllEvents()
    {
        if (_selectedRoot is null) return;
        if (Events.Count == 0)
        {
            InfoBanner = "当前范围里没有可清理的记录。";
            return;
        }

        var confirm = DangerBox.Show(
            $"清空「{_selectedRoot.Path}」的全部变化记录？\n\n" +
            $"将删除 {Events.Count} 条已显示的变化记录。\n\n" +
            "· 只删除「变化记录」，磁盘上的文件不会被改动。\n" +
            "· 已经保存的时间点不受影响，仍然可以恢复。\n" +
            "· 删除后无法撤销。\n\n继续吗？",
            "清空变化记录",
            System.Windows.MessageBoxButton.OKCancel);
        if (confirm != System.Windows.MessageBoxResult.OK) return;

        try
        {
            var deleted = _rt.Events.DeleteAll(_selectedRoot.Id);
            ReloadTimeline();
            ReloadHome();
            StatusText = BuildStatus();
            InfoBanner = $"已清理 {deleted} 条变化记录（文件本身没有被改动）。";
        }
        catch (Exception ex)
        {
            LastErrorBanner = "清空记录失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 清空**全部**历史数据（含恢复点与内容库），把占用的磁盘空间真正还回去。
    ///
    /// 和"清空变化记录"不同：这个连恢复点与内容库一起清，是真正释放空间的那一个。
    /// 受保护文件夹登记保留，但基线会被重置，下次启动重新建立保护基线。
    /// </summary>
    private void PurgeAllHistory()
    {
        var confirm = DangerBox.Show(
            "清空全部历史数据？\n\n" +
            "将要删除：\n" +
            "· 全部变化记录\n" +
            "· 全部时间点（清掉之后就再也回不到过去了）\n" +
            "· 保存的历史文件内容（占空间的大头）\n" +
            "· 恢复记录与文件索引\n\n" +
            "保留：受保护文件夹的登记（不用重新选文件夹）。\n" +
            "磁盘上你的原始文件不会被改动。\n\n" +
            "这一步无法撤销。确定继续吗？",
            "清空全部历史数据",
            System.Windows.MessageBoxButton.OKCancel);
        if (confirm != System.Windows.MessageBoxResult.OK) return;

        try
        {
            SetStatusNote("正在清空历史数据…", 60);
            var (ok, report) = _rt.Maintenance.PurgeAllHistory();
            LoadRoots();
            ReloadTimeline();
            ReloadHome();
            ReloadPoints();
            Raise(nameof(StorageLocationText));
            InfoBanner = report;
            StatusText = BuildStatus();
            if (!ok) LastErrorBanner = report;
        }
        catch (Exception ex)
        {
            LastErrorBanner = "清空失败：" + ex.Message;
        }
    }

    // ── 存储位置（数据 + 运行日志放哪个盘） ────────────────────────────────

    /// <summary>存储位置说明（"数据与日志放在哪个盘"）。</summary>
    public string StorageLocationText =>
        $"当前存储位置：{_rt.DataDirectory}" +
        (_rt.DataDirectoryIsFallback ? "（程序目录，因为默认位置不可写）" : string.Empty);

    /// <summary>让用户挑一个文件夹，把数据与运行日志放到别的盘。下次启动生效。</summary>
    private void PickStorageFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择用来存放历史数据与运行日志的文件夹",
            Multiselect = false,
        };
        try
        {
            if (dlg.ShowDialog() != true) return;
            var picked = dlg.FolderName;
            if (string.IsNullOrWhiteSpace(picked)) return;

            var target = Path.Combine(picked, "LastRegretData");
            AppRuntime.SetCustomDataDirectory(target);
            _pendingStorageDir = target;

            System.Windows.MessageBox.Show(
                $"已把存储位置设为：\n{target}\n\n" +
                "· **下次启动程序时生效**（历史数据已经打开，中途换位置不安全）。\n" +
                "· 现在的历史数据不会自动搬过去；需要的话请手动把旧目录里的内容复制过去，或者先用「清空记录」清理。\n" +
                "· 原来的位置会保留，确认新位置可用之后你可以自己删掉它。",
                "存储位置已设置",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);

            Raise(nameof(PendingStorageText));
            Raise(nameof(HasPendingStorage));
            Raise(nameof(StorageLocationText));
        }
        catch (Exception ex)
        {
            LastErrorBanner = "设置存储位置失败：" + ex.Message;
        }
    }

    /// <summary>改回默认位置。</summary>
    private void ResetStorageFolder()
    {
        AppRuntime.SetCustomDataDirectory(null);
        _pendingStorageDir = null;
        Raise(nameof(PendingStorageText));
        Raise(nameof(HasPendingStorage));
        Raise(nameof(StorageSummary));
        SetStatusNote("已改回默认存储位置，下次启动生效。", 10);
    }

    /// <summary>在资源管理器里打开当前存储位置（用户要自己看/清理时方便）。</summary>
    private void OpenStorageFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _rt.DataDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LastErrorBanner = "打开目录失败：" + ex.Message;
        }
    }

    /// <summary>在资源管理器里打开日志目录（让用户能自己看日志里写了什么）。</summary>
    private void OpenLogFolder()
    {
        try
        {
            var dir = Path.Combine(_rt.DataDirectory, "logs");
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LastErrorBanner = "打开日志目录失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 把全部保护范围连同历史与内容一起清掉，回到"从没用过"的状态。
    ///
    /// 与「清空全部历史数据」的区别：那个只清历史、保留保护范围登记；
    /// 这个连登记一起移除，所以用户想彻底清干净（例如转手卖机器、或想重新开始）时
    /// 不必在资源管理器里手动找目录删。
    /// 磁盘上的原始文件一律不动 —— 这一点必须在确认框里说清楚。
    /// </summary>
    private void ClearAllData()
    {
        var roots = _protection.ListRoots().ToList();
        var confirm = DangerBox.Show(
            "\u8fd9\u4f1a\u6e05\u7a7a\u300c\u56de\u6eaf\u300d\u8bb0\u5f55\u7684\u5168\u90e8\u6570\u636e\uff0c\u5e76\u79fb\u9664\u6240\u6709\u4fdd\u62a4\u6587\u4ef6\u5939\uff1a\n\n" +
            $"· 保护文件夹登记：{roots.Count} 个（会被移出保护范围）\n" +
            "· 变化记录、时间点、文件清单索引、恢复记录、保存的历史内容：全部删除\n\n" +
            "❗ 磁盘上的原始文件不会被删除或修改，只是以后不再记录它们的变化。\n" +
            "❗ 删除后无法撤销，历史将永久消失。\n\n" +
            $"数据目录：{_rt.DataDirectory}\n\n" +
            "确定要清空吗？",
            "清空全部数据",
            System.Windows.MessageBoxButton.OKCancel);
        if (confirm != System.Windows.MessageBoxResult.OK) return;

        try
        {
            // 先移除保护范围（连历史一起），再清剩余内容库
            foreach (var root in roots)
            {
                try { _protection.RemoveRoot(root.Id, true); }
                catch (Exception) { /* 单个失败不应挡住整体清理 */ }
            }
            var (ok, report) = _rt.Maintenance.PurgeAllHistory();

            _selectedRoot = null;
            ClearBrowseCaches();
            LoadRoots();
            ReloadTimeline();
            ReloadPoints();
            RefreshRestoreHistory();
            StatusText = BuildStatus();
            InfoBanner = ok
                ? "已清空全部数据，保护范围也已移除。" + report
                : "清空未完成：" + report;
        }
        catch (Exception ex)
        {
            LastErrorBanner = "清空失败：" + ex.Message;
        }
    }

    private string? _pendingStorageDir;

    public bool HasPendingStorage => !string.IsNullOrEmpty(_pendingStorageDir);

    public string PendingStorageText => _pendingStorageDir is null
        ? string.Empty
        : $"已设为：{_pendingStorageDir}　（重启程序后生效）";

    private void OnRestoreSelectionChanged()
    {
        // 批量勾选（全选/反选/取消全选/清空）期间不做逐行刷新：
        // 200 行 × 9 个绑定属性 = 1800 次通知，但用户只会看到最后一次的结果。
        if (_bulkSelection) return;

        Raise(nameof(SelectedRestoreCount));
        Raise(nameof(PickedSummary));
        Raise(nameof(CanConfirmRestore));
        Raise(nameof(BrowseIsEmpty));
        Raise(nameof(BrowseEmptyText));
        Raise(nameof(BrowseHeader));
        Raise(nameof(BrowsePathText));
        Raise(nameof(CanGoUp));
        Raise(nameof(PickedListClosed));
        Raise(nameof(DeletionCount));
        Raise(nameof(HasDeletions));
        QueueRequery();
    }

    /// <summary>批量勾选期间为 true，逐行通知被压掉。</summary>
    private bool _bulkSelection;

    /// <summary>把一批勾选变更当成一次操作：中途不刷新，结束后统一刷新一次。</summary>
    private void BulkSelection(Action change)
    {
        _bulkSelection = true;
        try { change(); }
        finally { _bulkSelection = false; }
        OnRestoreSelectionChanged();
    }

    /// <summary>当前浏览范围里已勾选的数量（面板底部显示用）。</summary>
    public int SelectedRestoreCount => RestorePicked.Count;

    /// <summary>
    /// 点击中间的箭头：交换"时间点"与"恢复到"两端。
    /// 方向说明与浏览器标题都会跟着变（不是只把箭头转一下）。
    /// </summary>
    private void SwapRestoreEnds()
    {
        var from = _selectedTimePointChoice;
        var to = _selectedTargetPoint;

        if (to is null)
        {
            InfoBanner = "「恢复到」现在是「当前状态」。想反过来推，请在右边下拉框里挑一个目标时间点，再点这个箭头。";
            return;
        }

        _selectedTimePointChoice = to;
        _selectedTargetPoint = from;
        _targetIsCurrentState = false;
        _browsePath = string.Empty;
        RaiseEnds();
        InfoBanner = "已交换两端：现在浏览的是新的来源时间点。恢复集合里的文件不受影响。";
        LoadBrowseDirectory();
    }

    /// <summary>右侧下拉框直接选"当前状态（现在）"。</summary>
    private void UseCurrentStateAsTarget()
    {
        _targetIsCurrentState = true;
        _selectedTargetPoint = null;
        RaiseEnds();
    }

    /// <summary>
    /// 把用户勾选的路径展开成"引擎能吃的精确路径集合"。
    ///
    /// <summary>
    /// 面板上的「确认恢复」：把**恢复集合里的文件**合起来，一次性交给现有 Restore Engine。
    ///
    /// 计划怎么合（分组、目录展开、去重、删除并入、指纹）已下沉到 <see cref="RestoreCoordinator"/>；
    /// 这里只负责确认文案、按钮状态与执行后的界面反应。
    /// </summary>
    private void ConfirmRestoreFromPanel()
    {
        if (_selectedRoot is null) return;
        if (RestorePicked.Count == 0 && _deletionPaths.Count == 0)
        {
            InfoBanner = "恢复集合里还没有文件。请在上面的浏览器里勾选要恢复的文件，或标记要删除的文件。";
            return;
        }

        try
        {
            // 分组、目录展开、去重、删除合并、指纹 —— 全部在恢复协调器里完成。
            // 这里只做界面的事：确认文案怎么措辞、按钮状态、完成后显示什么。
            var picks = RestorePicked
                .Where(p => !p.IsDeletion)
                .Select(p => (SnapshotId: p.SnapshotId, RelativePath: p.RelativePath))
                .ToList();

            var (merged, error, dropped, unavailable, latestTarget) =
                _restoreFlow.BuildMergedPlan(_selectedRoot.Id, picks, _deletionPaths.ToList());

            if (merged is null)
            {
                InfoBanner = error ?? "无法生成恢复计划。";
                return;
            }

            if (dropped > 0)
            {
                InfoBanner = $"有 {dropped} 个文件被重复勾选（同一个路径来自不同时间点），只按第一次选中的版本恢复。";
            }

            // 确认框必须把"要找回的"和"要删掉的"当成两件事分别报数（真实缺陷）。
            //   原来的写法用一个 counts 概括，用户明明做的是"恢复 1 个 + 删除 1 个"，
            //   弹出来的却是"删除 2 个文件" —— 他没法确认自己即将做的事。
            //   这里用的是**计划本身**的计数（merged），也就是真正会执行的操作，
            //   而不是用户勾选的数量：两者在"重复勾选/来源时间点之后新增"时会不一致。
            var pickedRestore = RestorePicked.Count(p => !p.IsDeletion);
            var picksByTime = RestorePicked
                .Where(p => !p.IsDeletion)
                .GroupBy(p => p.SnapshotLabel)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            var willRestoreFiles = merged.RestoreCount;
            var willRestoreDirs = merged.CreateDirectoryCount;
            var willRemoveFiles = merged.RemoveCount;
            var willRemoveDirs = merged.RemoveDirectoryCount;
            var markedDeletions = _deletionPaths.Count;

            var lines = new List<string>();
            if (willRestoreFiles > 0 || willRestoreDirs > 0)
            {
                lines.Add($"将恢复 {willRestoreFiles} 个文件" +
                          (willRestoreDirs > 0 ? $"、{willRestoreDirs} 个文件夹" : string.Empty) + "：");

                // ── 必须列出**实际要恢复的文件**，而不是只给一个计数 ──
                // 用户可能从不同时间点各勾了几项（跨时间点勾选是刻意允许的），
                // 只写"10:41（2 个）"的话他无法确认最终到底动哪些文件。
                // 每段最多列 6 个，其余用"还有 N 个"如实交代。
                foreach (var group in picksByTime)
                {
                    var paths = group.Select(p => p.RelativePath).ToList();
                    var shown = string.Join("、", paths.Take(6));
                    var more = paths.Count > 6 ? $"，…还有 {paths.Count - 6} 个" : string.Empty;
                    lines.Add($"  · {group.Key}：{shown}{more}");
                }

                // ── 口径说明：勾选数 ≠ 计划步数 ──
                // 与目标状态本来就一致的那些勾选项不会产生任何动作，
                // 不解释的话用户会看到"我勾了 3 个，怎么只说恢复 1 个"。
                var plannedRestores = merged.RestoreCount + merged.CreateDirectoryCount;
                if (pickedRestore > plannedRestores)
                {
                    lines.Add($"  （另外 {pickedRestore - plannedRestores} 项与目标状态一致，无需改动）");
                }
            }
            if (willRemoveFiles > 0 || willRemoveDirs > 0)
            {
                lines.Add($"将删除 {willRemoveFiles} 个文件" +
                          (willRemoveDirs > 0 ? $"、{willRemoveDirs} 个文件夹" : string.Empty) + "：");
                lines.AddRange(_deletionPaths.Take(6).Select(p => "  · " + p));
                if (markedDeletions > 6) lines.Add($"  · …还有 {markedDeletions - 6} 个");
            }

            var extraRemovals = willRemoveFiles - markedDeletions;
            var confirm = DangerBox.Show(
                string.Join("\n", lines) + "\n\n" +
                (extraRemovals > 0
                    ? $"上面要删的里面，有 {extraRemovals} 个是各自来源时间点之后新增的文件（恢复到这个时间点就会少掉它们）。\n"
                    : string.Empty) +
                $"不可恢复的内容：{unavailable} 个。\n\n" +
                "· 执行前会自动创建安全点，所以这次操作整体可以撤销。\n" +
                "· 删除的文件会先把内容留存一份，再删。\n" +
                "· 预览之后又被改过的文件会被跳过，不会被覆盖、也不会被误删。\n\n继续吗？",
                "确认执行",
                System.Windows.MessageBoxButton.OKCancel);
            if (confirm != System.Windows.MessageBoxResult.OK) return;

            // 计划与指纹直接闭包捕获，不再放进字段 —— 少一个"当前是哪个计划"的状态来源。
            var plan = merged;
            var fingerprint = merged.Fingerprint;

            IsRestoreDone = false;
            Raise(nameof(IsRestoreDone));

            var savedLabel = markedDeletions > 0 && pickedRestore == 0
                ? $"自定义删除（{markedDeletions} 个文件）"
                : latestTarget is null
                    ? "自定义恢复"
                    : "自定义恢复（" + latestTarget.Value.ToString("M月d日 HH:mm", CultureInfo.InvariantCulture) + " 等）";

            RunRestore(() => _restoreFlow.Execute(plan, fingerprint), savedLabel);
        }
        catch (Exception ex)
        {
            InfoBanner = "生成执行计划失败：" + ex.Message;
        }
    }

    /// <summary>回到第一步（重新选时间点）。</summary>

    /// <summary>
    /// 从"确认恢复"退回上一步。
    ///
    /// ⚠ 这里**只退步骤、不清预览**：
    ///     用户按"返回上一步"是想再看一遍会改哪些文件，不是想取消整件事。
    ///     如果连预览一起清掉，下一步的按钮会因为 PreviewActive=false 直接消失，
    ///     用户就卡在那一步 —— 既不能继续也不能回去。
    /// </summary>
}
