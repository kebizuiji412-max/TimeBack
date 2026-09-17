using LastRegret.Core.Abstractions;
using LastRegret.Core.Config;
using LastRegret.Core.Events;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Data;
using LastRegret.Windows.Processes;

namespace LastRegret.Engine;

/// <summary>
/// 事件流水线：把"合并后的事件"变成"落库的事实"。
///
/// 它负责的**全部**就是这条链：
/// <code>
/// CoalescedEvent
///   → 保存 before / after 内容（ContentWriter）
///   → 构造 FileEvent
///   → 进程归属（失败也绝不影响事件本身）
///   → 事件落库（AppendRange）
///   → 索引更新（UpdateIndex）
///   → 历史版本登记（AddVersionIfNeeded）
///   → 上报"已落库 N 条"给调度方记账
/// </code>
///
/// 它<b>不</b>负责：Timer、DirectoryWatcher 生命周期、StartWatching / StopWatching、
/// 暂停恢复、扫描、WPF、UI 集合。这些留在 <see cref="WatchService"/>。
///
/// 顺序是刻意的、不可调换：<b>事件先入库（拿到 Id），再更新索引与版本</b>
/// （见 <see cref="Persist"/> 内的注释）。本类只搬动代码位置，不改事务边界。
///
/// 说明：本类依赖较多，是因为它承担的正是原来那个"什么都串一遍"的方法；
/// 每一项都是真实需要，没有为了好看而加的抽象（无接口、无 DTO、无容器）。
/// </summary>
internal sealed class WatchEventPipeline
{
    private readonly IWatchedRootRepository _roots;
    private readonly FileIndexRepository _index;
    private readonly EventRepository _events;
    private readonly IFileVersionRepository _versions;
    private readonly ContentWriter _contentWriter;
    private readonly IProcessProbe _processProbe;
    private readonly IClock _clock;

    /// <summary>设置可以热更新，所以取的是当前值而不是构造时的快照。</summary>
    private readonly Func<AppSettings> _settings;

    /// <summary>运行状态由 <see cref="WatchService"/> 拥有，这里只取不建。</summary>
    private readonly Func<long, RootRuntimeState?> _stateOf;

    /// <summary>报告"某个根刚刚落库了多少条 / 最后一条的时间"，由调度方记账（自动快照用）。</summary>
    private readonly Action<long, DateTime> _onEventPersisted;

    private readonly Func<int> _mergerPendingCount;
    private readonly WatchStatistics _statistics;
    private readonly Action _timelineChanged;

    public WatchEventPipeline(
        IWatchedRootRepository roots,
        FileIndexRepository index,
        EventRepository events,
        IFileVersionRepository versions,
        ContentWriter contentWriter,
        IProcessProbe processProbe,
        IClock clock,
        Func<AppSettings> settings,
        Func<long, RootRuntimeState?> stateOf,
        Action<long, DateTime> onEventPersisted,
        Func<int> mergerPendingCount,
        WatchStatistics statistics,
        Action timelineChanged)
    {
        _roots = roots;
        _index = index;
        _events = events;
        _versions = versions;
        _contentWriter = contentWriter;
        _processProbe = processProbe;
        _clock = clock;
        _settings = settings;
        _stateOf = stateOf;
        _onEventPersisted = onEventPersisted;
        _mergerPendingCount = mergerPendingCount;
        _statistics = statistics;
        _timelineChanged = timelineChanged;
    }

