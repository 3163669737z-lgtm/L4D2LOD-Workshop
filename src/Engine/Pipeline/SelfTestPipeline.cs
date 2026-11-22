using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LodStudio.Engine
{
    /// <summary>
    /// 扫描 / 编译校验 / 报告 / 诊断模块自测（`--selftest --filter Pipeline` 运行）。
    /// 只操作 SelfTestContext.NewDir(...) 下的临时目录与 AppInfo.TempRoot，绝不碰用户真实工程/设置。
    ///
    /// 覆盖：
    ///   ParseBat（引号/裸路径/%~dp0/%CD%/rem/::/大小写/GBK 中文/一行多 token）、
    ///   FindRootQcs（bat 优先 + 生成物与备份排除 + 启发式 $modelname/$include 剔除 + 容错）、
    ///   FindLooseModels（深度/目录/产物排除）、IsGeneratedQc、包装 QC（不覆盖/ANSI/中文空格）、
    ///   VtxInspector（合成最小合法 vtx + 截断/字段异常容错）、Nekomdl（查找容错 + 坏 QC 不崩，
    ///   本机有 nekomdl 时真编译一次并读回真实 vtx）、ReportWriter（报告/日志结构/文件名/UTF-8 BOM）、
    ///   EnvironmentDiagnostics（关键词/不抛）、扫描器 + 计划器联动（真实 GLB/SMD 造数据）。
    /// </summary>
    internal static class SelfTestPipeline
    {
        // ───────────────────────────── 造数据/断言辅助 ─────────────────────────────

        private static Encoding Utf8() { return new UTF8Encoding(false); }

        private static Encoding Gbk()
        {
            try { return Encoding.GetEncoding(936); }
            catch { return Encoding.Default; }
        }

        private static void WriteText(string path, string text, Encoding encoding)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, (encoding ?? Utf8()).GetBytes(text ?? ""));
        }

        private static void WriteAscii(string path, string text)
        {
            WriteText(path, text, new UTF8Encoding(false));
        }

        private static string ReadTextAuto(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
                return new UTF8Encoding(false).GetString(b, 3, b.Length - 3);
            return QcTextFile.Decode(b, out _, out _);
        }

        private static bool HasUtf8Bom(byte[] b)
        {
            return b != null && b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
        }

        private static void AssertPathEq(SelfTestContext ctx, string expected, string actual, string what)
        {
            ctx.Assert(string.Equals(Path.GetFullPath(expected), Path.GetFullPath(actual), StringComparison.OrdinalIgnoreCase),
                what + "：期望=" + expected + " 实际=" + actual);
        }

        /// <summary>断言列表包含指定路径（大小写不敏感），返回其下标；找不到返回 -1。</summary>
        private static int IndexOfPath(List<string> list, string path)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], path, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>造一个 300 面的 SMD 模型（单骨骼 root，扇形网格，法线朝 +Z）。</summary>
        private static SmdModel MakeSmdModel(int triangles)
        {
            var model = new SmdModel();
            model.Bones.Add(new SmdBone { Index = 0, Name = "root", Parent = -1 });
            model.SkeletonPose.Add("0 0.000000 0.000000 0.000000 0.000000 0.000000 0.000000");

            for (int i = 0; i < triangles; i++)
            {
                double a = 2 * Math.PI * i / triangles;
                double b = 2 * Math.PI * (i + 1) / triangles;
                model.Triangles.Add(new SmdTriangle
                {
                    Material = "material",
                    V0 = MakeVertex(0, 0),
                    V1 = MakeVertex(10 * Math.Cos(a), 10 * Math.Sin(a)),
                    V2 = MakeVertex(10 * Math.Cos(b), 10 * Math.Sin(b))
                });
            }
            return model;
        }

        private static SmdVertex MakeVertex(double x, double y)
        {
            var v = new SmdVertex
            {
                X = (float)x,
                Y = (float)y,
                Z = 0,
                Nx = 0,
                Ny = 0,
                Nz = 1,
                U = (float)((x + 10) / 20.0),
                V = (float)((y + 10) / 20.0)
            };
            v.Weights.Add(new SmdWeight { BoneIndex = 0, Weight = 1f });
            return v;
        }

        private static LodPlan MakePlan(double[] distances, double[] ratios)
        {
            var plan = new LodPlan();
            for (int i = 0; i < distances.Length; i++) plan.Levels.Add(new LodLevel(distances[i], ratios[i]));
            return plan;
        }

        // ───────────────────────────── 用例 1：ParseBat ─────────────────────────────

        [SelfTest("Pipeline：ParseBat 引号/裸路径/变量/注释/GBK/多 token")]
        public static void ParseBatAllForms(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("parse_bat");
            foreach (string n in new[]
            {
                "a.qc", "b.qc", "c.qc", "e.qc", "bare1.qc", "bare3.qc", "bare4.qc",
                "rem.qc", "colon.qc", "REM.qc", "atrem.qc", "tabrem.qc"
            })
                WriteAscii(Path.Combine(dir, n), "$modelname \"models/x.mdl\"\n");
            Directory.CreateDirectory(Path.Combine(dir, "qc"));
            WriteAscii(Path.Combine(dir, "qc", "d.qc"), "$modelname \"models/d.mdl\"\n");
            WriteAscii(Path.Combine(dir, "中文.qc"), "$modelname \"models/cn.mdl\"\n");

            // bat 用 GBK（ANSI）写，含中文文件名 → 验证编码探测
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("rem \"%~dp0nekomdl.exe\" \"%~dp0rem.qc\"");
            sb.AppendLine(":: \"%~dp0nekomdl.exe\" \"%~dp0colon.qc\"");
            sb.AppendLine("REM \"%~dp0nekomdl.exe\" \"%~dp0REM.qc\"");
            sb.AppendLine("@rem \"%~dp0nekomdl.exe\" \"%~dp0atrem.qc\"");
            sb.AppendLine("\trem\t\"%~dp0nekomdl.exe\" \"%~dp0tabrem.qc\"");
            sb.AppendLine("echo 这一行没有编译命令");
            sb.AppendLine("\"%~dp0nekomdl.exe\" \"%~dp0a.qc\"");
            sb.AppendLine("nekomdl %CD%\\b.qc");
            sb.AppendLine("\"%~dp0nekomdl.exe\" \"%~dp0\\c.qc\"");
            sb.AppendLine("nekomdl \"%~dp0qc\\d.qc\"");
            sb.AppendLine("nekomdl \"%~dp0qc\\e.qc\" \"%~dp0a.qc\"");
            sb.AppendLine("nekomdl \"%~dp0缺失.qc\"");
            sb.AppendLine("NEKOMDL.EXE \"%~dp0中文.qc\"");
            sb.AppendLine("if exist x nekomdl.exe \"%~dp0bare1.qc\" bare2.qc");
            sb.AppendLine("nekomdl.exe bare3.qc bare4.qc");
            string bat = Path.Combine(dir, "编译.bat");
            WriteText(bat, sb.ToString(), Gbk());

            List<string> qcs = ProjectScanner.ParseBat(bat);
            ctx.AssertEqual(7, qcs.Count, "ParseBat 收集的 QC 数（注释/不存在/重复都要排除）");

            var expected = new List<string>
            {
                Path.Combine(dir, "a.qc"),
                Path.Combine(dir, "b.qc"),
                Path.Combine(dir, "c.qc"),
                Path.Combine(dir, "qc", "d.qc"),
                Path.Combine(dir, "中文.qc"),
                Path.Combine(dir, "bare1.qc"),
                Path.Combine(dir, "bare4.qc")
            };
            foreach (string e in expected)
                ctx.Assert(IndexOfPath(qcs, e) >= 0, "ParseBat 应包含 " + e);
            ctx.Assert(IndexOfPath(qcs, Path.Combine(dir, "rem.qc")) < 0, "rem 行不应被解析");
            ctx.Assert(IndexOfPath(qcs, Path.Combine(dir, "colon.qc")) < 0, ":: 行不应被解析");
            ctx.Assert(IndexOfPath(qcs, Path.Combine(dir, "tabrem.qc")) < 0, "Tab+rem 行不应被解析");
            ctx.Assert(IndexOfPath(qcs, Path.Combine(dir, "bare3.qc")) < 0, "一行多个裸 token 应取最后一个");

            // 路径必须已经展开（不含 %~dp0/%CD%）且是绝对路径
            foreach (string q in qcs)
            {
                ctx.Assert(Path.IsPathRooted(q), "ParseBat 返回绝对路径：" + q);
                ctx.AssertNotContains(q, "%~dp0", "ParseBat 应展开 %~dp0");
                ctx.AssertNotContains(q, "%CD%", "ParseBat 应展开 %CD%");
            }

            // 容错：不存在的 bat / 空路径不抛
            ctx.AssertEqual(0, ProjectScanner.ParseBat(Path.Combine(dir, "不存在.bat")).Count, "不存在的 bat 返回空表");
            ctx.AssertEqual(0, ProjectScanner.ParseBat(null).Count, "null 路径返回空表");
        }

        // ───────────────────────────── 用例 2：FindRootQcs（bat 优先）─────────────────────────────

        [SelfTest("Pipeline：FindRootQcs 优先 .bat 清单并排除生成物/备份")]
        public static void FindRootQcsBatPriority(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("findroot_bat");
            WriteAscii(Path.Combine(dir, "a.qc"), "$modelname \"models/a.mdl\"\n");
            WriteAscii(Path.Combine(dir, "b.qc"), "$modelname \"models/b.mdl\"\n");
            WriteAscii(Path.Combine(dir, "a_LOD.qc"), "$modelname \"models/a.mdl\"\n");
            WriteAscii(Path.Combine(dir, "a_autoLOD.qc"), "$modelname \"models/a.mdl\"\n");
            WriteAscii(Path.Combine(dir, "a_auto.qc"), "$modelname \"models/a.mdl\"\n");
            WriteAscii(Path.Combine(dir, "_LOD合集.qc"), "// 合集\n");
            WriteAscii(Path.Combine(dir, "自动LOD_b.qc"), "$modelname \"models/b.mdl\"\n");
            string backupDir = Path.Combine(dir, "_LOD备份", "deep");
            WriteAscii(Path.Combine(backupDir, "bak.qc"), "$modelname \"models/bak.mdl\"\n");
            WriteAscii(Path.Combine(backupDir, "bak.bat"), "nekomdl \"%~dp0bak.qc\"\n");

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("\"%~dp0nekomdl.exe\" \"%~dp0a.qc\"");
            sb.AppendLine("\"%~dp0nekomdl.exe\" \"%~dp0a.qc\"");
            sb.AppendLine("\"%~dp0nekomdl.exe\" \"%~dp0b.qc\"");
            sb.AppendLine("\"%~dp0nekomdl.exe\" \"%~dp0a_LOD.qc\"");
            sb.AppendLine("\"%~dp0nekomdl.exe\" \"%~dp0自动LOD_b.qc\"");
            sb.AppendLine("\"%~dp0nekomdl.exe\" \"%~dp0_LOD备份\\deep\\bak.qc\"");
            WriteAscii(Path.Combine(dir, "build.bat"), sb.ToString());

            var logs = new List<string>();
            List<string> roots = ProjectScanner.FindRootQcs(dir, s => { lock (logs) logs.Add(s); });

            ctx.AssertEqual(2, roots.Count, "bat 清单里的根 QC 数（去重 + 排除生成物/备份）");
            AssertPathEq(ctx, Path.Combine(dir, "a.qc"), roots[0], "bat 清单顺序：a.qc");
            AssertPathEq(ctx, Path.Combine(dir, "b.qc"), roots[1], "bat 清单顺序：b.qc");

            string joined = string.Join("\n", logs.ToArray());
            ctx.AssertContains(joined, "从 build.bat 读到", "日志应写明从哪个 bat 读到 QC");
            ctx.AssertContains(joined, "忽略本工具生成的", "日志应说明忽略了工具生成的 QC");

            // 没日志回调也不能崩（界面/CLI 会传 null）
            ctx.AssertEqual(2, ProjectScanner.FindRootQcs(dir, null).Count, "log 为 null 时也能扫描");
            // 目录不存在 → 空表 + 不抛
            ctx.AssertEqual(0, ProjectScanner.FindRootQcs(Path.Combine(dir, "不存在"), null).Count, "工程目录不存在返回空表");
        }

        // ───────────────────────────── 用例 3：FindRootQcs（启发式）─────────────────────────────

        [SelfTest("Pipeline：FindRootQcs 无 bat 时按 $modelname/$include 启发式扫描")]
        public static void FindRootQcsHeuristic(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("findroot_heuristic");
            WriteAscii(Path.Combine(dir, "lonely.qc"),
                "$modelname \"models/lonely.mdl\"\n$bodygroup \"b\"\n{\n\tstudio \"lonely.glb\"\n}\n");
            WriteAscii(Path.Combine(dir, "root.qc"),
                "$modelname \"models/root.mdl\"\n// 根：自己的 $modelname 在 include 之前\n$include \"child.qc\"\n$surfaceprop \"flesh\"\n");
            WriteAscii(Path.Combine(dir, "child.qc"),
                "$modelname \"models/child.mdl\"\n$bodygroup \"b\"\n{\n\tstudio \"child.glb\"\n}\n");
            WriteAscii(Path.Combine(dir, "commented.qc"),
                "// $modelname \"models/fake.mdl\" 这行只是注释\n/* $modelname \"models/fake2.mdl\" */\n");
            WriteAscii(Path.Combine(dir, "_LOD备份", "deep", "bak.qc"), "$modelname \"models/bak.mdl\"\n");
            WriteAscii(Path.Combine(dir, "broken.qc"), "$modelname \"models/broken.mdl\"\n");

            // 容错：锁住 broken.qc，读取必然失败 → 只记日志、不影响其它 QC
            List<string> roots;
            List<string> again;
            using (var fs = new FileStream(Path.Combine(dir, "broken.qc"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var logs = new List<string>();
                roots = ProjectScanner.FindRootQcs(dir, s => { lock (logs) logs.Add(s); });
                string joined = string.Join("\n", logs.ToArray());
                ctx.AssertContains(joined, "改为扫描含 $modelname 的 QC", "日志应说明退化为启发式扫描");
                ctx.AssertContains(joined, "读取 QC 失败", "读取失败的 QC 要记日志而不是抛异常");

                // 稳定：连续两次结果一致（同一状态下）
                again = ProjectScanner.FindRootQcs(dir, null);
            }

            ctx.AssertEqual(2, roots.Count, "启发式根 QC 数（child 被 include 剔除、备份/注释不算）");
            AssertPathEq(ctx, Path.Combine(dir, "lonely.qc"), roots[0], "按路径升序：lonely.qc");
            AssertPathEq(ctx, Path.Combine(dir, "root.qc"), roots[1], "按路径升序：root.qc");

            ctx.AssertEqual(roots.Count, again.Count, "两次扫描结果数量一致");
            for (int i = 0; i < roots.Count; i++) AssertPathEq(ctx, roots[i], again[i], "两次扫描结果顺序一致 #" + i);
        }

        // ───────────────────────────── 用例 4：FindLooseModels ─────────────────────────────

        [SelfTest("Pipeline：FindLooseModels 深度限制/跳过 _LOD/排除 _lodN 产物")]
        public static void FindLooseModelsRules(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("loose_models");
            WriteAscii(Path.Combine(dir, "a.glb"), "fake");
            WriteAscii(Path.Combine(dir, "a_lod1.glb"), "fake");
            WriteAscii(Path.Combine(dir, "b_lod12.smd"), "fake");
            WriteAscii(Path.Combine(dir, "keep.smd"), "fake");
            WriteAscii(Path.Combine(dir, "sub", "c.glb"), "fake");
            WriteAscii(Path.Combine(dir, "sub", "deep", "d.smd"), "fake");
            WriteAscii(Path.Combine(dir, "sub", "deep", "deeper", "e.glb"), "fake");
            WriteAscii(Path.Combine(dir, "sub", "deep", "deeper", "level4", "f.glb"), "fake");
            WriteAscii(Path.Combine(dir, "sub", "deep", "deeper", "level4", "level5", "g.glb"), "fake");
            WriteAscii(Path.Combine(dir, "_LOD备份", "h.glb"), "fake");
            WriteAscii(Path.Combine(dir, "_LODtmp", "i.glb"), "fake");

            List<string> found = ProjectScanner.FindLooseModels(dir, 3, null);
            var expected = new[]
            {
                Path.Combine(dir, "a.glb"),
                Path.Combine(dir, "keep.smd"),
                Path.Combine(dir, "sub", "c.glb"),
                Path.Combine(dir, "sub", "deep", "d.smd"),
                Path.Combine(dir, "sub", "deep", "deeper", "e.glb")
            };
            ctx.AssertEqual(expected.Length, found.Count, "深度 3 内应找到的模型数");
            foreach (string e in expected) ctx.Assert(IndexOfPath(found, e) >= 0, "应找到 " + e);
            ctx.Assert(IndexOfPath(found, Path.Combine(dir, "a_lod1.glb")) < 0, "应排除 _lodN 产物");
            ctx.Assert(IndexOfPath(found, Path.Combine(dir, "_LODtmp", "i.glb")) < 0, "应跳过 _LOD* 目录");
            ctx.Assert(IndexOfPath(found, Path.Combine(dir, "sub", "deep", "deeper", "level4", "f.glb")) < 0, "应受深度限制");

            // 结果必须排序且为绝对路径
            var sorted = new List<string>(found);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sorted.Count; i++) ctx.AssertEqual(sorted[i], found[i], "结果按路径排序 #" + i);
            foreach (string f in found) ctx.Assert(Path.IsPathRooted(f), "结果是绝对路径：" + f);

            // 空目录 / 不存在目录 → 空表不抛
            ctx.AssertEqual(0, ProjectScanner.FindLooseModels(ctx.NewDir("loose_empty"), 3, null).Count, "空目录返回空表");
            ctx.AssertEqual(0, ProjectScanner.FindLooseModels(Path.Combine(dir, "不存在"), 3, null).Count, "不存在目录返回空表");
        }

        // ───────────────────────────── 用例 5：IsGeneratedQc ─────────────────────────────

        [SelfTest("Pipeline：IsGeneratedQc 各种工具产物后缀")]
        public static void GeneratedQcDetection(SelfTestContext ctx)
        {
            var yes = new[] { "x_LOD.qc", "x_autoLOD.qc", "x_auto.qc", "_LOD合集.qc", "_LOD合集_2.qc", "自动LOD_x.qc", @"C:\p\a_LOD.QC" };
            var no = new[] { "x.qc", "my_LODX.qc", "LOD合集.qc", "x_LOD.qc.bak", "autoLOD_x.qc", "a_auto.qcx", "" };
            foreach (string y in yes) ctx.Assert(ProjectScanner.IsGeneratedQc(y), "应识别为工具产物：" + y);
            foreach (string n in no) ctx.Assert(!ProjectScanner.IsGeneratedQc(n), "不应识别为工具产物：" + n);
            ctx.Assert(!ProjectScanner.IsGeneratedQc(null), "null 不是工具产物");
        }

        // ───────────────────────────── 用例 6：包装 QC ─────────────────────────────

        [SelfTest("Pipeline：包装 QC 命名/不覆盖/ANSI 编码/中文空格")]
        public static void WrapperQcRules(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("wrapper_qc");
            string model = Path.Combine(dir, "模型 一.glb");
            WriteAscii(model, "not a real glb");

            string qc = ProjectScanner.WrapperQcPath(model);
            ctx.Assert(Path.IsPathRooted(qc), "包装 QC 路径是绝对路径");
            ctx.AssertEqual(Path.Combine(dir, "自动LOD_模型 一.qc"), qc, "包装 QC 命名规则");

            string created = ProjectScanner.EnsureWrapperQc(model, null);
            AssertPathEq(ctx, qc, created, "EnsureWrapperQc 返回路径");
            ctx.Assert(File.Exists(qc), "包装 QC 已创建");

            byte[] bytes = File.ReadAllBytes(qc);
            ctx.Assert(!HasUtf8Bom(bytes), "包装 QC 不应带 UTF-8 BOM");
            ctx.Assert(bytes.Length > 0, "包装 QC 非空");
            string text = ReadTextAuto(qc);
            ctx.AssertContains(text, "$modelname \"props/模型 一.mdl\"", "模板 $modelname（文件名空格保留）");
            ctx.AssertContains(text, "$bodygroup \"模型 一\"", "模板 $bodygroup");
            ctx.AssertContains(text, "studio \"模型 一.glb\"", "模板 studio");
            ctx.AssertContains(text, "$sequence \"idle\"", "模板 $sequence");
            ctx.AssertContains(text, "$cdmaterials", "模板 $cdmaterials");
            ctx.AssertContains(text, "$surfaceprop", "模板 $surfaceprop");

            // 必须用系统 ANSI 写（studiomdl 按 ANSI 读）；不能用 manifest 的 UTF-8 ACP
            byte[] ansi = SystemAnsi.Encoding.GetBytes(text);
            bool same = ansi.Length == bytes.Length;
            if (same)
                for (int i = 0; i < ansi.Length; i++) if (ansi[i] != bytes[i]) { same = false; break; }
            ctx.Assert(same, "包装 QC 必须按系统 ANSI 编码写入（当前 ANSI 代码页 " + SystemAnsi.CodePage + "）");

            // 已存在时绝不覆盖
            WriteAscii(qc, "// 用户自己改过的内容\r\n$modelname \"custom/custom.mdl\"\r\n");
            byte[] before = File.ReadAllBytes(qc);
            var logs = new List<KeyValuePair<LogLevel, string>>();
            string again = ProjectScanner.EnsureWrapperQc(model, (s, lvl) => { lock (logs) logs.Add(new KeyValuePair<LogLevel, string>(lvl, s)); });
            AssertPathEq(ctx, qc, again, "第二次调用返回同一路径");
            byte[] after = File.ReadAllBytes(qc);
            ctx.AssertEqual(before.Length, after.Length, "已存在的包装 QC 不能被覆盖（长度）");
            for (int i = 0; i < before.Length; i++) ctx.AssertEqual((int)before[i], (int)after[i], "已存在的包装 QC 不能被覆盖（内容）");
            bool logged = false;
            foreach (var kv in logs) if (kv.Value != null && kv.Value.IndexOf("沿用已有", StringComparison.Ordinal) >= 0) logged = true;
            ctx.Assert(logged, "沿用已有包装 QC 时要打日志");

            // smd 模型也能建；非模型扩展名/不存在文件 → 可读 IOException
            string smd = Path.Combine(dir, "b.smd");
            WriteAscii(smd, "version 1\n");
            ctx.Assert(File.Exists(ProjectScanner.EnsureWrapperQc(smd, null)), "给 .smd 也能建包装 QC");

            bool threw = false;
            try { ProjectScanner.EnsureWrapperQc(Path.Combine(dir, "不存在.glb"), null); }
            catch (IOException ex) { threw = ex.Message.IndexOf("不存在", StringComparison.Ordinal) >= 0; }
            ctx.Assert(threw, "模型不存在时应抛可读 IOException");

            threw = false;
            try { ProjectScanner.EnsureWrapperQc(Path.Combine(dir, "a.txt"), null); }
            catch (IOException) { threw = true; }
            ctx.Assert(threw, "非 glb/smd 应抛 IOException");
        }

        // ───────────────────────────── 用例 7：VtxInspector ─────────────────────────────

        /// <summary>
        /// 按旧 VtxReader 的布局拼一个最小合法 vtx：
        /// header 36 字节（numLods@20 / numBodyParts@28 / bodyPartOffset@32）→ bodyPart → model → lod → mesh → stripGroup。
        /// </summary>
        private static byte[] BuildVtx(int numLods, float[] switches, int[] triangles, int[] vertices)
        {
            int lodBase = 52;
            int meshStart = lodBase + 12 * numLods;
            int size = meshStart + numLods * 34;   // 每级 mesh(9) + stripGroup(25)
            var b = new byte[size];

            PutInt(b, 20, numLods);
            PutInt(b, 28, 1);          // numBodyParts
            PutInt(b, 32, 36);         // bodyPartOffset
            PutInt(b, 36, 1);          // bodyPart.numModels
            PutInt(b, 40, 8);          // bodyPart.modelOffset（相对 bodyPart 起点）
            PutInt(b, 44, numLods);    // model.numLods
            PutInt(b, 48, 8);          // model.lodOffset（相对 model 起点 → 52）

            int meshPos = meshStart;
            for (int li = 0; li < numLods; li++)
            {
                int lb = lodBase + li * 12;
                PutInt(b, lb, 1);                    // lod.numMeshes
                PutInt(b, lb + 4, meshPos - lb);     // lod.meshOffset（相对 lod 起点）
                PutFloat(b, lb + 8, switches[li]);

                PutInt(b, meshPos, 1);               // mesh.numStripGroups
                PutInt(b, meshPos + 4, 9);           // mesh.stripGroupOffset（相对 mesh 起点）

                PutInt(b, meshPos + 9, vertices[li]);           // stripGroup.numVertices
                PutInt(b, meshPos + 9 + 8, triangles[li] * 3);  // stripGroup.numIndices

                meshPos += 34;
            }
            return b;
        }

        private static void PutInt(byte[] b, int offset, int value)
        {
            byte[] one = BitConverter.GetBytes(value);
            Buffer.BlockCopy(one, 0, b, offset, 4);
        }

        private static void PutFloat(byte[] b, int offset, float value)
        {
            byte[] one = BitConverter.GetBytes(value);
            Buffer.BlockCopy(one, 0, b, offset, 4);
        }

        [SelfTest("Pipeline：VtxInspector 解析级数/面数/切换点并容错坏文件")]
        public static void VtxParsing(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("vtx");
            byte[] good = BuildVtx(3, new[] { 10f, 15f, 25f }, new[] { 10, 20, 30 }, new[] { 10, 20, 30 });
            string goodPath = Path.Combine(dir, "good.dx90.vtx");
            File.WriteAllBytes(goodPath, good);

            VtxInfo info = VtxInspector.Read(goodPath);
            ctx.Assert(info.Valid, "合法 vtx 应解析成功：" + (info.Error ?? ""));
            ctx.AssertEqual(3, info.NumLods, "LOD 级数");
            ctx.AssertEqual(3, info.Triangles.Count, "每级三角面数条目");
            ctx.AssertEqual(10, info.Triangles[0], "LOD0 三角面数");
            ctx.AssertEqual(20, info.Triangles[1], "LOD1 三角面数");
            ctx.AssertEqual(30, info.Triangles[2], "LOD2 三角面数");
            ctx.AssertEqual(3, info.Vertices.Count, "每级顶点数条目");
            ctx.AssertEqual(10, info.Vertices[0], "LOD0 顶点数");
            ctx.AssertEqual(10.0, info.SwitchPoints[0], 1e-6, "LOD0 切换点");
            ctx.AssertEqual(15.0, info.SwitchPoints[1], 1e-6, "LOD1 切换点");
            ctx.AssertEqual(25.0, info.SwitchPoints[2], 1e-6, "LOD2 切换点");
            ctx.AssertContains(VtxInspector.Describe(info), "LOD3", "Describe 摘要包含级数");

            // 截断 → 不抛、Valid=false、有中文错误
            string trunc = Path.Combine(dir, "trunc.dx90.vtx");
            File.WriteAllBytes(trunc, new byte[] { 7, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 });
            VtxInfo t = VtxInspector.Read(trunc);
            ctx.Assert(!t.Valid, "截断文件应判定无效");
            ctx.AssertContains(t.Error, "过小", "截断文件的错误说明");

            // 头部字段异常
            byte[] badCount = (byte[])good.Clone();
            PutInt(badCount, 20, 100);
            string badPath = Path.Combine(dir, "bad_count.dx90.vtx");
            File.WriteAllBytes(badPath, badCount);
            VtxInfo bc = VtxInspector.Read(badPath);
            ctx.Assert(!bc.Valid, "级数异常应判定无效");
            ctx.AssertContains(bc.Error, "异常", "级数异常的错误说明");

            // 偏移越界
            byte[] badOffset = (byte[])good.Clone();
            PutInt(badOffset, 32, 99999);
            string badOffPath = Path.Combine(dir, "bad_offset.dx90.vtx");
            File.WriteAllBytes(badOffPath, badOffset);
            VtxInfo bo = VtxInspector.Read(badOffPath);
            ctx.Assert(!bo.Valid, "bodyPart 偏移越界应判定无效");
            ctx.AssertContains(bo.Error, "越界", "偏移越界的错误说明");

            // 层内偏移越界（bodyPart → model 偏移错）
            byte[] badInner = (byte[])good.Clone();
            PutInt(badInner, 40, 40000);
            string badInnerPath = Path.Combine(dir, "bad_inner.dx90.vtx");
            File.WriteAllBytes(badInnerPath, badInner);
            ctx.Assert(!VtxInspector.Read(badInnerPath).Valid, "层内偏移越界应判定无效");

            // 文件不存在 / null → Valid=false 不抛
            ctx.Assert(!VtxInspector.Read(Path.Combine(dir, "nope.vtx")).Valid, "不存在的 vtx 返回无效");
            ctx.Assert(!VtxInspector.Read(null).Valid, "null 路径返回无效");
        }

        // ───────────────────────────── 用例 8：Nekomdl 查找 + 坏 QC 容错 ─────────────────────────────

        [SelfTest("Pipeline：Nekomdl 查找容错 + 坏 QC 编译失败不崩")]
        public static void NekomdlFindAndBadQc(SelfTestContext ctx)
        {
            string exe = null;
            try { exe = Nekomdl.Find(null); }
            catch (Exception ex) { ctx.Assert(false, "Nekomdl.Find(null) 不应抛异常：" + ex.Message); }
            ctx.Assert(exe == null || File.Exists(exe), "Find 返回的路径必须真实存在");
            if (exe != null) ctx.Log("检测到 nekomdl：" + exe);
            else ctx.Log("SKIP: 本机没有 nekomdl.exe（试编译相关用例跳过，不算失败）");

            // hint 非法也不抛
            ctx.Assert(Nekomdl.Find("Z:\\绝对不存在\\nekomdl.exe") == null || File.Exists(Nekomdl.Find("Z:\\绝对不存在\\nekomdl.exe")),
                "非法 hint 不抛异常");

            // 编译器不存在 → 可读错误，不抛
            var r1 = Nekomdl.Compile(null, "");
            ctx.Assert(!r1.Success, "没有编译器时 Success=false");
            ctx.Assert(r1.Errors.Count > 0 && r1.Errors[0].IndexOf("nekomdl", StringComparison.OrdinalIgnoreCase) >= 0,
                "没有编译器时给出可读错误");

            string dir = ctx.NewDir("nekomdl_bad");
            string qcMissing = Path.Combine(dir, "missing.qc");
            var r2 = Nekomdl.Compile(Path.Combine(dir, "nekomdl.exe"), qcMissing);
            ctx.Assert(!r2.Success, "编译器/QC 不存在时 Success=false");
            ctx.Assert(r2.Errors.Count > 0, "编译器/QC 不存在时给出错误说明");

            if (exe == null) return;

            // 坏 QC（studio 指向不存在的文件）→ 编译失败、不崩、无异常
            string qcPath = Path.Combine(dir, "bad.qc");
            WriteAscii(qcPath,
                "$modelname \"models/lodstudio_selftest_bad.mdl\"\n" +
                "$bodygroup \"x\"\n{\n\tstudio \"definitely_missing_file.glb\"\n}\n");
            CompileResult bad = null;
            try { bad = Nekomdl.Compile(exe, qcPath); }
            catch (Exception ex) { ctx.Assert(false, "坏 QC 编译不应抛异常：" + ex.Message); }
            ctx.Assert(!bad.Success, "坏 QC 编译应失败（Success=false）");
            ctx.Assert(bad.Errors.Count > 0, "坏 QC 编译应收集到 ERROR 行");
            ctx.Log("坏 QC 编译退出信息：" + (bad.Errors.Count > 0 ? bad.Errors[0] : "（无）"));
        }

        // ───────────────────────────── 用例 9：真实试编译 + 产物定位 + 真实 vtx ─────────────────────────────

        [SelfTest("Pipeline：真实试编译定位 mdl/vtx 并读回级数（无 nekomdl 时 SKIP）")]
        public static void NekomdlSuccessfulCompile(SelfTestContext ctx)
        {
            string exe = null;
            try { exe = Nekomdl.Find(null); } catch { }
            if (exe == null)
            {
                ctx.Log("SKIP: 本机没有 nekomdl.exe，跳过真实试编译用例");
                return;
            }

            string dir = ctx.NewDir("nekomdl_good");
            SmdModel smd = MakeSmdModel(300);
            SmdCodec.Write(Path.Combine(dir, "model.smd"), smd);
            string qcPath = Path.Combine(dir, "good.qc");
            WriteAscii(qcPath,
                "$modelname \"models/lodstudio_selftest.mdl\"\n" +
                "$surfaceprop \"default\"\n" +
                "$bodygroup \"mesh\"\n{\n\tstudio \"model.smd\"\n}\n" +
                "$sequence \"idle\" {\n\t\"model.smd\"\n\tfps 30\n\tactivity \"act_idle\" 1\n}\n");

            CompileResult cr = Nekomdl.Compile(exe, qcPath);
            ctx.Assert(cr.Success, "好 QC 应编译成功（exit 0）；错误：" +
                                   (cr.Errors.Count > 0 ? cr.Errors[0] : "（无）"));
            ctx.Assert(!string.IsNullOrEmpty(cr.MdlPath) && File.Exists(cr.MdlPath), "应定位到本次新生成的 .mdl");
            ctx.Assert(!string.IsNullOrEmpty(cr.VtxPath) && File.Exists(cr.VtxPath), "应定位到本次新生成的 .dx90.vtx");
            ctx.Assert(cr.Vtx != null && cr.Vtx.Valid, "应能解析真实 vtx：" +
                                                      (cr.Vtx == null ? "Vtx 为空" : (cr.Vtx.Error ?? "")));
            ctx.Assert(cr.Vtx.NumLods >= 1, "真实 vtx 至少 1 级 LOD");
            ctx.Assert(cr.Vtx.Triangles.Count >= 1, "真实 vtx 应有每级面数");
            ctx.Assert(cr.Vtx.Triangles[0] > 0, "真实 vtx LOD0 面数应大于 0");
            ctx.Log("真实编译产物：mdl=" + Path.GetFileName(cr.MdlPath) + " vtx=" + Path.GetFileName(cr.VtxPath));
            ctx.Log("真实 vtx：" + VtxInspector.Describe(cr.Vtx));

            // 编译产物必须落在临时目录里（nekomdl 的 gamedir = 工作目录 = QC 目录），不写用户游戏目录
            string full = Path.GetFullPath(cr.VtxPath);
            ctx.Assert(full.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase),
                "vtx 应生成在测试临时目录内：" + full);
        }

        // ───────────────────────────── 用例 10：ReportWriter ─────────────────────────────

        [SelfTest("Pipeline：ReportWriter 生成 Markdown 报告与 UTF-8 BOM 日志")]
        public static void ReportWriting(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("report");
            string qcOk = Path.Combine(dir, "子目录", "ok.qc");
            string qcFail = Path.Combine(dir, "fail.qc");
            WriteAscii(qcOk, "$modelname \"models/ok.mdl\"\n");
            WriteAscii(qcFail, "$modelname \"models/fail.mdl\"\n");

            var outcome = new BatchOutcome
            {
                Ok = false,
                Stats = new BatchStats
                {
                    RootQcCount = 2,
                    UniqueModelCount = 3,
                    JobCount = 8,
                    JobOk = 7,
                    JobFail = 1,
                    InjectedQcCount = 1,
                    SkippedQcCount = 1,
                    ErrorQcCount = 1,
                    CompileOk = 0,
                    CompileFail = 1,
                    TotalSourceTriangles = 12345,
                    TotalLastLodTriangles = 678
                }
            };
            outcome.QcOutcomes.Add(new QcOutcome
            {
                QcPath = qcOk,
                Injected = true,
                CompileTried = true,
                CompileOk = true,
                CompileSummary = "LOD2  面数 300 → 90"
            });
            outcome.QcOutcomes.Add(new QcOutcome
            {
                QcPath = qcFail,
                Skipped = true,
                SkipReason = "已有手写 $lod"
            });
            outcome.QcOutcomes.Add(new QcOutcome
            {
                QcPath = Path.Combine(dir, "err.qc"),
                CompileTried = true,
                CompileOk = false
            });
            outcome.QcOutcomes[2].Errors.Add("编译失败：ERROR: example");
            outcome.Errors.Add("全局错误样例");
            outcome.Warnings.Add("提示样例");

            var plan = MakePlan(new[] { 20.0, 50.0 }, new[] { 0.6, 0.3 });
            var rctx = new ReportContext
            {
                RootDir = dir,
                PresetName = "角色·标准（5级）",
                Plan = plan,
                Mode = OutputMode.InPlace,
                BackupEnabled = true,
                BackupDescription = "同目录的 *.qc.lodbak",
                StartedAt = DateTime.Now - TimeSpan.FromSeconds(125),
                Elapsed = TimeSpan.FromSeconds(125),
                Outcome = outcome,
                CompileAfter = true,
                LogLines = new List<string> { "═══ 阶段 1 ═══", "细节行 1", "细节行 2" }
            };

            string reportPath = ReportWriter.WriteMarkdownReport(rctx, Path.Combine(dir, "报告目录"));
            ctx.Assert(!string.IsNullOrEmpty(reportPath), "报告应写入成功");
            ctx.Assert(File.Exists(reportPath), "报告文件应存在：" + reportPath);
            string name = Path.GetFileName(reportPath);
            ctx.Assert(Regex.IsMatch(name, "^_LOD运行报告_\\d{4}_\\d{6}\\.md$"), "报告文件名格式：" + name);
            ctx.AssertContains(Path.GetFileName(Path.GetDirectoryName(reportPath)), "报告目录", "报告应写进指定目录");

            string text = File.ReadAllText(reportPath, Utf8());
            ctx.AssertContains(text, "# 远景工坊", "报告标题");
            ctx.AssertContains(text, "写入方式", "报告包含写入方式");
            ctx.AssertContains(text, "跳过", "报告包含跳过原因段");
            ctx.AssertContains(text, "## 错误", "报告包含错误段");
            ctx.AssertContains(text, "还原", "报告包含还原方法");
            ctx.AssertContains(text, "根 QC", "报告包含统计：根 QC");
            ctx.AssertContains(text, "唯一模型", "报告包含统计：唯一模型");
            ctx.AssertContains(text, "试编译", "报告包含统计：试编译");
            ctx.AssertContains(text, "12345", "报告包含原始面数");
            ctx.AssertContains(text, "678", "报告包含末级面数");
            ctx.AssertContains(text, "已有手写 $lod", "报告包含跳过原因原文");
            ctx.AssertContains(text, "全局错误样例", "报告包含全局错误");
            ctx.AssertContains(text, "提示样例", "报告包含提示");
            ctx.AssertContains(text, "LOD2  面数 300 → 90", "报告包含试编译摘要");
            ctx.AssertContains(text, "v" + AppInfo.Version, "报告结尾含工具版本");

            string logPath = ReportWriter.WriteLogFile(rctx, Path.Combine(dir, "报告目录"), "运行日志");
            ctx.Assert(!string.IsNullOrEmpty(logPath), "日志应写入成功");
            ctx.Assert(File.Exists(logPath), "日志文件应存在");
            ctx.Assert(Regex.IsMatch(Path.GetFileName(logPath), "^_LOD运行日志_\\d{8}_\\d{6}\\.txt$"),
                "日志文件名格式：" + Path.GetFileName(logPath));
            byte[] logBytes = File.ReadAllBytes(logPath);
            ctx.Assert(HasUtf8Bom(logBytes), "日志必须是 UTF-8 带 BOM");
            string logText = new UTF8Encoding(false).GetString(logBytes, 3, logBytes.Length - 3);
            ctx.AssertContains(logText, "细节行 1", "日志包含 ctx.LogLines");
            ctx.AssertContains(logText, "结果:", "日志末尾补结果统计");
            ctx.AssertContains(logText, "错误 /", "日志末尾含错误/提示计数");

            // ReportFileName 固定格式
            ctx.AssertEqual("_LOD运行报告_1115_183000.md",
                ReportWriter.ReportFileName(new DateTime(2026, 11, 15, 18, 30, 0)), "ReportFileName 固定格式");

            // outcome 为 null / 字段缺失也不能崩
            var empty = new ReportContext { RootDir = dir, Outcome = null, Plan = null, LogLines = null };
            string emptyPath = ReportWriter.WriteMarkdownReport(empty, Path.Combine(dir, "空报告"));
            ctx.Assert(!string.IsNullOrEmpty(emptyPath) && File.Exists(emptyPath), "outcome 为 null 也能写报告");
            string emptyText = File.ReadAllText(emptyPath, Utf8());
            ctx.AssertContains(emptyText, "写入方式", "空报告也含基本信息");

            // 写失败（报告目录其实是一个文件）→ 返回 null 不抛
            string fileAsDir = Path.Combine(dir, "不是目录");
            WriteAscii(fileAsDir, "x");
            string failed = null;
            try { failed = ReportWriter.WriteMarkdownReport(empty, fileAsDir); }
            catch (Exception ex) { ctx.Assert(false, "报告写失败不应抛异常：" + ex.Message); }
            ctx.Assert(failed == null || File.Exists(failed), "写失败时返回 null 或回退到可写目录");
        }

        // ───────────────────────────── 用例 11：EnvironmentDiagnostics ─────────────────────────────

        [SelfTest("Pipeline：EnvironmentDiagnostics 输出关键词且不抛")]
        public static void DiagnosticsText(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("diagnostics");
            var settings = new AppSettings
            {
                BlenderPath = @"C:\Program Files\Blender Foundation\Blender 4.1\blender.exe",
                NekomdlPath = @"E:\SteamLibrary\steamapps\common\Left 4 Dead 2\bin\nekomdl.exe",
                PresetName = "角色·标准（5级）"
            };
            string text = null;
            try
            {
                text = EnvironmentDiagnostics.Build(settings, new List<string> { dir, Path.Combine(dir, "不存在") },
                                                    "Blender 4.1.1", false, false);
            }
            catch (Exception ex) { ctx.Assert(false, "EnvironmentDiagnostics.Build 不应抛异常：" + ex.Message); }

            ctx.Assert(!string.IsNullOrEmpty(text), "诊断文本非空");
            ctx.AssertContains(text, "远景工坊", "诊断含工具名");
            ctx.AssertContains(text, "管理员", "诊断含管理员状态");
            ctx.AssertContains(text, "低完整性", "诊断含低完整性状态");
            ctx.AssertContains(text, "Blender", "诊断含 Blender");
            ctx.AssertContains(text, "Blender 4.1.1", "诊断含 Blender 版本");
            ctx.AssertContains(text, "编译器", "诊断含编译器");
            ctx.AssertContains(text, "设置文件", "诊断含设置文件路径");
            ctx.AssertContains(text, "临时目录", "诊断含临时目录");
            ctx.AssertContains(text, "磁盘", "诊断含磁盘剩余空间");
            ctx.AssertContains(text, "长路径", "诊断含长路径提示");
            ctx.AssertContains(text, "把这段发给开发者", "诊断结尾提示");
            ctx.AssertContains(text, "目录写入测试", "诊断含目录可写测试");
            ctx.AssertContains(text, dir, "诊断列出探测目录");

            // null 参数 / 异常参数也不能崩
            string nullText = null;
            try { nullText = EnvironmentDiagnostics.Build(null, null, null, true, true); }
            catch (Exception ex) { ctx.Assert(false, "Build(null,...) 不应抛异常：" + ex.Message); }
            ctx.AssertContains(nullText, "管理员", "null 设置也能输出管理员状态");
            ctx.AssertContains(nullText, "低完整性", "null 设置也能输出低完整性状态");
        }

        // ───────────────────────────── 用例 12：扫描器 + 计划器联动 ─────────────────────────────

        [SelfTest("Pipeline：FindRootQcs 与 BatchPlanner 联动（真实 GLB/SMD 造数据）")]
        public static void ScannerPlannerIntegration(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("planner_integration");
            string models = Path.Combine(dir, "models");
            string qcDir = Path.Combine(dir, "qc");
            Directory.CreateDirectory(models);
            Directory.CreateDirectory(qcDir);

            // 真实 GLB（由 SMD 生成，300 面）与真实 SMD
            SmdModel smd = MakeSmdModel(300);
            File.WriteAllBytes(Path.Combine(models, "a.glb"), GlbCodec.WriteFromSmd(smd));
            SmdCodec.Write(Path.Combine(models, "b.smd"), smd);

            WriteAscii(Path.Combine(qcDir, "root.qc"),
                "$modelname \"models/root.mdl\"\n$cd \"..\\models\"\n" +
                "$bodygroup \"mesh\"\n{\n\tstudio \"a.glb\"\n}\n");
            WriteAscii(Path.Combine(qcDir, "extra.qc"),
                "$modelname \"models/extra.mdl\"\n$cd \"..\\models\"\n" +
                "$bodygroup \"mesh\"\n{\n\tstudio \"a.glb\"\n}\n" +
                "$bodygroup \"body\"\n{\n\tstudio \"b.smd\"\n}\n");
            WriteAscii(Path.Combine(dir, "批量编译.bat"),
                "@echo off\r\n\"%~dp0nekomdl.exe\" \"%~dp0qc\\root.qc\"\r\n\"%~dp0nekomdl.exe\" \"%~dp0qc\\extra.qc\"\r\n");

            var logs = new List<string>();
            List<string> roots = ProjectScanner.FindRootQcs(dir, s => { lock (logs) logs.Add(s); });
            ctx.AssertEqual(2, roots.Count, "扫描根 QC 数");
            AssertPathEq(ctx, Path.Combine(qcDir, "root.qc"), roots[0], "根 QC 顺序 #0");
            AssertPathEq(ctx, Path.Combine(qcDir, "extra.qc"), roots[1], "根 QC 顺序 #1");
            bool sawBatLog = false;
            foreach (string l in logs) if (l != null && l.IndexOf("从 批量编译.bat 读到", StringComparison.Ordinal) >= 0) sawBatLog = true;
            ctx.Assert(sawBatLog, "扫描日志应写明从 bat 读到 QC");

            var options = new BatchRunOptions
            {
                Plan = MakePlan(new[] { 20.0, 50.0 }, new[] { 0.6, 0.25 }),
                PresetName = "测试方案",
                MinTriangles = 200,
                OutputMode = OutputMode.InPlace,
                ProjectRoot = dir
            };
            BatchPlan plan = null;
            try
            {
                plan = BatchPlanner.Build(new List<TaskRef> { new TaskRef(TaskKind.Folder, dir) }, dir, options,
                                          NullObserver.Instance, CancellationToken.None);
            }
            catch (Exception ex) { ctx.Assert(false, "BatchPlanner 不应抛异常：" + ex.Message); }

            ctx.Assert(plan != null, "计划非空");
            ctx.AssertEqual(2, plan.QcPlans.Count, "计划里的 QC 数");
            ctx.AssertEqual(2, plan.ProcessCount, "会处理的 QC 数");
            ctx.AssertEqual(2, plan.Models.Count, "唯一模型数（a.glb 被两个 QC 引用只算一次）");
            ctx.AssertEqual(4, plan.JobCount, "减面任务数 = 2 模型 × 2 级");
            ctx.Assert(plan.HasWork, "计划有可执行工作");
            ctx.AssertEqual(0, plan.ErrorCount, "计划无错误");

            int glb = 0, smdCount = 0;
            foreach (PlannedModel m in plan.Models)
            {
                ctx.Assert(m.Triangles >= 200, "模型面数应 ≥ 门槛 200（实际 " + m.Triangles + "）");
                ctx.AssertEqual(2, m.Jobs.Count, "每个模型 2 级任务");
                if (m.IsSmd) smdCount++; else glb++;
            }
            ctx.AssertEqual(1, glb, "1 个 GLB 模型");
            ctx.AssertEqual(1, smdCount, "1 个 SMD 模型");
        }
    }
}
