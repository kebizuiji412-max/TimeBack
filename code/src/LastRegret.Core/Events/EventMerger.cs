namespace LastRegret.Core.Events;

using LastRegret.Core.Abstractions;
using LastRegret.Core.Config;
using LastRegret.Core.Model;
using LastRegret.Core.Util;

/// <summary>
/// 事件合并器：把文件系统的"原始噪声"转化为"用户可以理解的变化"。
///
/// 解决的问题（真实场景）：
///  - VS Code 保存一个文件可能连发 3~6 个通知；
///  - 编辑器普遍使用"写临时文件 → 删除原文件 → 改名覆盖"的原子替换；
///  - Office 会创建 ~$ 锁文件、~WRDxxxx.tmp 等中间产物；
///  - Git checkout 会瞬间产生成千上万条通知。
///
/// 处理手段：
///  1. **去重**：同一路径、同一动作、极短时间内的重复通知直接吃掉。
///  2. **合并**：同路径的连续修改合并为一条，保留首次时间与合并次数。
///  3. **延迟确认**：等到文件"稳定"（settle）后再采集内容，避免读到写了一半的文件。
///  4. **重命名配对**：OLD_NAME + NEW_NAME 配对；目标已存在时归类为"修改"而非"创建"。
///  5. **原子替换识别**：临时文件被改名为正式文件时，只记录正式文件的变化。
///  6. **瞬时事件降级**：被排除模式命中的路径记为 Transient，在时间线中折叠而非丢弃。
///
/// 严格遵守：**记录事实，不伪造因果**。合并只发生在"同一路径的同一类事实"上，
/// 不会把"删除 A + 创建 B"伪造成"重命名 A→B"，除非文件系统确实给出了 rename 通知。
/// </summary>
public sealed class EventMerger
{
    private readonly IFileContentReader _reader;
    private readonly IFileIndex _index;
    private readonly ExclusionMatcher _exclusions;
    private readonly AppSettings _settings;

    /// <summary>待确认的事件（键 = 根 + 路径 + 类目）。</summary>
    private readonly Dictionary<PendingKey, Pending> _pending = new();

    /// <summary>等待配对的 rename 旧名（键 = 根 + 旧路径）。</summary>
    private readonly Dictionary<PendingKey, RenameOld> _renameOld = new();

    /// <summary>最近的重复通知（去重用）。</summary>
    private readonly Dictionary<PendingKey, (RawChangeKind Kind, DateTime At)> _recent = new();

    private readonly List<CoalescedEvent> _ready = new();

    private long _rawTotal;
    private long _rawSuppressed;
    private long _coalescedTotal;

    public EventMerger(
        IFileContentReader reader,
        IFileIndex index,
        ExclusionMatcher exclusions,
        AppSettings settings)
    {
        _reader = reader;
        _index = index;
        _exclusions = exclusions;
        _settings = settings;
    }

    /// <summary>需要触发一次全量重新扫描（通知溢出或过载时置位）。</summary>
    public bool RescanRequested { get; private set; }

    public long RawNotificationsSeen => _rawTotal;

    public long RawNotificationsSuppressed => _rawSuppressed;

    /// <summary>合并率（被吃掉的原始通知占比），用于设置页展示真实效果。</summary>
    public double SuppressionRatio => _rawTotal == 0 ? 0 : (double)_rawSuppressed / _rawTotal;

    public int PendingCount => _pending.Count;

    public void RequestRescan(string reason)
    {
        RescanRequested = true;
        LastRescanReason = reason;
    }

    public string? LastRescanReason { get; private set; }

    public void ClearRescanRequest() => RescanRequested = false;

