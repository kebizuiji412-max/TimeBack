using System.Globalization;
using LastRegret.Core.Compare;
using LastRegret.Core.Diff;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Engine;

namespace LastRegret.App;

/// <summary>
/// 恢复用迷你文件浏览器里的一行：某个历史时间点里的一条目录或文件。
///
/// 关键区别：这里列的是**那个时间点的文件结构**（不是"发生了什么变化"），
/// 用户像用资源管理器一样双击进入目录、勾选文件。
/// </summary>
public sealed class RestoreBrowseRow : ObservableObject
{
    private bool _isSelected;
    private bool _isMarkedForDeletion;

    public required string RelativePath { get; init; }

    public required EntryKind Kind { get; init; }

    public long Size { get; init; }

    public DateTime? MtimeUtc { get; init; }

    /// <summary>这个条目是从哪个时间点的状态里看到的（决定它用哪个版本恢复）。</summary>
    public required long SnapshotId { get; init; }

    public required string SnapshotLabel { get; init; }

    public bool IsDirectory => Kind == EntryKind.Directory;

    public string Name => PathUtil.NameOf(RelativePath);

    public string Icon => IsDirectory ? "📁" : "📄";

    /// <summary>
    /// 这一行能不能勾选。目录与文件都允许。
    ///
    /// 曾经目录被禁止勾选（注释写的是"勾目录等于整目录恢复，本期不做，避免误操作"），
    /// 后果是"误删整个文件夹"这个最常见场景彻底无解：
    /// 用户删掉 <c>2026-9/</c>，在历史里能看到它，但它的勾选框是灰的、
    /// 点上去毫无反应，而"确认恢复"按钮又因为集合为空而禁用 ——
    /// 界面上表现为"点了恢复，什么都没发生"。
    /// 现在目录可以勾选，勾选后会在生成计划时**展开成整棵子树**（见 MainViewModel.ExpandPickedPaths）。
    /// </summary>
    public bool CanSelect => true;

    /// <summary>勾选状态 = 该文件是否在恢复集合里（与浏览位置无关，切换时间点/目录都不会丢）。</summary>
    public bool IsSelected
    {
        get => _isSelected && CanSelect;
        set
        {
            if (!CanSelect) return;
            if (Set(ref _isSelected, value)) Raise(nameof(CheckMark));
        }
    }

    public string CheckMark => IsSelected ? "☑" : "☐";

    /// <summary>
    /// 是否被标记为"要删除"。
    ///
    /// 这是独立于勾选的第三种意图：勾选 = 从这个时间点恢复它；
    /// 标记删除 = 执行恢复后把它删掉。两者互斥（同一行不会既是恢复又是删除）。
    /// </summary>
    public bool IsMarkedForDeletion
    {
        get => _isMarkedForDeletion && CanSelect;
        set
        {
            if (!CanSelect) return;
            if (Set(ref _isMarkedForDeletion, value)) Raise(nameof(DisplayName));
        }
    }

    /// <summary>被标记删除时在名字后面加一个明确的标记，不靠颜色单独表达。</summary>
    public string DisplayName => IsMarkedForDeletion ? Name + "　（将删除）" : Name;

    public string SizeText => IsDirectory ? string.Empty : PathUtil.FormatBytes(Size);

    public string TimeText => MtimeUtc is null
        ? string.Empty
        : MtimeUtc.Value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>已加入恢复集合的一个文件（可能来自不同的时间点）。</summary>
public sealed class RestorePickedRow
{
    public required string RelativePath { get; init; }

    public required long SnapshotId { get; init; }

    /// <summary>来源时间点（"今天 15:30"这种给用户看的写法）。</summary>
    public required string SnapshotLabel { get; init; }

    /// <summary>true = 这一项是要被删除的，而不是要恢复的。</summary>
    public bool IsDeletion { get; init; }

    public string Name => PathUtil.NameOf(RelativePath);

    public string Folder => PathUtil.ParentOf(RelativePath);

    public string Display => string.IsNullOrEmpty(Folder) ? Name : Folder + "\\" + Name;
}

/// <summary>时间线上的一行（对应一条已合并的事件）。</summary>
public sealed class EventRow
{
    public required FileEvent Event { get; init; }

    /// <summary>日期分组标题（例如 "今天 · 9月11日"）。</summary>
    public string GroupTitle { get; init; } = string.Empty;

    public bool IsGroupStart { get; init; }

    public string TimeText => Event.TimestampLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string OpText => Event.Operation.ToChinese();