    /// <summary>把一批合并后的事件落库，并维护索引、历史版本与运行状态。</summary>
    public void Persist(IReadOnlyList<CoalescedEvent> produced)
    {
        if (produced.Count == 0) return;

        var settings = _settings();
        var rootCache = new Dictionary<long, WatchedRoot?>();
        var events = new List<FileEvent>(produced.Count);
        var newVersions = new List<FileVersion>();

        foreach (var c in produced)
        {
            if (!rootCache.TryGetValue(c.RootId, out var root))
            {
                root = _roots.Get(c.RootId);
                rootCache[c.RootId] = root;
            }
            if (root is null) continue;

            var maxSize = root.MaxFileSizeBytes > 0 ? root.MaxFileSizeBytes : settings.MaxStoreFileSizeBytes;

            var beforeResult = _contentWriter.Store(c.Before, c.RelativePath, maxSize);
            var afterResult = _contentWriter.Store(c.After, c.RelativePath, maxSize);

            var ev = new FileEvent
            {
                RootId = c.RootId,
                TimestampUtc = c.FirstUtc,
                TimestampLocal = c.FirstUtc.ToLocalTime(),
                Operation = c.Operation,
                Kind = c.Kind,
                RelativePath = c.RelativePath,
                OldRelativePath = c.OldRelativePath,
                SizeBefore = c.Before?.Size ?? beforeResult.Size,
                SizeAfter = c.After?.Size ?? afterResult.Size,
                HashBefore = beforeResult.Hash ?? c.Before?.Hash,
                HashAfter = afterResult.Hash ?? c.After?.Hash,
                ObjectIdBefore = beforeResult.ObjectId,
                ObjectIdAfter = afterResult.ObjectId,
                MtimeBeforeUtc = c.Before?.MtimeUtc,
                MtimeAfterUtc = c.After?.MtimeUtc,
                SuppressedCount = c.SuppressedCount,
                MergeCount = 1,
                IsCoalesced = c.SuppressedCount > 0,
                IsTransient = c.IsTransient,
                Source = "ReadDirectoryChangesW",
                Note = ComposeNote(c, beforeResult, afterResult),
            };

            if (c.Kind == EntryKind.Directory && ev.Operation is OperationType.Deleted or OperationType.Renamed or OperationType.Moved)
            {
                ev.AffectedDescendantCount = CountDescendants(c);
            }

            if (settings.EnableProcessAttribution && !c.IsTransient)
            {
                ApplyAttribution(ev, root);
            }

            events.Add(ev);
        }

        if (events.Count == 0) return;

        _events.AppendRange(events);

        // 事件已入库（带 Id），此时再更新索引与版本
        foreach (var ev in events)
        {
            UpdateIndex(ev);
            AddVersionIfNeeded(ev, newVersions);
            _onEventPersisted(ev.RootId, ev.TimestampUtc);

            var state = _stateOf(ev.RootId);
            if (state is not null)
            {
                state.LastEventUtc = ev.TimestampUtc;
                state.EventCount++;
            }
            _roots.TouchLastEvent(ev.RootId, ev.TimestampUtc);
        }

        if (newVersions.Count > 0) _versions.InsertRange(newVersions);

        _statistics.EventsPersisted += events.Count;
        _statistics.UnsavedEvents = _mergerPendingCount();
        _statistics.LastEventUtc = events[^1].TimestampUtc;
        _statistics.LastFlushUtc = _clock.UtcNow;

        _timelineChanged();
    }