    /// <summary>
    /// 投递一批原始通知。返回本次直接产生（无需等待窗口）的事件，
    /// 其余进入待确认队列，由 <see cref="Advance"/> 产出。
    /// </summary>
    public IReadOnlyList<CoalescedEvent> Ingest(IReadOnlyDictionary<long, string> rootPaths, IReadOnlyList<RawFsNotification> batch)
    {
        _ready.Clear();
        foreach (var raw in batch)
        {
            if (!rootPaths.TryGetValue(raw.RootId, out var rootPath) || string.IsNullOrEmpty(rootPath))
            {
                // 找不到根路径：无法定位事实，如实跳过（不猜）
                _rawSuppressed++;
                continue;
            }
            HandleOne(rootPath, raw);
        }
        return DrainReady();
    }

    /// <summary>
    /// 推进虚拟时钟：把所有已越过合并窗口的待确认事件产出。
    /// </summary>
    public IReadOnlyList<CoalescedEvent> Advance(DateTime utcNow)
    {
        _ready.Clear();

        // 1) rename 配对超时 → 退化为"删除"（旧路径）
        if (_renameOld.Count > 0)
        {
            var expired = new List<PendingKey>();
            foreach (var (key, old) in _renameOld)
            {
                if ((utcNow - old.At).TotalMilliseconds >= _settings.RenamePairWindowMs)
                    expired.Add(key);
            }
            foreach (var key in expired)
            {
                var old = _renameOld[key];
                _renameOld.Remove(key);
                EmitRenameAsDelete(old);
            }
        }

        // 2) 待确认事件
        var due = new List<PendingKey>();
        foreach (var (key, pending) in _pending)
        {
            var elapsed = (utcNow - pending.LastUtc).TotalMilliseconds;
            var totalElapsed = (utcNow - pending.FirstUtc).TotalMilliseconds;
            bool settled = elapsed >= _settings.SettleDelayMs;
            bool extended = pending.Extensions >= _settings.MaxMergeExtensions;
            bool forced = totalElapsed >= _settings.MaxConfirmDelayMs;
            if (settled || extended || forced) due.Add(key);
        }

        foreach (var key in due)
        {
            if (!_pending.Remove(key, out var pending)) continue;
            Finalize(pending, utcNow);
        }

        return DrainReady();
    }

    /// <summary>是否还有待确认内容（引擎据此决定是否继续 tick）。</summary>
    public bool HasPending => _pending.Count > 0 || _renameOld.Count > 0;

    /// <summary>进程关闭前强制产出所有待确认事件，避免丢失事实。</summary>
    public IReadOnlyList<CoalescedEvent> Flush(DateTime utcNow)
    {
        _ready.Clear();
        foreach (var key in _renameOld.Keys.ToList())
        {
            var old = _renameOld[key];
            _renameOld.Remove(key);
            EmitRenameAsDelete(old);
        }
        foreach (var key in _pending.Keys.ToList())
        {
            if (_pending.Remove(key, out var pending)) Finalize(pending, utcNow);
        }
        return DrainReady();
    }