    public string OpTone => Event.Operation switch
    {
        OperationType.Created => "created",
        OperationType.Deleted => "deleted",
        OperationType.Modified => "modified",
        OperationType.Renamed => "renamed",
        OperationType.Moved => "renamed",
        _ => "transient",
    };

    public string PathText => Event.RelativePath;

    public string? OldPathText => Event.OldRelativePath;

    public bool HasOldPath => !string.IsNullOrEmpty(Event.OldRelativePath);

    public string KindText => Event.Kind == EntryKind.Directory ? "目录" : "文件";

    /// <summary>进程归属的措辞必须与置信度匹配（记录事实，不伪造因果）。</summary>
    public string? ProcessText
    {
        get
        {
            if (Event.AttributedProcess is null) return null;
            var name = Windows.Processes.ProcessProbe.DescribeProcess(Event.AttributedProcess);
            var suffix = Event.Confidence switch
            {
                AttributionConfidence.Handler => "（确认持有句柄）",
                AttributionConfidence.Likely => "（该时刻前台进程）",
                AttributionConfidence.Nearby => "（附近检测到）",
                _ => string.Empty,
            };
            return name + suffix;
        }
    }

    public bool HasProcess => ProcessText is not null;

    public string? SizeText
    {
        get
        {
            var after = Event.SizeAfter;
            var before = Event.SizeBefore;
            if (Event.Operation == OperationType.Deleted && before is not null)
                return PathUtil.FormatBytes(before.Value);
            if (after is not null) return PathUtil.FormatBytes(after.Value);
            return null;
        }
    }

    public string? BadgeText
    {
        get
        {
            if (Event.SuppressedCount > 0) return $"合并 {Event.SuppressedCount} 次";
            if (Event.AffectedDescendantCount > 0) return $"影响 {Event.AffectedDescendantCount} 个子项";
            if (Event.IsTransient) return "瞬时";
            return null;
        }
    }

    public bool HasBadge => BadgeText is not null;

    /// <summary>这条事件能否恢复它的"变化前"状态（UI 必须如实显示）。</summary>
    public bool CanRestorePrevious => Event.CanRestorePrevious;

    public string RestoreHint => Event.CanRestorePrevious
        ? "可以恢复此变化之前的状态"
        : "此变化之前的内容未留存，无法恢复";

    public string? Note => Event.Note;

    public bool HasNote => !string.IsNullOrWhiteSpace(Event.Note);
}

/// <summary>"文件"页左侧：一个被记录过的路径。</summary>
public sealed class FileRow
{
    public required long RootId { get; init; }
    public required string RelativePath { get; init; }

    public int ChangeCount { get; init; }

    public DateTime? LastChangeUtc { get; init; }

    public long SizeBytes { get; init; }

    public bool ExistsNow { get; init; }

    public string Name => PathUtil.NameOf(RelativePath);

    public string Directory => PathUtil.ParentOf(RelativePath);

    public string LastChangeText => LastChangeUtc is null
        ? "—"
        : LastChangeUtc.Value.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string SizeText => PathUtil.FormatBytes(SizeBytes);

    public string StatusText => ExistsNow ? "存在" : "已不存在";
}

/// <summary>文件历史里的一个版本。</summary>
public sealed class VersionRow
{
    public required string Hash { get; init; }
    public required DateTime RecordedUtc { get; init; }
    public long Size { get; init; }
    public long? ObjectId { get; init; }
    public bool ContentAvailable { get; init; }
    public string? Note { get; init; }
    public bool IsCurrent { get; init; }

    public string TimeText => RecordedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string SizeText => PathUtil.FormatBytes(Size);

    public string HashShort => Hash.Length >= 12 ? Hash[..12] : Hash;

    public string AvailabilityText => ContentAvailable ? "可恢复" : "内容已被清理（不可恢复）";
}

/// <summary>Diff 视图的一行。</summary>
public sealed class DiffRow
{
    public required string Kind { get; init; }
    public required string Text { get; init; }
    public string Marker { get; init; } = " ";
    public string? OldLineDisplay { get; init; }
    public string? NewLineDisplay { get; init; }

