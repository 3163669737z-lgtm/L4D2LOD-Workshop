using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace LodStudio.Engine
{
    /// <summary>
    /// 无 Blender 后端（<see cref="GltfpackRunner"/>）自测：
    ///  1) 形状优先参数纯逻辑（ShapeFirstRatio / ShapeErrorForRatio 与 python 7.0.3 一致）；
    ///  2) 端到端：程序化 icosphere → SmdToGlb → RunAll 减面（比例排序 / Dst 去重复用 / 产物可读）；
    ///  3) SMD UV：无 Blender 链里 -kv 保留未被材质引用的 UV（回归）；
    ///  4) Body 内缩：双 mesh 手工 GLB 经 runner，Body 顶点位移≈内缩量、非 Body 不动；
    ///  5) 取消：预取消全部标「已取消」，运行中取消（Workers=1 确定性）杀进程不产文件；
    ///  6) 失败继续：坏任务只失败自己，不拖垮整批。
    ///
    /// 全部用例不依赖本机 Blender（gltfpack 为内嵌资源），产物只写 ctx.NewDir 临时目录。
    /// </summary>
    internal static class SelfTestGltfpack
    {
        // ═══════════════════════════════ 1) 纯逻辑 ═══════════════════════════════

        [SelfTest("gltfpack 后端：形状优先参数（ShapeFirstRatio/ShapeErrorForRatio 与 python 7.0.3 一致，纯逻辑）")]
        public static void ShapeLogic(SelfTestContext ctx)
        {
            // >= REF(4000) 面：权重=1，按原目标比例减
            ctx.AssertEqual(0.65, GltfpackRunner.ShapeFirstRatio(20000, 0.65), 1e-12, "20000 面权重钳到 1");
            ctx.AssertEqual(0.65, GltfpackRunner.ShapeFirstRatio(5000, 0.65), 1e-12, "5000 面权重钳到 1");
            ctx.AssertEqual(0.65, GltfpackRunner.ShapeFirstRatio(4000, 0.65), 1e-12, "恰好 REF 面权重=1");
            // 小模型：权重 = sqrt(before/REF)，面数越少减得越轻（实测值来自 python shape_first_ratio）
            ctx.AssertEqual(0.708, GltfpackRunner.ShapeFirstRatio(2784, 0.65), 1e-3, "2784 面 → ≈0.708");
            ctx.AssertEqual(0.825, GltfpackRunner.ShapeFirstRatio(1000, 0.65), 1e-12, "1000 面 → 0.825");
            ctx.AssertEqual(0.876, GltfpackRunner.ShapeFirstRatio(500, 0.65), 1e-3, "500 面 → ≈0.876");
            ctx.AssertEqual(0.922, GltfpackRunner.ShapeFirstRatio(200, 0.65), 1e-3, "200 面 → ≈0.922");
            // 最小权重 0.15：极小模型也不超过 1-0.9*0.15=0.865 的有效比
            ctx.AssertEqual(0.865, GltfpackRunner.ShapeFirstRatio(1, 0.1), 1e-12, "1 面 → 最小权重 0.15 → 0.865");
            // 边界：r>=0.995 / before<=0 原样返回
            ctx.AssertEqual(0.997, GltfpackRunner.ShapeFirstRatio(100, 0.997), 1e-12, "r>=0.995 原样返回");
            ctx.AssertEqual(1.0, GltfpackRunner.ShapeFirstRatio(1000, 1.0), 1e-12, "r=1.0 原样返回");
            ctx.AssertEqual(0.5, GltfpackRunner.ShapeFirstRatio(0, 0.5), 1e-12, "before<=0 原样返回");
            // 有效比只增不减且不超过 1
            ctx.Assert(GltfpackRunner.ShapeFirstRatio(300, 0.2) >= 0.2, "有效比不应低于请求比例");
            ctx.Assert(GltfpackRunner.ShapeFirstRatio(300, 0.9) <= 1.0, "有效比不应超过 1");

            // LOD 误差上限分档：r<=0.35 → 0.02；r<=0.50 → 0.015；否则 0.01
            ctx.AssertEqual(GltfpackRunner.ShapeErrorCoarse, GltfpackRunner.ShapeErrorForRatio(0.34), "0.34 → coarse");
            ctx.AssertEqual(GltfpackRunner.ShapeErrorCoarse, GltfpackRunner.ShapeErrorForRatio(0.35), "0.35 → coarse（边界含）");
            ctx.AssertEqual(GltfpackRunner.ShapeErrorMid, GltfpackRunner.ShapeErrorForRatio(0.5), "0.50 → mid（边界含）");
            ctx.AssertEqual(GltfpackRunner.ShapeErrorFine, GltfpackRunner.ShapeErrorForRatio(0.51), "0.51 → fine");
            ctx.AssertEqual(GltfpackRunner.ShapeErrorFine, GltfpackRunner.ShapeErrorForRatio(1.0), "1.0 → fine");

            // 空批次：不碰 gltfpack，直接返回空
            var empty = GltfpackRunner.RunAll(new List<BlenderJobSpec>(), null, CancellationToken.None, null);
            ctx.AssertEqual(0, empty.Count, "空批次应返回空结果列表");
            ctx.Log("形状优先表与 python 7.0.3 一致（REF=4000，MIN_W=0.15，se 分档 0.02/0.015/0.01）");
        }

        // ═══════════════════════════════ 2) 端到端 ═══════════════════════════════

        [SelfTest("gltfpack 后端：端到端减面（程序化球体 → RunAll，比例排序 / Dst 去重复用 / 产物可读）")]
        public static void EndToEnd(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("gp_e2e");
            var sphere = BuildSphere(3, 1.0, 0, 0, 0);   // 1280 三角面 / 642 顶点
            string src = Path.Combine(dir, "src.glb");
            File.WriteAllBytes(src, SmdGlbCodec.SmdToGlb(ToSmd(sphere, "models/test/gp_sphere")));
            int before = GlbCodec.CountTriangles(src);
            ctx.AssertEqual(1280, before, "3 级 icosphere 应为 1280 三角面");
            long weight = new FileInfo(src).Length;

            var specs = new List<BlenderJobSpec>
            {
                new BlenderJobSpec { Source = src, Destination = Path.Combine(dir, "lod1.glb"), Ratio = 0.5, Weight = weight },
                new BlenderJobSpec { Source = src, Destination = Path.Combine(dir, "lod2.glb"), Ratio = 0.2, Weight = weight },
                // 输出与任务 0 相同：应被 Dst 去重，直接复用结果（不重复执行）
                new BlenderJobSpec { Source = src, Destination = Path.Combine(dir, "lod1.glb"), Ratio = 0.9, Weight = weight },
            };

            var sync = new object();
            var logs = new List<string>();
            var done = new List<int>();
            var progress = new List<KeyValuePair<int, int>>();
            var options = new GltfpackRunner.RunOptions
            {
                Workers = 2,
                TaskTimeoutMinutes = 1,
                Log = delegate (string s) { lock (sync) logs.Add(s); },
                OnJobCompleted = delegate (int i, BlenderJobResult r) { lock (sync) done.Add(i); }
            };
            var results = GltfpackRunner.RunAll(specs, options, CancellationToken.None,
                delegate (int d, int t) { lock (sync) progress.Add(new KeyValuePair<int, int>(d, t)); });

            ctx.AssertEqual(specs.Count, results.Count, "结果数量应与任务数一致");
            // follower（重复输出）复用 owner 结果，target/after 区间断言只对 owner 做
            List<int>[] followerMap;
            var ownerIdx = BlenderScheduler.DedupOwners(specs, out followerMap);
            var isOwner = new bool[specs.Count];
            foreach (int o in ownerIdx) isOwner[o] = true;
            for (int i = 0; i < results.Count; i++)
            {
                string label = "任务" + i + "(" + Path.GetFileName(specs[i].Destination) + ")";
                ctx.Assert(results[i] != null, label + " 结果不应为 null");
                ctx.Assert(results[i].Ok, label + " 应成功：" + (results[i].Error ?? "(无错误)"));
                ctx.AssertEqual(before, results[i].BeforeTriangles, label + " before 应等于源面数");
                ctx.Assert(results[i].AfterTriangles > 0, label + " after 应 > 0");
                ctx.Assert(File.Exists(specs[i].Destination), label + " 产物应存在");
                // 产物可读且三角面数与报告一致
                int exported = GlbCodec.CountTriangles(specs[i].Destination);
                ctx.AssertEqual(results[i].AfterTriangles, exported, label + " 产物面数应与报告一致");
                var model = GlbCodec.Read(specs[i].Destination);
                ctx.Assert(model != null && model.TriangleCount == exported, label + " 产物应可被 GlbCodec.Read 解析");
                if (!isOwner[i]) continue;
                int expectedTarget = (int)Math.Round(before * GltfpackRunner.ShapeFirstRatio(before, specs[i].Ratio));
                ctx.AssertEqual(expectedTarget, results[i].TargetTriangles, label + " target 应=round(before×形状比)");
                // 形状优先：产物应落在目标附近（球体极易减，容差放宽到 ±50% 防 gltfpack 版本差异）
                ctx.Assert(results[i].AfterTriangles >= expectedTarget / 2,
                           label + " after " + results[i].AfterTriangles + " 不应低于目标一半 " + expectedTarget);
                ctx.Assert(results[i].AfterTriangles <= expectedTarget * 3 / 2 + 10,
                           label + " after " + results[i].AfterTriangles + " 不应超出目标 1.5 倍 " + expectedTarget);
                ctx.Log(label + "  ratio=" + specs[i].Ratio.ToString("0.##", CultureInfo.InvariantCulture) +
                        "  " + before + " → " + results[i].AfterTriangles + "（目标 " + expectedTarget + "）" +
                        (string.IsNullOrEmpty(results[i].Warning) ? "" : "  警告:" + results[i].Warning));
            }
            // 0.5 的产物应比 0.2 面数多，且都少于源
            ctx.Assert(results[0].AfterTriangles > results[1].AfterTriangles,
                       "ratio 0.5 的产物应比 0.2 面数多（" + results[0].AfterTriangles + " vs " + results[1].AfterTriangles + "）");
            ctx.Assert(results[0].AfterTriangles < before, "ratio 0.5 应减面（" + results[0].AfterTriangles + " < " + before + "）");

            // Dst 去重：follower 复用 owner 结果（逐字段一致）
            ctx.Assert(results[2].Ok, "follower 应成功（复用结果）");
            ctx.AssertEqual(results[0].BeforeTriangles, results[2].BeforeTriangles, "follower before 应复用 owner");
            ctx.AssertEqual(results[0].AfterTriangles, results[2].AfterTriangles, "follower after 应复用 owner");
            ctx.AssertEqual(results[0].TargetTriangles, results[2].TargetTriangles, "follower target 应复用 owner");

            // 回调 / 进度：每个任务恰好一次，done 覆盖 1..N
            ctx.AssertEqual(specs.Count, done.Count, "OnJobCompleted 次数应等于任务数");
            var seen = new bool[specs.Count];
            foreach (int i in done)
            {
                ctx.Assert(i >= 0 && i < specs.Count, "回调索引越界：" + i);
                ctx.Assert(!seen[i], "同一任务不应回调两次：" + i);
                seen[i] = true;
            }
            ctx.AssertEqual(specs.Count, progress.Count, "onProgress 次数应等于任务数");
            var doneVals = new List<int>();
            foreach (var kv in progress)
            {
                ctx.AssertEqual(specs.Count, kv.Value, "onProgress 的 total 应为任务数");
                doneVals.Add(kv.Key);
            }
            doneVals.Sort();
            for (int i = 0; i < doneVals.Count; i++)
                ctx.AssertEqual(i + 1, doneVals[i], "onProgress 的 done 应覆盖 1..任务数 且不重复");

            bool sawBackend = false;
            foreach (var line in logs)
                if (line != null && line.IndexOf("gltfpack（无 Blender 后端）", StringComparison.Ordinal) >= 0) { sawBackend = true; break; }
            ctx.Assert(sawBackend, "日志应说明使用了 gltfpack 无 Blender 后端");
            ctx.AssertEqual(0, BlenderScheduler.LiveProcessCount, "结束后调度器进程登记表应为空");
        }

        // ═══════════════════════════════ 3) SMD UV 保留（-kv 回归） ═══════════════════════════════

        [SelfTest("gltfpack 后端：SMD 经无 Blender 链保留 UV（-kv 回归）")]
        public static void SmdUvSurvives(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("gp_smd_uv");
            var original = new SmdModel();
            original.Triangles.Add(new SmdTriangle
            {
                Material = "models/test/uv",
                V0 = UvVertex(0, 0, 0, 0.1, 0.2),
                V1 = UvVertex(1, 0, 0, 0.8, 0.2),
                V2 = UvVertex(0, 1, 0, 0.1, 0.9)
            });
            original.Triangles.Add(new SmdTriangle
            {
                Material = "models/test/uv",
                V0 = UvVertex(1, 0, 0, 0.2, 0.1),
                V1 = UvVertex(1, 1, 0, 0.9, 0.8),
                V2 = UvVertex(0, 1, 0, 0.2, 0.9)
            });

            string src = Path.Combine(dir, "uv_src.glb");
            string dst = Path.Combine(dir, "uv_lod.glb");
            File.WriteAllBytes(src, SmdGlbCodec.SmdToGlb(original));
            var specs = new List<BlenderJobSpec>
            {
                new BlenderJobSpec
                {
                    Source = src,
                    Destination = dst,
                    Ratio = 1.0,
                    ShrinkInner = 0,
                    Weight = new FileInfo(src).Length
                }
            };
            var options = new GltfpackRunner.RunOptions { Workers = 1, TaskTimeoutMinutes = 1, Log = delegate { } };
            var results = GltfpackRunner.RunAll(specs, options, CancellationToken.None, null);
            ctx.Assert(results.Count == 1 && results[0].Ok,
                       "UV 用例减面应成功：" + (results.Count == 1 ? results[0].Error : "结果数量=" + results.Count));

            var back = SmdGlbCodec.GlbToSmd(GlbCodec.Read(dst), original);
            int total = 0, nonZero = 0;
            foreach (var tri in back.Triangles)
            {
                CheckUv(tri.V0, ref total, ref nonZero);
                CheckUv(tri.V1, ref total, ref nonZero);
                CheckUv(tri.V2, ref total, ref nonZero);
            }
            ctx.Log("SMD→GLB→gltfpack（无 Blender）→SMD：顶点=" + total + "，非零 UV=" + nonZero);
            ctx.AssertEqual(6, total, "两个三角形应有 6 个顶点");
            ctx.AssertEqual(total, nonZero, "gltfpack 必须保留未被材质引用的 UV（-kv），否则 SMD 会全部写成 0,0");
        }

        private static SmdVertex UvVertex(double x, double y, double z, double u, double v)
        {
            return new SmdVertex
            {
                X = (float)x, Y = (float)y, Z = (float)z,
                Nx = 0, Ny = 0, Nz = 1, U = (float)u, V = (float)v
            };
        }

        private static void CheckUv(SmdVertex vertex, ref int total, ref int nonZero)
        {
            total++;
            if (Math.Abs(vertex.U) > 1e-6 || Math.Abs(vertex.V) > 1e-6) nonZero++;
        }

        // ═══════════════════════════════ 4) Body 内缩经 runner ═══════════════════════════════

        [SelfTest("gltfpack 后端：Body 内缩经 runner 生效（Body 位移≈内缩量、非 Body 逐点不动）")]
        public static void BodyShrinkThroughRunner(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("gp_body_shrink");
            var main = BuildSphere(2, 1.0, 0, 0, 0);        // 320 三角面 / 162 顶点
            var body = BuildSphere(2, 0.5, 0, 0, 2.0);      // 内层部件（节点名含 Body）
            byte[] srcBytes = BuildTwoMeshGlb(main, body);
            string src = Path.Combine(dir, "two_mesh.glb");
            File.WriteAllBytes(src, srcBytes);
            ctx.AssertEqual(640, GlbCodec.CountTriangles(src), "双 mesh GLB 应有 640 三角面");

            const double shrinkRatio = 0.01;
            // 参考：直接在源上应用 C# 内缩（GlbInnerShrink 自身的正确性由模型模块自测覆盖）
            var reference = GlbInnerShrink.Apply(srcBytes, shrinkRatio, "two_mesh.glb");
            ctx.Assert(reference.Changed, "含 Body 的 GLB 应被内缩修改");
            ctx.Assert(reference.BodyMeshes >= 1, "应至少识别到一个 Body mesh");
            double expected = reference.ShrinkDistance;
            ctx.Assert(expected > 1e-6, "内缩距离应为正，实际=" + expected);
            string refPath = Path.Combine(dir, "reference.glb");
            File.WriteAllBytes(refPath, reference.Data);

            string dst = Path.Combine(dir, "two_mesh_lod.glb");
            var specs = new List<BlenderJobSpec>
            {
                new BlenderJobSpec
                {
                    Source = src,
                    Destination = dst,
                    Ratio = 1.0,                 // 不减面，只走 gltfpack + 内缩
                    ShrinkInner = shrinkRatio,
                    Weight = new FileInfo(src).Length
                }
            };
            var options = new GltfpackRunner.RunOptions { Workers = 1, TaskTimeoutMinutes = 1, Log = delegate { } };
            var results = GltfpackRunner.RunAll(specs, options, CancellationToken.None, null);
            ctx.Assert(results.Count == 1 && results[0].Ok,
                       "内缩任务应成功：" + (results.Count == 1 ? results[0].Error : "结果数量=" + results.Count));
            ctx.Assert(File.Exists(dst), "内缩产物应存在");

            var srcModel = GlbCodec.Read(src);
            var outModel = GlbCodec.Read(dst);
            var refModel = GlbCodec.Read(refPath);
            var srcMain = PositionsOf(srcModel, false);
            var srcBody = PositionsOf(srcModel, true);
            var outMain = PositionsOf(outModel, false);
            var outBody = PositionsOf(outModel, true);
            var refBody = PositionsOf(refModel, true);

            ctx.AssertEqual(srcMain.Count, outMain.Count, "非 Body 顶点数不应变化（ratio=1.0 不减面）");
            ctx.AssertEqual(srcBody.Count, outBody.Count, "Body 顶点数不应变化");
            ctx.AssertEqual(srcBody.Count, refBody.Count, "参考内缩只改数值，顶点数不应变化");

            // -noq 下 gltfpack 逐值保留 float 位置（只可能重排/焊接相同点；球体顶点全异）
            double tolPos = Math.Max(1e-4, expected * 0.02);
            double mainAB = CloudMaxNearest(outMain, srcMain);
            double mainBA = CloudMaxNearest(srcMain, outMain);
            ctx.Log("非 Body 点云差：out→src=" + mainAB.ToString("F8", CultureInfo.InvariantCulture) +
                    "  src→out=" + mainBA.ToString("F8", CultureInfo.InvariantCulture));
            ctx.Assert(mainAB <= tolPos && mainBA <= tolPos, "非 Body 顶点不应被内缩/扰动");

            double bodyAB = CloudMaxNearest(outBody, refBody);
            double bodyBA = CloudMaxNearest(refBody, outBody);
            ctx.Log("Body 点云差（runner vs 直接内缩）：out→ref=" + bodyAB.ToString("F8", CultureInfo.InvariantCulture) +
                    "  ref→out=" + bodyBA.ToString("F8", CultureInfo.InvariantCulture));
            ctx.Assert(bodyAB <= tolPos, "runner 内缩结果应与直接 GlbInnerShrink 一致（out→ref 最远 " + bodyAB + "）");
            ctx.Assert(bodyBA <= tolPos, "runner 内缩结果应与直接 GlbInnerShrink 一致（ref→out 最远 " + bodyBA + "）");

            // 每个输出 Body 顶点离源 Body 点云的最近距离都应≈内缩量（防「没缩/缩两遍/方向反」回归）
            double minD = double.MaxValue, maxD = 0;
            for (int i = 0; i + 2 < outBody.Count; i += 3)
            {
                double best = double.MaxValue;
                for (int j = 0; j + 2 < srcBody.Count; j += 3)
                {
                    double dx = outBody[i] - srcBody[j], dy = outBody[i + 1] - srcBody[j + 1], dz = outBody[i + 2] - srcBody[j + 2];
                    double d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 < best) best = d2;
                }
                double d = Math.Sqrt(best);
                if (d < minD) minD = d;
                if (d > maxD) maxD = d;
            }
            ctx.Log("内缩量=" + expected.ToString("F6", CultureInfo.InvariantCulture) +
                    "，输出 Body 顶点位移范围 [" + minD.ToString("F6", CultureInfo.InvariantCulture) +
                    ", " + maxD.ToString("F6", CultureInfo.InvariantCulture) + "]");
            ctx.Assert(minD >= expected * 0.75, "存在几乎未内缩的 Body 顶点（最小位移 " + minD + " < 0.75×" + expected + "）");
            ctx.Assert(maxD <= expected * 1.25, "存在内缩过度的 Body 顶点（最大位移 " + maxD + " > 1.25×" + expected + "）");
        }

        // ═══════════════════════════════ 5) 取消 ═══════════════════════════════

        [SelfTest("gltfpack 后端：取消（预取消全标已取消；运行中取消杀进程、不留产物/孤儿进程）")]
        public static void Cancel(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("gp_cancel");
            var sphere = BuildSphere(3, 1.0, 0, 0, 0);
            string src = Path.Combine(dir, "src.glb");
            File.WriteAllBytes(src, SmdGlbCodec.SmdToGlb(ToSmd(sphere, "models/test/gp_cancel")));
            long weight = new FileInfo(src).Length;

            // ── A. 预取消：所有任务标「已取消」，不产生任何文件 ──
            var preSpecs = new List<BlenderJobSpec>();
            for (int i = 0; i < 3; i++)
                preSpecs.Add(new BlenderJobSpec { Source = src, Destination = Path.Combine(dir, "pre" + i + ".glb"), Ratio = 0.5, Weight = weight });
            var ctsPre = new CancellationTokenSource();
            ctsPre.Cancel();
            int preProgress = 0;
            var preResults = GltfpackRunner.RunAll(preSpecs,
                new GltfpackRunner.RunOptions { Workers = 2, TaskTimeoutMinutes = 1, Log = delegate { } },
                ctsPre.Token, delegate (int d, int t) { Interlocked.Increment(ref preProgress); });
            ctx.AssertEqual(preSpecs.Count, preResults.Count, "预取消结果数量应完整");
            ctx.AssertEqual(preSpecs.Count, preProgress, "预取消 onProgress 仍应覆盖全部任务");
            for (int i = 0; i < preResults.Count; i++)
            {
                ctx.Assert(!preResults[i].Ok, "预取消任务不应成功（任务 " + i + "）");
                ctx.AssertContains(preResults[i].Error, "已取消", "预取消任务错误应为「已取消」（任务 " + i + "）");
                ctx.Assert(!File.Exists(preSpecs[i].Destination), "预取消不应产出文件：" + preSpecs[i].Destination);
            }

            // ── B. 运行中取消（Workers=1：第一个任务回调里取消，后续必然全部「已取消」）──
            var specs = new List<BlenderJobSpec>();
            for (int i = 0; i < 6; i++)
                specs.Add(new BlenderJobSpec { Source = src, Destination = Path.Combine(dir, "mid" + i + ".glb"), Ratio = 0.5, Weight = weight });
            var cts = new CancellationTokenSource();
            int progressCalls = 0;
            var options = new GltfpackRunner.RunOptions
            {
                Workers = 1,
                TaskTimeoutMinutes = 1,
                Log = delegate { },
                OnJobCompleted = delegate (int i, BlenderJobResult r) { if (i == 0) cts.Cancel(); }
            };
            var results = GltfpackRunner.RunAll(specs, options, cts.Token,
                delegate (int d, int t) { Interlocked.Increment(ref progressCalls); });

            ctx.AssertEqual(specs.Count, results.Count, "运行中取消结果数量应完整");
            ctx.AssertEqual(specs.Count, progressCalls, "运行中取消 onProgress 仍应覆盖全部任务");
            ctx.Assert(results[0].Ok, "第一个任务应在取消前完成：" + (results[0].Error ?? "(无错误)"));
            ctx.Assert(File.Exists(specs[0].Destination), "第一个任务产物应存在");
            for (int i = 1; i < results.Count; i++)
            {
                ctx.Assert(!results[i].Ok, "取消后任务不应成功（任务 " + i + "）");
                ctx.AssertContains(results[i].Error, "已取消", "取消后任务错误应为「已取消」（任务 " + i + "）");
                ctx.Assert(!File.Exists(specs[i].Destination), "被取消任务不应产出文件：" + specs[i].Destination);
            }
            ctx.AssertEqual(0, BlenderScheduler.LiveProcessCount, "取消后调度器进程登记表应为空");
            ctx.Log("取消用例：预取消 3/3 标已取消；运行中取消 1 成功 + 5 已取消");
        }

        // ═══════════════════════════════ 6) 失败继续 ═══════════════════════════════

        [SelfTest("gltfpack 后端：失败继续（源缺失只失败自己，好任务不受影响）")]
        public static void FailureContinue(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("gp_failure");
            // 用 1280 面球体：更小网格在 -se 0.015 误差上限下任何边折叠都超限，gltfpack 会保持原面数
            var sphere = BuildSphere(3, 1.0, 0, 0, 0);
            string src = Path.Combine(dir, "src.glb");
            File.WriteAllBytes(src, SmdGlbCodec.SmdToGlb(ToSmd(sphere, "models/test/gp_fail")));
            long weight = new FileInfo(src).Length;

            var specs = new List<BlenderJobSpec>
            {
                new BlenderJobSpec { Source = src, Destination = Path.Combine(dir, "ok1.glb"), Ratio = 0.5, Weight = weight },
                new BlenderJobSpec { Source = Path.Combine(dir, "不存在_src.glb"), Destination = Path.Combine(dir, "missing.glb"), Ratio = 0.5, Weight = 0 },
                new BlenderJobSpec { Source = src, Destination = Path.Combine(dir, "ok2.glb"), Ratio = 0.3, Weight = weight },
            };
            int progressCalls = 0;
            var results = GltfpackRunner.RunAll(specs,
                new GltfpackRunner.RunOptions { Workers = 2, TaskTimeoutMinutes = 1, Log = delegate { } },
                CancellationToken.None, delegate (int d, int t) { Interlocked.Increment(ref progressCalls); });

            ctx.AssertEqual(specs.Count, results.Count, "失败继续结果数量应完整");
            ctx.AssertEqual(specs.Count, progressCalls, "失败继续 onProgress 应覆盖全部任务");
            ctx.Assert(results[0].Ok, "好任务 0 应成功：" + (results[0].Error ?? "(无错误)"));
            ctx.Assert(results[2].Ok, "好任务 2 应成功：" + (results[2].Error ?? "(无错误)"));
            ctx.Assert(File.Exists(specs[0].Destination) && File.Exists(specs[2].Destination), "好任务产物应存在");
            ctx.Assert(results[0].AfterTriangles < results[0].BeforeTriangles, "好任务 0 应减面");
            ctx.Assert(!results[1].Ok, "源缺失任务应失败");
            ctx.AssertContains(results[1].Error, "源文件不存在", "源缺失任务错误应可读");
            ctx.Assert(!File.Exists(specs[1].Destination), "失败任务不应产出文件");
            ctx.Log("失败继续：好任务 2/2 成功，坏任务错误=" + results[1].Error);
        }

        // ═══════════════════════════════ 辅助：几何生成 ═══════════════════════════════

        private sealed class Sphere
        {
            internal float[] Pos;   // xyz
            internal float[] Nrm;   // xyz（单位）
            internal float[] Uv;    // uv
            internal int[] Idx;     // 三角形索引
        }

        /// <summary>程序化 icosphere：12 顶点 20 面起步，每级细分 ×4 面（中点归一到球面）。</summary>
        private static Sphere BuildSphere(int subdiv, double radius, double cx, double cy, double cz)
        {
            double t = (1.0 + Math.Sqrt(5.0)) / 2.0;
            var verts = new List<double[]>
            {
                new[] { -1, t, 0 }, new[] { 1, t, 0 }, new[] { -1, -t, 0 }, new[] { 1, -t, 0 },
                new[] { 0, -1, t }, new[] { 0, 1, t }, new[] { 0, -1, -t }, new[] { 0, 1, -t },
                new[] { t, 0, -1 }, new[] { t, 0, 1 }, new[] { -t, 0, -1 }, new[] { -t, 0, 1 }
            };
            for (int i = 0; i < verts.Count; i++) NormalizeInPlace(verts[i]);
            var faces = new List<int[]>
            {
                new[] { 0, 11, 5 }, new[] { 0, 5, 1 }, new[] { 0, 1, 7 }, new[] { 0, 7, 10 }, new[] { 0, 10, 11 },
                new[] { 1, 5, 9 }, new[] { 5, 11, 4 }, new[] { 11, 10, 2 }, new[] { 10, 7, 6 }, new[] { 7, 1, 8 },
                new[] { 3, 9, 4 }, new[] { 3, 4, 2 }, new[] { 3, 2, 6 }, new[] { 3, 6, 8 }, new[] { 3, 8, 9 },
                new[] { 4, 9, 5 }, new[] { 2, 4, 11 }, new[] { 6, 2, 10 }, new[] { 8, 6, 7 }, new[] { 9, 8, 1 }
            };
            for (int s = 0; s < subdiv; s++)
            {
                var cache = new Dictionary<long, int>();
                var next = new List<int[]>(faces.Count * 4);
                foreach (var f in faces)
                {
                    int a = Midpoint(verts, cache, f[0], f[1]);
                    int b = Midpoint(verts, cache, f[1], f[2]);
                    int c = Midpoint(verts, cache, f[2], f[0]);
                    next.Add(new[] { f[0], a, c });
                    next.Add(new[] { f[1], b, a });
                    next.Add(new[] { f[2], c, b });
                    next.Add(new[] { a, b, c });
                }
                faces = next;
            }

            int n = verts.Count;
            var s2 = new Sphere { Pos = new float[n * 3], Nrm = new float[n * 3], Uv = new float[n * 2], Idx = new int[faces.Count * 3] };
            for (int i = 0; i < n; i++)
            {
                double ux = verts[i][0], uy = verts[i][1], uz = verts[i][2];
                s2.Pos[i * 3] = (float)(ux * radius + cx);
                s2.Pos[i * 3 + 1] = (float)(uy * radius + cy);
                s2.Pos[i * 3 + 2] = (float)(uz * radius + cz);
                s2.Nrm[i * 3] = (float)ux;
                s2.Nrm[i * 3 + 1] = (float)uy;
                s2.Nrm[i * 3 + 2] = (float)uz;
                s2.Uv[i * 2] = (float)(0.5 + Math.Atan2(uz, ux) / (2.0 * Math.PI));
                s2.Uv[i * 2 + 1] = (float)(0.5 - Math.Asin(Math.Max(-1.0, Math.Min(1.0, uy))) / Math.PI);
            }
            for (int i = 0; i < faces.Count; i++)
            {
                s2.Idx[i * 3] = faces[i][0];
                s2.Idx[i * 3 + 1] = faces[i][1];
                s2.Idx[i * 3 + 2] = faces[i][2];
            }
            return s2;
        }

        private static int Midpoint(List<double[]> verts, Dictionary<long, int> cache, int i0, int i1)
        {
            long key = i0 < i1 ? ((long)i0 << 32) | (uint)i1 : ((long)i1 << 32) | (uint)i0;
            int mid;
            if (cache.TryGetValue(key, out mid)) return mid;
            var v = new[] { (verts[i0][0] + verts[i1][0]) * 0.5, (verts[i0][1] + verts[i1][1]) * 0.5, (verts[i0][2] + verts[i1][2]) * 0.5 };
            NormalizeInPlace(v);
            mid = verts.Count;
            verts.Add(v);
            cache[key] = mid;
            return mid;
        }

        private static void NormalizeInPlace(double[] v)
        {
            double len = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            v[0] /= len; v[1] /= len; v[2] /= len;
        }

        /// <summary>球体 → 三角 soup SMD（单材质、无骨骼权重；法线=径向，UV=球面展开）。</summary>
        private static SmdModel ToSmd(Sphere s, string material)
        {
            var model = new SmdModel();
            for (int i = 0; i + 2 < s.Idx.Length; i += 3)
            {
                model.Triangles.Add(new SmdTriangle
                {
                    Material = material,
                    V0 = ToSmdVertex(s, s.Idx[i]),
                    V1 = ToSmdVertex(s, s.Idx[i + 1]),
                    V2 = ToSmdVertex(s, s.Idx[i + 2])
                });
            }
            return model;
        }

        private static SmdVertex ToSmdVertex(Sphere s, int i)
        {
            return new SmdVertex
            {
                X = s.Pos[i * 3], Y = s.Pos[i * 3 + 1], Z = s.Pos[i * 3 + 2],
                Nx = s.Nrm[i * 3], Ny = s.Nrm[i * 3 + 1], Nz = s.Nrm[i * 3 + 2],
                U = s.Uv[i * 2], V = s.Uv[i * 2 + 1]
            };
        }

        // ═══════════════════════════════ 辅助：双 mesh 手工 GLB ═══════════════════════════════

        /// <summary>
        /// 拼一个最小 glTF 2.0：两个 mesh、两个节点（"Mesh_main" 与 "Body_inner"）、
        /// 每 primitive 仅 POSITION/NORMAL/indices，单 BIN buffer，无材质/骨骼。
        /// </summary>
        private static byte[] BuildTwoMeshGlb(Sphere main, Sphere body)
        {
            var bin = new MemoryStream();
            int mainPosOff = 0; WriteFloats(bin, main.Pos);
            int mainNrmOff = (int)bin.Position; WriteFloats(bin, main.Nrm);
            int mainIdxOff = (int)bin.Position; WriteU16(bin, main.Idx);
            Pad4(bin);
            int bodyPosOff = (int)bin.Position; WriteFloats(bin, body.Pos);
            int bodyNrmOff = (int)bin.Position; WriteFloats(bin, body.Nrm);
            int bodyIdxOff = (int)bin.Position; WriteU16(bin, body.Idx);
            Pad4(bin);
            int total = (int)bin.Position;

            string json =
                "{\"asset\":{\"version\":\"2.0\"},\"scene\":0," +
                "\"scenes\":[{\"nodes\":[0,1]}]," +
                "\"nodes\":[{\"name\":\"Mesh_main\",\"mesh\":0},{\"name\":\"Body_inner\",\"mesh\":1}]," +
                "\"meshes\":[" +
                "{\"name\":\"Mesh_main\",\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1},\"indices\":2}]}," +
                "{\"name\":\"Body_inner\",\"primitives\":[{\"attributes\":{\"POSITION\":3,\"NORMAL\":4},\"indices\":5}]}]," +
                "\"accessors\":[" +
                AccessorVec3(0, main.Pos.Length / 3, main.Pos, true) + "," +
                AccessorVec3(1, main.Nrm.Length / 3, main.Nrm, false) + "," +
                "{\"bufferView\":2,\"componentType\":5123,\"count\":" + main.Idx.Length + ",\"type\":\"SCALAR\"}," +
                AccessorVec3(3, body.Pos.Length / 3, body.Pos, true) + "," +
                AccessorVec3(4, body.Nrm.Length / 3, body.Nrm, false) + "," +
                "{\"bufferView\":5,\"componentType\":5123,\"count\":" + body.Idx.Length + ",\"type\":\"SCALAR\"}]," +
                "\"bufferViews\":[" +
                "{\"buffer\":0,\"byteOffset\":" + mainPosOff + ",\"byteLength\":" + main.Pos.Length * 4 + "}," +
                "{\"buffer\":0,\"byteOffset\":" + mainNrmOff + ",\"byteLength\":" + main.Nrm.Length * 4 + "}," +
                "{\"buffer\":0,\"byteOffset\":" + mainIdxOff + ",\"byteLength\":" + main.Idx.Length * 2 + "}," +
                "{\"buffer\":0,\"byteOffset\":" + bodyPosOff + ",\"byteLength\":" + body.Pos.Length * 4 + "}," +
                "{\"buffer\":0,\"byteOffset\":" + bodyNrmOff + ",\"byteLength\":" + body.Nrm.Length * 4 + "}," +
                "{\"buffer\":0,\"byteOffset\":" + bodyIdxOff + ",\"byteLength\":" + body.Idx.Length * 2 + "}]," +
                "\"buffers\":[{\"byteLength\":" + total + "}]}";
            return GlbJson.PackGlb(Encoding.UTF8.GetBytes(json), bin.ToArray());
        }

        private static string AccessorVec3(int bufferView, int count, float[] values, bool withMinMax)
        {
            string s = "{\"bufferView\":" + bufferView + ",\"componentType\":5126,\"count\":" + count + ",\"type\":\"VEC3\"";
            if (withMinMax)
            {
                float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                for (int i = 0; i + 2 < values.Length; i += 3)
                {
                    if (values[i] < minX) minX = values[i]; if (values[i] > maxX) maxX = values[i];
                    if (values[i + 1] < minY) minY = values[i + 1]; if (values[i + 1] > maxY) maxY = values[i + 1];
                    if (values[i + 2] < minZ) minZ = values[i + 2]; if (values[i + 2] > maxZ) maxZ = values[i + 2];
                }
                s += ",\"min\":[" + F(minX) + "," + F(minY) + "," + F(minZ) + "]" +
                     ",\"max\":[" + F(maxX) + "," + F(maxY) + "," + F(maxZ) + "]";
            }
            return s + "}";
        }

        private static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        private static void WriteFloats(MemoryStream bin, float[] values)
        {
            foreach (float v in values) bin.Write(BitConverter.GetBytes(v), 0, 4);
        }

        private static void WriteU16(MemoryStream bin, int[] values)
        {
            foreach (int v in values)
            {
                bin.WriteByte((byte)v);
                bin.WriteByte((byte)(v >> 8));
            }
        }

        private static void Pad4(MemoryStream bin)
        {
            while (bin.Position % 4 != 0) bin.WriteByte(0);
        }

        // ═══════════════════════════════ 辅助：点云 ═══════════════════════════════

        /// <summary>按节点名（含 body 与否）收集模型中 mesh 的 POSITION 展平数组。</summary>
        private static List<float> PositionsOf(GlbModel model, bool body)
        {
            var list = new List<float>();
            foreach (var node in model.Nodes)
            {
                bool isBody = (node.Name ?? "").IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isBody != body || node.Mesh == null) continue;
                int mi = node.Mesh.Value;
                if (mi < 0 || mi >= model.Meshes.Count) continue;
                foreach (var prim in model.Meshes[mi].Primitives)
                    if (prim.Positions != null) list.AddRange(prim.Positions);
            }
            return list;
        }

        /// <summary>点云 a 中每个点到点云 b 的最近距离的最大值（O(n²)，仅自测小规模用）。</summary>
        private static double CloudMaxNearest(List<float> a, List<float> b)
        {
            double worst = 0;
            for (int i = 0; i + 2 < a.Count; i += 3)
            {
                double best = double.MaxValue;
                for (int j = 0; j + 2 < b.Count; j += 3)
                {
                    double dx = a[i] - b[j], dy = a[i + 1] - b[j + 1], dz = a[i + 2] - b[j + 2];
                    double d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 < best) best = d2;
                }
                if (best > worst) worst = best;
            }
            return Math.Sqrt(worst);
        }
    }
}
