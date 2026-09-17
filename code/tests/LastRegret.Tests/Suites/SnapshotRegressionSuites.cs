using System.Diagnostics;
using System.Text;
using LastRegret.Core.Model;
using LastRegret.Engine;

namespace LastRegret.Tests.Suites;

/// <summary>
/// 第 7 刀：快照版本登记（<c>SnapshotService.RecordVersions</c>）的语义与规模性能。
///
/// 这里做两件事，缺一不可：
///  ① <b>语义等价</b>——把"批量判定"和"逐条 GetLatestBefore"做差分比对，
///     包括 A→B→A 这种"回到旧 hash 必须重新登记"的反例；
///  ② <b>规模证据</b>——同一份快照，分别用老算法（每文件一次查询）和新批量算法跑，
///     打印实际耗时与数据库调用次数，证明往返次数不再随文件数增长。
/// </summary>
public static class SnapshotRegressionSuites
{
    private static string HashOf(int i) => $"h{i:D6}" + new string('0', 58);

    /// <summary>老算法（修改前 RecordVersions 的形状）：每个文件查一次 GetLatestBefore。</summary>
    private static List<string> OldWayNeeding(Sandbox box, long rootId, long snapshotId, DateTime atUtc)
    {
        var files = box.SnapshotsRepo.LoadFiles(snapshotId);
        var result = new List<string>();
        foreach (var f in files)
        {
            if (f.Kind != EntryKind.File || f.Hash is null) continue;
            var latest = box.Versions.GetLatestBefore(rootId, f.RelativePath, atUtc);
            if (latest is not null && string.Equals(latest.Hash, f.Hash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            result.Add(f.RelativePath);
        }
        return result;
    }

    /// <summary>造一个含 n 个文件条目的快照（不走磁盘扫描，只写 snapshot_files，用于规模测量）。</summary>
    private static (long SnapshotId, DateTime TimestampUtc) BuildSyntheticSnapshot(Sandbox box, int n, string prefix)
    {
        var snap = box.SnapshotService.Create(box.RootId, SnapshotKind.Manual, $"synthetic-{prefix}-{n}");
        box.Db.Events.NonQuery("DELETE FROM snapshot_files WHERE snapshot_id = ?;", snap.Id);
        box.Db.Events.InTransaction(() =>
        {
            for (int i = 0; i < n; i++)
            {
                var path = $"{prefix}/f{i:D6}.txt";
                box.Db.Events.NonQuery(
                    "INSERT INTO snapshot_files(snapshot_id, path, path_key, kind, size, hash, object_id, mtime_utc, first_seen_utc, is_read_only) " +
                    "VALUES (?,?,?,?,?,?,?,?,?,?);",
                    snap.Id, path, path, "file", 16, HashOf(i), null, null, null, 0);
            }
        });
        return (snap.Id, snap.TimestampUtc);
    }

    public static IEnumerable<TestCase> All()
    {
        // ── 语义 1：没有历史版本 → 必须登记 ────────────────────────────────
        yield return new("快照版本登记", "无历史版本：快照登记必须生成一条历史版本", () =>
        {
            using var box = Sandbox.Create("ver-new");
            box.Protect();
            box.WriteFile("A.txt", "第一版");
            box.WaitForEvent("A.txt", OperationType.Created);
            box.Flush();
            box.Snapshot(SnapshotKind.Manual, "登记");                       // 基线快照 → 登记

            var list = box.Versions.ListForPath(box.RootId, "A.txt");
            Check.True(list.Count >= 1, "第一次登记必须留下历史版本，实际 " + list.Count + " 条");
            Check.NotNull(list[0].Hash, "登记的历史版本必须带 hash");
        });

        // ── 语义 2：相同 hash 不重复登记 ─────────────────────────────────
        yield return new("快照版本登记", "相同 hash 的重复快照：不得新增历史版本", () =>
        {
            using var box = Sandbox.Create("ver-same");
            box.Protect();
            box.WriteFile("A.txt", "内容不变");
            box.WaitForEvent("A.txt", OperationType.Created);
            box.Flush();
            box.Snapshot(SnapshotKind.Manual, "第一次");
            var before = box.Versions.ListForPath(box.RootId, "A.txt").Count;

            box.Snapshot(SnapshotKind.Manual, "第二次");
            box.Snapshot(SnapshotKind.Manual, "第三次");

            var after = box.Versions.ListForPath(box.RootId, "A.txt").Count;
            Check.Equal(before, after, "内容没变就不该新增历史版本");
        });

        // ── 语义 3：hash 改变必须登记 ────────────────────────────────────
        yield return new("快照版本登记", "hash 改变：必须登记新版本（AAA → BBB）", () =>
        {
            using var box = Sandbox.Create("ver-change");
            box.Protect();
            box.WriteFile("A.txt", "AAA");
            box.WaitForEvent("A.txt", OperationType.Created);
            box.Flush();
            box.Snapshot(SnapshotKind.Manual, "AAA 阶段");
            var h1 = box.Versions.ListForPath(box.RootId, "A.txt")[0].Hash;

            Thread.Sleep(300);                   // 与上一次写文件拉开时间，否则通知会被合并器吃掉
            box.WriteFile("A.txt", "BBB");
            box.WaitForEvent("A.txt", OperationType.Modified);
            box.Flush(); box.WaitForIndex("A.txt");
            box.Snapshot(SnapshotKind.Manual, "改过之后");

            var list = box.Versions.ListForPath(box.RootId, "A.txt");
            Check.True(list.Count >= 2, "hash 变了必须新增一条，实际 " + list.Count + " 条");
            Check.NotEqual(h1, list[0].Hash, "最新一条应是新内容的 hash");
        });

        // ── 语义 4（关键反例）：A → B → A 必须重新登记 ─────────────────────
        yield return new("快照版本登记", "回到旧 hash（A→B→A）：必须重新登记，不能因历史里有同 hash 就跳过", () =>
        {
            using var box = Sandbox.Create("ver-aba");
            box.Protect();
            box.WriteFile("A.txt", "AAA");
            box.WaitForEvent("A.txt", OperationType.Created);
            box.Flush();
            box.Snapshot(SnapshotKind.Manual, "AAA 阶段");
            var hA = box.Versions.ListForPath(box.RootId, "A.txt")[0].Hash;

            box.Snapshot(SnapshotKind.Manual, "A 阶段");

            Thread.Sleep(300);                   // 与上一次写文件拉开时间，否则通知会被合并器吃掉
            box.WriteFile("A.txt", "BBB");
            box.WaitForEvent("A.txt", OperationType.Modified);
            box.Flush(); box.WaitForIndex("A.txt");
            box.Snapshot(SnapshotKind.Manual, "B 阶段");
            var afterB = box.Versions.ListForPath(box.RootId, "A.txt").Count;
            Check.True(afterB >= 2, "B 阶段应新增一条，实际 " + afterB);

            Thread.Sleep(300);                   // 与上一次写文件拉开时间，否则通知会被合并器吃掉
            box.WriteFile("A.txt", "AAA");       // 回到旧内容
            box.WaitForEvent("A.txt", OperationType.Modified);
            box.Flush(); box.WaitForIndex("A.txt");
            box.Snapshot(SnapshotKind.Manual, "又回到 A");

            var list = box.Versions.ListForPath(box.RootId, "A.txt");
            Check.True(list.Count > afterB,
                "回到旧 hash 必须再登记一条（最新历史是 B，与当前 A 不同）。" +
                $"实际：B 阶段 {afterB} 条 → 现在 {list.Count} 条");
            Check.Equal(hA, list[0].Hash, "最新一条应回到 A 的 hash");
        });

        // ── 语义 5：多 root 不互相污染 ───────────────────────────────────
        yield return new("快照版本登记", "多 root：同路径的历史版本不得互相污染", () =>
        {
            using var box = Sandbox.Create("ver-roots");
            box.Protect();                          // 根1 开始监听
            box.WriteFile("A.txt", "根1 的内容");
            box.WaitForEvent("A.txt", OperationType.Created);
            box.Flush();
            box.Snapshot(SnapshotKind.Manual, "根1 快照");

            var dir2 = Path.Combine(box.Root, "watched2");
            Directory.CreateDirectory(dir2);
            File.WriteAllText(Path.Combine(dir2, "A.txt"), "根2 的内容");   // 先放文件，基线扫描时会索引到
            var add = box.Watch.AddRoot(dir2);
            Check.True(add.Ok, "应能加第二个根：" + add.Message);
            var root2 = add.RootId;
            box.Watch.RunBaseline(root2);          // 索引 + 基线快照（顺带登记根2 的历史版本）

            var v1 = box.Versions.ListForPath(box.RootId, "A.txt");
            var v2 = box.Versions.ListForPath(root2, "A.txt");
            Check.True(v1.Count >= 1, "根1 应有历史版本");
            Check.True(v2.Count >= 1, "根2 应有历史版本");
            Check.NotEqual(v1[0].Hash, v2[0].Hash, "两个根内容不同，hash 不应相同（历史被污染了）");
        });

        // ── 语义 6：删除后重新出现 ───────────────────────────────────────
        yield return new("快照版本登记", "删除后重新出现：必须重新登记（不能因为路径有历史就跳过）", () =>
        {
            using var box = Sandbox.Create("ver-recreate");
            box.Protect();
            box.WriteFile("A.txt", "会被删掉");
            box.WaitForEvent("A.txt", OperationType.Created);
            box.Flush();
            box.Snapshot(SnapshotKind.Manual, "删之前");

            box.DeleteFile("A.txt");
            box.WaitForGone("A.txt");
            box.Snapshot(SnapshotKind.Manual, "删之后");
            var afterDelete = box.Versions.ListForPath(box.RootId, "A.txt").Count;

            Thread.Sleep(300);                   // 与删除拉开时间，否则"重新创建"会被当成同一批
            box.WriteFile("A.txt", "重新出现的内容");
            box.WaitForEvent("A.txt", OperationType.Created);
            box.Flush(); box.WaitForIndex("A.txt");
            box.Snapshot(SnapshotKind.Manual, "又出现");

            var list = box.Versions.ListForPath(box.RootId, "A.txt");
            Check.True(list.Count > afterDelete,
                $"重新出现的内容必须登记（删除前 {afterDelete} 条 → 现在 {list.Count} 条）");
        });

        // ── 差分：批量判定 vs 逐条查询，必须完全一致 ──────────────────────
        yield return new("快照版本登记", "差分一致性：批量判定与逐条 GetLatestBefore 结果完全相同", () =>
        {
            using var box = Sandbox.Create("ver-diff");
            box.Protect();
            var (snapshotId, ts) = BuildSyntheticSnapshot(box, 400, "diff");

            // 给其中一部分路径造混合历史：同 hash / 不同 hash / 更晚的时间 / 恰好等于 ts
            var history = new List<FileVersion>();
            for (int i = 0; i < 400; i++)
            {
                var path = $"diff/f{i:D6}.txt";
                if (i % 4 == 0) continue;                                   // 完全没有历史 → 必须登记
                var hash = i % 4 == 1 ? HashOf(i)                            // 同 hash → 不登记
                         : "DIFFERENT" + i.ToString("D6");                   // 不同 hash → 登记
                var at = i % 8 == 2 ? ts.AddSeconds(-30)                     // 早于快照时间
                       : i % 8 == 3 ? ts                                     // 恰好等于快照时间（含）
                       : ts.AddSeconds(30);                                  // 晚于快照时间（不参与）
                history.Add(new FileVersion
                {
                    RootId = box.RootId, RelativePath = path, Hash = hash, Size = 16,
                    RecordedUtc = at, RecordedLocal = at.ToLocalTime(),
                });
            }
            box.Versions.InsertRange(history);

            var oldSet = OldWayNeeding(box, box.RootId, snapshotId, ts);
            var newSet = box.Versions.FindFilesNeedingVersion(box.RootId, snapshotId, ts)
                                       .Select(f => f.RelativePath).ToList();

            oldSet.Sort(StringComparer.Ordinal);
            newSet.Sort(StringComparer.Ordinal);

            Check.Equal(oldSet.Count, newSet.Count,
                "批量判定与逐条判定的条数必须一致（老=" + oldSet.Count + " 新=" + newSet.Count + "）");
            for (int i = 0; i < oldSet.Count; i++)
            {
                Check.Equal(oldSet[i], newSet[i], $"第 {i} 条不一致");
            }
            StringBuilder sb = new();
            sb.AppendLine($"[差分] 400 文件：老算法需要登记 {oldSet.Count} 条，新算法 {newSet.Count} 条，完全一致");
            Console.WriteLine(sb.ToString().TrimEnd());
        });

        // ── 时间边界（含"恰好等于"）──────────────────────────────────────
        yield return new("快照版本登记", "时间边界：仅 recorded_utc <= 快照时间 的历史参与比较", () =>
        {
            using var box = Sandbox.Create("ver-time");
            box.Protect();
            var (snapshotId, ts) = BuildSyntheticSnapshot(box, 3, "edge");

            box.Versions.InsertRange(new[]
            {
                // f000000：历史在快照之前，hash 与快照相同 → 不登记
                new FileVersion { RootId = box.RootId, RelativePath = "edge/f000000.txt", Hash = HashOf(0),
                                  Size = 16, RecordedUtc = ts.AddSeconds(-1), RecordedLocal = ts.AddSeconds(-1).ToLocalTime() },
                // f000001：历史恰好等于快照时间，hash 与快照相同 → 含该时间 → 不登记
                new FileVersion { RootId = box.RootId, RelativePath = "edge/f000001.txt", Hash = HashOf(1),
                                  Size = 16, RecordedUtc = ts, RecordedLocal = ts.ToLocalTime() },
                // f000002：历史晚于快照时间，hash 与快照相同 → 不参与比较 → 必须登记
                new FileVersion { RootId = box.RootId, RelativePath = "edge/f000002.txt", Hash = HashOf(2),
                                  Size = 16, RecordedUtc = ts.AddSeconds(5), RecordedLocal = ts.AddSeconds(5).ToLocalTime() },
            });

            var needing = box.Versions.FindFilesNeedingVersion(box.RootId, snapshotId, ts)
                                     .Select(f => f.RelativePath).ToList();

            Check.False(needing.Contains("edge/f000000.txt"), "快照之前的同 hash 历史应让它跳过");
            Check.False(needing.Contains("edge/f000001.txt"), "恰好等于快照时间（<=）也应让它跳过");
            Check.True(needing.Contains("edge/f000002.txt"), "晚于快照时间的版本不该参与比较");
        });

        // ── 规模对照：调用次数与耗时 ─────────────────────────────────────
        yield return new("快照版本登记·规模", "规模对照：批量判定不再随文件数增加数据库往返", () =>
        {
            using var box = Sandbox.Create("ver-scale");
            box.Protect();

            var report = new StringBuilder();
            report.AppendLine("[规模] 文件数 | 老算法(每文件一次查询) | 新批量(一次查询) | 倍数 | 需要登记");
            foreach (var n in new[] { 1000, 10000, 50000 })
            {
                var (snapshotId, ts) = BuildSyntheticSnapshot(box, n, $"scale{n}");

                // 造一份"每个路径都有旧历史、但内容已变"的真实场景：
                // 老算法必须逐条取回旧版本行（含反射式的 Map 分配）才能比较。
                var history = new List<FileVersion>(n);
                for (int i = 0; i < n; i++)
                {
                    history.Add(new FileVersion
                    {
                        RootId = box.RootId,
                        RelativePath = $"scale{n}/f{i:D6}.txt",
                        Hash = "OLD" + i.ToString("D9"),
                        Size = 16,
                        RecordedUtc = ts.AddSeconds(-60),
                        RecordedLocal = ts.AddSeconds(-60).ToLocalTime(),
                    });
                }
                box.Versions.InsertRange(history);

                var swOld = Stopwatch.StartNew();
                var oldSet = OldWayNeeding(box, box.RootId, snapshotId, ts);
                swOld.Stop();

                var swNew = Stopwatch.StartNew();
                var newSet = box.Versions.FindFilesNeedingVersion(box.RootId, snapshotId, ts);
                swNew.Stop();

                Check.Equal(oldSet.Count, newSet.Count, $"N={n} 两种算法结果条数必须一致");
                Check.Equal(n, newSet.Count, $"N={n} 内容都变了，每个文件都应需要登记");

                var ratio = swOld.Elapsed.TotalMilliseconds / Math.Max(0.01, swNew.Elapsed.TotalMilliseconds);
                report.AppendLine(
                    $"[规模] {n,7} | 老 {swOld.ElapsedMilliseconds,5} ms / 调用 {n,6} 次 | " +
                    $"新 {swNew.ElapsedMilliseconds,4} ms / 调用 1 次 | {ratio,6:N1}x | {newSet.Count}");

                if (n >= 10000)
                {
                    Check.True(swNew.ElapsedMilliseconds * 3 < swOld.ElapsedMilliseconds,
                        $"N={n} 时批量查询应显著快于逐条查询（老 {swOld.ElapsedMilliseconds} ms / 新 {swNew.ElapsedMilliseconds} ms）");
                }
            }
            Console.WriteLine(report.ToString().TrimEnd());
        });

        // ── 端到端：真实 1000 文件，走 SnapshotService.Create 全链路 ──────
        yield return new("快照版本登记·规模", "端到端 1000 个真实文件：快照登记耗时与结果", () =>
        {
            using var box = Sandbox.Create("ver-e2e");
            for (int i = 0; i < 1000; i++) box.WriteFile($"e2e/f{i:D4}.txt", "内容 " + i);
            box.Protect();                        // 基线快照：登记 1000 条
            var afterBaseline = box.Versions.Count(box.RootId);
            Check.True(afterBaseline >= 1000, "基线应登记 1000 条，实际 " + afterBaseline);

            // 内容没变 → 再快照一次：RecordVersions 要逐个路径比较，但不应新增任何版本
            var sw = Stopwatch.StartNew();
            box.SnapshotService.Create(box.RootId, SnapshotKind.Manual, "端到端测量");
            sw.Stop();

            var after = box.Versions.Count(box.RootId);
            Check.Equal(afterBaseline, after, "内容没变时不得新增历史版本（语义必须保持）");
            Console.WriteLine($"[端到端] 真实 1000 文件二次快照登记耗时 = {sw.ElapsedMilliseconds} ms（新增版本 {after - afterBaseline} 条）");
        });

        // ── 第二个隐患：只测量，不修改（对应提示词 §十七）─────────────────
        yield return new("快照版本登记·规模", "测量（不修改）：快照清单写入 StageOne 的逐行开销", () =>
        {
            using var box = Sandbox.Create("ver-stage");
            box.Protect();

            foreach (var n in new[] { 1000, 10000 })
            {
                var snap = new Snapshot
                {
                    RootId = box.RootId,
                    TimestampUtc = DateTime.UtcNow,
                    TimestampLocal = DateTime.Now,
                    Kind = SnapshotKind.Manual,
                    Note = "stage 测量",
                };
                var files = new List<SnapshotFile>(n);
                for (int i = 0; i < n; i++)
                {
                    files.Add(new SnapshotFile
                    {
                        RelativePath = $"stage{n}/f{i:D6}.txt",
                        Kind = EntryKind.File,
                        Size = 16,
                        Hash = HashOf(i),
                    });
                }

                var sw = Stopwatch.StartNew();
                box.SnapshotsRepo.Insert(snap, files);
                sw.Stop();

                Console.WriteLine($"[StageOne] 快照清单写入 {n,6} 行耗时 = {sw.ElapsedMilliseconds} ms" +
                                  $"（≈{sw.Elapsed.TotalMilliseconds * 1000 / n:N1} µs/行；本刀未修改）");
                Check.True(sw.ElapsedMilliseconds >= 0, "仅测量");
            }
        });

        // ── 索引利用证据 ────────────────────────────────────────────────
        yield return new("快照版本登记·规模", "批量查询必须走现有索引 ix_versions_path（EXPLAIN QUERY PLAN）", () =>
        {
            using var box = Sandbox.Create("ver-plan");
            box.Protect();
            var plan = new StringBuilder();
            box.Db.Events.Query(
                "EXPLAIN QUERY PLAN " +
                "SELECT sf.path FROM snapshot_files sf " +
                "WHERE sf.snapshot_id = ? AND sf.kind = 'file' AND sf.hash IS NOT NULL " +
                "  AND lower(COALESCE((SELECT v.hash FROM file_versions v " +
                "        WHERE v.root_id = ? AND v.path_key = sf.path_key AND v.recorded_utc <= ? " +
                "        ORDER BY v.recorded_utc DESC LIMIT 1), '')) <> lower(sf.hash);",
                new object?[] { 1L, box.RootId, 0L },
                row => plan.AppendLine("  " + row.GetString(3)));

            var text = plan.ToString();
            Console.WriteLine("[查询计划] 批量判定 SQL 的执行计划：" + Environment.NewLine + text.TrimEnd());
            Check.Contains(text, "ix_versions_path",
                "批量判定的子查询必须用上现有索引 ix_versions_path，而不是全表扫描 file_versions");
        });
    }
}
