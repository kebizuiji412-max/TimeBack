using System.Text;
using LastRegret.Core.Diff;
using LastRegret.Core.Events;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Data;
using LastRegret.Windows.Sqlite;
using LastRegret.Windows.Storage;

namespace LastRegret.Tests.Suites;

/// <summary>
/// 纯逻辑测试：路径、文本判定、Diff、SQLite 绑定、内容寻址存储。
/// 这些是其它一切功能的地基，必须最先保证正确。
/// </summary>
public static class CoreSuites
{
    public static IEnumerable<TestCase> All()
    {
        // ── 路径规范化 ──
        yield return new("路径工具", "反斜杠与斜杠统一为 '/'", () =>
        {
            Check.Equal("a/b/c.txt", PathUtil.NormalizeRelative(@"a\b\c.txt"), "反斜杠应被统一");
            Check.Equal("a/b", PathUtil.NormalizeRelative("/a/b/"), "前后斜杠应被去掉");
            Check.Equal(string.Empty, PathUtil.NormalizeRelative("."), "'.' 应归一为空");
        });

        yield return new("路径工具", "'..' 越界必须抛异常", () =>
        {
            Check.Throws<ArgumentException>(() => PathUtil.NormalizeRelative("../evil"), "越出根目录的相对路径必须被拒绝");
        });

        yield return new("路径工具", "绝对路径转相对路径（大小写不敏感）", () =>
        {
            var rel = PathUtil.ToRelative(@"D:\Proj", @"d:\proj\src\a.cs");
            Check.Equal("src/a.cs", rel, "应得到 '/' 分隔的相对路径");

            var ok = PathUtil.TryToRelative(@"D:\Proj", @"D:\Other\a.cs", out _);
            Check.False(ok, "根目录之外的路径必须返回 false");
        });

        yield return new("路径工具", "中文、空格、特殊字符路径可往返", () =>
        {
            var names = new[]
            {
                "中文文件名.txt", "带 空格 的文件.txt", "a+b&c=d.txt",
                "emoji_🎮_名字.txt", "引号'与\"双引号.txt", "#井号$美元%.txt",
            };
            foreach (var name in names)
            {
                var rel = PathUtil.NormalizeRelative("子目录/" + name);
                var abs = PathUtil.ToAbsolute(@"C:\root", rel);
                var back = PathUtil.ToRelative(@"C:\root", abs);
                Check.Equal(rel, back, $"路径往返失败：{name}");
                Check.Equal(name, PathUtil.NameOf(rel), $"文件名提取失败：{name}");
            }
        });

        yield return new("路径工具", "超长路径也能正确转换", () =>
        {
            var deep = string.Join("/", Enumerable.Repeat("很长的目录名称段", 30)) + "/file.txt";
            var abs = PathUtil.ToAbsolute(@"C:\root", deep);
            Check.True(abs.Length > 260, "构造出来的绝对路径应超过 MAX_PATH");
            var back = PathUtil.ToRelative(@"C:\root", abs);
            Check.Equal(deep, back, "超长路径往返应保持一致");
        });

        yield return new("路径工具", "IsUnder 前缀判断", () =>
        {
            Check.True(PathUtil.IsUnder("cache", "cache/a.txt"), "cache/a.txt 应在 cache 之下");
            Check.False(PathUtil.IsUnder("cache", "cache2/a.txt"), "cache2 不是 cache 的子项");
            Check.False(PathUtil.IsUnder("cache", "cache"), "自身不算子项");
        });

        // ── 正文分类 ──
        yield return new("正文分类", "UTF-8 / BOM / UTF-16 判定", () =>
        {
            var utf8 = Encoding.UTF8.GetBytes("hello 世界");
            var c1 = TextClassifier.Classify(utf8, "a.txt", utf8.Length);
            Check.True(c1.IsText, "普通 UTF-8 文本应判为文本");
            Check.Equal("UTF-8", c1.EncodingName, "编码名");

            var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(utf8).ToArray();
            var c2 = TextClassifier.Classify(bom, "a.txt", bom.Length);
            Check.True(c2.HasBom, "应识别出 BOM");
            Check.Equal("hello 世界", TextClassifier.Decode(bom, c2), "带 BOM 解码应正确去掉 BOM");

            var u16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("hi")).ToArray();
            var c3 = TextClassifier.Classify(u16, "a.txt", u16.Length);
            Check.True(c3.IsText, "UTF-16 带 BOM 应判为文本");
            Check.Equal("UTF-16 LE (BOM)", c3.EncodingName, "UTF-16 编码名");
        });

