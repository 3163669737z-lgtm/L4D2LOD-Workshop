using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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

        [SelfTest("QC安全：阴影 LOD 格式、八级上限与分档指令")]
        public static void ShadowLodAndTieredDirectives(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("qcsafety_shadow_tiers");
            File.WriteAllText(Path.Combine(dir, "mesh.smd"), "");
            var info = ParseQc(dir, "actor.qc", "$modelname \"actor.mdl\"\n$body \"mesh\" \"mesh.smd\"\n");
            var plan = new LodPlan();
            var published = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int level = 1; level <= 7; level++)
            {
                plan.Levels.Add(new LodLevel(level * 10, 1 - level * 0.1));
                published.Add(Path.Combine(dir, "mesh_lod" + level + ".smd"));
            }
            ctx.AssertEqual(7, Regex.Matches(QcWriter.BuildLodBlock(info, plan, published, false, false),
                @"(?m)^\$lod ").Count, "不生成阴影 LOD 时可写七档");

            var extra = new QcLodBlockOptions
            {
                GenerateShadowLod = true,
                ShadowPublishedPath = _ => Path.Combine(dir, "mesh_shadowlod.smd"),
                NoFacialFromLevel = 2,
                CollapseBonesFromLevel = 4,
                CollapseBones = new List<string> { "  detail  ", "toe", "", "  ", "bad\"quote", "bad\rreturn", "bad\nline" }
            };
            string block = QcWriter.BuildLodBlock(info, plan, published, false, true, extra);
            ctx.AssertContains(block, "$shadowlod\n{\n\treplacemodel \"mesh.smd\" \"mesh_shadowlod.smd\"\n}", "阴影指令独占一行，花括号另起一行");
            var visible = Regex.Matches(block, @"(?m)^\$lod [^\n]+\n\{\n([^}]*)\}");
            ctx.AssertEqual(6, visible.Count, "基础档和阴影档各占一级，最多输出六个距离档");
            ctx.AssertEqual(1, Regex.Matches(block, @"(?m)^\$shadowlod\b").Count, "恰好一个阴影档");
            ctx.AssertNotContains(block, "mesh_lod7.smd", "末档受八级上限限制");
            ctx.AssertEqual(5, Regex.Matches(block, @"(?m)^\tnofacial$").Count, "表情禁用指令总数");
            for (int i = 0; i < visible.Count; i++)
            {
                string commands = visible[i].Groups[1].Value;
                ctx.Assert(commands.Contains("\tnofacial\n") == (i + 1 >= 2), "nofacial 开始档位错误");
                int boneCount = Regex.Matches(commands, @"(?m)^\tbonetreecollapse ").Count;
                ctx.AssertEqual(i + 1 >= 4 ? 2 : 0, boneCount, "骨骼折叠开始档位及清理后骨骼数");
                if (i + 1 >= 4)
                    ctx.AssertContains(commands, "\tbonetreecollapse \"detail\"\n\tbonetreecollapse \"toe\"\n", "保留合法骨骼并去掉首尾空格");
            }
            ctx.AssertNotContains(block, "bad", "过滤引号与换行的骨骼名");
        }

        [SelfTest("QC安全：复用按实际写入的档位选网格，跳过未接受的候选")]
        public static void ReuseEmittedShadowTier(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("qcsafety_shadow_reuse");
            File.WriteAllText(Path.Combine(dir, "mesh.smd"), "");
            File.WriteAllText(Path.Combine(dir, "spare.smd"), "");
            var info = ParseQc(dir, "actor.qc",
                "$modelname \"actor.mdl\"\n$body \"mesh\" \"mesh.smd\"\n$body \"spare\" \"spare.smd\"\n");
            var plan = new LodPlan();
            for (int level = 1; level <= 4; level++) plan.Levels.Add(new LodLevel(level * 10, 1 - level * 0.1));
            var published = new HashSet<string>(new[] { "mesh_lod1.smd", "mesh_lod2.smd", "mesh_lod3.smd",
                "mesh_lod4.smd", "spare_lod4.smd" }.Select(name => Path.Combine(dir, name)), StringComparer.OrdinalIgnoreCase);
            var accepted = new Dictionary<int, AutomaticLodResult>();
            foreach (int candidate in new[] { 1, 3, 4 })
                accepted.Add(candidate, new AutomaticLodResult { Candidate = candidate, SwitchPoint = candidate * 10 });

            var options = new QcLodBlockOptions
            {
                GenerateShadowLod = true,
                ShadowLodMode = ShadowLodMode.ReuseExistingLod,
                ShadowLodReuseLevel = 2,
                ShadowPublishedPath = _ => throw new InvalidOperationException("复用模式不应查找独立阴影网格")
            };
            string block = QcWriter.BuildLodBlock(info, plan, published, false, false, options, false, null, accepted, null, out var emitted);
            ctx.AssertEqual(3, emitted.EmittedVisibleLevels, "跳过一个未接受的候选");
            ctx.Assert(emitted.HasShadowLod && !emitted.UsesDedicatedShadow, "写入复用阴影档");
            ctx.AssertEqual(2, emitted.ShadowReusedLevel, "复用实际写入的第二档");
            ctx.AssertContains(block, "$shadowlod\n{\n\treplacemodel \"mesh.smd\" \"mesh_lod3.smd\"\n" +
                "\treplacemodel \"spare.smd\" \"spare_lod4.smd\"\n}", "首选目标档，缺少条目时使用该模型最远可见档");

            block = QcWriter.BuildLodBlock(info, plan, published, false, false, options, automaticLevels: new Dictionary<int, AutomaticLodResult>());
            ctx.Assert(block == null, "没有可见档时跳过复用阴影");

            options.ShadowLodMode = ShadowLodMode.Dedicated;
            options.ShadowPublishedPath = null;
            block = QcWriter.BuildLodBlock(info, plan, published, false, false, options, false, null, accepted, null, out emitted);
            ctx.AssertEqual(3, emitted.ShadowReusedLevel, "独立网格不可用时回退到最远可见档");
            ctx.AssertContains(block, "// Shadow LOD  条目 2\n$shadowlod\n{\n\treplacemodel \"mesh.smd\" \"mesh_lod4.smd\"",
                "独立模式回退时保留注释格式");
        }

        [SelfTest("QC安全：nekomdl 编译骨骼折叠及阴影 LOD 并验证 VTX")]
        public static void CompileShadowAndBoneCollapse(SelfTestContext ctx)
        {
            string exe = Nekomdl.Find(null);
            if (exe == null)
            {
                ctx.Log("SKIP: 本机没有 nekomdl.exe，跳过真实试编译用例");
                return;
            }

            string dir = Path.GetFullPath(ctx.NewDir("qcsafety_compile_shadow"));
            const string skeleton = "version 1\nnodes\n0 \"root\" -1\n1 \"detail\" 0\nend\n" +
                                    "skeleton\ntime 0\n0 0 0 0 0 0 0\n1 0 0 0 0 0 0\nend\n";
            void WriteMesh(string name, int count)
            {
                var smd = new StringBuilder(skeleton).Append("triangles\n");
                for (int i = 0; i < count; i++)
                {
                    int x = i * 10;
                    smd.Append("material\n")
                        .Append("0 ").Append(x).Append(" 0 0 0 0 1 0 0 2 0 0.5 1 0.5\n")
                        .Append("0 ").Append(x + 5).Append(" 0 0 0 0 1 1 0 2 0 0.5 1 0.5\n")
                        .Append("0 ").Append(x).Append(" 5 0 0 0 1 0 1 2 0 0.5 1 0.5\n");
                }
                File.WriteAllText(Path.Combine(dir, name), smd.Append("end\n").ToString(), Encoding.ASCII);
            }
            WriteMesh("mesh.smd", 3);
            WriteMesh("mesh_lod1.smd", 2);
            WriteMesh("mesh_shadowlod.smd", 1);
            File.WriteAllText(Path.Combine(dir, "idle.smd"), skeleton, Encoding.ASCII);

            var info = ParseQc(dir, "actor.qc",
                "$modelname \"models/lodstudio_selftest_shadow.mdl\"\n$surfaceprop \"default\"\n" +
                "$bodygroup \"mesh\"\n{\n\tstudio \"mesh.smd\"\n}\n$sequence \"idle\" \"idle.smd\" fps 30\n");
            var plan = new LodPlan();
            plan.Levels.Add(new LodLevel(20, 0.67));
            var published = new HashSet<string> { Path.Combine(dir, "mesh_lod1.smd") };
            var extra = new QcLodBlockOptions
            {
                GenerateShadowLod = true,
                ShadowPublishedPath = _ => Path.Combine(dir, "mesh_shadowlod.smd"),
                CollapseBones = new List<string> { "detail" },
                CollapseBonesFromLevel = 1
            };
            string block = QcWriter.BuildLodBlock(info, plan, published, false, false, extra);
            ctx.AssertContains(block, "$lod 20\n{\n", "写入可编译的距离档");
            ctx.AssertContains(block, "\tbonetreecollapse \"detail\"\n", "写入可编译的骨骼折叠指令");
            ctx.AssertContains(block, "$shadowlod\n{\n", "写入可编译的阴影档");
            string qc = Path.Combine(dir, "actor.qc");
            File.AppendAllText(qc, block, Encoding.ASCII);

            CompileResult cr = Nekomdl.Compile(exe, qc);
            ctx.Assert(cr.Success, "nekomdl 编译失败：" + string.Join("; ", cr.Errors));
            ctx.Assert(File.Exists(cr.VtxPath) && cr.VtxPath.EndsWith(".dx90.vtx", StringComparison.OrdinalIgnoreCase),
                "应有 .dx90.vtx 编译产物");
            ctx.Assert(cr.Vtx != null && cr.Vtx.Valid, "应能解析 VTX：" + cr.Vtx?.Error);
            ctx.AssertEqual(3, cr.Vtx.NumLods, "VTX 包含基础档、距离档和阴影档");
            ctx.AssertEqual(3, cr.Vtx.Triangles[0], "VTX 基础档面数");
            ctx.Assert(Path.GetFullPath(cr.VtxPath).StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "VTX 应生成在测试临时目录内");
            ctx.Log("真实 VTX：" + VtxInspector.Describe(cr.Vtx));
        }

        [SelfTest("QC安全：nekomdl 编译含 VTA 的分档表情与阴影 LOD")]
        public static void CompileTieredFacialAndShadow(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("qcsafety_compile_facial");
            const string bones = "version 1\nnodes\n0 \"root\" -1\nend\n";
            const string pose = "0 0 0 0 0 0 0\n";
            const string skeleton = bones + "skeleton\ntime 0\n" + pose + "end\n";
            const string a = "0 0 0 0 0 0 1 0 0\n";
            const string b = "0 10 0 0 0 0 1 1 0\n";
            const string c = "0 10 10 0 0 0 1 1 1\n";
            const string d = "0 0 10 0 0 0 1 0 1\n";
            const string e = "0 20 0 0 0 0 1 2 0\n";
            const string first = "material\n" + a + b + c;
            const string second = "material\n" + a + c + d;
            const string third = "material\n" + b + e + c;
            void WriteMesh(string name, string triangles) =>
                WriteRaw(dir, name, skeleton + "triangles\n" + triangles + "end\n", Encoding.ASCII, false);

            WriteMesh("face.smd", first + second);
            WriteMesh("face_lod1.smd", first);
            WriteMesh("face_lod2.smd", first);
            WriteMesh("face_shadowlod.smd", first);
            WriteMesh("body.smd", first + second + third);
            WriteMesh("body_lod1.smd", first + second);
            WriteMesh("body_shadowlod.smd", first);
            WriteRaw(dir, "idle.smd", skeleton, Encoding.ASCII, false);
            WriteRaw(dir, "face.vta", bones + "skeleton\ntime 0\n" + pose + "time 1\n" + pose + "end\n" +
                "vertexanimation\ntime 0\n0 0 0 0 0 0 1\n1 10 0 0 0 0 1\n2 10 10 0 0 0 1\n3 0 10 0 0 0 1\n" +
                "time 1\n2 10 10 2 0 0 1\nend\n", Encoding.ASCII, false);
            var info = ParseQc(dir, "actor.qc",
                "$modelname \"lodstudio_selftest_facial.mdl\"\n$body \"body\" \"body.smd\"\n" +
                "$model \"face\" \"face.smd\" {\n\tflexfile \"face.vta\" {\n\t\tdefaultflex frame 0\n" +
                "\t\tflex \"smile\" frame 1\n\t}\n\tflexcontroller mouth range 0 1 smile\n\t%smile = smile\n}\n" +
                "$sequence \"idle\" \"idle.smd\" fps 30\n");
            ctx.Assert(info.Studios.Single(s => s.RawFile == "face.smd").HasFacial, "真实 VTA 声明标记为表情模型");
            var plan = new LodPlan();
            plan.Levels.Add(new LodLevel(15, 0.67));
            plan.Levels.Add(new LodLevel(30, 0.33));
            var published = new HashSet<string>(new[] { "body_lod1.smd", "face_lod1.smd", "face_lod2.smd" }
                .Select(name => Path.Combine(dir, name)));
            var extra = new QcLodBlockOptions
            {
                GenerateShadowLod = true,
                NoFacialFromLevel = 2,
                ShadowPublishedPath = source => Path.Combine(dir, Path.GetFileNameWithoutExtension(source) + "_shadowlod.smd")
            };
            string protectedBlock = QcWriter.BuildLodBlock(info, plan, published, false, false, extra, reuseEarlierLods: true);
            ctx.AssertContains(protectedBlock, "$shadowlod\n{\n\treplacemodel \"body.smd\"", "关闭表情开关时仍可减面身体阴影");
            ctx.AssertNotContains(protectedBlock, "replacemodel \"face.smd\"", "直接写入时可见档和阴影档均保护表情");
            ctx.AssertNotContains(protectedBlock, "\tnofacial\n", "表情关闭指令由用户启用");

            string block = QcWriter.BuildLodBlock(info, plan, published, false, true, extra, reuseEarlierLods: true);
            ctx.AssertContains(block, "$lod 15\n{\n\treplacemodel \"body.smd\" \"body_lod1.smd\"\n}", "近档只替换身体");
            ctx.AssertContains(block, "\treplacemodel \"face.smd\" \"face_lod2.smd\"\n\tnofacial\n}", "远档替换表情并禁用形变");
            ctx.AssertContains(block, "\treplacemodel \"face.smd\" \"face_shadowlod.smd\"\n\tnofacial\n}", "独立阴影档显式关闭表情");
            extra.ShadowLodMode = ShadowLodMode.ReuseExistingLod;
            string reused = QcWriter.BuildLodBlock(info, plan, published, false, true, extra, reuseEarlierLods: true);
            ctx.AssertContains(reused.Substring(reused.IndexOf("$shadowlod", StringComparison.Ordinal)),
                "\treplacemodel \"face.smd\" \"face_lod2.smd\"\n\tnofacial\n}", "复用阴影档也保护表情替换");

            string exe = Nekomdl.Find(null);
            if (exe == null)
            {
                ctx.Log("SKIP: 本机没有 nekomdl.exe，跳过真实 VTA 试编译");
                return;
            }
            File.AppendAllText(info.QcPath, block, Encoding.ASCII);
            CompileResult cr = Nekomdl.Compile(exe, info.QcPath);
            ctx.Assert(cr.Success, "nekomdl 编译失败：" + string.Join("; ", cr.Errors));
            ctx.Assert(File.Exists(cr.VtxPath) && cr.Vtx?.Valid == true, "应能解析真实 VTX：" + cr.Vtx?.Error);
            ctx.AssertEqual(4, cr.Vtx.NumLods, "VTX 包含基础档、两个距离档及阴影档");
            ctx.Assert(cr.Vtx.Triangles.SequenceEqual(new[] { 5, 4, 3, 2 }),
                "VTX 面数应依次为 5 → 4 → 3 → 2，实际为 " + string.Join(" → ", cr.Vtx.Triangles));
            ctx.Log("真实 VTA / VTX：" + VtxInspector.Describe(cr.Vtx));
        }

        [SelfTest("QC安全：扫描备份与孤立网格、保护引用、按字节恢复")]
        public static void BackupScanAndRestore(SelfTestContext ctx)
        {
            string dir = Path.GetFullPath(ctx.NewDir("qcsafety_restore"));
            const string original = "// 原始工程\r\n$modelname \"actor.mdl\"\n";
            string qc = WriteRaw(dir, "actor.qc", original, new UTF8Encoding(false), true);
            byte[] before = File.ReadAllBytes(qc);
            string backup = qc + ".lodbak";
            File.Copy(qc, backup, overwrite: true);
            File.AppendAllText(qc,
                "$lod 20\n{\n\treplacemodel \"mesh.smd\" \"mesh_lod1.smd\"\n}\n$include \"shadow.qci\"\n");
            File.WriteAllText(Path.Combine(dir, "shadow.qci"),
                "$shadowlod\n{\n\treplacemodel \"mesh.smd\" \"mesh_shadowlod.smd\"\n}\n");
            var meshes = new[] { "mesh_lod1.smd", "mesh_shadowlod.smd", "unused_lod2.smd", "unused_shadowlod.smd" }
                .Select(name => Path.Combine(dir, name)).ToArray();
            foreach (string mesh in meshes) File.WriteAllText(mesh, "");

            var scan = QcBackupManager.Scan(new[] { qc });
            ctx.Assert(scan.BackupFiles.SequenceEqual(new[] { backup }), "扫描应找到 .qc.lodbak");
            ctx.Assert(new HashSet<string>(scan.OrphanLodFiles, StringComparer.OrdinalIgnoreCase).SetEquals(meshes.Skip(2)),
                "只列出未被 QC 或 include 引用的距离和阴影网格");
            ctx.AssertEqual(0, scan.SkippedReasons.Count, "有效 QC 应能完整扫描");
            ctx.AssertEqual(1, QcBackupManager.RestoreOriginalQcs(scan.BackupFiles, out var errors), "恢复一个 QC");
            ctx.AssertEqual(0, errors.Count, "恢复错误：" + string.Join("; ", errors));
            ctx.Assert(File.ReadAllBytes(qc).SequenceEqual(before), "恢复须保留原 QC 的 BOM、混合换行及所有字节");
            ctx.Assert(File.Exists(backup) && File.ReadAllBytes(backup).SequenceEqual(before), "恢复后须保留原始备份");
        }

        [SelfTest("QC安全：删除孤立网格时复查新增的 QC 引用")]
        public static void OrphanDeletionRechecksReferences(SelfTestContext ctx)
        {
            string dir = Path.GetFullPath(ctx.NewDir("qcsafety_delete_orphan"));
            string orphan = Path.Combine(dir, "mesh_lod1.smd");
            string newlyReferenced = Path.Combine(dir, "mesh_shadowlod.smd");
            File.Delete(Path.Combine(dir, "new.qc"));
            File.WriteAllText(orphan, "");
            File.WriteAllText(newlyReferenced, "");
            var scan = QcBackupManager.Scan(new[] { dir });
            ctx.AssertEqual(2, scan.OrphanLodFiles.Count, "初次扫描找到两个孤立网格");

            ParseQc(dir, "new.qc", "$modelname \"new.mdl\"\n$shadowlod\n{\n\treplacemodel \"mesh.smd\" \"mesh_shadowlod.smd\"\n}\n");
            int deleted = QcBackupManager.DeleteOrphanLodFiles(scan.OrphanLodFiles, out var errors);
            ctx.AssertEqual(1, deleted, "只删除仍未被引用的网格");
            ctx.Assert(!File.Exists(orphan) && File.Exists(newlyReferenced), "新增引用必须受到保护");
            ctx.AssertEqual(1, errors.Count, "拒绝删除已被引用的文件");
            ctx.AssertContains(errors[0], newlyReferenced, "拒绝删除信息包含受保护路径");
        }

        [SelfTest("QC安全：缺失 include 或损坏 QC 时保护整个工程目录")]
        public static void OrphanDeletionProtectsIncompleteRoot(SelfTestContext ctx)
        {
            foreach (string broken in new[] { "$include \"missing.qci\"\n", "$lod 20\n{\n" })
            {
                string dir = Path.GetFullPath(ctx.NewDir("qcsafety_protected_root"));
                string mesh = Path.Combine(dir, "mesh_lod1.smd");
                string qc = Path.Combine(dir, "broken.qc");
                File.Delete(qc);
                File.WriteAllText(mesh, "");
                var before = QcBackupManager.Scan(new[] { dir });
                ctx.AssertEqual(1, before.OrphanLodFiles.Count, "QC 出现前能确认孤立网格");
                File.WriteAllText(qc, "$modelname \"actor.mdl\"\n" + broken);

                var after = QcBackupManager.Scan(new[] { dir });
                ctx.AssertEqual(0, after.OrphanLodFiles.Count, "无法完整扫描的工程不得列出孤立文件");
                ctx.Assert(after.SkippedReasons.Any(reason => reason.Contains(qc)), "报告导致工程被保护的 QC");
                ctx.AssertEqual(0, QcBackupManager.DeleteOrphanLodFiles(before.OrphanLodFiles, out var errors),
                    "已有删除请求也须重新检查受保护工程");
                ctx.Assert(errors.Count == 1 && File.Exists(mesh), "扫描不完整时拒绝删除网格");
            }
        }
    }
}