    private string? ComposeNote(CoalescedEvent c, ContentWriter.StoreResult before, ContentWriter.StoreResult after)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(c.Note)) parts.Add(c.Note);

        if (c.Operation == OperationType.Deleted && c.Kind == EntryKind.File && before.ObjectId is null && !c.IsTransient)
        {
            parts.Add(before.Problem ?? "变化前的内容未留存，此删除无法恢复");
        }

        if (after.Problem is not null && c.Operation != OperationType.Deleted)
        {
            parts.Add(after.Problem);
        }

        if (c.SuppressedCount > 0)
        {
            parts.Add($"已合并 {c.SuppressedCount} 次重复通知");
        }

        return parts.Count == 0 ? null : string.Join("；", parts);
    }

    private int CountDescendants(CoalescedEvent c)
    {
        // 该操作影响面：目录自身的子项数量（用于 UI 提示"影响 N 个子项"）
        var target = c.Operation == OperationType.Deleted ? c.RelativePath : (c.OldRelativePath ?? c.RelativePath);
        try
        {
            return _index.ListUnder(c.RootId, target).Count(e => !PathUtil.Comparer.Equals(e.RelativePath, target));
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void ApplyAttribution(FileEvent ev, WatchedRoot root)
    {
        try
        {
            var candidates = ((ProcessProbe)_processProbe).FindNearbyCandidates(ev.TimestampUtc, _settings().AttributionWindowMs);
            if (candidates.Count == 0) return;

            // 先试内核级证据（大多数情况下拿不到，这正是要如实标注的原因）
            ProcessAttribution? best = null;
            if (ev.Kind == EntryKind.File && ev.Operation is OperationType.Modified or OperationType.Created)
            {
                var absolute = PathUtil.ToAbsolute(root.Path, ev.RelativePath);
                best = _processProbe.TryFindHandleOwner(absolute, candidates);
            }

            best ??= candidates[0];
            ev.Attribution = best;
            ev.AttributedPid = best.Pid;
            ev.AttributedProcess = best.ProcessName;
            ev.Confidence = best.Confidence;
        }
        catch (Exception)
        {
            // 归属失败不能影响事件本身的可靠性
        }
    }

    private void UpdateIndex(FileEvent ev)
    {
        var now = ev.TimestampUtc;

        switch (ev.Operation)
        {
            case OperationType.Created:
            case OperationType.Modified:
            {
                var existing = _index.Get(ev.RootId, ev.RelativePath);

                // ── 关键修正（真实缺陷，由测试暴露）──
                // 若这一次没能采到"变化后"的内容（文件刚好在此期间消失/被锁），
                // 绝不能把索引里已知的 hash/object_id 覆盖成 NULL：
                // 那会让"这个文件的当前版本"凭空消失，随后它的删除就变成"无法恢复"。
                // 规则：没有新内容时保留旧内容引用，只更新时间戳。
                bool hasNewContent = ev.HashAfter is not null || ev.SizeAfter is not null;
                bool keepExistingContent = existing is { IsDeleted: false } && !hasNewContent;

                _index.Upsert(ev.RootId, new IndexEntry
                {
                    RootId = ev.RootId,
                    RelativePath = ev.RelativePath,
                    Kind = ev.Kind,
                    Size = keepExistingContent ? existing!.Size : ev.SizeAfter ?? 0,
                    Hash = keepExistingContent ? existing!.Hash : ev.HashAfter,
                    ObjectId = keepExistingContent ? existing!.ObjectId : ev.ObjectIdAfter,
                    MtimeUtc = ev.MtimeAfterUtc ?? existing?.MtimeUtc ?? now,
                    FirstSeenUtc = existing?.FirstSeenUtc ?? now,
                    LastChangedUtc = now,
                    LastEventId = ev.Id,
                });
                break;
            }

            case OperationType.Deleted:
            {
                if (ev.Kind == EntryKind.Directory)
                {
                    _index.MarkSubtreeDeleted(ev.RootId, ev.RelativePath, now);
                }
                else
                {
                    _index.MarkDeleted(ev.RootId, ev.RelativePath, now);
                }
                break;
            }

            case OperationType.Renamed:
            case OperationType.Moved:
            {
                var oldPath = ev.OldRelativePath;
                if (string.IsNullOrEmpty(oldPath))
                {
                    // 没有旧路径的"重命名"只能按同路径更新处理
                    goto case OperationType.Modified;
                }

                if (ev.Kind == EntryKind.Directory)
                {
                    _index.MoveSubtree(ev.RootId, oldPath, ev.RelativePath, now);
                }
                else
                {
                    var before = _index.Get(ev.RootId, oldPath);
                    _index.MarkDeleted(ev.RootId, oldPath, now);
                    _index.Upsert(ev.RootId, new IndexEntry
                    {
                        RootId = ev.RootId,
                        RelativePath = ev.RelativePath,
                        Kind = ev.Kind,
                        Size = ev.SizeAfter ?? before?.Size ?? 0,
                        Hash = ev.HashAfter ?? before?.Hash,
                        ObjectId = ev.ObjectIdAfter ?? before?.ObjectId,
                        MtimeUtc = ev.MtimeAfterUtc ?? before?.MtimeUtc ?? now,
                        FirstSeenUtc = before?.FirstSeenUtc ?? now,
                        LastChangedUtc = now,
                        LastEventId = ev.Id,
                    });
                }
                break;
            }

            case OperationType.Transient:
                // 瞬时事件不改变"当前状态"（文件已经不在了），只留事实
                break;
        }
    }

    private void AddVersionIfNeeded(FileEvent ev, List<FileVersion> sink)
    {
        if (ev.Kind != EntryKind.File) return;

        // "变化后"的内容成为新版本
        if (ev.Operation is OperationType.Created or OperationType.Modified or OperationType.Renamed or OperationType.Moved)
        {
            if (ev.HashAfter is null) return;
            sink.Add(new FileVersion
            {
                RootId = ev.RootId,
                RelativePath = ev.RelativePath,
                Hash = ev.HashAfter,
                ObjectId = ev.ObjectIdAfter,
                Size = ev.SizeAfter ?? 0,
                RecordedUtc = ev.TimestampUtc,
                RecordedLocal = ev.TimestampLocal,
                MtimeUtc = ev.MtimeAfterUtc,
                EventId = ev.Id,
                Note = ev.Operation.ToChinese(),
            });
        }

        // "变化前"的内容也登记一次（这是"恢复被删除文件"的依据）
        if (ev.Operation == OperationType.Deleted && ev.HashBefore is not null)
        {
            sink.Add(new FileVersion
            {
                RootId = ev.RootId,
                RelativePath = ev.RelativePath,
                Hash = ev.HashBefore,
                ObjectId = ev.ObjectIdBefore,
                Size = ev.SizeBefore ?? 0,
                RecordedUtc = ev.TimestampUtc,
                RecordedLocal = ev.TimestampLocal,
                MtimeUtc = ev.MtimeBeforeUtc,
                EventId = ev.Id,
                Note = "删除前的内容",
            });
        }
    }
}