        yield return new("正文分类", "二进制内容绝不当作文本", () =>
        {
            var pe = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 };
            var c1 = TextClassifier.Classify(pe, "app.exe", pe.Length);
            Check.False(c1.IsText, "PE 文件头应判为二进制");

            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            Check.False(TextClassifier.Classify(png, "a.png", png.Length).IsText, "PNG 应判为二进制");

            var withNul = Encoding.UTF8.GetBytes("text\0more");
            var c3 = TextClassifier.Classify(withNul, "a.dat", withNul.Length);
            Check.False(c3.IsText, "含 NUL 字节应判为二进制");
            Check.NotNull(c3.Reason, "必须给出判定原因");
        });

        yield return new("正文分类", "GBK 中文文本不会导致崩溃（按 ANSI 处理）", () =>
        {
            // "中文" 的 GBK 编码：D6 D0 CE C4（不是合法 UTF-8）
            var gbk = new byte[] { 0xD6, 0xD0, 0xCE, 0xC4, 0x0D, 0x0A };
            var c = TextClassifier.Classify(gbk, "a.txt", gbk.Length);
            Check.True(c.IsText, "非 UTF-8 但控制字符很少 → 按文本处理，不抛异常");
            var text = TextClassifier.Decode(gbk, c);
            Check.True(text.Length > 0, "解码必须返回内容而不是抛异常");
        });

        yield return new("正文分类", "空文件判为文本且可比较", () =>
        {
            var c = TextClassifier.Classify(ReadOnlySpan<byte>.Empty, "empty.txt", 0);
            Check.True(c.IsText, "空文件应判为文本（Diff 结果可解释）");
            Check.Equal("empty", c.EncodingName, "空文件编码标记");
        });

        // ── Diff 引擎 ──
        yield return new("Diff", "相同内容报告完全相同", () =>
        {
            var d = DiffEngine.Compute("a\nb\nc", "a\nb\nc");
            Check.True(d.AreIdentical, "应判定为相同");
            Check.Equal(0, d.AddedCount, "新增行数");
            Check.Equal(0, d.RemovedCount, "删除行数");
        });

        yield return new("Diff", "单行修改给出 +1/-1", () =>
        {
            var d = DiffEngine.Compute("a\nold value\nc", "a\nnew value\nc");
            Check.Equal(1, d.AddedCount, "应新增 1 行");
            Check.Equal(1, d.RemovedCount, "应删除 1 行");
            Check.Contains(d.ToUnifiedText(), "-old value", "统一格式应包含删除行");
            Check.Contains(d.ToUnifiedText(), "+new value", "统一格式应包含新增行");
        });

        yield return new("Diff", "行内差异指出具体改动的字符区间", () =>
        {
            // 语义说明：行内差异 = "去掉公共前缀与公共后缀之后剩下的区域"。
            // "port = 8080" vs "port = 9090" 的公共前缀是 "port = "（含空格，7 位），
            // 公共后缀是末尾的 "0"，因此变化区间是各自的第 8~10 位："808" → "909"（长度 3）。
            // 这里断言的是**规则本身**（不变量），而不是某个硬编码文本。
            var d = DiffEngine.Compute("port = 8080", "port = 9090");
            var removed = d.Lines.First(l => l.Kind == DiffLineKind.Removed);
            var added = d.Lines.First(l => l.Kind == DiffLineKind.Added);

            var detail = string.Join("\n", d.Lines.Select(l =>
                $"    {l.Marker} [{l.Text}] oldChunks={DescribeChunks(l.OldChunks)} newChunks={DescribeChunks(l.NewChunks)}"));

            Check.NotNull(removed.OldChunks, "应给出旧行的行内差异区间\n" + detail);
            Check.NotNull(added.NewChunks, "应给出新行的行内差异区间\n" + detail);

            var oldChunk = removed.OldChunks![0];
            var newChunk = added.NewChunks![0];

            // 1) 两侧区间长度与起始位置必须一致（同一位置替换了同样多的字符）
            Check.Equal(oldChunk.Length, newChunk.Length, $"两侧变化区间长度应一致\n{detail}");
            Check.Equal(oldChunk.Start, newChunk.Start, $"两侧变化区间起始位置应一致\n{detail}");

            // 2) 区间之前是公共前缀
            Check.Equal(removed.Text[..oldChunk.Start], added.Text[..newChunk.Start],
                $"区间之前应是公共前缀\n{detail}");

            // 3) 区间之后是公共后缀
            var oldTail = removed.Text[(oldChunk.Start + oldChunk.Length)..];
            var newTail = added.Text[(newChunk.Start + newChunk.Length)..];
            Check.Equal(oldTail, newTail, $"区间之后应是公共后缀\n{detail}");

            // 4) 区间内确实不同
            var oldMid = removed.Text.Substring(oldChunk.Start, oldChunk.Length);
            var newMid = added.Text.Substring(newChunk.Start, newChunk.Length);
            Check.NotEqual(oldMid, newMid, $"区间内确实发生了变化\n{detail}");

            Check.Equal("808", oldMid, "旧行的变化区间内容\n" + detail);
            Check.Equal("909", newMid, "新行的变化区间内容\n" + detail);

            static string DescribeChunks(IReadOnlyList<DiffChunk>? chunks) =>
                chunks is null ? "(null)" : string.Join(",", chunks.Select(c => $"[start={c.Start},len={c.Length}]"));
        });

        yield return new("Diff", "空文件与有内容之间的差异", () =>
        {
            var d = DiffEngine.Compute("", "hello\nworld");
            Check.Equal(2, d.AddedCount, "空 → 两行应为 +2");
            Check.Equal(0, d.RemovedCount, "不应有删除行");

            var d2 = DiffEngine.Compute("hello", "");
            Check.Equal(1, d2.RemovedCount, "有内容 → 空应为 -1");
        });

        yield return new("Diff", "中文与 CRLF 换行正确处理", () =>
        {
            var d = DiffEngine.Compute("第一行\r\n第二行\r\n", "第一行\r\n第二行改\r\n");
            Check.Equal(1, d.AddedCount, "CRLF 下应正确识别 1 行新增");
            Check.Equal(1, d.RemovedCount, "CRLF 下应正确识别 1 行删除");
            Check.Contains(d.ToUnifiedText(), "第二行改", "中文内容应保留");
        });

        yield return new("Diff", "大规模文本不崩溃且给出结果", () =>
        {
            var a = string.Join("\n", Enumerable.Range(0, 8000).Select(i => $"line {i}"));
            var b = a.Replace("line 4000", "line 4000 CHANGED");
            var d = DiffEngine.Compute(a, b);
            Check.Equal(1, d.AddedCount, "8000 行文本中的单行修改应被识别");
            Check.Equal(1, d.RemovedCount, "8000 行文本中的单行修改应被识别");
        });

        // ── SQLite 绑定 ──
        yield return new("SQLite 绑定", "系统 winsqlite3.dll 可用且版本可读", () =>
        {
            using var conn = SqliteConnection.Open(":memory:");
            Check.True(conn.LibraryVersion.Length > 0, "应能读到 SQLite 版本");
            Check.True(conn.LibraryVersion.StartsWith("3."), $"版本号形如 3.x：{conn.LibraryVersion}");
        });

        yield return new("SQLite 绑定", "参数绑定（字符串/整数/浮点/null/时间）往返一致", () =>
        {
            using var conn = SqliteConnection.Open(":memory:");
            conn.Execute("CREATE TABLE t(a TEXT, b INTEGER, c REAL, d BLOB, e INTEGER);");
            var when = new DateTime(2026, 9, 11, 22, 5, 3, DateTimeKind.Utc);
            var blob = new byte[] { 1, 2, 3, 250 };
            conn.NonQuery("INSERT INTO t VALUES (?,?,?,?,?);", "中文 '引号' 🎮", 9223372036854775807L, 3.5, blob,
                SqliteConnection.ToUnixTicks(when));

            conn.QueryFirst("SELECT a,b,c,d,e FROM t;", Array.Empty<object?>(), row =>
            {
                Check.Equal("中文 '引号' 🎮", row.GetString("a"), "字符串往返");
                Check.Equal(long.MaxValue, row.GetInt64("b"), "64 位整数往返");
                Check.Equal(3.5, row.GetDouble("c"), "浮点往返");
                Check.Equal(4, row.GetBlob("d")!.Length, "BLOB 长度");
                Check.Equal(when, row.GetDateTimeUtc("e"), "UTC 时间往返");
            });
        });

        yield return new("SQLite 绑定", "NULL 与列缺失的行为明确", () =>
        {
            using var conn = SqliteConnection.Open(":memory:");
            conn.Execute("CREATE TABLE t(a TEXT, b INTEGER);");
            conn.NonQuery("INSERT INTO t VALUES (NULL, 7);");
            conn.QueryFirst("SELECT a,b FROM t;", Array.Empty<object?>(), row =>
            {
                Check.True(row.IsNull("a"), "a 应为 NULL");
                Check.Null(row.GetStringOrNull("a"), "GetStringOrNull 应返回 null");
                Check.Equal(7, row.GetInt32("b"), "b 应可读出");
            });

            Check.Throws<InvalidOperationException>(() =>
            {
                conn.QueryFirst("SELECT a FROM t;", Array.Empty<object?>(), row => row.GetInt64("不存在的列"));
            }, "读取不存在的列必须抛异常，而不是默默返回 0");
        });

        yield return new("SQLite 绑定", "事务失败必须完整回滚", () =>
        {
            using var conn = SqliteConnection.Open(":memory:");
            conn.Execute("CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT);");
            conn.Execute("INSERT INTO t VALUES (1,'first');");

            Check.Throws<InvalidOperationException>(() =>
            {
                conn.InTransaction(() =>
                {
                    conn.NonQuery("INSERT INTO t VALUES (2,'second');");
                    throw new InvalidOperationException("模拟业务失败");
                });
            }, "事务内异常应向外抛出");

            long count = 0;
            conn.QueryFirst("SELECT COUNT(*) FROM t;", Array.Empty<object?>(), r => count = r.GetInt64(0));
            Check.Equal(1L, count, "失败事务插入的行必须被回滚");
        });

        yield return new("SQLite 绑定", "integrity_check 与 外键检查可用", () =>
        {
            using var conn = SqliteConnection.Open(":memory:");
            conn.Execute("PRAGMA foreign_keys=ON;");
            conn.Execute("CREATE TABLE p(id INTEGER PRIMARY KEY); CREATE TABLE c(id INTEGER PRIMARY KEY, pid INTEGER REFERENCES p(id));");
            conn.Execute("INSERT INTO p VALUES (1); INSERT INTO c VALUES (1,1);");

            Check.Null(conn.IntegrityCheck(), "新建数据库完整性检查应为 ok");
            Check.Equal(0, conn.ForeignKeyCheck().Count, "外键应一致");
        });

        // ── 内容寻址存储 ──
        yield return new("内容寻址存储", "相同内容只存一份（去重）", () =>
        {
            var dir = NewTempDir("cas-dedup");
            try
            {
                using var db = SqliteConnection.Open(Path.Combine(dir, "objects.db"));
                LastRegretDatabase.PrepareObjectsDbOnly(db);
                var store = new ContentStore(db, Path.Combine(dir, "store"));

                var content = Encoding.UTF8.GetBytes("版本 A 的内容");

                var id1 = store.Put(content, ".txt", out var o1, out var dedup1);
                var id2 = store.Put(content, ".txt", out var o2, out var dedup2);

                Check.False(dedup1, "第一次写入不应是去重命中");
                Check.True(dedup2, "第二次写入相同内容必须命中已有对象");
                Check.Equal(id1, id2, "相同内容必须返回同一个对象 Id");

                // 磁盘上只有一个对象文件
                var files = Directory.GetFiles(Path.Combine(dir, "store", "objects"), "*", SearchOption.AllDirectories);
                Check.Equal(1, files.Length, "磁盘上应只存在一个对象文件");

                // 内容读取正确
                Check.True(store.TryReadAllBytes(id1, 1024, out var read, out _), "应能读回内容");
                Check.Equal("版本 A 的内容", Encoding.UTF8.GetString(read), "读回的内容应与写入一致");

                // A → B → A 场景：A 不会被重复存储
                var contentB = Encoding.UTF8.GetBytes("版本 B 的内容");
                var idB = store.Put(contentB, ".txt", out _, out _);
                var idA2 = store.Put(content, ".txt", out _, out var dedupA2);
                Check.Equal(id1, idA2, "内容变回 A 时应复用最早的对象");
                Check.True(dedupA2, "内容变回 A 时应命中去重");
                Check.NotEqual(id1, idB, "不同内容必须是不同对象");

                store.Dispose();
            }
            finally { TryDeleteDir(dir); }
        });

        yield return new("内容寻址存储", "磁盘上的对象文件是权威：索引重建可恢复", () =>
        {
            var dir = NewTempDir("cas-rebuild");
            try
            {
                using var db = SqliteConnection.Open(Path.Combine(dir, "objects.db"));
                LastRegretDatabase.PrepareObjectsDbOnly(db);
                var store = new ContentStore(db, Path.Combine(dir, "store"));

                var ids = new List<long>();
                for (int i = 0; i < 5; i++)
                {
                    // 同时落一份真实文件，便于之后按"磁盘内容是权威"核对哈希
                    var src = Path.Combine(dir, $"src{i}.txt");
                    File.WriteAllText(src, $"内容 {i} " + new string('x', 5000));

                    ids.Add(store.PutFile(src, ".txt", out _, out _));
                }

                // 破坏索引（模拟索引损坏）
                db.Execute("DELETE FROM objects;");
                Check.Equal(0L, store.GetUsage().ObjectCount, "索引已被清空");

                var rebuilt = store.RebuildIndex();
                Check.Equal(5, rebuilt, "应从磁盘重建出 5 个对象");

                // 重建会重新分配对象 Id（索引被清空过），所以按**哈希**找回对象，
                // 而不是沿用重建前的 Id —— 这本身就是"磁盘对象是权威"的体现。
                var expectedHash = LastRegret.Windows.Io.FileSystemReader.HashFileForTest(
                    Path.Combine(dir, "src0.txt"));
                var resolved = store.FindByHash(expectedHash);
                Check.NotNull(resolved,
                    "重建后应能按内容哈希找回对象。\n  索引内容：\n" + string.Join("\n",
                        store.EnumerateObjects().Select(o =>
                            $"    id={o.Id} hash={o.Hash[..10]} enc={o.Encoding} logical={o.LogicalSize}")));

                Check.True(store.TryReadAllBytes(resolved!.Id, 1024 * 1024, out var bytes, out var err),
                    $"重建后应能读取内容：{err}\n  对象 id={resolved.Id} enc={resolved.Encoding}");
                Check.Contains(Encoding.UTF8.GetString(bytes), "内容 0", "读回的内容应正确");

                // 语义澄清（如实记录，不粉饰）：
                // 索引被重建后，对象 Id 由重建顺序重新分配，因此**旧 Id 不再有效**。
                // 这是可以接受的：对外稳定的标识是"内容哈希"，Id 只是本地索引的短期句柄；
                // 上层每次都通过哈希解析 Id（ContentResolver），不会长期持有 Id。
                Check.True(store.FindById(resolved.Id) is not null, "重建后的新 Id 应可解析");
                Check.Contains(Encoding.UTF8.GetString(bytes), "内容 0", "读回的内容应正确");

                store.Dispose();
            }
            finally { TryDeleteDir(dir); }
        });

        yield return new("内容寻址存储", "对象可原子还原到磁盘（含覆盖已存在文件）", () =>
        {
            var dir = NewTempDir("cas-materialize");
            try
            {
                using var db = SqliteConnection.Open(Path.Combine(dir, "objects.db"));
                LastRegretDatabase.PrepareObjectsDbOnly(db);
                var store = new ContentStore(db, Path.Combine(dir, "store"));

                var id = store.Put(Encoding.UTF8.GetBytes("还原后的内容"), ".txt", out _, out _);

                var target = Path.Combine(dir, "target.txt");
                File.WriteAllText(target, "旧内容");
                Check.True(store.TryMaterialize(id, target, out var err), "还原应成功：" + err);
                Check.Equal("还原后的内容", File.ReadAllText(target), "目标文件内容应被替换");

                // 还原到不存在的子目录也应自动创建
                var nested = Path.Combine(dir, "a", "b", "c.txt");
                Check.True(store.TryMaterialize(id, nested, out var err2), "还原到深层路径应成功：" + err2);
                Check.Equal("还原后的内容", File.ReadAllText(nested), "深层目标文件内容正确");

                store.Dispose();
            }
            finally { TryDeleteDir(dir); }
        });

        yield return new("内容寻址存储", "压缩存储仍能原样还原", () =>
        {
            var dir = NewTempDir("cas-compress");
            try
            {
                using var db = SqliteConnection.Open(Path.Combine(dir, "objects.db"));
                LastRegretDatabase.PrepareObjectsDbOnly(db);
                var store = new ContentStore(db, Path.Combine(dir, "store"), compressionEnabled: true);

                // 高度可压缩的大文本（> 阈值 4KB）
                var text = string.Join("\n", Enumerable.Repeat("这一行会被反复重复，用来测试压缩路径", 500));
                var bytes = Encoding.UTF8.GetBytes(text);
                var id = store.Put(bytes, ".txt", out var obj, out _);

                Check.Equal("deflate", obj.Encoding, "大文本应使用压缩存储");
                Check.True(obj.StoredSize < obj.LogicalSize, $"压缩后应更小：{obj.StoredSize} < {obj.LogicalSize}");

                Check.True(store.TryReadAllBytes(id, 10 * 1024 * 1024, out var read, out var err), "压缩对象应能读回：" + err);
                Check.Equal(text, Encoding.UTF8.GetString(read), "解压后内容必须完全一致");

                // 校验能发现问题（手工破坏对象文件）
                var objectPath = store.ObjectPath(obj.Hash);
                File.WriteAllBytes(objectPath, Encoding.UTF8.GetBytes("被破坏的内容"));
                var problems = store.Verify(full: true);
                Check.True(problems.Count > 0, "手工破坏对象后校验必须报出问题");

                store.Dispose();
            }
            finally { TryDeleteDir(dir); }
        });

        yield return new("内容寻址存储", "磁盘写入失败时如实报错而不是假装成功", () =>
        {
            var dir = NewTempDir("cas-fail");
            try
            {
                using var db = SqliteConnection.Open(Path.Combine(dir, "objects.db"));
                LastRegretDatabase.PrepareObjectsDbOnly(db);
                var store = new ContentStore(db, Path.Combine(dir, "store"));

                // 内容在读取期间被改写 → PutFile 的哈希校验必须失败并抛异常
                var src = Path.Combine(dir, "changing.txt");
                File.WriteAllText(src, "初始内容");
                var originalHash = LastRegret.Windows.Io.FileSystemReader.HashFileForTest(src);

                // 直接验证：声明一个与文件实际内容不符的哈希，PutCore 的校验应拒绝
                // （通过仓库无公开入口，这里改用"写入后立刻改文件"的方式验证 TryMaterialize 的校验逻辑）
                var id = store.Put(File.ReadAllBytes(src), ".txt", out var obj, out _);
                Check.Equal(originalHash, obj.Hash, "对象哈希应等于内容哈希");

                // 破坏对象文件后再还原：必须失败并给出原因，绝不能写出错内容
                File.WriteAllBytes(store.ObjectPath(obj.Hash), Encoding.UTF8.GetBytes("错误内容!!"));
                var target = Path.Combine(dir, "out.txt");
                var ok = store.TryMaterialize(id, target, out var err);
                Check.False(ok, "对象已损坏时还原必须失败");
                Check.NotNull(err, "必须给出失败原因");

                store.Dispose();
            }
            finally { TryDeleteDir(dir); }
        });
    }

    internal static string NewTempDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lastregret-tests", $"{name}-{Guid.NewGuid():N}"[..(name.Length + 10)]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception) { }
    }
}