    public static DiffRow From(DiffLine line)
    {
        var chunks = line.Kind == DiffLineKind.Removed ? line.OldChunks : line.NewChunks;
        string text = line.Text;
        if (chunks is { Count: > 0 })
        {
            // 用 «» 标出行内变化区间（比复杂的内联 Run 着色更可靠）
            var c = chunks[0];
            if (c.Start >= 0 && c.Length > 0 && c.Start + c.Length <= text.Length)
            {
                text = text[..c.Start] + "«" + text.Substring(c.Start, c.Length) + "»" + text[(c.Start + c.Length)..];
            }
        }

        return new DiffRow
        {
            Kind = line.Kind switch
            {
                DiffLineKind.Added => "added",
                DiffLineKind.Removed => "removed",
                _ => "context",
            },
            Text = text,
            Marker = line.Marker,
            OldLineDisplay = line.OldLineNumber?.ToString(CultureInfo.InvariantCulture),
            NewLineDisplay = line.NewLineNumber?.ToString(CultureInfo.InvariantCulture),
        };
    }
}



/// <summary>恢复点（时间线节点）。</summary>
public sealed class PointRow : ObservableObject
{
    private string? _note;

    public required SnapshotPoint Point { get; init; }

    /// <summary>内部/管理用的完整时间（设置与恢复点管理里显示）。</summary>
    public string TimeText => Point.TimestampLocal.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// 普通用户看到的时间：今天 11:47 / 昨天 10:15 / 9月10日 09:03。
    /// 用户是照着"我刚才什么时候改的"来找的，所以要跟他的记忆对齐。
    /// </summary>
    public string FriendlyTime
    {
        get
        {
            var t = Point.TimestampLocal;
            var today = DateTime.Today;
            if (t.Date == today) return "今天 " + t.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (t.Date == today.AddDays(-1)) return "昨天 " + t.ToString("HH:mm", CultureInfo.InvariantCulture);
            return t.ToString("M月d日 HH:mm", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 这个时间点凭什么被认出来：优先用户自己留的备注，为空时按"它是怎么来的"回落。
    ///
    /// ⚠ 备注**不是**唯一键：选择永远绑定 PointRow 对象本身（SelectedItem），
    ///   显示文本从不参与反查。这里只解决"人能不能认出它"。
    /// </summary>
    public string IdentityLabel => !string.IsNullOrWhiteSpace(Note) ? Note! : UserKindText;

    /// <summary>
    /// 带身份的时间（时间 · 备注/来源）。下拉、工具栏末端的确认文本、浏览器标题、
    /// 恢复集合的来源标签都用它 —— 只显示"9月15日 02:27"的话，同一分钟里的
    /// 两个时间点在界面上完全一样，用户没有依据确认自己选的是哪一个。
    /// </summary>
    public string IdentifiedTime => FriendlyTime + " · " + IdentityLabel;

    /// <summary>
    /// 弱化显示的编号兜底（备注相同或都为空时，靠它区分同一分钟里的多个时间点）。
    /// </summary>
    public string IdSuffix => " · #" + Point.SnapshotId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 这个时间点是什么：用户自己留的 / 保护开始时 / 恢复前自动留的。
    /// 不向普通用户暴露"手动恢复点 / 基线 / 安全点"这类内部叫法。
    /// </summary>
    public string UserKindText => Point.Kind switch
    {
        SnapshotKind.Manual => "你留的",
        SnapshotKind.Baseline => "保护开始时",
        SnapshotKind.PreRestore => "恢复前自动留的",
        SnapshotKind.PostRestore => "上次恢复后",
        SnapshotKind.Auto => "自动留的",
        _ => Point.KindText,
    };

    public string KindText => Point.KindText;

    public string DetailText => $"{Point.FileCount} 文件 / {Point.DirectoryCount} 目录 · {PathUtil.FormatBytes(Point.TotalBytes)}";

    /// <summary>备注（用户可自定义；为空时回落到系统自动生成的说明）。</summary>
    public string? Note
    {
        get => string.IsNullOrWhiteSpace(_note) ? Point.Note : _note;
        set { if (Set(ref _note, value)) { Raise(nameof(HasNote)); Raise(nameof(EmptyHint)); } }
    }

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    public bool IsCurrent { get; set; }

    /// <summary>恢复点是否"看起来不完整"（由引擎的健康检查给出，不在这里猜）。</summary>
    public bool IsSuspect { get; set; }

    public string? SuspectReason { get; set; }

    public bool HasSuspectReason => IsSuspect && !string.IsNullOrWhiteSpace(SuspectReason);

    /// <summary>文件数为 0 的恢复点通常是"扫描被中断"留下的空壳，必须提醒用户。</summary>
    public bool IsEmpty => Point.FileCount == 0 && Point.DirectoryCount == 0;

    public bool HasEmptyHint => IsEmpty || IsSuspect;

    public string? EmptyHint => IsSuspect
        ? "⚠ " + (SuspectReason ?? "这个时间点的内容可能不完整，建议删除。")
        : (IsEmpty ? "⚠ 这个时间点没有记录到任何文件，建议删除后重新扫描补齐。" : null);

    /// <summary>是否允许删除（由引擎判定；这里只用于按钮可用性提示）。</summary>
    public bool CanDelete { get; set; } = true;

    public string? DeleteBlockReason { get; set; }

    public string DeleteHint => CanDelete
        ? "删除这个时间点（不会改动磁盘文件）"
        : (DeleteBlockReason ?? "该时间点受保护，不能删除");
}

/// <summary>一次恢复操作的展示行（带行内"撤销"动作与撤销可用性说明）。</summary>
public sealed class RestoreRow
{
    private readonly Action<RestoreRow>? _undo;

    public RestoreRow(RestoreOperation operation, Action<RestoreRow>? undo = null)
    {
        Operation = operation;
        _undo = undo;
        UndoCommand = new DelegateCommand(() => _undo?.Invoke(this), () => CanUndo);
    }

    public RestoreOperation Operation { get; }

    public DelegateCommand UndoCommand { get; }

    public string TimeText => Operation.StartedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string TargetText => Operation.TargetTimeLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>只有"完成/部分完成"、且还没被撤销过、且有安全点的操作才能撤销。</summary>
    public bool CanUndo =>
        Operation.UndoneByOperationId is null &&
        Operation.PreRestoreSnapshotId is not null &&
        Operation.Status is RestoreStatus.Completed or RestoreStatus.PartiallyCompleted;

    public string StatusText => Operation.Status.ToChinese() +
        (Operation.Status is RestoreStatus.Completed or RestoreStatus.PartiallyCompleted
            ? $"（成功 {Operation.SucceededCount} / 计划 {Operation.PlannedCount}" +
              (Operation.FailedCount > 0 ? $"，失败 {Operation.FailedCount}" : string.Empty) + "）"
            : string.Empty);

    public string Tone => Operation.Status switch
    {
        RestoreStatus.Completed => "added",
        RestoreStatus.PartiallyCompleted => "modified",
        RestoreStatus.Undone => "renamed",
        RestoreStatus.Failed => "deleted",
        _ => "context",
    };

    public string DetailText => string.IsNullOrWhiteSpace(Operation.Message) ? "—" : Operation.Message!;

    /// <summary>为什么能/不能撤销——必须让用户一眼看懂，而不是对着灰按钮猜。</summary>
    public string UndoHint => CanUndo
        ? "把保护范围恢复到这次恢复之前的状态"
        : Operation.UndoneByOperationId is not null
            ? "该操作已经撤销过了"
            : Operation.PreRestoreSnapshotId is null
                ? "该操作没有恢复前安全点，无法撤销"
                : "该操作没有成功完成，无需撤销";
}

/// <summary>受保护范围的一行。</summary>
public sealed class RootRow : ObservableObject
{
    private bool _enabled;
    private bool _watching;
    private bool _scanning;
    private string? _lastError;
    private long _eventCount;
    private DateTime? _lastEventUtc;
    private string? _scanNote;
    private bool _hasBaseline;

    public required WatchedRoot Root { get; init; }

    public long Id => Root.Id;

    public string Path => Root.Path;

    public string DisplayName => Root.DisplayName;

    /// <summary>扫描进度说明（扫描期间实时更新，界面直接显示）。</summary>
    public string? ScanNote
    {
        get => _scanNote;
        set { if (Set(ref _scanNote, value)) Raise(nameof(StatusText)); }
    }

    private int _scanProgress;
    private int _scanTotal;

    /// <summary>已处理条目数（进度条用）。</summary>
    public int ScanProgress
    {
        get => _scanProgress;
        set { if (Set(ref _scanProgress, value)) { Raise(nameof(HasScanProgress)); Raise(nameof(ScanUserText)); } }
    }

    /// <summary>预估总条目数（0 = 未知，此时界面显示不确定进度）。</summary>
    public int ScanTotal
    {
        get => _scanTotal;
        set { if (Set(ref _scanTotal, value)) { Raise(nameof(HasScanProgress)); Raise(nameof(ScanPercent)); Raise(nameof(ScanUserText)); } }
    }

    public bool HasScanProgress => _scanTotal > 0;

    /// <summary>进度百分比（总数为 0 时无意义，界面用不确定进度动画）。</summary>
    public double ScanPercent => _scanTotal <= 0 ? 0 : Math.Min(100, _scanProgress * 100.0 / _scanTotal);

    /// <summary>
    /// 是否已经完整扫描过一次（内部叫"基线"）。
    /// 界面**不显示这个词**：普通用户只需要知道"准备好了没有"。
    /// </summary>
    public bool HasBaseline
    {
        get => _hasBaseline;
        set { if (Set(ref _hasBaseline, value)) { Raise(nameof(BaselineWarning)); Raise(nameof(HasBaselineWarning)); Raise(nameof(ReadyText)); Raise(nameof(ProtectStateText)); } }
    }

    public bool HasBaselineWarning => !_hasBaseline;

    /// <summary>需要用户重新扫描时的提示：说清"现在怎样、为什么、下一步做什么"。</summary>
    public string? BaselineWarning => _hasBaseline
        ? null
        : "上次准备没有做完，这段时间的文件变化不会被记录。\n点「重新扫描补齐」就能接着做完，你原来的文件不会被改动。";

    /// <summary>扫描完成后给用户看的一句话（代替"已建立基线"）。</summary>
    public string ReadyText => _hasBaseline
        ? "从现在开始，这个文件夹里的变化会被记录。"
        : "正在准备保护你的文件夹……";

    /// <summary>
    /// 保护状态（首页/设置页共用，普通用户语言）。
    ///
    /// 判定顺序就是优先级，**暂停必须排在监听前面**：
    /// 用户点了暂停之后，监听虽然停了，但如果先看 Watching 就会显示成
    /// 「● 已开始保护」，用户以为还在记录（真实缺陷，Release 黑盒压测发现）。
    ///
    /// 另外 <c>_hasBaseline</c> 现在只代表"基线快照存在"，与文件多少无关 ——
    /// 空文件夹也是合法已就绪状态，不会再显示"还没准备好"。
    /// </summary>
    public string ProtectStateText
    {
        get
        {
            if (Scanning) return "正在准备保护…";
            if (!Enabled) return "已暂停保护";
            if (!_hasBaseline) return "还没准备好";
            return Watching ? "● 正在保护" : "● 已开始保护";
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public bool Watching
    {
        get => _watching;
        set { if (Set(ref _watching, value)) Raise(nameof(StatusText)); }
    }

    public bool Scanning
    {
        get => _scanning;
        set { if (Set(ref _scanning, value)) Raise(nameof(StatusText)); }
    }

    public string? LastError
    {
        get => _lastError;
        set { if (Set(ref _lastError, value)) { Raise(nameof(HasError)); Raise(nameof(StatusText)); } }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_lastError);

    public long EventCount
    {
        get => _eventCount;
        set { if (Set(ref _eventCount, value)) Raise(nameof(StatusText)); }
    }

    public DateTime? LastEventUtc
    {
        get => _lastEventUtc;
        set { if (Set(ref _lastEventUtc, value)) Raise(nameof(StatusText)); }
    }

    public string StatusText
    {
        get
        {
            if (Scanning) return ScanNote ?? "正在准备保护…";
            if (!Enabled) return "已暂停保护";
            if (!Watching) return "监听未运行";
            return "正在保护";
        }
    }

    /// <summary>扫描期间给普通用户看的一句进度。不出现"基线/水位/索引"这类词。</summary>
    public string ScanUserText => ScanTotal > 0
        ? $"已检查 {ScanProgress:N0} 个文件（约 {ScanPercent:0}%）"
        : "正在检查文件夹里已有的文件……";

    public string StatsText
    {
        get
        {
            var last = LastEventUtc is null
                ? "暂无变化"
                : "最近变化 " + LastEventUtc.Value.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return $"{last} · 近 24 小时 {EventCount} 条";
        }
    }

    public void RefreshStats(long last24, DateTime? lastEventUtc)
    {
        EventCount = last24;
        LastEventUtc = lastEventUtc;
        Raise(nameof(StatsText));
    }
}

/// <summary>引擎日志的一行（"运行日志"面板）。</summary>
public sealed class LogRow
{
    public required DateTime Utc { get; init; }
    public required string Level { get; init; }
    public required string Message { get; init; }

    public string TimeText => Utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string Tone => Level switch
    {
        "error" => "deleted",
        "warn" => "modified",
        _ => "context",
    };
}