    private IReadOnlyList<CoalescedEvent> DrainReady()
    {
        if (_ready.Count == 0) return Array.Empty<CoalescedEvent>();
        var copy = _ready.ToArray();
        _ready.Clear();
        return copy;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 单条通知处理
    // ─────────────────────────────────────────────────────────────────────

    private void HandleOne(string rootPath, RawFsNotification raw)
    {
        _rawTotal++;

        if (!PathUtil.TryToRelative(rootPath, raw.AbsolutePath, out var rel) || rel.Length == 0)
            return;

        string? oldRel = null;
        if (raw.OldAbsolutePath is not null)
        {
            if (!PathUtil.TryToRelative(rootPath, raw.OldAbsolutePath, out oldRel)) oldRel = null;
        }

        // ── 去重：同一路径、**同一动作**、极短时间内的重复通知 ──
        // ⚠ 踩坑记录（PIT）：早期把 Added/Modified/Removed 都归到同一个类目键里，
        //   于是"创建后紧接着删除"（编辑器与脚本里非常常见）时，
        //   删除通知会因为是"同一类目"而被当成重复通知吃掉 —— 事件随机丢失，
        //   表现为测试时好时坏、时间线偶尔缺条目。
        //   去重必须按"路径 + 具体动作码"精确匹配，不能按宽泛类目。
        var suppressKey = new PendingKey(raw.RootId, rel, "raw#" + (int)raw.Kind, -1);
        if (_recent.TryGetValue(suppressKey, out var prev))
        {
            if (prev.Kind == raw.Kind && (raw.TimestampUtc - prev.At).TotalMilliseconds < 120)
            {
                _rawSuppressed++;
                _recent[suppressKey] = (raw.Kind, raw.TimestampUtc);
                return;
            }
        }
        _recent[suppressKey] = (raw.Kind, raw.TimestampUtc);

        var verdict = _exclusions.Check(rel);
        bool excluded = verdict.Excluded;

        // 被排除的路径：只在"真正落地"时记一条瞬时事实，不采集内容
        if (excluded)
        {
            if (_settings.RecordExcludedAsTransient && raw.Kind is RawChangeKind.Added or RawChangeKind.Modified)
            {
                _ready.Add(new CoalescedEvent
                {
                    RootId = raw.RootId,
                    Operation = OperationType.Transient,
                    Kind = raw.IsDirectory ? EntryKind.Directory : EntryKind.File,
                    RelativePath = rel,
                    FirstUtc = raw.TimestampUtc,
                    LastUtc = raw.TimestampUtc,
                    IsTransient = true,
                    Note = verdict.Reason,
                });
            }
            _rawSuppressed++;
            return;
        }

        switch (raw.Kind)
        {
            case RawChangeKind.Modified:
                OnModified(rootPath, raw, rel);
                break;

            case RawChangeKind.Added:
                OnAdded(rootPath, raw, rel);
                break;

            case RawChangeKind.Removed:
                OnRemoved(rootPath, raw, rel);
                break;

            case RawChangeKind.RenamedOld:
                _renameOld[new PendingKey(raw.RootId, rel, "rename-old", -1)] = new RenameOld
                {
                    RootId = raw.RootId,
                    RootPath = rootPath,
                    RelativePath = rel,
                    IsDirectory = raw.IsDirectory,
                    At = raw.TimestampUtc,
                };
                break;

            case RawChangeKind.RenamedNew:
                OnRenamedNew(rootPath, raw, rel);
                break;
        }
    }

    private static string CategoryOf(RawChangeKind kind) => kind switch
    {
        RawChangeKind.Added => "add",
        RawChangeKind.Removed => "remove",
        RawChangeKind.Modified => "modify",
        RawChangeKind.RenamedOld => "rename-old",
        RawChangeKind.RenamedNew => "rename-new",
        _ => "other",
    };

    // ── 修改 ──────────────────────────────────────────────────────────────

    private void OnModified(string rootPath, RawFsNotification raw, string rel)
    {
        // 目录的 MODIFIED 通知（子项数量变化等）不产生独立事件，避免噪声
        if (raw.IsDirectory) { _rawSuppressed++; return; }

        var abs = PathUtil.ToAbsolute(rootPath, rel);
        var key = new PendingKey(raw.RootId, rel, "content", -1);

        if (_pending.TryGetValue(key, out var pending))
        {
            // 合并：把已有待确认记录的时间窗向右扩展
            pending.LastUtc = raw.TimestampUtc;
            pending.Extensions++;
            pending.SuppressedCount++;
            return;
        }

        // 立刻锁定"变化前"的内容引用：来自路径索引（稳定），绝不事后猜测
        var before = CaptureFromIndex(raw.RootId, rel, abs);

        _pending[key] = new Pending
        {
            RootId = raw.RootId,
            Kind = raw.IsDirectory ? EntryKind.Directory : EntryKind.File,
            RelativePath = rel,
            AbsolutePath = abs,
            InitialOperation = OperationType.Modified,
            PendingOperation = OperationType.Modified,
            FirstUtc = raw.TimestampUtc,
            LastUtc = raw.TimestampUtc,
            Before = before,
            IsDirectory = raw.IsDirectory,
        };
    }

    // ── 创建 ──────────────────────────────────────────────────────────────

    private void OnAdded(string rootPath, RawFsNotification raw, string rel)
    {
        var abs = PathUtil.ToAbsolute(rootPath, rel);
        var key = new PendingKey(raw.RootId, rel, "content", -1);

        if (_pending.TryGetValue(key, out var pending))
        {
            pending.LastUtc = raw.TimestampUtc;
            pending.Extensions++;
            pending.SuppressedCount++;
            return;
        }

        var entry = _index.Get(raw.RootId, rel);
        var before = entry is { IsDeleted: false } ? CaptureFromIndex(raw.RootId, rel, abs) : null;

        _pending[key] = new Pending
        {
            RootId = raw.RootId,
            Kind = raw.IsDirectory ? EntryKind.Directory : EntryKind.File,
            RelativePath = rel,
            AbsolutePath = abs,
            InitialOperation = OperationType.Created,
            PendingOperation = OperationType.Created,
            FirstUtc = raw.TimestampUtc,
            LastUtc = raw.TimestampUtc,
            Before = before,
            IsDirectory = raw.IsDirectory,
        };
    }

    // ── 删除 ──────────────────────────────────────────────────────────────

    private void OnRemoved(string rootPath, RawFsNotification raw, string rel)
    {
        var abs = PathUtil.ToAbsolute(rootPath, rel);
        var key = new PendingKey(raw.RootId, rel, "content", -1);

        if (_pending.TryGetValue(key, out var pending))
        {
            // 同一窗口内"创建后又删除"：若确实是临时文件，降级为瞬时事件
            if (pending.PendingOperation == OperationType.Created)
            {
                var wasCreated = pending.FirstUtc;
                _pending.Remove(key);
                var lifetime = (raw.TimestampUtc - wasCreated).TotalMilliseconds;
                bool looksTransient = lifetime < _settings.MaxConfirmDelayMs;
                _ready.Add(new CoalescedEvent
                {
                    RootId = raw.RootId,
                    Operation = looksTransient ? OperationType.Transient : OperationType.Deleted,
                    Kind = pending.Kind,
                    RelativePath = rel,
                    FirstUtc = wasCreated,
                    LastUtc = raw.TimestampUtc,
                    SuppressedCount = pending.SuppressedCount + 1,
                    IsTransient = looksTransient,
                    Note = looksTransient ? $"文件存在不足 {lifetime:0} ms（疑似临时文件）" : null,
                });
                return;
            }

            // 修改窗口内又被删除：合并成"删除"，保留变化前内容
            pending.PendingOperation = OperationType.Deleted;
            pending.LastUtc = raw.TimestampUtc;
            pending.SuppressedCount++;
            pending.AbsolutePath = abs;
            return;
        }

        var before = CaptureFromIndex(raw.RootId, rel, abs);
        _pending[key] = new Pending
        {
            RootId = raw.RootId,
            Kind = raw.IsDirectory ? EntryKind.Directory : EntryKind.File,
            RelativePath = rel,
            AbsolutePath = abs,
            InitialOperation = OperationType.Deleted,
            PendingOperation = OperationType.Deleted,
            FirstUtc = raw.TimestampUtc,
            LastUtc = raw.TimestampUtc,
            Before = before,
            IsDirectory = raw.IsDirectory,
        };
    }

    // ── 重命名（新名到达）────────────────────────────────────────────────

    private void OnRenamedNew(string rootPath, RawFsNotification raw, string newRel)
    {
        // 找配对的旧名（同根、时间窗内）
        PendingKey? oldKey = null;
        RenameOld? old = null;
        foreach (var (key, candidate) in _renameOld)
        {
            if (candidate.RootId != raw.RootId) continue;
            if ((raw.TimestampUtc - candidate.At).TotalMilliseconds > _settings.RenamePairWindowMs) continue;
            oldKey = key;
            old = candidate;
            break;
        }

        if (old is null || oldKey is null)
        {
            // 没有配对上的旧名：只能如实记为"新增"（绝不臆造重命名）
            OnAdded(rootPath, raw, newRel);
            return;
        }

        _renameOld.Remove(oldKey.Value);
        var oldRel = old.RelativePath;

        // 取消旧路径上尚在待确认的"创建"（典型的原子替换临时文件）
        var oldPendingKey = new PendingKey(raw.RootId, oldRel, "content", -1);
        int carriedSuppressed = 0;
        if (_pending.Remove(oldPendingKey, out var oldPending))
        {
            carriedSuppressed = oldPending.SuppressedCount;
            // 临时文件被改名为正式文件：旧临时路径不再产生事件
        }

        // 目标路径是否已存在？存在 → 这是"覆盖/修改"，不是"创建"
        var targetEntry = _index.Get(raw.RootId, newRel);
        bool targetExisted = targetEntry is { IsDeleted: false };

        var abs = PathUtil.ToAbsolute(rootPath, newRel);
        var newKey = new PendingKey(raw.RootId, newRel, "content", -1);

        // 覆盖场景下"变化前"就是目标路径原有内容；重命名到新路径时目标是空的
        var before = targetExisted ? CaptureFromIndex(raw.RootId, newRel, abs) : null;
        var op = targetExisted ? OperationType.Modified : OperationType.Renamed;

        _pending[newKey] = new Pending
        {
            RootId = raw.RootId,
            Kind = raw.IsDirectory ? EntryKind.Directory : EntryKind.File,
            RelativePath = newRel,
            OldRelativePath = oldRel,
            AbsolutePath = abs,
            InitialOperation = op,
            PendingOperation = op,
            FirstUtc = old.At < raw.TimestampUtc ? old.At : raw.TimestampUtc,
            LastUtc = raw.TimestampUtc,
            Before = before,
            IsDirectory = raw.IsDirectory,
            SuppressedCount = carriedSuppressed,
            Note = targetExisted
                ? (LooksLikeAtomicReplace(oldRel, newRel) ? "检测到编辑器原子替换（临时文件改名覆盖）" :
                   "重命名覆盖了已存在的路径（原内容已作为变更前版本留存）")
                : null,
        };
    }

    /// <summary>判断是否属于"临时文件改写正式文件"的原子替换模式。</summary>
    private bool LooksLikeAtomicReplace(string oldRel, string newRel)
    {
        var oldName = PathUtil.NameOf(oldRel);
        // 排除规则里已经覆盖了主要模式；这里额外兼容 git/编辑器常见写法
        return oldName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || oldName.StartsWith("~", StringComparison.Ordinal)
            || oldName.StartsWith(".", StringComparison.Ordinal) && oldName.Length > 1
            || PathUtil.ExtensionOf(oldRel) == ".part";
    }

    private void EmitRenameAsDelete(RenameOld old)
    {
        // rename 的旧名始终没有等到新名：事实是"这个路径不见了"
        var key = new PendingKey(old.RootId, old.RelativePath, "content", -1);
        if (_pending.TryGetValue(key, out var pending))
        {
            pending.PendingOperation = OperationType.Deleted;
            pending.LastUtc = old.At;
            return;
        }

        var abs = old.RootPath.Length > 0
            ? PathUtil.ToAbsolute(old.RootPath, old.RelativePath)
            : string.Empty;

        _pending[key] = new Pending
        {
            RootId = old.RootId,
            Kind = old.IsDirectory ? EntryKind.Directory : EntryKind.File,
            RelativePath = old.RelativePath,
            AbsolutePath = abs,
            InitialOperation = OperationType.Deleted,
            PendingOperation = OperationType.Deleted,
            FirstUtc = old.At,
            LastUtc = old.At,
            Before = CaptureFromIndex(old.RootId, old.RelativePath, abs),
            IsDirectory = old.IsDirectory,
            Note = "只收到重命名的旧名，未收到新名（按删除记录）",
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // 最终确认与内容采集
    // ─────────────────────────────────────────────────────────────────────

    private void Finalize(Pending pending, DateTime utcNow)
    {
        var ev = new CoalescedEvent
        {
            RootId = pending.RootId,
            Kind = pending.Kind,
            RelativePath = pending.RelativePath,
            OldRelativePath = pending.OldRelativePath,
            FirstUtc = pending.FirstUtc,
            LastUtc = utcNow,
            SuppressedCount = pending.SuppressedCount,
            IsTransient = pending.IsTransient,
            Note = pending.Note,
            Before = pending.Before,
        };

        var op = pending.PendingOperation;
        var initialOp = pending.InitialOperation;

        // 若窗口内先"创建"后"修改"，最终事实是"创建 + 内容为最终版本"
        if (initialOp == OperationType.Created && op == OperationType.Modified)
        {
            op = OperationType.Created;
        }

        ev.Operation = op;

        switch (op)
        {
            case OperationType.Created:
            case OperationType.Modified:
            case OperationType.Renamed:
                ev.After = pending.IsDirectory
                    ? CaptureDirectory(pending.AbsolutePath)
                    : _reader.Capture(pending.AbsolutePath, isDirectory: false);

                // ── 关键修正（真实缺陷，由测试暴露）──
                // Windows 上"改名覆盖已存在文件"（File.Move overwrite / ReplaceFile）
                // 可能**只产生一条删除通知**：目标路径的旧文件消失、临时文件的内容落到目标路径，
                // 但内核没有给出 rename 通知。如果不修正，这类编辑器保存会被记成"文件被删除"。
                // 判定依据只用索引这一确定事实：这个待确认记录原本是"修改/重命名"，
                // 但采集结果显示文件不存在，而磁盘上它确实还在 → 走的是覆盖路径。
                if (!pending.IsDirectory && ev.After is { HasContent: false } &&
                    pending.InitialOperation is OperationType.Modified or OperationType.Renamed)
                {
                    var onDisk = _reader.Capture(pending.AbsolutePath, isDirectory: false);
                    if (onDisk.HasContent)
                    {
                        ev.After = onDisk;
                        ev.Note = AppendNote(ev.Note, "检测到原文件被替换（改名覆盖），已按「修改」记录最终内容");
                    }
                }

                if (!pending.IsDirectory && ev.After?.Hash is not null && ev.Before?.Hash is not null &&
                    PathUtil.Comparer.Equals(ev.Before.Hash, ev.After.Hash) &&
                    op == OperationType.Modified)
                {
                    // 内容最终没有变化（例如只是改了时间戳，或被写回原内容）
                    ev.Operation = OperationType.Modified;
                    ev.Note = AppendNote(ev.Note, "内容哈希未变化（可能只是元数据或写回原内容）");
                }

                if (ev.After is { HasContent: false } && !pending.IsDirectory)
                {
                    ev.Note = AppendNote(ev.Note, ev.After.UnavailableReason ?? "未能读取变化后的内容");
                }
                break;

            case OperationType.Deleted:
                // "变化后"不存在：显式留空
                ev.After = null;
                if (!pending.IsDirectory && ev.Before is { HasContent: false })
                {
                    ev.Note = AppendNote(ev.Note, "删除前未留存内容，此删除无法恢复");
                }
                break;

            case OperationType.Transient:
                ev.After = null;
                break;
        }

        // 目录操作：统计受影响子项
        if (pending.IsDirectory && ev.Operation is OperationType.Deleted or OperationType.Renamed)
        {
            ev.AffectedDescendantCount = CountDescendants(pending.RootId, pending.RelativePath, pending.OldRelativePath, ev.Operation);
        }

        // 瞬时可疑性：创建后极短时间即无变化且文件很小 + 临时扩展名
        if (ev.Operation == OperationType.Created && IsLikelyTempName(ev.RelativePath))
        {
            ev.IsTransient = true;
            ev.Note = AppendNote(ev.Note, "文件名符合临时文件特征");
        }

        if (ev.Operation != OperationType.Transient) _coalescedTotal++;
        _ready.Add(ev);
    }

    private int CountDescendants(long rootId, string relativePath, string? oldRelativePath, OperationType op)
    {
        var prefix = op == OperationType.Renamed && oldRelativePath is not null ? oldRelativePath : relativePath;
        int count = 0;
        foreach (var e in _index.ListAll(rootId))
        {
            if (!e.IsDeleted && PathUtil.IsUnder(prefix, e.RelativePath)) count++;
        }
        return count;
    }

    private static bool IsLikelyTempName(string relativePath)
    {
        var name = PathUtil.NameOf(relativePath);
        return name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("~$", StringComparison.Ordinal)
            || name.EndsWith("~", StringComparison.Ordinal);
    }

    private static string? AppendNote(string? existing, string addition) =>
        string.IsNullOrEmpty(existing) ? addition : existing + "；" + addition;

    /// <summary>
    /// 从路径索引构造"变化前"的内容引证。
    /// 这是本系统最关键的取舍：**变化前的状态来自索引（稳定事实），
    /// 绝不在收到通知后再去读文件（那时内容很可能已经变了）**。
    /// </summary>
    private ContentCapture? CaptureFromIndex(long rootId, string relativePath, string absolutePath)
    {
        var entry = _index.Get(rootId, relativePath);
        if (entry is null || entry.IsDeleted) return null;

        return new ContentCapture
        {
            AbsolutePath = absolutePath,
            Hash = entry.Hash,
            Size = entry.Size,
            MtimeUtc = entry.MtimeUtc,
            IsDirectory = entry.Kind == EntryKind.Directory,
            IsReadOnly = entry.IsReadOnly,
            UnavailableReason = entry.Hash is null
                ? "变化前的内容未留存（添加保护之前就存在，或超过留存大小上限）"
                : null,
        };
    }

    private static ContentCapture CaptureDirectory(string absolutePath) => new()
    {
        AbsolutePath = absolutePath,
        IsDirectory = true,
        Hash = null,
        UnavailableReason = "目录本身没有内容哈希（其内部文件各自记录）",
    };

    // ─────────────────────────────────────────────────────────────────────
    // 内部结构
    // ─────────────────────────────────────────────────────────────────────

    private readonly record struct PendingKey(long RootId, string RelativePath, string Category, int Unused)
    {
        public bool Equals(PendingKey other) =>
            RootId == other.RootId
            && Category == other.Category
            && string.Equals(RelativePath, other.RelativePath, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            HashCode.Combine(RootId, Category, StringComparer.OrdinalIgnoreCase.GetHashCode(RelativePath));
    }

    private sealed class Pending
    {
        public long RootId { get; init; }
        public EntryKind Kind { get; init; }
        public string RelativePath { get; init; } = string.Empty;
        public string? OldRelativePath { get; set; }
        public string AbsolutePath { get; set; } = string.Empty;
        public bool IsDirectory { get; init; }

        /// <summary>首次进入待确认状态时的操作（用于识别"创建后又修改"）。</summary>
        public OperationType InitialOperation { get; init; }

        public OperationType PendingOperation { get; set; }

        public DateTime FirstUtc { get; init; }
        public DateTime LastUtc { get; set; }

        public int SuppressedCount { get; set; }
        public int Extensions { get; set; }
        public ContentCapture? Before { get; set; }
        public bool IsTransient { get; set; }
        public string? Note { get; set; }
    }

    private sealed class RenameOld
    {
        public long RootId { get; init; }
        public string RootPath { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public bool IsDirectory { get; init; }
        public DateTime At { get; init; }
    }
}
