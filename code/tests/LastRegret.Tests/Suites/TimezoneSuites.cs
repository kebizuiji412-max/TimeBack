using LastRegret.Core.Abstractions;
using LastRegret.Core.Model;

namespace LastRegret.Tests.Suites;

/// <summary>
/// 时间显示与时区一致性测试。
///
/// 起因（真实缺陷，来自第一次真实使用验收）：
///   SQLite 里的时间统一以 "UTC ticks" 存整数，`ts_local` 列写进去的其实也是同一个
///   UTC ticks（见 SqliteConnection.ToUnixTicks）。但读取时曾被当成"本地时间"直接
///   new DateTime(ts_local, DateTimeKind.Local) 用，于是：
///     ① 界面上把 UTC 当本地显示 —— 文件 11:47 改的，应用里显示 03:47；
///     ② 用「本地时间反查快照」的入口拿这个值去查，等于把 03:47 再当本地时间去换算，
///        结果查不到任何快照，「查看与当前的差异」直接报「无法读取该时间点的状态」。
/// 这一组测试把这两件事都钉死：写入 → 持久化 → 重新读出后，
/// 本地时间必须等于"真实发生时刻的本地时间"。
/// </summary>
public static class TimezoneSuites
{
    public static IEnumerable<TestCase> All()
    {
        yield return new("时间显示", "快照读出后的本地时间必须等于真实发生时刻的本地时间", () =>
        {
            using var box = Sandbox.Create("tz-snapshot");
            box.WriteFile("a.txt", "内容 A");
            box.Protect();

            var before = DateTime.Now;
            var snap = box.Snapshot(SnapshotKind.Manual, "时区回归");
            var after = DateTime.Now;

            var reloaded = box.SnapshotsRepo.Get(snap.Id);
            Check.NotNull(reloaded, "快照应能从数据库读回");
            var re = reloaded!;

            // 本地时间必须落在"刚才"这个区间里；如果差一个时区，这里会直接失败
            Check.True(re.TimestampLocal >= before.AddSeconds(-2) && re.TimestampLocal <= after.AddSeconds(2),
                $"读回的本地时间应等于真实发生时刻的本地时间（期望约 {before:HH:mm:ss}，实际 {re.TimestampLocal:HH:mm:ss}）");

            // UTC 与本地必须真的差一个时区偏移（本机不是 UTC 时）
            var offset = TimeZoneInfo.Local.GetUtcOffset(re.TimestampUtc);
            Check.Equal(re.TimestampUtc.Add(offset).ToString("yyyy-MM-dd HH:mm:ss"),
                re.TimestampLocal.ToString("yyyy-MM-dd HH:mm:ss"),
                "本地时间必须等于 UTC + 本机时区偏移（读回时不能把 UTC 直接当本地时间）");

            // 时间点反查也必须命中同一条快照
            var found = box.SnapshotService.GetAtOrBefore(box.RootId, re.TimestampUtc);
            Check.NotNull(found, "按 UTC 时间点应能查到这条快照");
            Check.Equal(re.Id, found!.Id, "按 UTC 时间点查到的应当是同一条快照");
        });

        yield return new("时间显示", "事件读出后的本地时间必须等于真实发生时刻的本地时间", () =>
        {
            using var box = Sandbox.Create("tz-event");
            box.Protect();

            var before = DateTime.Now;
            box.WriteFile("b.txt", "内容 B");
            var ev = box.WaitForEvent("b.txt", OperationType.Created);
            var all = box.Events.Query(new EventQuery
            {
                RootId = box.RootId,
                IncludeTransient = true,
                Limit = 50,
            });
            var persisted = all.First(e => e.Id == ev.Id);

            Check.True(persisted.TimestampLocal >= before.AddSeconds(-2) &&
                       persisted.TimestampLocal <= DateTime.Now.AddSeconds(2),
                $"事件读回的本地时间应等于真实发生时刻的本地时间（期望约 {before:HH:mm:ss}，实际 {persisted.TimestampLocal:HH:mm:ss}）");

            var offset = TimeZoneInfo.Local.GetUtcOffset(persisted.TimestampUtc);
            Check.Equal(persisted.TimestampUtc.Add(offset).ToString("yyyy-MM-dd HH:mm:ss"),
                persisted.TimestampLocal.ToString("yyyy-MM-dd HH:mm:ss"),
                "事件的本地时间必须等于 UTC + 本机时区偏移");
        });

        yield return new("时间显示", "恢复点用它自己的快照对比与预览（不能靠本地时间反查）", () =>
        {
            using var box = Sandbox.Create("tz-compare");
            box.WriteFile("c.txt", "版本 1");
            box.Protect();

            var point = box.Snapshot(SnapshotKind.Manual, "对比用");
            box.WriteFile("c.txt", "版本 2 —— 已经改了");
            box.WaitForIndex("c.txt");
            box.Flush();

            // 界面上的「查看与当前的差异」：等价的调用方式
            var pointSnapshot = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(pointSnapshot, "应能按快照 Id 取到快照");
            var pointSnap = pointSnapshot!;
            var comparison = box.Compare.CompareWithSnapshot(box.RootId, pointSnap);
            Check.NotNull(comparison, "按快照对比不应返回 null");
            Check.True(comparison!.Diff.Changes.Count > 0, "文件已改动，差异清单不应为空");

            // 界面上的「预览恢复」：等价的调用方式（按 UTC 精确指定）
            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, pointSnap.TimestampUtc);
            Check.NotNull(plan, "恢复预览不应生成失败：" + (error ?? string.Empty));
            var built = plan;
            Check.True(built is not null && built.HasEffect, "文件已改动，恢复预览应当有效果");

            // 计划里的目标时间必须还原成同一个本地时刻
            Check.Equal(pointSnap.TimestampLocal.ToString("yyyy-MM-dd HH:mm:ss"),
                built!.TargetTimeLocal.ToString("yyyy-MM-dd HH:mm:ss"),
                "恢复计划的目标本地时间必须与快照的本地时间一致");
        });
        yield return new("时间显示", "恢复记录的目标时间与时间戳必须还原成本地时间", () =>
        {
            using var box = Sandbox.Create("tz-restore");
            box.WriteFile("d.txt", "第一版");
            box.Protect();
            var point = box.Snapshot(SnapshotKind.Manual, "撤销用");

            box.WriteFile("d.txt", "第二版 —— 现在改过了");
            box.WaitForIndex("d.txt");
            box.Flush();

            var outcome = box.RestoreTo(point.TimestampLocal);
            Check.True(outcome.Ok, "恢复应当成功：" + (outcome.Message ?? string.Empty));

            var ops = box.RestoreRepo.ListRecent(box.RootId, 10);
            var op = ops.FirstOrDefault(o => o.Status == RestoreStatus.Completed);
            Check.NotNull(op, "应当有一条已完成的恢复记录");
            var record = op!;

            // 记录里的目标本地时间必须能还原成一个正常的日期年份，
            // 而不是被当作 UTC 解释后错位（旧实现在 UTC+8 下会整体偏移 8 小时）
            Check.True(record.TargetTimeLocal.Year >= 2000,
                $"恢复记录的目标本地时间应当有效（实际 {record.TargetTimeLocal:yyyy-MM-dd HH:mm:ss}）");
            Check.Equal(record.TargetTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                record.TargetTimeLocal.ToString("yyyy-MM-dd HH:mm:ss"),
                "恢复记录的本地时间必须等于 UTC 换算过来的本地时间");

            // 操作开始时间也要落在"刚才"这个区间
            Check.True(record.StartedUtc.ToLocalTime() >= point.TimestampLocal.AddMinutes(-1) &&
                       record.StartedUtc.ToLocalTime() <= DateTime.Now.AddMinutes(1),
                $"恢复记录的开始时间应当贴近真实发生时刻（实际 {record.StartedUtc.ToLocalTime():HH:mm:ss}）");

            // 历史版本里的本地时间同样不能错位
            var versions = box.Versions.ListForPath(box.RootId, "d.txt", 10);
            Check.True(versions.Count > 0, "应当有历史版本");
            foreach (var v in versions)
            {
                Check.Equal(v.RecordedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    v.RecordedLocal.ToString("yyyy-MM-dd HH:mm:ss"),
                    "历史版本的本地时间必须等于 UTC 换算过来的本地时间");
            }
        });
    }
}
