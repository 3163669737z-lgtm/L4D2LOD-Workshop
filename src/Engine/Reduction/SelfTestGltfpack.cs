using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace LodStudio.Engine
{
    /// <summary>
    /// 无 Blender 后端（<see cref="GltfpackRunner"/>）自测：
    ///  1) 形状优先预算与几何误差阈值；
    ///  2) 端到端：程序化 icosphere → SmdToGlb → RunAll 减面（比例排序 / Dst 去重复用 / 产物可读）；
    ///  3) SMD UV：无 Blender 链里 -kv 保留未被材质引用的 UV（回归）；
    ///  4) 取消：预取消全部标「已取消」，运行中取消（Workers=1 确定性）杀进程不产文件；
    ///  5) 失败继续：坏任务只失败自己，不拖垮整批。
    ///
    /// 全部用例不依赖本机 Blender（gltfpack 为内嵌资源），产物只写 ctx.NewDir 临时目录。
    /// </summary>
    internal static class SelfTestGltfpack
    {
        // ═══════════════════════════════ 1) 纯逻辑 ═══════════════════════════════

        [SelfTest("gltfpack 后端：形状优先预算与误差阈值")]
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
            ctx.AssertEqual(0.25, GltfpackRunner.AdaptiveRatio(340, 0.25), 1e-12, "小文件留给连通部件预算处理");
            ctx.AssertEqual(0.70, GltfpackRunner.AdaptiveRatio(25000, 0.70), 1e-12, "近档不增加高面压力");
            ctx.Assert(GltfpackRunner.AdaptiveRatio(25000, 0.25) < GltfpackRunner.AdaptiveRatio(9000, 0.25),
                       "远档高面模型应有更低的保留目标");

            // 按目标保留比例校验误差分档的边界。
            ctx.AssertEqual(GltfpackRunner.ShapeErrorCoarse, GltfpackRunner.ShapeErrorForRatio(0.34), "0.34 → coarse");
            ctx.AssertEqual(GltfpackRunner.ShapeErrorCoarse, GltfpackRunner.ShapeErrorForRatio(0.35), "0.35 → coarse（边界含）");
            ctx.AssertEqual(GltfpackRunner.ShapeErrorMid, GltfpackRunner.ShapeErrorForRatio(0.5), "0.50 → mid（边界含）");
            ctx.AssertEqual(GltfpackRunner.ShapeErrorFine, GltfpackRunner.ShapeErrorForRatio(0.51), "0.51 → fine");
            ctx.AssertEqual(GltfpackRunner.ShapeErrorFine, GltfpackRunner.ShapeErrorForRatio(1.0), "1.0 → fine");

            // 空批次：不碰 gltfpack，直接返回空
            var empty = GltfpackRunner.RunAll(new List<ReductionJob>(), null, CancellationToken.None, null);
            ctx.AssertEqual(0, empty.Count, "空批次应返回空结果列表");
            ctx.Log("形状优先误差档：" + GltfpackRunner.ShapeErrorCoarse + "/" +
                    GltfpackRunner.ShapeErrorMid + "/" + GltfpackRunner.ShapeErrorFine);
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

            var specs = new List<ReductionJob>
            {
                new ReductionJob { Source = src, Destination = Path.Combine(dir, "lod1.glb"), Ratio = 0.5, Weight = weight },
                new ReductionJob { Source = src, Destination = Path.Combine(dir, "lod2.glb"), Ratio = 0.2, Weight = weight },
                // 输出与任务 0 相同：应被 Dst 去重，直接复用结果（不重复执行）
                new ReductionJob { Source = src, Destination = Path.Combine(dir, "lod1.glb"), Ratio = 0.9, Weight = weight },
            };

            var sync = new object();
            var logs = new List<string>();
            var done = new List<int>();
            var progress = new List<KeyValuePair<int, int>>();
            var options = new GltfpackRunner.RunOptions
            {
                Workers = 2,
                AdaptiveTriangleBudget = false,
                TaskTimeoutMinutes = 1,
                Log = delegate (string s) { lock (sync) logs.Add(s); },
                OnJobCompleted = delegate (int i, ReductionResult r) { lock (sync) done.Add(i); }
            };
            var results = GltfpackRunner.RunAll(specs, options, CancellationToken.None,
                delegate (int d, int t) { lock (sync) progress.Add(new KeyValuePair<int, int>(d, t)); });

            ctx.AssertEqual(specs.Count, results.Count, "结果数量应与任务数一致");
            // follower（重复输出）复用 owner 结果，target/after 区间断言只对 owner 做
            List<int>[] followerMap;
            var ownerIdx = ReductionScheduler.DedupOwners(specs, out followerMap);
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
                if (line != null && line.IndexOf("内置 meshoptimizer", StringComparison.Ordinal) >= 0) { sawBackend = true; break; }
            ctx.Assert(sawBackend, "日志应说明使用了内置 meshoptimizer");
            ctx.AssertEqual(0, ReductionScheduler.LiveProcessCount, "结束后调度器进程登记表应为空");
        }

        // ═══════════════════════════════ 3) SMD UV 保留（-kv 回归） ═══════════════════════════════

        [SelfTest("内置减面：同材质的独立连通块保留小部件和轮廓极值")]
        public static void IndependentIslands(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("gp_islands");
            var main = ToSmd(BuildSphere(3, 1.0, -2, 0, 0), "shared");
            var small = ToSmd(BuildSphere(0, 0.25, 2, 0, 0), "shared");
            int largeTriangles = main.TriangleCount;
            main.Triangles.AddRange(small.Triangles);
            string src = Path.Combine(dir, "双连通块.glb");
            string dst = Path.Combine(dir, "减面.glb");
            string exact = Path.Combine(dir, "保留100%.glb");
            File.WriteAllBytes(src, SmdGlbCodec.SmdToGlb(main));

            var results = GltfpackRunner.RunAll(new[]
            {
                new ReductionJob { Source = src, Destination = dst, Ratio = 0.3 },
                new ReductionJob { Source = src, Destination = exact, Ratio = 1.0 }
            }, new GltfpackRunner.RunOptions { Workers = 1 }, CancellationToken.None, null);
            ctx.Assert(results.All(r => r.Ok), string.Join("，", results.Select(r => r.Error)));
            ctx.Assert(File.ReadAllBytes(src).SequenceEqual(File.ReadAllBytes(exact)), "100% 时原样复制 GLB");

            var original = GlbCodec.Read(src).Meshes.Single().Primitives.Single();
            var simplified = GlbCodec.Read(dst).Meshes.Single().Primitives.Single();
            int smallTriangles = 0;
            for (int i = 0; i < simplified.Indices.Length; i += 3)
            {
                bool right = simplified.Positions[simplified.Indices[i] * 3] > 0;
                ctx.Assert((simplified.Positions[simplified.Indices[i + 1] * 3] > 0) == right &&
                           (simplified.Positions[simplified.Indices[i + 2] * 3] > 0) == right,
                           "独立部件之间不能产生连接面");
                if (right) smallTriangles++;
            }
            ctx.AssertEqual(small.TriangleCount, smallTriangles, "小部件应保持完整");
            ctx.Assert(simplified.TriangleCount - smallTriangles < largeTriangles * 0.8, "大部件应实际减面");

            foreach (int x in new[] { -1, 0, 1 })
                foreach (int y in new[] { -1, 0, 1 })
                    foreach (int z in new[] { -1, 0, 1 })
                    {
                        if (x == 0 && y == 0 && z == 0) continue;
                        Func<GlbPrimitive, double> support = p => p.Indices
                            .Where(i => p.Positions[i * 3] < 0)
                            .Max(i => x * p.Positions[i * 3] + y * p.Positions[i * 3 + 1] + z * p.Positions[i * 3 + 2]);
                        ctx.AssertEqual(support(original), support(simplified), 1e-5, "大部件轮廓极值");
                    }
        }

        [SelfTest("内置减面：同一源部件的局部减面比例受均摊约束")]
        public static void BalanceUnevenSurface(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("gp_balanced_density");
            const int columns = 80, rows = 24;
            Func<int, int, SmdVertex> vertex = (x, y) => new SmdVertex
            {
                X = x, Y = y, Nz = 1,
                U = x / (float)columns + (x >= columns / 2 ? (x % 2) * 0.027f : 0),
                V = y / (float)rows + (x >= columns / 2 ? (y % 2) * 0.029f : 0)
            };
            var mesh = new SmdModel();
            for (int x = 0; x < columns; x++)
                for (int y = 0; y < rows; y++)
                {
                    var a = vertex(x, y);
                    var b = vertex(x + 1, y);
                    var c = vertex(x + 1, y + 1);
                    var d = vertex(x, y + 1);
                    mesh.Triangles.Add(new SmdTriangle { Material = "test", V0 = a, V1 = b, V2 = c });
                    mesh.Triangles.Add(new SmdTriangle { Material = "test", V0 = a, V1 = c, V2 = d });
                }

            string src = Path.Combine(dir, "mixed.glb");
            string dst = Path.Combine(dir, "lod.glb");
            File.WriteAllBytes(src, SmdGlbCodec.SmdToGlb(mesh));
            var result = GltfpackRunner.RunAll(new[]
            {
                new ReductionJob { Source = src, Destination = dst, Ratio = 0.25 }
            }, new GltfpackRunner.RunOptions(), CancellationToken.None, null).Single();
            ctx.Assert(result.Ok, result.Error ?? "均匀减面应成功");
            ctx.AssertEqual(mesh.TriangleCount, result.BudgetSourceTriangles, "分配应以完整源部件为准");
            ctx.Assert(result.BalancedRegions > 0, "UV 复杂和简单的区域应触发均摊");
            ctx.Assert(result.MaximumRegionalSpread <= 4100, "LOD 25% 时整体区域减面比例差不得超过 41 个百分点");
            ctx.Assert(result.MaximumAdjacentRegionalSpread <= 2600, "相邻区域的减面比例差不得超过 26 个百分点");
            ctx.Assert(result.AfterTriangles < mesh.TriangleCount, "均摊后仍应实际减面");
        }

        [SelfTest("内置减面：低面平直轮廓可在不改变外边界时简化")]
        public static void SmallStraightBorder(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("gp_straight_border");
            const int sections = 12;
            Func<int, int, SmdVertex> vertex = (x, y) => new SmdVertex
            {
                X = x, Y = y, Nz = 1, U = x / (float)sections, V = y
            };
            var strip = new SmdModel();
            for (int x = 0; x < sections; x++)
            {
                var a = vertex(x, 0);
                var b = vertex(x + 1, 0);
                var c = vertex(x + 1, 1);
                var d = vertex(x, 1);
                strip.Triangles.Add(new SmdTriangle { Material = "strip", V0 = a, V1 = b, V2 = c });
                strip.Triangles.Add(new SmdTriangle { Material = "strip", V0 = a, V1 = c, V2 = d });
            }
            string src = Path.Combine(dir, "strip.glb");
            string dst = Path.Combine(dir, "strip_lod1.glb");
            File.WriteAllBytes(src, SmdGlbCodec.SmdToGlb(strip));
            var r = GltfpackRunner.RunAll(new[]
            {
                new ReductionJob { Source = src, Destination = dst, Ratio = 0.75 }
            }, new GltfpackRunner.RunOptions(), CancellationToken.None, null).Single();
            ctx.Assert(r.Ok, r.Error ?? "低面条带应处理成功");
            ctx.Assert(r.AfterTriangles < strip.TriangleCount, "直边允许移除共线点");
            var reduced = GlbCodec.Read(dst).Meshes.Single().Primitives.Single();
            foreach (var corner in new[] { (0, 0), (sections, 0), (sections, 1), (0, 1) })
                ctx.Assert(reduced.Indices.Any(i => reduced.Positions[i * 3] == corner.Item1 &&
                                                    reduced.Positions[i * 3 + 1] == corner.Item2),
                           "四个外轮廓拐点应保留");
        }

        [SelfTest("内置减面：硬法线分缝的曲面仍可生成远档 LOD")]
        public static void FacetedOpenSurface(SelfTestContext ctx)
        {
            const int sections = 32;
            var tube = BuildFacetedTube(sections);
            string dir = ctx.NewDir("gp_faceted_surface");
            string sourcePath = Path.Combine(dir, "source.glb");
            string lodPath = Path.Combine(dir, "lod.glb");
            File.WriteAllBytes(sourcePath, SmdGlbCodec.SmdToGlb(tube));
            var result = GltfpackRunner.RunAll(new[]
            {
                new ReductionJob { Source = sourcePath, Destination = lodPath, Ratio = 0.25 }
            }, new GltfpackRunner.RunOptions(), CancellationToken.None, null).Single();
            ctx.Assert(result.Ok, result.Error ?? "生成应成功");
            ctx.Assert(result.AfterTriangles <= tube.TriangleCount * 0.8, "硬法线分缝不应卡住远档减面");

            var reduced = GlbCodec.Read(lodPath).Meshes.Single().Primitives.Single();
            foreach (int axis in new[] { 0, 1 })
            {
                ctx.AssertEqual(-1, reduced.Indices.Min(i => reduced.Positions[i * 3 + axis]), 0.02, "保留圆柱外轮廓");
                ctx.AssertEqual(1, reduced.Indices.Max(i => reduced.Positions[i * 3 + axis]), 0.02, "保留圆柱外轮廓");
            }
            ctx.AssertEqual(0, reduced.Indices.Min(i => reduced.Positions[i * 3 + 2]), 1e-6, "保留底部开口");
            ctx.AssertEqual(1, reduced.Indices.Max(i => reduced.Positions[i * 3 + 2]), 1e-6, "保留顶部开口");
        }

        [SelfTest("内置减面：保真档位与几何偏差指标")]
        public static void FidelityProfilesAndMetrics(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("gp_fidelity_profiles");
            string src = Path.Combine(dir, "sphere.glb");
            File.WriteAllBytes(src, SmdGlbCodec.SmdToGlb(ToSmd(BuildSphere(3, 1, 0, 0, 0), "sphere")));

            var results = new ReductionResult[3];
            for (int profile = 0; profile < results.Length; profile++)
            {
                string dst = Path.Combine(dir, "lod_" + profile + ".glb");
                var result = GltfpackRunner.RunAll(new[]
                {
                    new ReductionJob { Source = src, Destination = dst, Ratio = 0.20, Distance = 60 }
                }, new GltfpackRunner.RunOptions { ReductionProfile = profile }, CancellationToken.None, null).Single();
                results[profile] = result;
                ctx.Assert(result.Ok, "保真档位 " + profile + " 应生成 LOD：" + result.Error);
                ctx.Assert(result.HasDeviationMetrics, "保真档位 " + profile + " 应报告几何偏差样本");
                ctx.Assert(result.MaxSurfaceDeviationPpm >= result.MeanSurfaceDeviationPpm && result.MeanSurfaceDeviationPpm >= 0,
                           "几何偏差应满足峰值 ≥ 均值 ≥ 0");
                ctx.Log("保真档位 " + profile + "：" + result.BeforeTriangles + " → " + result.AfterTriangles +
                        " 面，几何偏差 " + result.MeanSurfaceDeviationPpm + "/" + result.MaxSurfaceDeviationPpm + " ppm（均值/峰值）");
            }
            ctx.Assert(results[2].AfterTriangles <= results[0].AfterTriangles, "极致减面档面数应不高于轮廓精保档");
        }

        [SelfTest("内置减面：阴影 LOD 减面并保留包围盒轮廓")]
        public static void ShadowLodMode(SelfTestContext ctx)
        {
            const double ratio = 0.15;
            var sphere = ToSmd(BuildSphere(3, 1, 0, 0, 0), "sphere");
            string dir = ctx.NewDir("gp_shadow");
            string src = Path.Combine(dir, "sphere.glb");
            string dst = Path.Combine(dir, "shadow.glb");
            File.WriteAllBytes(src, SmdGlbCodec.SmdToGlb(sphere));

            var result = GltfpackRunner.RunAll(new[]
            {
                new ReductionJob { Source = src, Destination = dst, Ratio = ratio, ShadowMode = true }
            }, new GltfpackRunner.RunOptions { AdaptiveTriangleBudget = false }, CancellationToken.None, null).Single();
            ctx.Assert(result.Ok, result.Error ?? "应生成阴影 LOD");
            ctx.AssertEqual((int)Math.Round(sphere.TriangleCount * ratio), result.TargetTriangles, "阴影 LOD 应直接使用请求比例");
            ctx.Assert(result.AfterTriangles <= sphere.TriangleCount * 0.60, "阴影 LOD 应至少减少 40% 的面数");

            var original = GlbCodec.Read(src).Meshes.Single().Primitives.Single();
            var reduced = GlbCodec.Read(dst).Meshes.Single().Primitives.Single();
            for (int axis = 0; axis < 3; axis++)
            {
                int component = axis;
                ctx.AssertEqual(original.Indices.Min(i => original.Positions[i * 3 + component]),
                                reduced.Indices.Min(i => reduced.Positions[i * 3 + component]),
                                0.02, "阴影 LOD 应保留轮廓下界");
                ctx.AssertEqual(original.Indices.Max(i => original.Positions[i * 3 + component]),
                                reduced.Indices.Max(i => reduced.Positions[i * 3 + component]),
                                0.02, "阴影 LOD 应保留轮廓上界");
            }
            ctx.Log("阴影 LOD：" + result.BeforeTriangles + " → " + result.AfterTriangles + " 面");
        }

        [SelfTest("内置减面：保留比例相同时远距离允许更多轮廓简化")]
        public static void DistanceBasedOutline(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("gp_distance_outline");
            string sourcePath = Path.Combine(dir, "source.glb");
            string nearPath = Path.Combine(dir, "near.glb");
            string farPath = Path.Combine(dir, "far.glb");
            File.WriteAllBytes(sourcePath, SmdGlbCodec.SmdToGlb(BuildFacetedTube(32)));

            var results = GltfpackRunner.RunAll(new[]
            {
                new ReductionJob { Source = sourcePath, Destination = nearPath, Ratio = 0.15, Distance = 10 },
                new ReductionJob { Source = sourcePath, Destination = farPath, Ratio = 0.15, Distance = 60 }
            }, new GltfpackRunner.RunOptions(), CancellationToken.None, null);
            ctx.Assert(results.All(r => r.Ok), string.Join("，", results.Select(r => r.Error)));
            ctx.Assert(results[1].AfterTriangles < results[0].AfterTriangles,
                       "相同面数目标下，远档应能进一步简化弧形边界");

            var reduced = GlbCodec.Read(farPath).Meshes.Single().Primitives.Single();
            ctx.AssertEqual(0, reduced.Indices.Min(i => reduced.Positions[i * 3 + 2]), 1e-6, "保留底部开口高度");
            ctx.AssertEqual(1, reduced.Indices.Max(i => reduced.Positions[i * 3 + 2]), 1e-6, "保留顶部开口高度");
        }

        [SelfTest("内置减面：远档整块候选可进一步减面并保留平面轮廓")]
        public static void FarWholePrimitive(SelfTestContext ctx)
        {
            const int size = 12;
            var mesh = new SmdModel();
            Func<int, int, SmdVertex> vertex = (x, y) => new SmdVertex
            {
                X = x, Y = y, Nz = 1, U = x / (float)size, V = y / (float)size
            };
            for (int x = 0; x < size; x++)
                for (int y = 0; y < size; y++)
                {
                    var a = vertex(x, y);
                    var b = vertex(x + 1, y);
                    var c = vertex(x + 1, y + 1);
                    var d = vertex(x, y + 1);
                    mesh.Triangles.Add(new SmdTriangle { Material = "surface", V0 = a, V1 = b, V2 = c });
                    mesh.Triangles.Add(new SmdTriangle { Material = "surface", V0 = a, V1 = c, V2 = d });
                }
            string dir = ctx.NewDir("gp_far_whole");
            string source = Path.Combine(dir, "source.glb");
            string near = Path.Combine(dir, "near.glb");
            string far = Path.Combine(dir, "far.glb");
            File.WriteAllBytes(source, SmdGlbCodec.SmdToGlb(mesh));
            var results = GltfpackRunner.RunAll(new[]
            {
                new ReductionJob { Source = source, Destination = near, Distance = 30, Ratio = 0.25 },
                new ReductionJob { Source = source, Destination = far, Distance = 60, Ratio = 0.25 }
            }, new GltfpackRunner.RunOptions(), CancellationToken.None, null);
            ctx.Assert(results.All(r => r.Ok), string.Join("，", results.Select(r => r.Error)));
            ctx.AssertEqual(0, results[0].WholePrimitiveTrianglesSaved, "近档沿用分区候选");
            ctx.Assert(results[1].WholePrimitiveTrianglesSaved > 0, "远档整块候选应进一步减少面数");
            ctx.Assert(results[1].AfterTriangles < results[0].AfterTriangles, "相同目标下远档应更轻");
            var lod = GlbCodec.Read(far).Meshes.Single().Primitives.Single();
            foreach (var corner in new[] { (0, 0), (size, 0), (size, size), (0, size) })
                ctx.Assert(lod.Indices.Any(i => lod.Positions[i * 3] == corner.Item1 &&
                                               lod.Positions[i * 3 + 1] == corner.Item2),
                           "远档应保留四个轮廓拐点");
        }

        [SelfTest("内置减面：远档验证并保留第二套 UV")]
        public static void FarAdditionalUv(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("gp_far_additional_uv");
            string source = Path.Combine(dir, "source.glb");
            string near = Path.Combine(dir, "near.glb");
            string far = Path.Combine(dir, "far.glb");
            var model = BuildFacetedTube(32);
            var input = GlbJson.SplitGlb(SmdGlbCodec.SmdToGlb(model), source);
            var mesh = GlbJson.ObjOf(GlbJson.Arr(input.Root, "meshes").Single());
            var primitive = GlbJson.ObjOf(GlbJson.Arr(mesh, "primitives").Single());
            var attributes = GlbJson.Obj(primitive, "attributes");
            int accessor = GlbJson.Int(attributes, "TEXCOORD_0", -1);
            string primary = "\"TEXCOORD_0\":" + accessor.ToString(CultureInfo.InvariantCulture);
            string json = input.JsonText.Replace(primary, primary + ",\"TEXCOORD_1\":" + accessor.ToString(CultureInfo.InvariantCulture));
            File.WriteAllBytes(source, GlbJson.PackGlb(Encoding.UTF8.GetBytes(json), input.Bin));

            var results = GltfpackRunner.RunAll(new[]
            {
                new ReductionJob { Source = source, Destination = near, Ratio = 0.15, Distance = 15 },
                new ReductionJob { Source = source, Destination = far, Ratio = 0.15, Distance = 60 }
            }, new GltfpackRunner.RunOptions(), CancellationToken.None, null);
            ctx.Assert(results.All(r => r.Ok), string.Join("，", results.Select(r => r.Error)));
            ctx.AssertEqual(model.TriangleCount, results[0].AfterTriangles, "近档沿用双 UV 原网格");
            ctx.Assert(results[1].AfterTriangles < model.TriangleCount, "远档可对双 UV 图元减面");
            var output = GlbJson.SplitGlb(File.ReadAllBytes(far), far);
            mesh = GlbJson.ObjOf(GlbJson.Arr(output.Root, "meshes").Single());
            primitive = GlbJson.ObjOf(GlbJson.Arr(mesh, "primitives").Single());
            attributes = GlbJson.Obj(primitive, "attributes");
            int uv0 = GlbJson.Int(attributes, "TEXCOORD_0", -1);
            int uv1 = GlbJson.Int(attributes, "TEXCOORD_1", -1);
            ctx.Assert(uv0 >= 0 && uv1 >= 0, "远档保留两套 UV");
            ctx.Assert(GlbJson.ReadFloats(output.Root, output.Bin, uv0, far, 2, "TEXCOORD_0").SequenceEqual(
                GlbJson.ReadFloats(output.Root, output.Bin, uv1, far, 2, "TEXCOORD_1")),
                "相同输入的两套 UV 在远档仍应对应");
        }

        [SelfTest("内置减面：局部非流形接合保留，周围曲面继续减面")]
        public static void PreserveLocalJunction(SelfTestContext ctx)
        {
            const int sections = 32;
            var tube = BuildFacetedTube(sections);
            for (int section = 0; section < sections; section++)
                for (int height = 0; height <= 1; height++)
                {
                    SmdVertex CapVertex(int column)
                    {
                        double angle = (column % sections) * 2 * Math.PI / sections;
                        float x = (float)Math.Cos(angle), y = (float)Math.Sin(angle);
                        return new SmdVertex { X = x, Y = y, Z = height, Nz = 2 * height - 1, U = x, V = y };
                    }
                    var a = CapVertex(section);
                    var b = CapVertex(section + 1);
                    var center = new SmdVertex { Z = height, Nz = 2 * height - 1 };
                    tube.Triangles.Add(new SmdTriangle
                    {
                        Material = "tube", V0 = center, V1 = height == 0 ? b : a, V2 = height == 0 ? a : b
                    });
                }
            tube.Triangles.Add(new SmdTriangle
            {
                Material = "tube",
                V0 = new SmdVertex { X = 1, Ny = 1 },
                V1 = new SmdVertex { X = 1, Z = 1, Ny = 1, U = 1 },
                V2 = new SmdVertex { X = 1.2f, Z = 0.5f, Ny = 1, U = 0.5f, V = 1 }
            });

            string dir = ctx.NewDir("gp_local_junction");
            string sourcePath = Path.Combine(dir, "source.glb");
            string lodPath = Path.Combine(dir, "lod.glb");
            File.WriteAllBytes(sourcePath, SmdGlbCodec.SmdToGlb(tube));
            var result = GltfpackRunner.RunAll(new[]
            {
                new ReductionJob { Source = sourcePath, Destination = lodPath, Ratio = 0.25 }
            }, new GltfpackRunner.RunOptions(), CancellationToken.None, null).Single();
            ctx.Assert(result.Ok, result.Error ?? "生成应成功");
            ctx.Assert(result.AfterTriangles <= tube.TriangleCount * 0.8, "局部接合周围应继续减面");

            var reduced = GlbCodec.Read(lodPath).Meshes.Single().Primitives.Single();
            bool AtJunction(int index, int height) =>
                reduced.Positions[index * 3] == 1 && reduced.Positions[index * 3 + 1] == 0 &&
                reduced.Positions[index * 3 + 2] == height;
            int incident = 0;
            for (int i = 0; i < reduced.Indices.Length; i += 3)
                for (int edge = 0; edge < 3; edge++)
                {
                    int a = reduced.Indices[i + edge], b = reduced.Indices[i + (edge + 1) % 3];
                    if ((AtJunction(a, 0) && AtJunction(b, 1)) || (AtJunction(a, 1) && AtJunction(b, 0)))
                        incident++;
                }
            ctx.AssertEqual(3, incident, "接合边应仍由原来的三张面共用");
        }

        private static SmdModel BuildFacetedTube(int sections)
        {
            var tube = new SmdModel();
            for (int section = 0; section < sections; section++)
            {
                double facing = (section + 0.5) * 2 * Math.PI / sections;
                Func<int, int, SmdVertex> vertex = (column, height) =>
                {
                    double angle = (column % sections) * 2 * Math.PI / sections;
                    return new SmdVertex
                    {
                        X = (float)Math.Cos(angle), Y = (float)Math.Sin(angle), Z = height,
                        Nx = (float)Math.Cos(facing), Ny = (float)Math.Sin(facing),
                        U = column / (float)sections, V = height
                    };
                };

                var a = vertex(section, 0);
                var b = vertex(section + 1, 0);
                var c = vertex(section + 1, 1);
                var d = vertex(section, 1);
                tube.Triangles.Add(new SmdTriangle { Material = "tube", V0 = a, V1 = b, V2 = c });
                tube.Triangles.Add(new SmdTriangle { Material = "tube", V0 = a, V1 = c, V2 = d });
            }

            return tube;
        }

        [SelfTest("内置减面：顶点微调只采用通过检查且更少面的候选")]
        public static void GuardedVertexUpdates(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("gp_vertex_updates");
            string src = Path.Combine(dir, "sphere.glb");
            string fixedPath = Path.Combine(dir, "fixed.glb");
            string updatedPath = Path.Combine(dir, "updated.glb");
            File.WriteAllBytes(src, SmdGlbCodec.SmdToGlb(ToSmd(BuildSphere(4, 1, 0, 0, 0), "sphere")));

            // Leave room for the optional solver after the fixed-vertex pass.
            const double retention = 0.1;
            var fixedResult = GltfpackRunner.RunAll(new[]
            {
                new ReductionJob { Source = src, Destination = fixedPath, Ratio = retention }
            }, new GltfpackRunner.RunOptions(), CancellationToken.None, null).Single();
            var updatedResult = GltfpackRunner.RunAll(new[]
            {
                new ReductionJob { Source = src, Destination = updatedPath, Ratio = retention }
            }, new GltfpackRunner.RunOptions { AllowVertexUpdates = true }, CancellationToken.None, null).Single();

            ctx.Assert(fixedResult.Ok, fixedResult.Error ?? "固定顶点模式应成功");
            ctx.Assert(updatedResult.Ok, updatedResult.Error ?? "顶点微调模式应成功");
            ctx.AssertEqual(0, fixedResult.VertexUpdateTrials, "默认不尝试移动顶点");
            ctx.Assert(updatedResult.VertexUpdateTrials > 0, "开启后应对符合条件的区域尝试微调");
            ctx.AssertEqual(fixedResult.AfterTriangles - updatedResult.AfterTriangles,
                            updatedResult.VertexUpdateTrianglesSaved, "额外减少的面数应与实际产物一致");
            ctx.Assert(updatedResult.AfterTriangles <= fixedResult.AfterTriangles, "微调模式不能增加面数");

            var source = GlbCodec.Read(src).Meshes.Single().Primitives.Single();
            var updated = GlbCodec.Read(updatedPath).Meshes.Single().Primitives.Single();
            for (int axis = 0; axis < 3; axis++)
            {
                int component = axis;
                ctx.AssertEqual(source.Indices.Min(i => source.Positions[i * 3 + component]),
                                updated.Indices.Min(i => updated.Positions[i * 3 + component]),
                                1e-6, "轮廓下界不变");
                ctx.AssertEqual(source.Indices.Max(i => source.Positions[i * 3 + component]),
                                updated.Indices.Max(i => updated.Positions[i * 3 + component]),
                                1e-6, "轮廓上界不变");
            }
            if (updatedResult.VertexUpdateAccepted == 0)
                ctx.Assert(File.ReadAllBytes(fixedPath).SequenceEqual(File.ReadAllBytes(updatedPath)),
                           "没有合格候选时回退至固定顶点结果");
        }

        [SelfTest("内置减面：平面几何的非线性 UV 插值受保护")]
        public static void ProtectTexturedSurface(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("gp_uv_guard");
            const int cells = 18;
            Func<int, double> columnU = column => column / (double)cells + (column % 2) * 0.04;
            Func<int, int, SmdVertex> vertex = (x, y) => new SmdVertex
            {
                X = x, Y = y, Nz = 1, U = (float)columnU(x), V = y / (float)cells
            };
            var plane = new SmdModel();
            for (int x = 0; x < cells; x++)
                for (int y = 0; y < cells; y++)
                {
                    var a = vertex(x, y);
                    var b = vertex(x + 1, y);
                    var c = vertex(x + 1, y + 1);
                    var d = vertex(x, y + 1);
                    plane.Triangles.Add(new SmdTriangle { Material = "test", V0 = a, V1 = b, V2 = c });
                    plane.Triangles.Add(new SmdTriangle { Material = "test", V0 = a, V1 = c, V2 = d });
                }
            string src = Path.Combine(dir, "source.glb");
            string dst = Path.Combine(dir, "lod.glb");
            File.WriteAllBytes(src, SmdGlbCodec.SmdToGlb(plane));
            var result = GltfpackRunner.RunAll(new[] { new ReductionJob { Source = src, Destination = dst, Ratio = 0.2 } },
                new GltfpackRunner.RunOptions(), CancellationToken.None, null).Single();
            ctx.Assert(result.Ok, result.Error ?? "生成应成功");
            var primitive = GlbCodec.Read(dst).Meshes.Single().Primitives.Single();
            ctx.Assert(primitive.TriangleCount < plane.TriangleCount * 0.95, "在不扭曲 UV 的方向应能减面");
            for (int i = 0; i < primitive.Indices.Length; i += 3)
            {
                int a = primitive.Indices[i], b = primitive.Indices[i + 1], c = primitive.Indices[i + 2];
                double x = (primitive.Positions[a * 3] + primitive.Positions[b * 3] + primitive.Positions[c * 3]) / 3.0;
                double u = (primitive.Uvs[a * 2] + primitive.Uvs[b * 2] + primitive.Uvs[c * 2]) / 3.0;
                int column = Math.Min(cells - 1, (int)x);
                double expected = columnU(column) + (columnU(column + 1) - columnU(column)) * (x - column);
                ctx.AssertEqual(expected, u, 0.008, "减面后面中心的源 UV 插值");
            }
        }

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
            var specs = new List<ReductionJob>
            {
                new ReductionJob
                {
                    Source = src,
                    Destination = dst,
                    Ratio = 1.0,
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
            var preSpecs = new List<ReductionJob>();
            for (int i = 0; i < 3; i++)
                preSpecs.Add(new ReductionJob { Source = src, Destination = Path.Combine(dir, "pre" + i + ".glb"), Ratio = 0.5, Weight = weight });
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
            var specs = new List<ReductionJob>();
            for (int i = 0; i < 6; i++)
                specs.Add(new ReductionJob { Source = src, Destination = Path.Combine(dir, "mid" + i + ".glb"), Ratio = 0.5, Weight = weight });
            var cts = new CancellationTokenSource();
            int progressCalls = 0;
            var options = new GltfpackRunner.RunOptions
            {
                Workers = 1,
                TaskTimeoutMinutes = 1,
                Log = delegate { },
                OnJobCompleted = delegate (int i, ReductionResult r) { if (i == 0) cts.Cancel(); }
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
            ctx.AssertEqual(0, ReductionScheduler.LiveProcessCount, "取消后调度器进程登记表应为空");
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

            var specs = new List<ReductionJob>
            {
                new ReductionJob { Source = src, Destination = Path.Combine(dir, "ok1.glb"), Ratio = 0.5, Weight = weight },
                new ReductionJob { Source = Path.Combine(dir, "不存在_src.glb"), Destination = Path.Combine(dir, "missing.glb"), Ratio = 0.5, Weight = 0 },
                new ReductionJob { Source = src, Destination = Path.Combine(dir, "ok2.glb"), Ratio = 0.3, Weight = weight },
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
    }
}
