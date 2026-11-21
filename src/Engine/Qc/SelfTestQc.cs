using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>
    /// QC 子系统自测（`--selftest --filter QC` 运行）。
    /// 只操作 SelfTestContext.NewDir(...) 下的临时目录，绝不碰用户真实文件。
    /// 覆盖：注入幂等、损坏标记拒绝、Force 注释、备份永不覆盖/失败回滚、原子写失败回滚、
    ///       编码/BOM/换行保持、include 链、模型路径解析、$model/$bodygroup/facial、区域无关数字、
    ///       $lod 块逐字格式、独立 _LOD.qc、注释边界。
    /// </summary>
    internal static class SelfTestQc
    {
        // ───────────────────────────── 造数据/断言辅助 ─────────────────────────────

        private static Encoding Utf8()
        {
            return new UTF8Encoding(false);
        }

        private static Encoding Gbk()
        {
            return Encoding.GetEncoding(936);
        }

        /// <summary>按指定编码/BOM 写文件（自己拼 BOM，避免与被测代码互相验证）。</summary>
        private static void WriteText(string path, string text, Encoding encoding, bool bom)
        {
            var enc = encoding ?? new UTF8Encoding(false);
            byte[] body = enc.GetBytes(text ?? "");
            if (bom && enc is UTF8Encoding)
            {
                var all = new byte[body.Length + 3];
                all[0] = 0xEF;
                all[1] = 0xBB;
                all[2] = 0xBF;
                Buffer.BlockCopy(body, 0, all, 3, body.Length);
                File.WriteAllBytes(path, all);
            }
            else
            {
                File.WriteAllBytes(path, body);
            }
        }

        /// <summary>读文件文本（自动跳过 UTF-8 BOM；其它编码按给定 encoding）。</summary>
        private static string ReadText(string path, Encoding encoding)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
                return new UTF8Encoding(false).GetString(b, 3, b.Length - 3);
            return (encoding ?? new UTF8Encoding(false)).GetString(b);
        }

        private static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        /// <summary>CRLF 文件里不允许出现"前面不是 \r 的 \n"。</summary>
        private static bool HasLoneLf(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 1; i < text.Length; i++)
                if (text[i] == '\n' && text[i - 1] != '\r') return true;
            return false;
        }

        private static void AssertBytesEqual(SelfTestContext ctx, byte[] expected, byte[] actual, string what)
        {
            if (expected == null || actual == null)
            {
                ctx.Assert(expected == actual, what + "：一方为 null");
                return;
            }
            bool equal = expected.Length == actual.Length;
            if (equal)
            {
                for (int i = 0; i < expected.Length; i++)
                    if (expected[i] != actual[i]) { equal = false; break; }
            }
            ctx.Assert(equal, what + "：字节不一致（期望 " + expected.Length + " 字节，实际 " + actual.Length + " 字节）");
        }

        private static void AssertPathEqual(SelfTestContext ctx, string expected, string actual, string what)
        {
            ctx.Assert(string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase),
                what + "：期望=" + (expected ?? "(null)") + " 实际=" + (actual ?? "(null)"));
        }

        private static int CountOccurrences(string text, string needle)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
            int count = 0, at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }

        private static LodPlan MakePlan(double[] distances, double[] ratios)
        {
            var plan = new LodPlan();
            for (int i = 0; i < distances.Length; i++) plan.Levels.Add(new LodLevel(distances[i], ratios[i]));
            return plan;
        }

        private static HashSet<string> Generated(params string[] files)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in files) if (f != null) set.Add(f);
            return set;
        }

        private static void Touch(string path)
        {
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        }

        private static int Backups(string dir)
        {
            return Directory.GetFiles(dir, "*.lodbak*").Length;
        }

        // ───────────────────────────── 用例 ─────────────────────────────

        [SelfTest("QC：工具块注入幂等（第二次 NoChange 且字节不变）")]
        public static void ToolBlockIdempotent(SelfTestContext ctx)
        {
            // 与旧版逐字一致的标记文本（改这里等于让旧工程无法被接管，必须锁死）
            ctx.AssertEqual("//>>> L4D2 自动 LOD 工具 生成块 开始 >>>", QcLexer.MarkerBegin, "开始标记逐字一致");
            ctx.AssertEqual("//<<< L4D2 自动 LOD 工具 生成块 结束 <<<", QcLexer.MarkerEnd, "结束标记逐字一致");
            ctx.AssertEqual("// [autoLOD 已停用] ", QcLexer.DisabledPrefix, "停用前缀逐字一致");

            string dir = ctx.NewDir("qc_idem");
            Touch(Path.Combine(dir, "m.glb"));
            string qc = Path.Combine(dir, "a.qc");
            WriteText(qc, "$modelname \"a.mdl\"\r\n$model \"body\" \"m.glb\"\r\n", Utf8(), false);

            var plan = MakePlan(new[] { 10.0 }, new[] { 0.997 });
            var info = QcParser.Parse(qc);
            ctx.Assert(!info.ReadFailed, "读取 QC 成功");
            ctx.AssertEqual(1, info.Studios.Count, "解析到 1 个 studio");

            string block = QcWriter.BuildLodBlock(info, plan, Generated(Path.Combine(dir, "m_lod1.glb")), false, true);
            ctx.Assert(block != null, "BuildLodBlock 有内容");
            string body = QcWriter.WrapMarker(block, "测试预设", plan, false);

            var r1 = QcWriter.Apply(info, body, false, new QcBackupRequest());
            ctx.Assert(r1.Success, "第一次 Apply 成功: " + r1.Error);
            ctx.Assert(!r1.NoChange, "第一次确实写盘");
            ctx.AssertEqual(1, r1.WrittenFiles.Count, "写入 1 个文件");
            AssertPathEqual(ctx, Path.GetFullPath(qc), r1.WrittenFiles[0], "WrittenFiles 是根 QC 绝对路径");

            string once = ReadText(qc, null);
            string expectedOnce =
                "$modelname \"a.mdl\"\r\n$model \"body\" \"m.glb\"\r\n\r\n" +
                QcLexer.MarkerBegin + "\r\n" +
                "// 预设: 测试预设（10 HU→99.7%）\r\n" +
                "// 由「L4D2 自动 LOD 工具」生成；重新运行会整块替换，不要手工改这一段。\r\n" +
                "// LOD1  切换距离 10  目标 99.7%  条目 1\r\n" +
                "$lod 10\r\n{\r\n\treplacemodel \"m.glb\" \"m_lod1.glb\"\r\n\tnofacial\r\n}\r\n" +
                QcLexer.MarkerEnd + "\r\n";
            ctx.AssertEqual(expectedOnce, once, "首次注入全文逐字（空行 + 标记 + 头部注释 + $lod 段）");
            ctx.AssertContains(once, QcLexer.MarkerBegin, "写入开始标记");
            ctx.AssertContains(once, QcLexer.MarkerEnd, "写入结束标记");
            ctx.AssertContains(once, "// 预设: 测试预设（10 HU→99.7%）", "头部预设注释逐字");
            ctx.AssertContains(once, "// 由「L4D2 自动 LOD 工具」生成；重新运行会整块替换，不要手工改这一段。", "头部生成注释逐字");
            ctx.AssertContains(once, "\r\n\r\n" + QcLexer.MarkerBegin, "工具块与前文之间空一行");
            ctx.AssertEqual(1, CountOccurrences(once, QcLexer.MarkerBegin), "只有一个开始标记");
            ctx.AssertEqual(1, CountOccurrences(once, QcLexer.MarkerEnd), "只有一个结束标记");
            ctx.Assert(once.EndsWith(QcLexer.MarkerEnd + "\r\n", StringComparison.Ordinal), "文件以结束标记+换行收尾");
            byte[] bytesOnce = File.ReadAllBytes(qc);

            var info2 = QcParser.Parse(qc);
            ctx.Assert(info2.HasToolMarker && info2.RootHasToolMarker, "再次解析识别工具块");
            ctx.Assert(!info2.HasForeignLod, "工具块里的 $lod 不算手写");

            var r2 = QcWriter.Apply(info2, body, false, new QcBackupRequest());
            ctx.Assert(r2.Success, "第二次 Apply 成功: " + r2.Error);
            ctx.Assert(r2.NoChange, "第二次 NoChange");
            ctx.AssertEqual(0, r2.WrittenFiles.Count, "第二次不写文件");
            AssertBytesEqual(ctx, bytesOnce, File.ReadAllBytes(qc), "幂等：第二次文件字节完全相同");
            ctx.AssertEqual(1, Backups(dir), "只产生一份 .lodbak");
        }

        [SelfTest("QC：缺结束标记拒绝修改且原文件不动")]
        public static void BrokenMarkerRefused(SelfTestContext ctx)
        {
            // A) 根 QC 里工具块有开始没结束
            string dir = ctx.NewDir("qc_broken");
            string qc = Path.Combine(dir, "a.qc");
            string original = "$modelname \"x.mdl\"\r\n" + QcLexer.MarkerBegin +
                              "\r\n$lod 10\r\n{\r\n}\r\n$sequence idle \"idle\"\r\n";
            WriteText(qc, original, Utf8(), false);
            byte[] originalBytes = File.ReadAllBytes(qc);

            string removed;
            ctx.Assert(!QcLexer.TryRemoveToolBlock(original, out removed), "TryRemoveToolBlock 缺结束标记返回 false");
            ctx.AssertEqual(original, removed, "失败时 result = 原文");

            bool broken;
            string masked = QcLexer.MaskToolBlocks(original, out broken);
            ctx.Assert(broken, "MaskToolBlocks 标记 broken");
            ctx.AssertEqual(original, masked, "broken 时不遮蔽");

            var info = QcParser.Parse(qc);
            ctx.Assert(info.ToolBlockBroken, "Parse 标记 ToolBlockBroken");
            ctx.AssertEqual(1, info.ToolBlockBrokenFiles.Count, "坏文件列表 1 个");

            var r = QcWriter.Apply(info, "// body\n", false, new QcBackupRequest());
            ctx.Assert(!r.Success, "Apply 拒绝修改");
            ctx.AssertContains(r.Error, "缺结束标记", "错误可读（含「缺结束标记」）");
            AssertBytesEqual(ctx, originalBytes, File.ReadAllBytes(qc), "文件未被修改");
            ctx.AssertEqual(0, Backups(dir), "拒绝时没有产生备份");

            // B) 坏块在被 include 的文件里：同样必须一个文件都不动
            string dir2 = ctx.NewDir("qc_broken_include");
            Touch(Path.Combine(dir2, "body.glb"));
            string root = Path.Combine(dir2, "zoey.qc");
            string main = Path.Combine(dir2, "main.qc");
            string rootOriginal = "$modelname \"z.mdl\"\r\n$include \"main.qc\"\r\n";
            string mainOriginal = "$bodygroup body\r\n{\r\n\tstudio \"body.glb\"\r\n}\r\n" +
                                  QcLexer.MarkerBegin + "\r\n$lod 10\r\n{\r\n}\r\n";
            WriteText(root, rootOriginal, Utf8(), false);
            WriteText(main, mainOriginal, Utf8(), false);

            var info2 = QcParser.Parse(root);
            ctx.Assert(info2.ToolBlockBroken, "include 文件里的坏块被识别");
            var r2 = QcWriter.Apply(info2, "$lod 5\r\n{\r\n}\r\n", true, new QcBackupRequest());
            ctx.Assert(!r2.Success, "include 坏块 → 拒绝修改");
            ctx.AssertContains(r2.Error, "缺结束标记", "错误可读");
            ctx.AssertEqual(rootOriginal, ReadText(root, null), "根 QC 未被修改");
            ctx.AssertEqual(mainOriginal, ReadText(main, null), "include 文件未被修改");
            ctx.AssertEqual(0, Backups(dir2), "没有产生备份");
        }

        [SelfTest("QC：Force 注释 include 文件里的手写 $lod（含 $shadowlod）")]
        public static void ForceCommentsInclude(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("qc_force");
            Touch(Path.Combine(dir, "body.glb"));
            string root = Path.Combine(dir, "zoey.qc");
            string main = Path.Combine(dir, "main.qc");

            WriteText(root,
                "$modelname \"survivors/zoey.mdl\"\r\n$include \"main.qc\"\r\n$sequence idle \"idle\"\r\n",
                Utf8(), false);
            string mainText =
                "$bodygroup body\r\n{\r\n\tstudio \"body.glb\"\r\n}\r\n" +
                "// $lod 99 注释里的不算\r\n" +
                "$lod 20\r\n{\r\n\treplacemodel \"body.glb\" \"body_old.glb\"\r\n}\r\n" +
                "$shadowlod\r\n{\r\n\treplacemodel \"body.glb\" \"body_old.glb\"\r\n}\r\n" +
                "$sequence idle \"idle\"\r\n";
            WriteText(main, mainText, Utf8(), false);

            var info = QcParser.Parse(root);
            ctx.Assert(info.HasForeignLod, "检测到 include 里的手写 $lod");
            ctx.AssertEqual(1, info.ForeignLodFiles.Count, "ForeignLodFiles 1 个");
            AssertPathEqual(ctx, Path.GetFullPath(main), info.ForeignLodFiles[0], "ForeignLodFiles 指向 main.qc");
            ctx.AssertEqual(1, info.Studios.Count, "include 里的 studio 被解析");

            var plan = MakePlan(new[] { 10.0 }, new[] { 0.997 });
            string block = QcWriter.BuildLodBlock(info, plan, Generated(Path.Combine(dir, "body_lod1.glb")), false, true);
            ctx.Assert(block != null, "生成 $lod 块");
            ctx.AssertContains(block, "replacemodel \"body.glb\" \"body_lod1.glb\"", "replacemodel 第一参数逐字");

            var r = QcWriter.Apply(info, QcWriter.WrapMarker(block, "测试", plan, false), true, new QcBackupRequest());
            ctx.Assert(r.Success, "Apply(force) 成功: " + r.Error);
            ctx.AssertEqual(2, r.DisabledForeignLodBlocks, "停用 2 个块（$lod + $shadowlod）");
            ctx.AssertEqual(2, r.WrittenFiles.Count, "写入 include 文件 + 根 QC");

            string mainAfter = ReadText(main, null);
            ctx.AssertContains(mainAfter, QcLexer.DisabledPrefix + "$lod 20", "$lod 行被注释");
            ctx.AssertContains(mainAfter, QcLexer.DisabledPrefix + "\treplacemodel", "块内 replacemodel 被注释");
            ctx.AssertContains(mainAfter, QcLexer.DisabledPrefix + "}", "块结束行被注释");
            ctx.AssertContains(mainAfter, QcLexer.DisabledPrefix + "$shadowlod", "$shadowlod 被注释");
            ctx.AssertContains(mainAfter, "$sequence idle \"idle\"", "块外 $sequence 保留且未加前缀");
            ctx.AssertNotContains(mainAfter, QcLexer.DisabledPrefix + "$sequence", "块外内容未加前缀");
            ctx.AssertNotContains(mainAfter, QcLexer.DisabledPrefix + "// $lod", "本来就是注释的 $lod 不处理");
            ctx.AssertEqual(8, CountOccurrences(mainAfter, QcLexer.DisabledPrefix), "共注释 8 行（4+4）");

            string rootAfter = ReadText(root, null);
            ctx.AssertContains(rootAfter, QcLexer.MarkerBegin, "根 QC 写入工具块");
            ctx.AssertContains(rootAfter, "$lod 10", "根 QC 写入新 $lod");
            ctx.AssertNotContains(rootAfter, QcLexer.DisabledPrefix, "根 QC 没有被加停用前缀");
            ctx.AssertContains(rootAfter, "$sequence idle \"idle\"", "根 QC 块外内容保留");
            ctx.Assert(File.Exists(main + ".lodbak"), "include 文件先备份");
            ctx.Assert(File.Exists(root + ".lodbak"), "根 QC 也备份");

            // 重复（force）apply：不重复加前缀、字节不变、不新增备份
            var info2 = QcParser.Parse(root);
            ctx.Assert(!info2.HasForeignLod, "再次解析：手写 $lod 已停用");
            var r2 = QcWriter.Apply(info2, QcWriter.WrapMarker(block, "测试", plan, false), true, new QcBackupRequest());
            ctx.Assert(r2.Success && r2.NoChange, "重复 Apply 成功且 NoChange");
            string mainAfter2 = ReadText(main, null);
            ctx.AssertEqual(mainAfter, mainAfter2, "include 文件内容不再变化");
            ctx.AssertEqual(8, CountOccurrences(mainAfter2, QcLexer.DisabledPrefix), "不会重复加前缀");
            ctx.AssertEqual(1, Directory.GetFiles(dir, "main.qc.lodbak*").Length, "没有产生第二份备份");
        }

        [SelfTest("QC：备份 .lodbak 永不覆盖（第二次得时间戳副本）")]
        public static void BackupNeverOverwrite(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("qc_backup");
            string f = Path.Combine(dir, "a.qc");
            WriteText(f, "V1\r\n", Utf8(), false);

            string err;
            string b1 = QcWriter.BackupFile(f, new QcBackupRequest(), out err);
            ctx.Assert(b1 != null, "首次备份成功: " + err);
            ctx.AssertEqual(f + ".lodbak", b1, "首次备份名 = xxx.qc.lodbak");
            ctx.AssertEqual("V1\r\n", ReadText(b1, null), "首次备份内容正确");

            WriteText(f, "V2\r\n", Utf8(), false);
            string b2 = QcWriter.BackupFile(f, new QcBackupRequest(), out err);
            ctx.Assert(b2 != null, "第二次备份成功: " + err);
            if (b2 != null)
            {
                ctx.Assert(b2 != b1, "第二次备份另存新文件");
                ctx.Assert(Path.GetFileName(b2).StartsWith("a.qc.lodbak_", StringComparison.Ordinal),
                    "第二次备份带时间戳（实际 " + Path.GetFileName(b2) + "）");
                ctx.AssertEqual("V2\r\n", ReadText(b2, null), "时间戳副本内容正确");
            }
            ctx.AssertEqual("V1\r\n", ReadText(b1, null), ".lodbak 未被覆盖");

            string b3 = QcWriter.BackupFile(f, new QcBackupRequest(), out err);
            ctx.Assert(b3 != null && b3 != b2, "同一秒再次备份不冲突");
            ctx.AssertEqual("V1\r\n", ReadText(b1, null), "再备份后 .lodbak 仍是 V1");

            // 集中备份：按相对 ProjectRoot 的路径镜像子目录（原 QC 旁不生成 .lodbak）
            string proj = Path.Combine(dir, "proj");
            string chars = Path.Combine(proj, "models", "chars");
            Directory.CreateDirectory(chars);
            string qc = Path.Combine(chars, "zoey.qc");
            WriteText(qc, "ORIG", Utf8(), false);
            string bakRoot = Path.Combine(dir, "我的备份");
            var req = new QcBackupRequest { Enabled = true, CentralBaseDir = bakRoot, ProjectRoot = proj };
            string cb = QcWriter.BackupFile(qc, req, out err);
            string expect = Path.Combine(bakRoot, "models", "chars", "zoey.qc.lodbak");
            AssertPathEqual(ctx, expect, cb, "集中备份镜像目录");
            ctx.Assert(!File.Exists(qc + ".lodbak"), "集中备份时原 QC 旁不生成 .lodbak");
            if (cb != null) ctx.AssertEqual("ORIG", ReadText(cb, null), "集中备份内容正确");
        }

        [SelfTest("QC：备份失败时整个 QC 一个字节都不改")]
        public static void BackupFailureAborts(SelfTestContext ctx)
        {
            // A) 根 QC 的 .lodbak 位置被同名目录占住 → 备份必然失败
            string dir = ctx.NewDir("qc_backupfail_root");
            Touch(Path.Combine(dir, "m.glb"));
            string qc = Path.Combine(dir, "a.qc");
            string original = "$modelname \"a.mdl\"\r\n$model \"body\" \"m.glb\"\r\n";
            WriteText(qc, original, Utf8(), false);
            Directory.CreateDirectory(qc + ".lodbak");   // 同名目录：目标不可写

            var info = QcParser.Parse(qc);
            var r = QcWriter.Apply(info, "$lod 5\r\n{\r\n}\r\n", false, new QcBackupRequest());
            ctx.Assert(!r.Success, "备份失败 → Apply 失败");
            ctx.AssertContains(r.Error, "备份", "错误说明是备份失败");
            ctx.AssertEqual(original, ReadText(qc, null), "QC 内容未改");
            ctx.AssertNotContains(ReadText(qc, null), QcLexer.MarkerBegin, "没有写入工具块");

            // B) Force 场景：include 文件备份失败 → 根 QC 与 include 都不改
            string dir2 = ctx.NewDir("qc_backupfail_include");
            Touch(Path.Combine(dir2, "body.glb"));
            string root = Path.Combine(dir2, "zoey.qc");
            string main = Path.Combine(dir2, "main.qc");
            string rootOriginal = "$modelname \"z.mdl\"\r\n$include \"main.qc\"\r\n";
            string mainOriginal = "$bodygroup body\r\n{\r\n\tstudio \"body.glb\"\r\n}\r\n$lod 20\r\n{\r\n}\r\n";
            WriteText(root, rootOriginal, Utf8(), false);
            WriteText(main, mainOriginal, Utf8(), false);
            Directory.CreateDirectory(main + ".lodbak");

            var info2 = QcParser.Parse(root);
            var r2 = QcWriter.Apply(info2, "$lod 10\r\n{\r\n}\r\n", true, new QcBackupRequest());
            ctx.Assert(!r2.Success, "include 文件备份失败 → Apply 失败");
            ctx.AssertContains(r2.Error, "main.qc", "错误里说明是哪个文件的备份失败");
            ctx.AssertEqual(rootOriginal, ReadText(root, null), "根 QC 未改");
            ctx.AssertEqual(mainOriginal, ReadText(main, null), "include 文件未改");
            ctx.Assert(!File.Exists(root + ".lodbak"), "顺序：include 先失败，根 QC 也没备份");

            // C) 关闭备份：能正常写入、不生成 .lodbak
            string dir3 = ctx.NewDir("qc_backup_off");
            Touch(Path.Combine(dir3, "m.glb"));
            string qc3 = Path.Combine(dir3, "c.qc");
            WriteText(qc3, "$modelname \"c.mdl\"\r\n$model \"body\" \"m.glb\"\r\n", Utf8(), false);
            var info3 = QcParser.Parse(qc3);
            var r3 = QcWriter.Apply(info3, "$lod 5\r\n{\r\n}\r\n", false, new QcBackupRequest { Enabled = false });
            ctx.Assert(r3.Success, "关闭备份时可正常写入: " + r3.Error);
            ctx.AssertEqual(0, Backups(dir3), "关闭备份时不生成 .lodbak");
            ctx.AssertContains(ReadText(qc3, null), "$lod 5", "内容已写入");
        }

        [SelfTest("QC：原子写失败回滚（目标被独占占用）")]
        public static void AtomicWriteFailureRollsBack(SelfTestContext ctx)
        {
            // A) 目标被 FileShare.None 独占：连读取都失败 → Apply 失败、文件不变、无备份
            string dir = ctx.NewDir("qc_atomic_none");
            Touch(Path.Combine(dir, "m.glb"));
            string qc = Path.Combine(dir, "a.qc");
            string original = "$modelname \"a.mdl\"\r\n$model \"body\" \"m.glb\"\r\n";
            WriteText(qc, original, Utf8(), false);
            var info = QcParser.Parse(qc);
            string errorA = "";
            using (new FileStream(qc, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var r = QcWriter.Apply(info, "$lod 5\r\n{\r\n}\r\n", false, new QcBackupRequest());
                errorA = r.Error;
                ctx.Assert(!r.Success, "被独占时 Apply 失败");
            }
            ctx.Assert(!string.IsNullOrEmpty(errorA), "错误可读: " + errorA);
            ctx.AssertEqual(original, ReadText(qc, null), "原文件内容不变");
            ctx.AssertEqual(0, Backups(dir), "没有产生备份");

            // B) 允许读、禁止替换（FileShare.Read）：include 先写成功、根 QC 写失败 → include 回滚
            string dir2 = ctx.NewDir("qc_atomic_rollback");
            Touch(Path.Combine(dir2, "body.glb"));
            string root = Path.Combine(dir2, "zoey.qc");
            string main = Path.Combine(dir2, "main.qc");
            string rootOriginal = "$modelname \"z.mdl\"\r\n$include \"main.qc\"\r\n";
            string mainOriginal = "$bodygroup body\r\n{\r\n\tstudio \"body.glb\"\r\n}\r\n$shadowlod\r\n{\r\n}\r\n";
            WriteText(root, rootOriginal, Utf8(), false);
            WriteText(main, mainOriginal, Utf8(), false);
            var info2 = QcParser.Parse(root);
            ctx.Assert(info2.HasForeignLod, "main.qc 有手写 $shadowlod");

            QcWriteResult r2 = null;
            using (new FileStream(root, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                r2 = QcWriter.Apply(info2, "$lod 10\r\n{\r\n}\r\n", true, new QcBackupRequest());
            }
            ctx.Assert(!r2.Success, "根 QC 写入失败 → Apply 失败");
            ctx.AssertContains(r2.Error, "写 ", "错误说明是写入失败: " + r2.Error);
            ctx.AssertEqual(rootOriginal, ReadText(root, null), "根 QC 原文件内容不变");
            ctx.AssertEqual(mainOriginal, ReadText(main, null), "已写的 include 文件被回滚成原内容");
            ctx.AssertEqual(0, Directory.GetFiles(dir2, "*.tmp_*").Length, "没有残留 tmp 文件");
            ctx.AssertEqual(0, Directory.GetFiles(dir2, "*.old_*").Length, "没有残留 old 文件");
            ctx.AssertEqual(2, Directory.GetFiles(dir2, "*.lodbak").Length, "备份先于写入已生成（include + 根）");
        }

        [SelfTest("QC：编码/BOM/换行读写保持（UTF-8/BOM/GBK × CRLF/LF）")]
        public static void EncodingPreserved(SelfTestContext ctx)
        {
            // 直接验证 Encode/Decode 的规则
            ctx.AssertEqual(4, QcTextFile.Encode("x", Utf8(), true).Length, "UTF-8 + bom=true 前置 3 字节 BOM");
            ctx.AssertEqual(1, QcTextFile.Encode("x", Gbk(), true).Length, "GBK 永不加 BOM");
            Encoding decEnc;
            bool decBom;
            ctx.AssertEqual("佐伊", QcTextFile.Decode(Gbk().GetBytes("佐伊"), out decEnc, out decBom), "GBK 字节正确解出中文");
            ctx.AssertEqual(936, decEnc.CodePage, "GBK 被识别为 936");
            ctx.Assert(!decBom, "GBK 无 BOM");

            string[] cases = { "utf8_crlf", "utf8bom_crlf", "gbk_lf", "utf8_lf" };
            foreach (string c in cases)
            {
                string dir = ctx.NewDir("qc_enc_" + c);
                Touch(Path.Combine(dir, "m.glb"));
                string qc = Path.Combine(dir, "a.qc");

                Encoding enc;
                bool bom;
                string nl;
                if (c == "utf8_crlf") { enc = Utf8(); bom = false; nl = "\r\n"; }
                else if (c == "utf8bom_crlf") { enc = Utf8(); bom = true; nl = "\r\n"; }
                else if (c == "gbk_lf") { enc = Gbk(); bom = false; nl = "\n"; }
                else { enc = Utf8(); bom = false; nl = "\n"; }

                string original = "// 中文注释：佐伊" + nl +
                                  "$modelname \"survivors/佐伊.mdl\"" + nl +
                                  "$model \"body\" \"m.glb\"" + nl;
                WriteText(qc, original, enc, bom);

                var info = QcParser.Parse(qc);
                ctx.Assert(!info.ReadFailed, c + "：读取成功");
                ctx.AssertEqual(enc.CodePage, info.TextEncoding.CodePage, c + "：编码探测正确");
                ctx.Assert(bom == info.HasBom, c + "：BOM 标志正确（期望 " + bom + " 实际 " + info.HasBom + "）");
                ctx.AssertEqual(nl, info.NewLine, c + "：换行探测正确");

                var plan = MakePlan(new[] { 12.5 }, new[] { 0.6 });
                string block = QcWriter.BuildLodBlock(info, plan, Generated(Path.Combine(dir, "m_lod1.glb")), false, true);
                string body = QcWriter.WrapMarker(block, "角色·标准（中文预设）", plan, false);
                var r = QcWriter.Apply(info, body, false, new QcBackupRequest());
                ctx.Assert(r.Success, c + "：Apply 成功: " + r.Error);

                byte[] bytes = File.ReadAllBytes(qc);
                bool hasBom = HasUtf8Bom(bytes);
                ctx.Assert(bom == hasBom, c + "：写回后 BOM 状态不变（期望 " + bom + " 实际 " + hasBom + "）");
                if (bom)
                    ctx.Assert(!(bytes.Length >= 6 && bytes[3] == 0xEF && bytes[4] == 0xBB), c + "：没有重复 BOM");

                string text = hasBom ? new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3) : enc.GetString(bytes);
                ctx.AssertContains(text, "// 中文注释：佐伊", c + "：原中文内容完好");
                ctx.AssertContains(text, "// 预设: 角色·标准（中文预设）", c + "：追加的中文头部注释正确（编码一致）");
                ctx.AssertContains(text, "// 由「L4D2 自动 LOD 工具」生成；重新运行会整块替换，不要手工改这一段。",
                    c + "：全角标点（「」）在目标编码里没有丢失");
                ctx.AssertContains(text, "$lod 12.5", c + "：写入的 $lod 正确");
                if (nl == "\r\n") ctx.Assert(!HasLoneLf(text), c + "：CRLF 文件中没有裸 LF");
                else ctx.Assert(text.IndexOf('\r') < 0, c + "：LF 文件中没有 CR");
            }
        }

        [SelfTest("QC：include 链解析（$cd/$pushd/$popd/多级/环路）")]
        public static void IncludeChainResolved(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("qc_include");
            string sub = Path.Combine(dir, "sub");
            string meshes = Path.Combine(sub, "meshes");
            string deep = Path.Combine(meshes, "deep");
            Directory.CreateDirectory(deep);
            Touch(Path.Combine(meshes, "torso.glb"));
            Touch(Path.Combine(deep, "deep.glb"));
            Touch(Path.Combine(sub, "legs.glb"));

            string root = Path.Combine(dir, "root.qc");
            string one = Path.Combine(sub, "one.qc");
            string two = Path.Combine(sub, "two.qc");

            WriteText(root,
                "$modelname \"survivors/zoey.mdl\"\r\n$include \"sub/one.qc\"\r\n$staticprop\r\n",
                Utf8(), false);
            // 1 $include  2 $cd  3 $body  4 $pushd  5 $body  6 $popd  7 环 include
            WriteText(one,
                "$include \"two.qc\"\r\n" +
                "$cd \"sub/meshes\"\r\n" +
                "$body torso \"torso.glb\"\r\n" +
                "$pushd \"deep\"\r\n" +
                "$body deep \"deep.glb\"\r\n" +
                "$popd\r\n" +
                "$include \"../root.qc\"\r\n",
                Utf8(), false);
            WriteText(two,
                "$bodygroup legs\r\n{\r\n\tstudio \"legs.glb\"\r\n}\r\n",
                Utf8(), false);

            var info = QcParser.Parse(root);     // 环 include 必须不死循环
            ctx.AssertEqual("survivors/zoey.mdl", info.ModelName, "根 QC 的 $modelname");
            ctx.Assert(info.IsStaticProp, "$staticprop 被识别");
            ctx.AssertEqual(3, info.Studios.Count, "3 个 studio（torso/deep/legs）");
            ctx.AssertEqual(3, info.FilesRead, "读了 3 个文件（root/one/two）");

            StudioRef torso = info.Studios.Find(s => s.Name == "torso.glb");
            StudioRef deepRef = info.Studios.Find(s => s.Name == "deep.glb");
            StudioRef legs = info.Studios.Find(s => s.Name == "legs.glb");
            ctx.Assert(torso != null && deepRef != null && legs != null, "三个引用都被收集");
            if (torso == null || deepRef == null || legs == null) return;

            ctx.Assert(torso.Exists, "$cd 后的当前目录里找到 torso.glb");
            ctx.Assert(deepRef.Exists, "$pushd 后的目录里找到 deep.glb");
            ctx.Assert(legs.Exists, "被 include 文件的目录里找到 legs.glb（声明文件目录兜底）");
            ctx.AssertEqual("torso.glb", torso.RawFile, "RawFile 原样");
            ctx.AssertEqual(3, torso.DeclaredLine, "torso 声明行 = 3");
            ctx.AssertEqual(5, deepRef.DeclaredLine, "deep 声明行 = 5");
            ctx.AssertEqual(3, legs.DeclaredLine, "legs 声明行 = 3（two.qc 内）");
            ctx.AssertEqual(1, torso.IncludeDepth, "torso include 深度 1");
            ctx.AssertEqual(2, legs.IncludeDepth, "legs include 深度 2");
            AssertPathEqual(ctx, Path.GetFullPath(one), torso.DeclaredInQc, "torso 声明文件 = one.qc");
            AssertPathEqual(ctx, Path.GetFullPath(two), legs.DeclaredInQc, "legs 声明文件 = two.qc");

            ctx.AssertEqual(2, info.IncludedFiles.Count, "IncludedFiles 2 个（one/two，不含根）");
            bool hasOne = false, hasTwo = false;
            foreach (string f in info.IncludedFiles)
            {
                if (string.Equals(f, Path.GetFullPath(one), StringComparison.OrdinalIgnoreCase)) hasOne = true;
                if (string.Equals(f, Path.GetFullPath(two), StringComparison.OrdinalIgnoreCase)) hasTwo = true;
            }
            ctx.Assert(hasOne && hasTwo, "IncludedFiles 含 one.qc 与 two.qc");
        }

        [SelfTest("QC：模型路径解析（补扩展名/SMD 子目录/上级目录/Fallback/缺失）")]
        public static void ModelPathResolution(SelfTestContext ctx)
        {
            // A) 无扩展名：.glb 优先于 .smd
            string a = ctx.NewDir("qc_path_ext");
            Touch(Path.Combine(a, "body.glb"));
            Touch(Path.Combine(a, "body.smd"));
            string qa = Path.Combine(a, "a.qc");
            WriteText(qa, "$modelname \"a.mdl\"\r\n$body body \"body\"\r\n", Utf8(), false);
            var ia = QcParser.Parse(qa);
            ctx.AssertEqual(1, ia.Studios.Count, "解析到 1 个 studio");
            StudioRef sa = ia.Studios[0];
            ctx.Assert(sa.Exists, "无扩展名 studio 找到文件");
            AssertPathEqual(ctx, Path.Combine(a, "body.glb"), sa.ResolvedPath, "无扩展名优先补 .glb");
            ctx.AssertEqual("body", sa.RawFile, "RawFile 保留 QC 原样写法（replacemodel 用）");

            // B) 只有 .smd 时补 .smd
            string b = ctx.NewDir("qc_path_smd");
            Touch(Path.Combine(b, "legs.smd"));
            string qb = Path.Combine(b, "b.qc");
            WriteText(qb, "$modelname \"b.mdl\"\r\n$body legs \"legs\"\r\n", Utf8(), false);
            var ib = QcParser.Parse(qb);
            ctx.Assert(ib.Studios[0].Exists, "无扩展名 studio 找到 .smd");
            ctx.Assert(string.Equals(ib.Studios[0].ResolvedPath, Path.Combine(b, "legs.smd"), StringComparison.OrdinalIgnoreCase),
                "缺 .glb 时补 .smd（实际 " + ib.Studios[0].ResolvedPath + "）");

            // C) SMD 子目录
            string c = ctx.NewDir("qc_path_smd_dir");
            Directory.CreateDirectory(Path.Combine(c, "SMD"));
            Touch(Path.Combine(c, "SMD", "hair.glb"));
            string qc = Path.Combine(c, "c.qc");
            WriteText(qc, "$modelname \"c.mdl\"\r\n$bodygroup hair\r\n{\r\n\tstudio \"hair.glb\"\r\n}\r\n", Utf8(), false);
            var ic = QcParser.Parse(qc);
            ctx.Assert(ic.Studios[0].Exists, "SMD 子目录里的模型被找到");
            AssertPathEqual(ctx, Path.Combine(c, "SMD", "hair.glb"), ic.Studios[0].ResolvedPath, "SMD 子目录候选生效");

            // D) 根目录的上一级
            string d = ctx.NewDir("qc_path_up1");
            string proj = Path.Combine(d, "proj");
            Directory.CreateDirectory(proj);
            Touch(Path.Combine(d, "part.glb"));
            string qd = Path.Combine(proj, "d.qc");
            WriteText(qd, "$modelname \"d.mdl\"\r\n$body part \"part.glb\"\r\n", Utf8(), false);
            var id = QcParser.Parse(qd);
            ctx.Assert(id.Studios[0].Exists, "上级目录里的模型被找到");
            AssertPathEqual(ctx, Path.Combine(d, "part.glb"), id.Studios[0].ResolvedPath, "上级目录候选生效");

            // E) 按文件名兜底：Fallback=true / Exists=false / 只提示不处理
            string e = ctx.NewDir("qc_path_fallback");
            string projE = Path.Combine(e, "proj");
            Directory.CreateDirectory(Path.Combine(projE, "elsewhere", "deep"));
            Touch(Path.Combine(projE, "elsewhere", "deep", "hair2.glb"));
            string qe = Path.Combine(projE, "e.qc");
            WriteText(qe, "$modelname \"e.mdl\"\r\n$bodygroup hair\r\n{\r\n\tstudio \"models\\hair2.glb\"\r\n}\r\n", Utf8(), false);
            var ie = QcParser.Parse(qe);
            StudioRef se = ie.Studios[0];
            ctx.Assert(se.Fallback, "同名文件在别处 → Fallback=true");
            ctx.Assert(!se.Exists, "Fallback 条目不参与处理（Exists=false）");
            ctx.AssertContains(JoinWarnings(ie), "为安全起见未处理", "给用户可读警告");
            string fallbackBlock = QcWriter.BuildLodBlock(ie, MakePlan(new[] { 10.0 }, new[] { 0.997 }),
                Generated(Path.Combine(projE, "elsewhere", "deep", "hair2_lod1.glb")), false, true);
            ctx.Assert(fallbackBlock == null, "Fallback 条目不会进 $lod");

            // F) 完全缺失：Exists=false / Fallback=false
            string f = ctx.NewDir("qc_path_missing");
            string qf = Path.Combine(f, "f.qc");
            WriteText(qf, "$modelname \"f.mdl\"\r\n$body missing \"nope.glb\"\r\n", Utf8(), false);
            var iff = QcParser.Parse(qf);
            ctx.Assert(!iff.Studios[0].Exists, "缺失文件 Exists=false");
            ctx.Assert(!iff.Studios[0].Fallback, "缺失文件 Fallback=false");
        }

        [SelfTest("QC：$model 主模型 / $bodygroup studio innode / HasFacial")]
        public static void ModelBodygroupFacial(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("qc_model");
            string[] files = { "body.glb", "plain.glb", "face.glb", "lvar.glb", "inc.glb", "eye1.glb", "eye2.glb" };
            foreach (string f in files) Touch(Path.Combine(dir, f));

            string qc = Path.Combine(dir, "a.qc");
            string text =
                "$modelname \"a.mdl\"\r\n" +
                "$model \"body\" \"body.glb\"\r\n{\r\n\tflexfile \"x.vta\" { }\r\n}\r\n" +
                "$model \"plain\" \"plain.glb\"\r\n" +
                "$model \"face\" \"face.glb\" {\r\n\tflexcontroller \"eyes\" range 0 1\r\n}\r\n" +
                "$model \"lvar\" \"lvar.glb\" {\r\n\tlocalvar \"lv\" \"value\"\r\n}\r\n" +
                "$model \"inc\" \"inc.glb\" {\r\n\t$include \"flexes.qci\"\r\n}\r\n" +
                "$bodygroup eyes\r\n{\r\n\tstudio \"eye1.glb\" innode eye1\r\n\tstudio eye2.glb\r\n\tblank\r\n}\r\n";
            WriteText(qc, text, Utf8(), false);

            var info = QcParser.Parse(qc);
            ctx.AssertEqual(7, info.Studios.Count, "7 个 studio（5 个主模型 + 2 个 bodygroup）");

            StudioRef body = info.Studios.Find(s => s.Name == "body.glb");
            StudioRef plain = info.Studios.Find(s => s.Name == "plain.glb");
            StudioRef face = info.Studios.Find(s => s.Name == "face.glb");
            StudioRef lvar = info.Studios.Find(s => s.Name == "lvar.glb");
            StudioRef inc = info.Studios.Find(s => s.Name == "inc.glb");
            StudioRef eye1 = info.Studios.Find(s => s.Name == "eye1.glb");
            StudioRef eye2 = info.Studios.Find(s => s.Name == "eye2.glb");
            ctx.Assert(body != null && plain != null && face != null && lvar != null && inc != null && eye1 != null && eye2 != null,
                "所有 studio 都能按文件名找到");
            if (body == null || plain == null || face == null || lvar == null || inc == null || eye1 == null || eye2 == null) return;

            ctx.Assert(body.IsMainModel, "$model 是主模型");
            ctx.Assert(body.HasFacial, "flexfile → HasFacial");
            ctx.AssertEqual(-1, body.BodygroupIndex, "主模型 BodygroupIndex = -1");
            ctx.AssertEqual("(主模型 body)", body.Bodygroup, "主模型 Bodygroup 文案");
            ctx.Assert(!plain.HasFacial, "没有块的 $model 不会误取下一个模型的 flexfile");
            ctx.Assert(face.HasFacial, "flexcontroller → HasFacial");
            ctx.Assert(lvar.HasFacial, "localvar → HasFacial");
            ctx.Assert(inc.HasFacial, "$model 块里的 $include → HasFacial");

            ctx.AssertEqual("eyes", eye1.Bodygroup, "bodygroup 名");
            ctx.AssertEqual("eye1", eye1.Innnode, "同行 innode 参数被记录");
            ctx.Assert(string.IsNullOrEmpty(eye2.Innnode), "另一行的 studio 不会误取 innode");
            ctx.AssertEqual(eye1.BodygroupIndex, eye2.BodygroupIndex, "同一 bodygroup 内 studio 共享序号（旧版语义）");
            ctx.AssertEqual("eye2.glb", eye2.Name, "无引号 studio 也能解析");

            // 诊断信息
            var missing = QcParser.Parse(Path.Combine(dir, "missing.qc"));
            ctx.Assert(missing.ReadFailed, "不存在的 QC → ReadFailed");
            ctx.Assert(missing.Warnings.Count > 0, "不存在的 QC 有警告");
            string noModelName = Path.Combine(dir, "noname.qc");
            WriteText(noModelName, "$body body \"body.glb\"\r\n", Utf8(), false);
            var infoNoName = QcParser.Parse(noModelName);
            ctx.AssertContains(JoinWarnings(infoNoName), "没有找到 $modelname", "缺 $modelname 时给警告");
        }

        [SelfTest("QC：de-DE 区域下 $lod 数字仍用小数点")]
        public static void CultureInvariantNumbers(SelfTestContext ctx)
        {
            CultureInfo old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                string dir = ctx.NewDir("qc_culture");
                Touch(Path.Combine(dir, "m.glb"));
                string qc = Path.Combine(dir, "a.qc");
                WriteText(qc, "$modelname \"a.mdl\"\r\n$model \"body\" \"m.glb\"\r\n", Utf8(), false);

                var info = QcParser.Parse(qc);
                var plan = MakePlan(new[] { 12.5, 30.0 }, new[] { 0.6, 0.3 });
                string block = QcWriter.BuildLodBlock(info, plan,
                    Generated(Path.Combine(dir, "m_lod1.glb"), Path.Combine(dir, "m_lod2.glb")), false, true);
                ctx.Assert(block != null, "生成 $lod 块");
                ctx.AssertContains(block, "$lod 12.5", "$lod 距离用小数点");
                ctx.AssertNotContains(block, "12,5", "没有德式逗号小数");
                ctx.AssertContains(block, "切换距离 12.5", "注释行的距离也用小数点");
                ctx.AssertContains(block, "目标 60%", "比例无小数逗号");

                string body = QcWriter.WrapMarker(block, "德区测试", plan, false);
                ctx.AssertContains(body, "12.5 HU→60%", "LodPlan.Summary 用小数点");
                var r = QcWriter.Apply(info, body, false, new QcBackupRequest());
                ctx.Assert(r.Success, "Apply 成功: " + r.Error);
                string written = ReadText(qc, null);
                ctx.AssertContains(written, "$lod 12.5", "写盘内容也是小数点");

                // 直接验证 plan 摘要本身与区域无关
                ctx.AssertContains(plan.Summary(), "12.5", "Summary 含 12.5");
                ctx.AssertNotContains(plan.Summary(), "12,5", "Summary 不含 12,5");
            }
            finally
            {
                CultureInfo.CurrentCulture = old;
            }
        }

        [SelfTest("QC：BuildLodBlock 逐字格式与各级过滤")]
        public static void BuildLodBlockFormat(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("qc_block");
            string smd = Path.Combine(dir, "SMD");
            Directory.CreateDirectory(smd);
            Touch(Path.Combine(smd, "body.glb"));
            string qc = Path.Combine(dir, "a.qc");
            WriteText(qc, "$modelname \"a.mdl\"\r\n$bodygroup body\r\n{\r\n\tstudio \"SMD\\body\"\r\n}\r\n", Utf8(), false);

            var info = QcParser.Parse(qc);
            ctx.AssertEqual(1, info.Studios.Count, "解析到 1 个 studio");
            ctx.Assert(info.Studios[0].Exists, "SMD 子目录无扩展名模型已解析");

            var plan = MakePlan(new[] { 10.0, 15.0, 25.0 }, new[] { 0.997, 0.653, 0.34 });
            // 只生成 LOD1 / LOD3 → LOD2 整级不能写
            var generated = Generated(Path.Combine(smd, "body_lod1.glb"), Path.Combine(smd, "body_lod3.glb"));
            string block = QcWriter.BuildLodBlock(info, plan, generated, false, true);

            string expected =
                "// LOD1  切换距离 10  目标 99.7%  条目 1\n" +
                "$lod 10\n" +
                "{\n" +
                "\treplacemodel \"SMD\\body\" \"SMD\\body_lod1.glb\"\n" +
                "\tnofacial\n" +
                "}\n" +
                "// LOD3  切换距离 25  目标 34%  条目 1\n" +
                "$lod 25\n" +
                "{\n" +
                "\treplacemodel \"SMD\\body\" \"SMD\\body_lod3.glb\"\n" +
                "\tnofacial\n" +
                "}\n";
            ctx.AssertEqual(expected, block, "BuildLodBlock 逐字格式（Tab 缩进 / 两个空格分隔 / 缺级不写）");

            string noFacial = QcWriter.BuildLodBlock(info, plan, generated, false, false);
            ctx.AssertNotContains(noFacial, "nofacial", "noFacial=false 时不写 nofacial");
            ctx.AssertContains(noFacial, "replacemodel \"SMD\\body\" \"SMD\\body_lod1.glb\"", "关闭 nofacial 后 replacemodel 照写");

            // skipMainModel：主模型不写、bodygroup 照写
            string dir2 = ctx.NewDir("qc_block_skipmain");
            Touch(Path.Combine(dir2, "face.glb"));
            Touch(Path.Combine(dir2, "hair.glb"));
            string qc2 = Path.Combine(dir2, "b.qc");
            WriteText(qc2,
                "$modelname \"b.mdl\"\r\n$model \"face\" \"face.glb\"\r\n" +
                "$bodygroup hair\r\n{\r\n\tstudio \"hair.glb\"\r\n}\r\n", Utf8(), false);
            var info2 = QcParser.Parse(qc2);
            var plan1 = MakePlan(new[] { 10.0 }, new[] { 0.997 });
            var gen2 = Generated(Path.Combine(dir2, "face_lod1.glb"), Path.Combine(dir2, "hair_lod1.glb"));

            string skip = QcWriter.BuildLodBlock(info2, plan1, gen2, true, true);
            ctx.Assert(skip != null, "跳过主模型后仍可写 bodygroup 条目");
            ctx.AssertNotContains(skip, "face.glb", "skipMainModel=true 不写主模型");
            ctx.AssertContains(skip, "hair.glb", "bodygroup 模型照常写");

            string normal = QcWriter.BuildLodBlock(info2, plan1, gen2, false, true);
            ctx.AssertContains(normal, "replacemodel \"face.glb\" \"face_lod1.glb\"", "普通 QC 写主模型");

            // 没有生成的等级 → null
            string none = QcWriter.BuildLodBlock(info2, plan1, new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, true);
            ctx.Assert(none == null, "没有生成任何 LOD 文件时返回 null");
            ctx.Assert(QcWriter.BuildLodBlock(null, plan1, gen2, false, true) == null, "info=null 时返回 null");
        }

        [SelfTest("QC：独立 _LOD.qc 生成（编码/换行沿用根 QC）")]
        public static void SeparateQcGenerated(SelfTestContext ctx)
        {
            // A) GBK 无 BOM + LF
            string dir = ctx.NewDir("qc_sep_gbk");
            Touch(Path.Combine(dir, "body.glb"));
            string qc = Path.Combine(dir, "佐伊.qc");
            WriteText(qc, "// 中文注释：佐伊\n$modelname \"survivors/佐伊.mdl\"\n$body body \"body.glb\"\n", Gbk(), false);

            var info = QcParser.Parse(qc);
            ctx.AssertEqual(936, info.TextEncoding.CodePage, "根 QC 识别 GBK");
            ctx.AssertEqual("\n", info.NewLine, "根 QC 换行 LF");

            var plan = MakePlan(new[] { 10.0 }, new[] { 0.997 });
            string block = QcWriter.BuildLodBlock(info, plan, Generated(Path.Combine(dir, "body_lod1.glb")), false, true);
            // 独立 QC 模式下根 QC 块里只放一行 $include，头部第二行换成对应说明
            string includeBody = QcWriter.WrapMarker("$include \"佐伊_LOD.qc\"", "角色·标准", plan, true);
            ctx.AssertContains(includeBody,
                "// $lod 写在旁边那个 _LOD.qc 里，本文件只加一行 include。重新运行会整块替换，不要手工改这一段。",
                "isIncludeLine=true 时的头部说明逐字");
            ctx.AssertContains(includeBody, "$include \"佐伊_LOD.qc\"", "include 行保留");
            ctx.AssertEqual(ReadText(qc, Gbk()), QcParser.ReadTextSmart(qc), "ReadTextSmart 与智能读文本一致");

            string sep = QcWriter.SeparateQcPath(qc);
            AssertPathEqual(ctx, Path.Combine(dir, "佐伊_LOD.qc"), sep, "独立 QC 路径 = <QC名>_LOD.qc");

            ctx.Assert(QcWriter.WriteSeparateQc(sep, info, "角色·标准", plan, block) == null, "写独立 QC 成功");
            byte[] bytes = File.ReadAllBytes(sep);
            ctx.Assert(!HasUtf8Bom(bytes), "GBK 根 QC → 独立 QC 无 BOM");
            string txt = Gbk().GetString(bytes);
            ctx.AssertContains(txt, "// L4D2 自动 LOD 工具 —— 独立 $lod 文件（被 佐伊.qc 通过 $include 引用）", "头部说明含根 QC 名");
            ctx.AssertContains(txt, "// 生成时间: ", "头部含生成时间");
            ctx.AssertContains(txt, "// 涉及模型: survivors/佐伊.mdl", "头部含模型名");
            ctx.AssertContains(txt, "// 预设: 角色·标准", "头部含预设名");
            ctx.AssertContains(txt, "$lod 10", "含 $lod 正文");
            ctx.AssertContains(txt, "\treplacemodel \"body.glb\" \"body_lod1.glb\"", "含 replacemodel");
            ctx.Assert(txt.IndexOf('\r') < 0, "换行沿用根 QC 的 LF");
            var back = QcTextFile.Read(sep);
            ctx.AssertEqual(936, back.Encoding.CodePage, "独立 QC 回读仍识别 GBK");

            // B) UTF-8 BOM + CRLF 根 QC → 独立 QC 也带 BOM、CRLF
            string dir2 = ctx.NewDir("qc_sep_utf8bom");
            Touch(Path.Combine(dir2, "m.glb"));
            string qc2 = Path.Combine(dir2, "b.qc");
            WriteText(qc2, "$modelname \"b.mdl\"\r\n$model \"body\" \"m.glb\"\r\n", Utf8(), true);
            var info2 = QcParser.Parse(qc2);
            ctx.Assert(info2.HasBom, "根 QC 带 BOM");
            string block2 = QcWriter.BuildLodBlock(info2, MakePlan(new[] { 10.0 }, new[] { 0.997 }),
                Generated(Path.Combine(dir2, "m_lod1.glb")), false, true);
            string sep2 = QcWriter.SeparateQcPath(qc2);
            ctx.Assert(QcWriter.WriteSeparateQc(sep2, info2, "预设B", MakePlan(new[] { 10.0 }, new[] { 0.997 }), block2) == null,
                "写独立 QC 成功（BOM 场景）");
            byte[] b2 = File.ReadAllBytes(sep2);
            ctx.Assert(HasUtf8Bom(b2), "独立 QC 沿用 BOM");
            string t2 = new UTF8Encoding(false).GetString(b2, 3, b2.Length - 3);
            ctx.AssertContains(t2, "// 生成时间: ", "头部完整");
            ctx.Assert(!HasLoneLf(t2), "换行沿用 CRLF（没有裸 LF）");
            ctx.AssertContains(t2, "$lod 10", "正文完整");
        }

        [SelfTest("QC：注释遮蔽边界（字符串里的 //、块注释、花括号）")]
        public static void CommentMaskingEdges(SelfTestContext ctx)
        {
            string text =
                "$definitioninfo \"x { } y\"\r\n" +
                "$modelname \"a.mdl\"\r\n" +
                "$body weird \"a//b.glb\"\r\n" +
                "$body comment \"c.glb\" // 行注释 } {\r\n" +
                "/* 块注释 $lod 10 { } */\r\n" +
                "$body unterminated \"u.glb\r\n" +
                "$bodygroup g\r\n{\r\n\tstudio \"w{1}.glb\"\r\n}\r\n";

            string masked = QcLexer.MaskComments(text);
            ctx.AssertEqual(text.Length, masked.Length, "遮蔽后长度不变（位置一一对应）");
            ctx.AssertContains(masked, "$definitioninfo \"x { } y\"", "字符串里的花括号原样保留");
            ctx.AssertContains(masked, "\"a//b.glb\"", "字符串里的 // 不被当注释");
            ctx.AssertNotContains(masked, "行注释", "行注释被遮蔽");
            ctx.AssertNotContains(masked, "块注释", "块注释被遮蔽");
            ctx.AssertNotContains(masked, "$lod", "块注释里的 $lod 被遮蔽");

            var tokens = QcLexer.Tokenize(masked);
            int braces = 0;
            foreach (QcToken t in tokens) if (t.IsBrace) braces++;
            ctx.AssertEqual(2, braces, "只承认真正的 { }（引号里的不算）");
            ctx.AssertEqual("a//b.glb", FindQuoted(tokens, "a//b.glb"), "引号内的 // 完整保留");
            int uAt = tokens.FindIndex(t => t.Quoted && t.Text == "u.glb");
            ctx.Assert(uAt >= 0, "未闭合字符串也产出 token");
            bool sawAfter = false;
            for (int i = uAt + 1; uAt >= 0 && i < tokens.Count; i++)
            {
                if (tokens[i].IsWord("$bodygroup")) { sawAfter = true; break; }
            }
            ctx.Assert(sawAfter, "未闭合字符串没有吞掉后面的内容（遇到行尾就结束）");

            // 括号匹配：跳过引号内与注释里的花括号
            int open = -1;
            foreach (QcToken t in tokens)
            {
                if (!t.IsBrace || t.Text != "{") continue;
                open = t.Pos;
            }
            ctx.Assert(open > 0, "找到 $bodygroup 的 '{'");
            int close = QcLexer.FindMatchingBrace(masked, open);
            ctx.Assert(close > open, "找到匹配的 '}'");
            ctx.AssertEqual(masked.LastIndexOf('}'), close, "匹配到的是 bodygroup 的真实结束括号");
            ctx.AssertContains(masked.Substring(open, close - open), "\"w{1}.glb\"", "引号内的花括号不干扰匹配");

            // StripComments：删注释、保留字符串
            string stripped = QcLexer.StripComments(text);
            ctx.AssertNotContains(stripped, "行注释", "StripComments 删掉行注释");
            ctx.AssertNotContains(stripped, "块注释", "StripComments 删掉块注释");
            ctx.AssertContains(stripped, "\"a//b.glb\"", "StripComments 保留字符串内容");
            ctx.AssertEqual(CountOccurrences(text, "\r\n"), CountOccurrences(stripped, "\r\n"), "StripComments 保留行结构");

            // CommentOutForeignLod：注释里的 $lod 不算，字符串里的不算
            string foreign =
                "$modelname \"a.mdl\"\r\n" +
                "// $lod 20\r\n" +
                "$lod 10\r\n{\r\n\treplacemodel \"a.glb\" \"b.glb\"\r\n}\r\n" +
                "$sequence idle \"idle\"\r\n";
            int count;
            string commented = QcLexer.CommentOutForeignLod(foreign, out count);
            ctx.AssertEqual(1, count, "只停用真实的 1 个 $lod 块（注释里的不算）");
            ctx.AssertContains(commented, QcLexer.DisabledPrefix + "$lod 10", "真实 $lod 行被注释");
            ctx.AssertContains(commented, QcLexer.DisabledPrefix + "\treplacemodel", "块内行被注释");
            ctx.AssertContains(commented, QcLexer.DisabledPrefix + "}", "块结束行被注释");
            ctx.AssertContains(commented, "// $lod 20", "本来就是注释的 $lod 不重复加前缀");
            ctx.AssertContains(commented, "$sequence idle \"idle\"", "块外内容不动");

            // 工具块完整时：块内 $lod 不算手写，TryRemoveToolBlock 能移除
            string withTool = "$modelname \"a.mdl\"\r\n" + QcLexer.MarkerBegin + "\r\n$lod 5\r\n{\r\n}\r\n" +
                              QcLexer.MarkerEnd + "\r\n$sequence idle \"idle\"\r\n";
            bool broken;
            string toolMasked = QcLexer.MaskToolBlocks(withTool, out broken);
            ctx.Assert(!broken, "完整工具块不标 broken");
            ctx.AssertNotContains(toolMasked, "$lod 5", "工具块内容被遮蔽");
            string noTool;
            ctx.Assert(QcLexer.TryRemoveToolBlock(withTool, out noTool), "完整工具块可以移除");
            ctx.AssertNotContains(noTool, QcLexer.MarkerBegin, "移除后没有开始标记");
            ctx.AssertContains(noTool, "$sequence idle \"idle\"", "移除后其它内容保留");
        }

        [SelfTest("QC：toolBody=null 只移除工具块并还原原文")]
        public static void RemoveToolBlockRestoresOriginal(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("qc_remove");
            Touch(Path.Combine(dir, "m.glb"));
            string qc = Path.Combine(dir, "a.qc");
            string original = "$modelname \"a.mdl\"\r\n$model \"body\" \"m.glb\"\r\n";
            WriteText(qc, original, Utf8(), false);

            var info = QcParser.Parse(qc);
            var r1 = QcWriter.Apply(info, "$lod 5\n{\n}\n", false, new QcBackupRequest());
            ctx.Assert(r1.Success, "第一次注入成功: " + r1.Error);
            ctx.AssertContains(ReadText(qc, null), QcLexer.MarkerBegin, "已有工具块");
            ctx.AssertEqual(1, Backups(dir), "第一次产生一份备份");

            var info2 = QcParser.Parse(qc);
            var r2 = QcWriter.Apply(info2, null, false, new QcBackupRequest());
            ctx.Assert(r2.Success, "移除工具块成功: " + r2.Error);
            ctx.Assert(!r2.NoChange, "移除是一次真实修改");
            ctx.AssertEqual(original, ReadText(qc, null), "内容恢复成原文（末尾单个换行）");
            ctx.AssertNotContains(ReadText(qc, null), QcLexer.MarkerBegin, "没有工具块残留");

            // 已无工具块时再传 null → NoChange，不备份
            var info3 = QcParser.Parse(qc);
            int backups = Backups(dir);
            var r3 = QcWriter.Apply(info3, null, false, new QcBackupRequest());
            ctx.Assert(r3.Success && r3.NoChange, "无块时 toolBody=null → NoChange");
            ctx.AssertEqual(backups, Backups(dir), "NoChange 不产生备份");
        }

        [SelfTest("QC：工具产物识别与 FindModelName")]
        public static void ToolGeneratedAndModelName(SelfTestContext ctx)
        {
            ctx.Assert(QcParser.IsToolGeneratedQc("x_LOD.qc"), "_LOD.qc 是工具产物");
            ctx.Assert(QcParser.IsToolGeneratedQc("x_autoLOD.qc"), "_autoLOD.qc 是工具产物");
            ctx.Assert(QcParser.IsToolGeneratedQc("_LOD合集.qc"), "_LOD合集.qc 是工具产物");
            ctx.Assert(QcParser.IsToolGeneratedQc("_LOD合集_2.qc"), "_LOD合集_2.qc 是工具产物");
            ctx.Assert(QcParser.IsToolGeneratedQc("自动LOD_chair.qc"), "自动LOD_*.qc 是工具产物");
            ctx.Assert(!QcParser.IsToolGeneratedQc("zoey.qc"), "普通 QC 不是工具产物");
            ctx.Assert(!QcParser.IsToolGeneratedQc("我的_LOD说明.qc"), "中间含 _LOD 但结尾不是则不算");
            ctx.Assert(!QcParser.IsToolGeneratedQc(null), "null 安全返回 false");
            ctx.Assert(!QcParser.IsToolGeneratedQc(""), "空串安全返回 false");

            string dir = ctx.NewDir("qc_findmodel");
            string sub = Path.Combine(dir, "sub");
            Directory.CreateDirectory(sub);
            string root = Path.Combine(dir, "a.qc");
            string inc = Path.Combine(sub, "m.qc");

            // $modelname 只取第一个，反斜杠统一成正斜杠
            WriteText(root, "$modelname \"props\\chair.mdl\"\r\n$modelname \"second/other.mdl\"\r\n", Utf8(), false);
            var modelNameInfo = QcParser.Parse(root);
            ctx.AssertEqual("props/chair.mdl", modelNameInfo.ModelName, "只取第一个 $modelname 且 \\ 归一为 /");

            WriteText(root, "$include \"sub/m.qc\"\r\n", Utf8(), false);
            WriteText(inc, "$modelname \"props/chair.mdl\"\r\n", Utf8(), false);
            ctx.AssertEqual("props/chair.mdl", QcParser.FindModelName(root, 8), "沿 include 链找到 $modelname");
            ctx.AssertEqual("props/chair.mdl", QcParser.FindModelName(root, 0), "maxDepth<=0 用默认层数");
            WriteText(root, "$modelname \"own/model.mdl\"\r\n$include \"sub/m.qc\"\r\n", Utf8(), false);
            ctx.AssertEqual("own/model.mdl", QcParser.FindModelName(root, 8), "根 QC 自己的 $modelname 优先");
            ctx.AssertEqual("", QcParser.FindModelName(Path.Combine(dir, "nope.qc"), 8), "文件不存在返回空串");

            // 环 include 不死循环
            WriteText(root, "$include \"sub/m.qc\"\r\n", Utf8(), false);
            WriteText(inc, "$include \"../a.qc\"\r\n", Utf8(), false);
            ctx.AssertEqual("", QcParser.FindModelName(root, 8), "环里找不到返回空串（不死循环）");
        }

        // ───────────────────────────── 小工具 ─────────────────────────────

        private static string FindQuoted(List<QcToken> tokens, string text)
        {
            foreach (QcToken t in tokens)
                if (t.Quoted && string.Equals(t.Text, text, StringComparison.Ordinal)) return t.Text;
            return null;
        }

        private static string JoinWarnings(QcInfo info)
        {
            if (info == null || info.Warnings == null) return "";
            return string.Join(" | ", info.Warnings.ToArray());
        }
    }
}
