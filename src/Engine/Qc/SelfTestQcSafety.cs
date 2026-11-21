using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>评审修复的回归用例：编码安全 / 只读拒绝 / 混合换行幂等 / include 与路径提示。</summary>
    internal static class SelfTestQcSafety
    {
        private static string WriteRaw(string dir, string name, string text, Encoding enc, bool bom)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, QcTextFile.Encode(text, enc, bom));
            return path;
        }

        private static QcInfo ParseQc(string dir, string name, string text)
        {
            string path = WriteRaw(dir, name, text, new UTF8Encoding(false), false);
            return QcParser.Parse(path);
        }

        [SelfTest("QC安全：混合换行文件重复注入保持字节幂等")]
        public static void MixedNewLineIdempotent(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("qcsafety_mixednl");
            // 故意混用 CRLF 与 LF（真实工程里编辑器合并后很常见）
            string text = "// 注释行一\r\n$modelname \"models/x.mdl\"\n$bodygroup \"mesh\"\r\n{\n    studio \"mesh.smd\"\r\n}\n";
            File.WriteAllText(Path.Combine(dir, "mesh.smd"),
                "version 1\nnodes\n0 \"root\" -1\nend\nskeleton\ntime 0\n0 0 0 0 0 0\nend\ntriangles\nend\n",
                new UTF8Encoding(false));
            string path = WriteRaw(dir, "mixed.qc", text, new UTF8Encoding(false), false);
            var info = QcParser.Parse(path);
            ctx.Assert(!info.ReadFailed, "解析失败：" + string.Join(";", info.Warnings.ToArray()));
            var plan = new LodPlan();
            plan.Levels.Add(new LodLevel(20, 0.6));
            plan.Levels.Add(new LodLevel(50, 0.25));
            var dst = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            dst.Add(Path.GetFullPath(Path.Combine(dir, "mesh_lod1.smd")));
            dst.Add(Path.GetFullPath(Path.Combine(dir, "mesh_lod2.smd")));
            string block = QcWriter.BuildLodBlock(info, plan, dst, false, true);
            ctx.Assert(block != null, "应能生成 $lod 块");
            string body = QcWriter.WrapMarker(block, "测试", plan, false);
            var backup = new QcBackupRequest { Enabled = true };
            var r1 = QcWriter.Apply(info, body, false, backup);
            ctx.Assert(r1.Success, "第一次写入失败：" + r1.Error);
            byte[] after1 = File.ReadAllBytes(path);
            var info2 = QcParser.Parse(path);
            var r2 = QcWriter.Apply(info2, body, false, backup);
            ctx.Assert(r2.Success, "第二次写入失败：" + r2.Error);
            byte[] after2 = File.ReadAllBytes(path);
            ctx.Assert(after1.Length == after2.Length, "第二次运行改变了文件大小（" + after1.Length + " → " + after2.Length + "），不幂等");
            for (int i = 0; i < after1.Length; i++)
                if (after1[i] != after2[i]) throw new SelfTestFailureException("第二次运行改变了第 " + i + " 个字节，不幂等");
            // 原文里只被注入一处：原有行的 CRLF/LF 混用应保留（未被整篇归一化）
            string final = QcTextFile.Read(path).Text;
            ctx.AssertContains(final, "// 注释行一\r\n", "原有 CRLF 行应保留");
            ctx.Assert(final.IndexOf("// 注释行一'dummy", StringComparison.Ordinal) < 0, "无意义断言");
        }

        [SelfTest("QC安全：只读 QC 被拒绝修改且不留备份/残留")]
        public static void ReadOnlyRefused(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("qcsafety_readonly");
            string path = WriteRaw(dir, "ro.qc",
                "$modelname \"models/x.mdl\"\r\n$bodygroup \"m\"\r\n{\r\n    studio \"mesh.smd\"\r\n}\r\n",
                new UTF8Encoding(false), false);
            byte[] before = File.ReadAllBytes(path);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            try
            {
                var info = QcParser.Parse(path);
                var plan = new LodPlan();
                plan.Levels.Add(new LodLevel(20, 0.6));
                var dst = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(Path.Combine(dir, "mesh_lod1.glb")) };
                string body = QcWriter.WrapMarker(QcWriter.BuildLodBlock(info, plan, dst, false, true), "测试", plan, false);
                var r = QcWriter.Apply(info, body, false, new QcBackupRequest { Enabled = true });
                ctx.Assert(!r.Success, "只读文件应拒绝修改");
                ctx.AssertContains(r.Error ?? "", "只读", "错误原因");
                byte[] after = File.ReadAllBytes(path);
                ctx.Assert(before.Length == after.Length, "只读文件内容被改变");
                for (int i = 0; i < before.Length; i++) if (before[i] != after[i]) throw new SelfTestFailureException("只读文件字节被修改");
                ctx.Assert(Directory.GetFiles(dir, "*.lodbak*").Length == 0, "拒绝修改时不应留下 .lodbak");
                ctx.Assert(Directory.GetFiles(dir, "*.old_*").Length == 0, "拒绝修改时不应留下 .old_ 临时文件");
            }
            finally
            {
                try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            }
        }

        [SelfTest("QC安全：BOM 与正文编码不一致时拒绝读取（不写回替换字符）")]
        public static void BomMismatchRefused(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("qcsafety_bom");
            string path = Path.Combine(dir, "bad.qc");
            // UTF-8 BOM + GBK 正文（真实事故：编辑器保存编码与 BOM 不一致）
            byte[] gbkBody = Encoding.GetEncoding(936).GetBytes("$modelname \"models/中文.mdl\"\r\n");
            var bytes = new byte[gbkBody.Length + 3];
            bytes[0] = 0xEF; bytes[1] = 0xBB; bytes[2] = 0xBF;
            Buffer.BlockCopy(gbkBody, 0, bytes, 3, gbkBody.Length);
            File.WriteAllBytes(path, bytes);
            bool threw = false;
            try { QcTextFile.Read(path); }
            catch (InvalidDataException) { threw = true; }
            ctx.Assert(threw, "BOM 与正文不一致应抛 InvalidDataException（拒绝修改）");
            var info = QcParser.Parse(path);
            ctx.Assert(info.ReadFailed, "解析应标记 ReadFailed");
            ctx.AssertContains(string.Join(";", info.Warnings.ToArray()), "BOM", "警告里应说明 BOM/编码问题");
        }

        [SelfTest("QC安全：$include 缺失与按文件名兜底都会给警告")]
        public static void IncludeAndNameFallbackWarnings(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("qcsafety_warn");
            // 声明 SMD/body.smd（不存在），但同目录有 body.smd → 按文件名命中并给警告
            File.WriteAllText(Path.Combine(dir, "body.smd"), "version 1\nnodes\nend\nskeleton\ntime 0\nend\ntriangles\n", new UTF8Encoding(false));
            var info = ParseQc(dir, "warn.qc",
                "$modelname \"models/x.mdl\"\r\n$include \"missing_include.qc\"\r\n" +
                "$bodygroup \"m\"\r\n{\r\n    studio \"SMD/body.smd\"\r\n}\r\n");
            string all = string.Join(";", info.Warnings.ToArray());
            ctx.AssertContains(all, "include", "缺失 include 应有警告");
            ctx.AssertContains(all, "按文件名", "按文件名兜底应有警告");
            ctx.AssertEqual(1, info.Studios.Count, "studio 条目数");
            ctx.Assert(info.Studios[0].Exists, "按文件名兜底应仍能命中文件（参与处理）");
        }
    }
}
