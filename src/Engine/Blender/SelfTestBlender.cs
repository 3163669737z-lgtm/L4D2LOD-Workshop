using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using LodStudio.Engine.Platform;

namespace LodStudio.Engine
{
    /// <summary>
    /// Blender 后端自测：
    ///  1) 内嵌脚本释放（SHA-1 命名 / 幂等复用 / 字节一致）；
    ///  2) BlenderLocator.ProbeVersion 对 4.0 / 4.1 返回版本行；
    ///  3) 纯调度逻辑（Dst 去重 / 加权分桶 / 自动并行数 / 看门狗时长 / 任务 JSON 格式）；
    ///  4) 真实 Blender 端到端：4.0 与 4.1 各跑一遍「生成 GLB → RunAll 减面 → 校验产物」；
    ///  5) 取消：跑到一半 KillAll()，未完成的任务标「已取消」且无残留 blender 进程；
    ///  6) 崩溃续跑：原生崩溃只失败当前任务、坏 GLB 只失败它自己、其余任务成功。
    ///
    /// 真实 Blender 用例在本机没有对应 Blender 时记 SKIP（不算失败）。
    /// 所有产物都写在 ctx.NewDir 的临时目录里，绝不碰用户工程。
    /// </summary>
    internal static class SelfTestBlender
    {
        private const string Ver40 = "4.0";
        private const string Ver41 = "4.1";

        // ═══════════════════════════════ 1) 内嵌脚本释放 ═══════════════════════════════

        [SelfTest("Blender 后端：内嵌脚本释放（SHA-1 命名 / 幂等复用 / 字节与资源一致）")]
        public static void BackendScriptExtract(SelfTestContext ctx)
        {
            byte[] bytes = BackendScript.LoadBytes();
            ctx.Assert(bytes != null && bytes.Length > 2000, "内嵌后端脚本字节数应 > 2000，实际=" + (bytes == null ? -1 : bytes.Length));

            string hash = BackendScript.ContentHash;
            ctx.Assert(hash != null && hash.Length == 8, "ContentHash 应是 8 位十六进制，实际=" + (hash ?? "(null)"));
            ctx.AssertEqual(hash.ToLowerInvariant(), hash, "ContentHash 应是小写");
            string expectHash;
            using (var sha = System.Security.Cryptography.SHA1.Create())
                expectHash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").Substring(0, 8).ToLowerInvariant();
            ctx.AssertEqual(expectHash, hash, "ContentHash 应为脚本内容 SHA-1 的前 8 位");

            string text = BackendScript.SourceText;
            ctx.AssertContains(text, "JOBS=", "脚本源码应包含 JOBS= 启动行");
            ctx.AssertContains(text, "RESULT ", "脚本源码应包含 RESULT 结果行");
            ctx.AssertContains(text, "read_factory_settings", "脚本源码应按任务重置场景");
            ctx.AssertContains(text, "export_apply=False", "导出应使用 export_apply=False");
            ctx.Log("内嵌脚本：" + bytes.Length + " 字节，" + BackendScript.SourceLineCount + " 行，SHA-1 前 8 位=" + hash);

            string e1;
            string p1 = BackendScript.EnsureExtracted(out e1);
            ctx.Assert(e1 == null, "首次释放不应报错：" + e1);
            ctx.Assert(p1 != null, "首次释放应返回路径");
            ctx.Assert(File.Exists(p1), "释放出的脚本应存在：" + (p1 ?? "(null)"));
            ctx.AssertEqual("blender_backend_" + hash + ".py", Path.GetFileName(p1), "脚本文件名应为 blender_backend_<hash>.py");
            string backendDir = Path.GetFullPath(AppInfo.TempBackendDir);
            ctx.Assert(Path.GetFullPath(p1).StartsWith(backendDir, StringComparison.OrdinalIgnoreCase),
                       "脚本应释放到临时 backend 目录：" + backendDir);

            byte[] onDisk = File.ReadAllBytes(p1);
            ctx.AssertEqual(bytes.Length, onDisk.Length, "释放出的脚本长度应与嵌入资源一致");
            bool same = true;
            for (int i = 0; i < bytes.Length; i++) { if (bytes[i] != onDisk[i]) { same = false; break; } }
            ctx.Assert(same, "释放出的脚本字节应与嵌入资源逐字节一致");

            string e2;
            string p2 = BackendScript.EnsureExtracted(out e2);
            ctx.Assert(e2 == null, "重复调用不应报错：" + e2);
            ctx.AssertEqual(p1, p2, "重复调用应复用同一路径");
            ctx.AssertEqual(hash, BackendScript.ContentHash, "ContentHash 应稳定");
            ctx.AssertEqual(Path.GetFullPath(p1), Path.GetFullPath(BackendScript.ExpectedPath), "ExpectedPath 应与实际释放路径一致");
            ctx.Log("释放路径：" + p1);
        }

        // ═══════════════════════════════ 2) 版本探测 ═══════════════════════════════

        [SelfTest("Blender 定位：ProbeVersion 读取 4.0 / 4.1 版本行（缺失则 SKIP）")]
        public static void ProbeVersionBoth(SelfTestContext ctx)
        {
            int found = 0;
            foreach (string want in new[] { Ver40, Ver41 })
            {
                string exe = FindBlenderForVersion(ctx, want);
                if (exe == null)
                {
                    ctx.Log("SKIP 本机未找到 Blender " + want);
                    continue;
                }
                string ver = BlenderLocator.ProbeVersion(exe, 25000);
                ctx.Log("Blender " + want + " → " + (ver ?? "(null)") + "   [" + exe + "]");
                ctx.Assert(ver != null, "ProbeVersion 应返回版本行（" + exe + "）");
                ctx.Assert(ver.StartsWith("Blender " + want, StringComparison.Ordinal),
                           "版本行应以 'Blender " + want + "' 开头，实际=" + ver);
                ctx.Assert(BlenderLocator.IsUsable(exe), "IsUsable 应为 true（" + exe + "）");
                found++;
            }
            if (found == 0)
            {
                ctx.Log("SKIP 本机没有任何可用的 Blender，跳过版本探测用例");
                return;
            }

            string auto = BlenderLocator.Find(null);
            ctx.Log("Find(null) = " + (auto ?? "(null)"));
            ctx.Assert(auto != null, "Find(null) 应能找到本机的 blender.exe");
            ctx.Assert(File.Exists(auto), "Find(null) 返回的路径应存在：" + (auto ?? "(null)"));
            ctx.Assert(BlenderLocator.ProbeVersion(null, 1000) == null, "ProbeVersion(null) 应返回 null");
            ctx.Assert(BlenderLocator.ProbeVersion(Path.Combine(Path.GetTempPath(), "不存在的_blender.exe"), 1000) == null,
                       "ProbeVersion(不存在路径) 应返回 null");
        }

        // ═══════════════════════════════ 3) 纯调度逻辑 ═══════════════════════════════

        [SelfTest("Blender 调度：Dst 去重 / 加权分桶 / 自动并行数 / 看门狗时长 / 任务 JSON 格式（纯逻辑）")]
        public static void SchedulerLogic(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("scheduler_logic");

            // ── Dst 去重（大小写不敏感 + 相对路径归一） ──
            var jobs = new List<BlenderJobSpec>
            {
                new BlenderJobSpec { Source = @"C:\m\a.glb", Destination = Path.Combine(dir, "out.glb"), Weight = 100 },
                new BlenderJobSpec { Source = @"C:\m\b.glb", Destination = Path.Combine(dir, "OUT.GLB"), Weight = 80 },
                new BlenderJobSpec { Source = @"C:\m\c.glb", Destination = Path.Combine(dir, "x.glb"), Weight = 60 },
                new BlenderJobSpec { Source = @"C:\m\d.glb", Destination = Path.Combine(dir, "y.glb"), Weight = 40 },
                new BlenderJobSpec { Source = @"C:\m\e.glb", Destination = Path.Combine(dir, "out.glb"), Weight = 20 },
            };
            List<int>[] followers;
            List<int> owners = BlenderScheduler.DedupOwners(jobs, out followers);
            ctx.AssertEqual(3, owners.Count, "5 个任务里 job1/job4 与 job0 输出重复 → 3 个 owner");
            ctx.AssertEqual(0, owners[0], "第一个任务应是 owner");
            ctx.Assert(followers[0] != null && followers[0].Count == 2, "第一个 owner 应有 2 个 follower（job1 与 job4）");
            ctx.Assert(followers[0][0] == 1 && followers[0][1] == 4, "follower 下标应为 1 和 4");

            // ── 加权分桶：降序轮流进最轻桶 ──
            var buckets = BlenderScheduler.PlanBuckets(jobs, owners, 2);
            ctx.AssertEqual(2, buckets.Count, "2 个 worker 应有 2 个桶");
            int seen = 0;
            foreach (var b in buckets) seen += b.Count;
            ctx.AssertEqual(owners.Count, seen, "每个 owner 必须恰好进一个桶");
            foreach (int o in owners)
            {
                int hits = 0;
                foreach (var b in buckets) if (b.Contains(o)) hits++;
                ctx.AssertEqual(1, hits, "owner " + o + " 应恰好出现一次");
            }
            long load0 = BucketLoad(jobs, buckets[0]), load1 = BucketLoad(jobs, buckets[1]);
            ctx.Log("分桶负载：" + load0 + " / " + load1 + "   桶内容：" + Describe(buckets));
            ctx.Assert(Math.Abs(load0 - load1) <= 100, "两桶负载应尽量均衡（100/80/60/40/20 权重）");
            long maxLoad = Math.Max(load0, load1), minLoad = Math.Min(load0, load1);
            ctx.Assert(maxLoad <= 160, "最重桶负载不应超过 160，实际=" + maxLoad + "（最小 " + minLoad + "）");

            // ── workers 解析 ──
            ctx.AssertEqual(2, BlenderScheduler.ResolveWorkers(2, 10), "手动 2 个 worker");
            ctx.AssertEqual(3, BlenderScheduler.ResolveWorkers(16, 3), "手动 16 但只有 3 个任务 → 夹到 3");
            ctx.AssertEqual(1, BlenderScheduler.ResolveWorkers(1, 100), "手动 1 个 worker");
            ctx.AssertEqual(0, BlenderScheduler.ResolveWorkers(0, 0), "0 个任务 → 0");
            int auto = BlenderScheduler.ResolveWorkers(0, 100);
            ctx.Assert(auto >= 1 && auto <= 8, "自动并行数应在 1..8，实际=" + auto);
            ctx.Log("自动并行数=" + auto + "（CPU=" + Environment.ProcessorCount + "，空闲内存=" +
                    BlenderScheduler.FreePhysicalGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB）");

            // ── 看门狗时长 ──
            ctx.AssertEqual(20 * 60000, (int)BlenderScheduler.ResolveTimeoutMs(
                new BlenderJobSpec { Source = Path.Combine(dir, "不存在.bin"), Weight = 0 }, 20), "小任务 = 20 分钟");
            ctx.AssertEqual(20 * 60000 + 10 * 60000, (int)BlenderScheduler.ResolveTimeoutMs(
                new BlenderJobSpec { Source = "x", Weight = 25L * 1024 * 1024 }, 20), "25MB → +10 分钟");
            ctx.AssertEqual(20 * 60000 + 100 * 60000, (int)BlenderScheduler.ResolveTimeoutMs(
                new BlenderJobSpec { Source = "x", Weight = 500L * 1024 * 1024 }, 20), "500MB → 封顶 +100 分钟");
            ctx.AssertEqual(20 * 60000, (int)BlenderScheduler.ResolveTimeoutMs(
                new BlenderJobSpec { Source = "x", Weight = 0 }, 0), "0 分钟参数 → 默认 20 分钟");

            // ── 任务 JSON 落盘格式（字段序/数字格式/无 BOM/可选 quality） ──
            string jsonPath = Path.Combine(dir, "tasks_format.json");
            var fmtJobs = new List<BlenderJobSpec>
            {
                new BlenderJobSpec { Source = @"D:\p\a.glb", Destination = @"D:\p\a_lod1.glb", Ratio = 0.997, ShrinkInner = 0.0009, MinFaces = 12, GuardPercent = 35 },
                new BlenderJobSpec { Source = @"D:\p\a.glb", Destination = @"D:\p\a_lod2.glb", Ratio = 0.653, ShrinkInner = 0.001035, MinFaces = 12, GuardPercent = 35, QualityOverride = false },
            };
            BlenderScheduler.WriteTaskFile(jsonPath, fmtJobs, true);
            byte[] raw = File.ReadAllBytes(jsonPath);
            ctx.Assert(raw.Length > 3 && !(raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF), "任务 JSON 必须是 UTF-8 无 BOM");
            string expect =
                "{\r\n" +
                " \"quality\": true,\r\n" +
                " \"jobs\": [\r\n" +
                "  {\"src\": \"D:\\\\p\\\\a.glb\", \"dst\": \"D:\\\\p\\\\a_lod1.glb\", \"ratio\": 0.997, \"shrinkInner\": 0.0009, \"minFaces\": 12, \"guardPct\": 35},\r\n" +
                "  {\"src\": \"D:\\\\p\\\\a.glb\", \"dst\": \"D:\\\\p\\\\a_lod2.glb\", \"ratio\": 0.653, \"shrinkInner\": 0.001035, \"minFaces\": 12, \"guardPct\": 35, \"quality\": false}\r\n" +
                " ]\r\n" +
                "}\r\n";
            ctx.AssertEqual(expect, Encoding.UTF8.GetString(raw), "任务 JSON 应与旧版格式逐字一致");
            ctx.Log("任务 JSON 格式校验通过（无 BOM，字段序 src,dst,ratio,shrinkInner,minFaces,guardPct[,quality]）");
        }

        private static long BucketLoad(List<BlenderJobSpec> jobs, List<int> bucket)
        {
            long sum = 0;
            foreach (int i in bucket) sum += jobs[i].Weight > 0 ? jobs[i].Weight : 1;
            return sum;
        }

        private static string Describe(List<List<int>> buckets)
        {
            var sb = new StringBuilder();
            for (int b = 0; b < buckets.Count; b++)
            {
                if (b > 0) sb.Append(" | ");
                sb.Append('[');
                for (int k = 0; k < buckets[b].Count; k++)
                {
                    if (k > 0) sb.Append(',');
                    sb.Append(buckets[b][k]);
                }
                sb.Append(']');
            }
            return sb.ToString();
        }

        // ═══════════════════════════════ 4) 真实端到端 ═══════════════════════════════

        [SelfTest("Blender 后端：真实端到端（Blender 4.0，生成 GLB → 减面 → 校验产物）")]
        public static void EndToEndBlender40(SelfTestContext ctx) { EndToEnd(ctx, Ver40); }

        [SelfTest("Blender 后端：真实端到端（Blender 4.1，生成 GLB → 减面 → 校验产物）")]
        public static void EndToEndBlender41(SelfTestContext ctx) { EndToEnd(ctx, Ver41); }

        private static void EndToEnd(SelfTestContext ctx, string wanted)
        {
            string blender = FindBlenderForVersion(ctx, wanted);
            if (blender == null)
            {
                ctx.Log("SKIP 未找到 Blender " + wanted + "，跳过真实端到端用例（不算失败）");
                return;
            }
            string ver = BlenderLocator.ProbeVersion(blender, 25000);
            ctx.Assert(ver != null && ver.StartsWith("Blender " + wanted, StringComparison.Ordinal),
                       "ProbeVersion 应返回 Blender " + wanted + "，实际=" + (ver ?? "(null)"));
            ctx.Log("使用 " + blender + "（" + ver + "）");

            int baseline = CountBlenderProcesses();
            string dir = ctx.NewDir("e2e_" + wanted.Replace('.', '_'));
            string src = Path.Combine(dir, "src_" + wanted.Replace('.', '_') + ".glb");
            GenerateTestGlb(ctx, blender, src, false);
            int srcTris = CountGlbTriangles(src);
            ctx.Assert(srcTris > 1000, "测试 GLB 应有上千个三角面，实际=" + srcTris);
            ctx.Log("测试模型：" + Path.GetFileName(src) + "  " + srcTris + " 三角面  " + new FileInfo(src).Length + " 字节");

            long weight = new FileInfo(src).Length;
            var specs = new List<BlenderJobSpec>
            {
                new BlenderJobSpec { Source = src, Destination = Path.Combine(dir, "lod1.glb"), Ratio = 0.5, ShrinkInner = 0.0009, MinFaces = 12, GuardPercent = 35, Weight = weight },
                new BlenderJobSpec { Source = src, Destination = Path.Combine(dir, "lod2.glb"), Ratio = 0.2, ShrinkInner = 0.001035, MinFaces = 12, GuardPercent = 35, Weight = weight, QualityOverride = false },
                // 第 3 个任务输出与第 1 个相同：应被 Dst 去重（不执行），直接复用第 1 个的结果
                new BlenderJobSpec { Source = src, Destination = Path.Combine(dir, "lod1.glb"), Ratio = 0.9, MinFaces = 12, GuardPercent = 35, Weight = weight },
            };

            var sync = new object();
            var logs = new List<string>();
            var done = new List<KeyValuePair<int, BlenderJobResult>>();
            var progress = new List<KeyValuePair<int, int>>();
            var options = new BlenderRunOptions
            {
                BlenderPath = blender,
                Workers = 2,          // 两个任务、两个进程：同时覆盖多进程并行路径
                QualityFirst = true,
                TaskTimeoutMinutes = 3,
                Log = delegate (string s) { lock (sync) logs.Add(s); },
                OnJobCompleted = delegate (int i, BlenderJobResult r) { lock (sync) done.Add(new KeyValuePair<int, BlenderJobResult>(i, r)); }
            };
            var results = RunAllWithWatchdog(ctx, specs, options, CancellationToken.None,
                delegate (int d, int t) { lock (sync) progress.Add(new KeyValuePair<int, int>(d, t)); }, 240000);

            ctx.AssertEqual(specs.Count, results.Count, "结果数量应与任务数一致");
            AssertJobOutputs(ctx, specs, results);

            // Dst 去重：第 3 个任务与第 1 个输出相同 → 不执行、复用结果
            ctx.AssertEqual(results[0].BeforeTriangles, results[2].BeforeTriangles, "follower 的 before 应复用 owner 的结果");
            ctx.AssertEqual(results[0].AfterTriangles, results[2].AfterTriangles, "follower 的 after 应复用 owner 的结果");
            ctx.Assert(results[2].Ok, "follower 任务应成功（复用结果）");
            bool sawDedup = false;
            foreach (var line in logs)
                if (line != null && line.IndexOf("输出路径重复", StringComparison.Ordinal) >= 0) { sawDedup = true; break; }
            ctx.Assert(sawDedup, "日志应提示跳过了重复输出任务");
            bool sawUnique2 = false;
            foreach (var line in logs)
                if (line != null && line.IndexOf("唯一任务 2 个", StringComparison.Ordinal) >= 0) { sawUnique2 = true; break; }
            ctx.Assert(sawUnique2, "日志应说明实际只执行 2 个唯一任务（3 个任务里有 1 个重复输出）");

            // 质量模式（0.5）应比速度模式（0.2）保留更多面；两者都应显著少于源面数
            ctx.Assert(results[0].BeforeTriangles == srcTris,
                       "before 应等于源 GLB 三角面数 " + srcTris + "，实际=" + results[0].BeforeTriangles);
            ctx.Assert(results[0].AfterTriangles > results[1].AfterTriangles,
                       "ratio 0.5 的产物应比 0.2 面数多（实际 " + results[0].AfterTriangles + " vs " + results[1].AfterTriangles + "）");
            ctx.Assert(results[0].AfterTriangles < srcTris / 2 + srcTris / 10, "ratio 0.5 的产物应明显少于源面数");
            ctx.Assert(results[0].Blocks >= 3, "质量模式应统计到至少 3 个连通块（2 个在合并对象里 + 1 个内层部件），实际=" + results[0].Blocks);
            ctx.Assert(results[0].AppliedBlocks >= 1, "质量模式应至少简化 1 个连通块");
            ctx.Assert(results[1].AppliedBlocks >= 1, "速度模式也应报告简化块数");

            // 回调次数 / 顺序 / 进度
            ctx.AssertEqual(specs.Count, progress.Count, "onProgress 次数应等于任务数");
            var doneValues = new List<int>();
            foreach (var kv in progress)
            {
                ctx.AssertEqual(specs.Count, kv.Value, "onProgress 的 total 应为任务数");
                doneValues.Add(kv.Key);
            }
            doneValues.Sort();
            for (int i = 0; i < doneValues.Count; i++)
                ctx.AssertEqual(i + 1, doneValues[i], "onProgress 的 done 应覆盖 1..任务数 且不重复");
            ctx.AssertEqual(specs.Count, done.Count, "OnJobCompleted 次数应等于任务数");
            var idxSeen = new bool[specs.Count];
            foreach (var kv in done)
            {
                ctx.Assert(kv.Key >= 0 && kv.Key < specs.Count, "回调索引越界：" + kv.Key);
                ctx.Assert(!idxSeen[kv.Key], "同一任务不应回调两次：" + kv.Key);
                idxSeen[kv.Key] = true;
            }
            int workersStarted = 0;
            for (int w = 1; w <= 2; w++)
            {
                bool seen2 = false;
                foreach (var line in logs)
                    if (line != null && line.IndexOf("[并行 " + w + "/2]", StringComparison.Ordinal) >= 0) { seen2 = true; break; }
                if (seen2) workersStarted++;
            }
            ctx.AssertEqual(2, workersStarted, "两个 worker 都应启动（多进程并行路径）");
            bool sawBlenderVer = false;
            foreach (var line in logs) if (line != null && line.IndexOf("BLENDER=" + wanted, StringComparison.Ordinal) >= 0) { sawBlenderVer = true; break; }
            ctx.Assert(sawBlenderVer, "调度日志应包含 Blender 版本行（BLENDER=" + wanted + "）");

            AssertNoResidualBlender(ctx, baseline, "端到端结束后");
        }

        // ═══════════════════════════════ 5) 取消 ═══════════════════════════════

        [SelfTest("Blender 后端：取消（KillAll 立即杀进程、未完成标已取消、无残留进程）")]
        public static void KillAllCancel(SelfTestContext ctx)
        {
            string blender = FindAnyBlender(ctx);
            if (blender == null)
            {
                ctx.Log("SKIP 未找到 Blender，跳过取消用例（不算失败）");
                return;
            }
            int baseline = CountBlenderProcesses();
            string dir = ctx.NewDir("cancel");
            string src = Path.Combine(dir, "src_big.glb");
            GenerateTestGlb(ctx, blender, src, true);
            long weight = new FileInfo(src).Length;
            int srcTris = CountGlbTriangles(src);
            ctx.Log("取消用例模型：" + srcTris + " 三角面，" + weight + " 字节");

            // 单个 Blender 进程顺序跑 6 个质量模式任务：第一个完成后立即 KillAll，后面必然未完成
            var specs = new List<BlenderJobSpec>();
            for (int i = 0; i < 6; i++)
            {
                specs.Add(new BlenderJobSpec
                {
                    Source = src,
                    Destination = Path.Combine(dir, "lod" + (i + 1) + ".glb"),
                    Ratio = 0.35,
                    ShrinkInner = 0.0009,
                    MinFaces = 12,
                    GuardPercent = 35,
                    Weight = weight
                });
            }

            var sync = new object();
            var completed = new List<int>();
            var options = new BlenderRunOptions
            {
                BlenderPath = blender,
                Workers = 1,
                QualityFirst = true,
                TaskTimeoutMinutes = 3,
                Log = delegate (string s) { },
                OnJobCompleted = delegate (int i, BlenderJobResult r) { lock (sync) completed.Add(i); }
            };

            List<BlenderJobResult> results = null;
            Exception runError = null;
            int progressCalls = 0;
            var th = new Thread(delegate ()
            {
                try
                {
                    results = BlenderScheduler.RunAll(specs, options, CancellationToken.None,
                        delegate (int d, int t) { Interlocked.Increment(ref progressCalls); });
                }
                catch (Exception ex) { runError = ex; }
            });
            th.IsBackground = true;
            th.Name = "BlenderCancelTest";
            th.Start();

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 120000)
            {
                lock (sync) if (completed.Count >= 1) break;
                if (!th.IsAlive) break;
                Thread.Sleep(20);
            }
            int beforeKill;
            lock (sync) beforeKill = completed.Count;
            ctx.Log("KillAll 前已完成 " + beforeKill + " 个任务（已用 " + sw.ElapsedMilliseconds + " ms）");
            BlenderScheduler.KillAll();

            ctx.Assert(th.Join(60000), "KillAll 后 RunAll 应在 60s 内返回");
            ctx.Assert(runError == null, "RunAll 不应抛异常：" + (runError == null ? "" : runError.ToString()));
            ctx.Assert(results != null, "RunAll 应返回结果列表");
            ctx.AssertEqual(specs.Count, results.Count, "取消后结果数量仍应与任务数一致");
            ctx.AssertEqual(specs.Count, progressCalls, "取消后 onProgress 仍应覆盖全部任务");

            int ok = 0, cancelled = 0;
            for (int i = 0; i < results.Count; i++)
            {
                BlenderJobResult r = results[i];
                ctx.Assert(r != null, "结果 " + i + " 不应为 null");
                if (r.Ok) ok++;
                else
                {
                    cancelled++;
                    ctx.Assert(!string.IsNullOrEmpty(r.Error), "失败结果必须有可读错误（任务 " + i + "）");
                    ctx.AssertContains(r.Error, "已取消", "KillAll 后未完成任务的错误应为「已取消」（任务 " + i + "，实际=" + r.Error + "）");
                }
            }
            ctx.Log("取消结果：成功 " + ok + "，取消/失败 " + cancelled + "（共 " + results.Count + "）");
            ctx.Assert(cancelled > 0, "KillAll 后应至少有一个任务被标记为已取消");
            ctx.Assert(ok + cancelled == results.Count, "每个任务要么成功要么已取消");

            // KillAll 后不允许再启动新进程：登记表必须清空，系统里也不能有我们留下的 blender
            AssertNoResidualBlender(ctx, baseline, "KillAll 后");
            ctx.Assert(BlenderScheduler.LiveProcessCount == 0,
                       "KillAll 后调度器进程登记表应为空，实际=" + BlenderScheduler.LiveProcessCount);
        }

        // ═══════════════════════════════ 6) 崩溃续跑 ═══════════════════════════════

        [SelfTest("Blender 后端：崩溃续跑（原生崩溃只失败当前任务、坏 GLB 单任务失败、其余成功）")]
        public static void CrashContinue(SelfTestContext ctx)
        {
            string blender = FindAnyBlender(ctx);
            if (blender == null)
            {
                ctx.Log("SKIP 未找到 Blender，跳过崩溃续跑用例（不算失败）");
                return;
            }
            int baseline = CountBlenderProcesses();
            string dir = ctx.NewDir("crash");

            string good = Path.Combine(dir, "good_src.glb");
            GenerateTestGlb(ctx, blender, good, false);
            long weight = new FileInfo(good).Length;

            // 1) 原生崩溃：环境变量让 Python 在处理 src 含 "crashme.glb" 的任务时 os.abort()
            string crashSrc = Path.Combine(dir, "crashme.glb");
            File.Copy(good, crashSrc, true);
            // 2) 坏 GLB：截断文件 → 导入抛异常 → 该任务 FAIL，进程继续
            string badSrc = Path.Combine(dir, "bad_truncated.glb");
            byte[] goodBytes = File.ReadAllBytes(good);
            int keep = Math.Min(300, goodBytes.Length / 3);
            var badBytes = new byte[keep];
            Array.Copy(goodBytes, badBytes, keep);
            File.WriteAllBytes(badSrc, badBytes);

            var specs = new List<BlenderJobSpec>
            {
                new BlenderJobSpec { Source = good, Destination = Path.Combine(dir, "ok1.glb"), Ratio = 0.6, MinFaces = 12, GuardPercent = 35, Weight = weight },
                new BlenderJobSpec { Source = crashSrc, Destination = Path.Combine(dir, "crash_out.glb"), Ratio = 0.6, MinFaces = 12, GuardPercent = 35, Weight = weight },
                new BlenderJobSpec { Source = good, Destination = Path.Combine(dir, "ok2.glb"), Ratio = 0.4, MinFaces = 12, GuardPercent = 35, Weight = weight },
                new BlenderJobSpec { Source = badSrc, Destination = Path.Combine(dir, "bad_out.glb"), Ratio = 0.5, MinFaces = 12, GuardPercent = 35, Weight = 0 },
            };

            var sync = new object();
            var done = new List<KeyValuePair<int, BlenderJobResult>>();
            var logs = new List<string>();
            int progressCalls = 0;
            var options = new BlenderRunOptions
            {
                BlenderPath = blender,
                Workers = 1,
                QualityFirst = true,
                TaskTimeoutMinutes = 3,
                Log = delegate (string s) { lock (sync) logs.Add(s); },
                OnJobCompleted = delegate (int i, BlenderJobResult r) { lock (sync) done.Add(new KeyValuePair<int, BlenderJobResult>(i, r)); }
            };

            string oldEnv = Environment.GetEnvironmentVariable("L4D2LOD_TEST_CRASH");
            List<BlenderJobResult> results;
            try
            {
                Environment.SetEnvironmentVariable("L4D2LOD_TEST_CRASH", "crashme.glb");
                results = RunAllWithWatchdog(ctx, specs, options, CancellationToken.None,
                    delegate (int d, int t) { Interlocked.Increment(ref progressCalls); }, 240000);
            }
            finally
            {
                Environment.SetEnvironmentVariable("L4D2LOD_TEST_CRASH", oldEnv);
            }

            ctx.AssertEqual(specs.Count, results.Count, "崩溃续跑后结果数量应完整");
            ctx.Assert(results != null, "RunAll 应返回结果列表");
            ctx.AssertEqual(specs.Count, done.Count, "每个任务都应回调一次");
            ctx.AssertEqual(specs.Count, progressCalls, "onProgress 应覆盖全部任务");

            // 0、2 成功（崩溃/坏文件前后的任务都被新进程续跑完成了）
            AssertJobOutputs(ctx, new List<BlenderJobSpec> { specs[0], specs[2] },
                             new List<BlenderJobResult> { results[0], results[2] });
            ctx.Assert(File.Exists(specs[2].Destination), "崩溃后续跑的任务应产出文件：" + specs[2].Destination);

            // 1 原生崩溃：错误含「崩溃」与「退出码」
            ctx.Assert(!results[1].Ok, "模拟崩溃的任务应失败");
            ctx.Log("崩溃任务错误：" + results[1].Error);
            ctx.AssertContains(results[1].Error, "崩溃", "崩溃任务的错误应说明崩溃");
            ctx.AssertContains(results[1].Error, "退出码", "崩溃任务的错误应含退出码");
            ctx.Assert(!File.Exists(specs[1].Destination), "崩溃任务不应产出文件");

            // 3 坏 GLB：单任务失败，错误可读，不含「崩溃」
            ctx.Assert(!results[3].Ok, "坏 GLB 任务应失败");
            ctx.Log("坏 GLB 任务错误：" + results[3].Error);
            ctx.Assert(!string.IsNullOrEmpty(results[3].Error), "坏 GLB 任务的错误不应为空");
            ctx.AssertNotContains(results[3].Error, "已取消", "坏 GLB 任务不应被标为已取消");

            bool sawRestart = false;
            foreach (var line in logs) if (line != null && line.IndexOf("重新启动 Blender", StringComparison.Ordinal) >= 0) { sawRestart = true; break; }
            ctx.Assert(sawRestart, "日志应出现「重新启动 Blender 继续剩余 N 个任务」");
            AssertNoResidualBlender(ctx, baseline, "崩溃续跑结束后");
        }

        // ═══════════════════════════════ 7) 看门狗空闲超时 ═══════════════════════════════

        [SelfTest("Blender 后端：看门狗（空闲超时只失败卡住的任务、剩余任务换新进程续跑）")]
        public static void WatchdogTimeout(SelfTestContext ctx)
        {
            string blender = FindAnyBlender(ctx);
            if (blender == null)
            {
                ctx.Log("SKIP 未找到 Blender，跳过看门狗用例（不算失败）");
                return;
            }
            int baseline = CountBlenderProcesses();
            string dir = ctx.NewDir("watchdog");
            string good = Path.Combine(dir, "good_src.glb");
            GenerateTestGlb(ctx, blender, good, false);
            string sleepy = Path.Combine(dir, "sleepme.glb");
            File.Copy(good, sleepy, true);   // 源名含 sleepme：后端脚本会按测试注入卡住 75s
            long weight = new FileInfo(good).Length;

            var specs = new List<BlenderJobSpec>
            {
                new BlenderJobSpec { Source = good, Destination = Path.Combine(dir, "ok1.glb"), Ratio = 0.6, MinFaces = 12, GuardPercent = 35, Weight = weight },
                new BlenderJobSpec { Source = sleepy, Destination = Path.Combine(dir, "sleep_out.glb"), Ratio = 0.6, MinFaces = 12, GuardPercent = 35, Weight = weight },
                new BlenderJobSpec { Source = good, Destination = Path.Combine(dir, "ok2.glb"), Ratio = 0.4, MinFaces = 12, GuardPercent = 35, Weight = weight },
            };

            var sync = new object();
            var logs = new List<string>();
            var options = new BlenderRunOptions
            {
                BlenderPath = blender,
                Workers = 1,
                QualityFirst = true,
                TaskTimeoutMinutes = 1,   // 最小 60s：卡住 75s 必然触发「空闲超时」
                Log = delegate (string s) { lock (sync) logs.Add(s); },
                OnJobCompleted = delegate (int i, BlenderJobResult r) { }
            };

            string oldEnv = Environment.GetEnvironmentVariable("L4D2LOD_TEST_SLEEP");
            List<BlenderJobResult> results;
            var sw = Stopwatch.StartNew();
            try
            {
                Environment.SetEnvironmentVariable("L4D2LOD_TEST_SLEEP", "75");
                results = RunAllWithWatchdog(ctx, specs, options, CancellationToken.None, null, 300000);
            }
            finally
            {
                Environment.SetEnvironmentVariable("L4D2LOD_TEST_SLEEP", oldEnv);
            }
            ctx.Log("看门狗用例耗时 " + (sw.ElapsedMilliseconds / 1000) + " 秒（空闲超时阈值 60s，模拟卡住 75s）");

            ctx.AssertEqual(specs.Count, results.Count, "看门狗超时后结果数量应完整");
            AssertJobOutputs(ctx, new List<BlenderJobSpec> { specs[0], specs[2] },
                             new List<BlenderJobResult> { results[0], results[2] });
            ctx.Assert(!results[1].Ok, "卡住的任务应因空闲超时失败");
            ctx.Log("超时任务错误：" + results[1].Error);
            ctx.AssertContains(results[1].Error, "超时", "超时任务的错误应说明超时");
            ctx.AssertContains(results[1].Error, "已终止进程", "超时任务的错误应说明已终止进程");
            bool sawRestart = false;
            foreach (var line in logs)
                if (line != null && line.IndexOf("重新启动 Blender", StringComparison.Ordinal) >= 0) { sawRestart = true; break; }
            ctx.Assert(sawRestart, "超时后应换新进程继续剩余任务");
            AssertNoResidualBlender(ctx, baseline, "看门狗超时结束后");
        }

        // ═══════════════════════════════ 辅助：真实 Blender ═══════════════════════════════

        /// <summary>按版本号（"4.0"/"4.1"）找本机 blender.exe；找不到返回 null</summary>
        private static string FindBlenderForVersion(SelfTestContext ctx, string wanted)
        {
            string envName = "L4D2LOD_BLENDER_" + wanted.Replace(".", "_");
            string env = Environment.GetEnvironmentVariable(envName);
            var cands = new List<string>();
            if (!string.IsNullOrEmpty(env)) cands.Add(env);
            // 用与 BlenderLocator.Find 相同的方式发现候选目录，再按版本过滤
            foreach (var root in new[] { @"C:\Program Files\Blender Foundation", @"D:\", @"C:\Program Files", @"D:\Program Files\Blender Foundation" })
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var d in Directory.GetDirectories(root, "Blender*"))
                    {
                        if (Path.GetFileName(d).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                            cands.Add(Path.Combine(d, "blender.exe"));
                    }
                }
                catch { }
            }
            // 本机已知固定位置（Parallels/手动安装常见）
            cands.Add(@"D:\Blender " + wanted + @"\blender.exe");
            cands.Add(@"D:\blender " + wanted + @"\blender.exe");
            cands.Add(@"C:\Program Files\Blender Foundation\Blender " + wanted + @"\blender.exe");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cands)
            {
                if (string.IsNullOrEmpty(c)) continue;
                string full;
                try { full = Path.GetFullPath(c); } catch { continue; }
                if (!seen.Add(full)) continue;
                bool exists;
                try { exists = File.Exists(full); } catch { exists = false; }
                if (!exists) continue;
                string ver = BlenderLocator.ProbeVersion(full, 25000);
                if (ver != null && ver.StartsWith("Blender " + wanted, StringComparison.Ordinal))
                {
                    ctx.Log("Blender " + wanted + " 候选：" + full + "（" + ver + "）");
                    return full;
                }
            }
            return null;
        }

        /// <summary>找任意可用 Blender（优先 4.1，再 4.0，再自动查找）</summary>
        private static string FindAnyBlender(SelfTestContext ctx)
        {
            string b = FindBlenderForVersion(ctx, Ver41) ?? FindBlenderForVersion(ctx, Ver40);
            if (b != null) return b;
            string found = BlenderLocator.Find(null);
            if (found != null && BlenderLocator.IsUsable(found)) { ctx.Log("使用自动发现的 Blender：" + found); return found; }
            return null;
        }

        /// <summary>用 Blender 自己生成一个测试 GLB（多连通块 + 名字含 body 的内层部件）</summary>
        private static void GenerateTestGlb(SelfTestContext ctx, string blender, string dst, bool big)
        {
            string dir = Path.GetDirectoryName(dst);
            Directory.CreateDirectory(dir);
            string script = Path.Combine(dir, big ? "gen_big.py" : "gen.py");
            File.WriteAllText(script, big ? GenBigPython : GenPython, new UTF8Encoding(false));

            string stdout, stderr;
            int code = RunProcessCapture(blender,
                "--background --factory-startup --python-exit-code 2 --python \"" + script + "\" -- \"" + dst + "\"",
                180000, out stdout, out stderr);
            ctx.Assert(code == 0, "生成测试 GLB 失败（exit=" + code + "）\nstdout:\n" + Tail(stdout, 20) + "\nstderr:\n" + Tail(stderr, 20));
            ctx.Assert(File.Exists(dst), "生成测试 GLB 后文件应存在：" + dst);
            long len = new FileInfo(dst).Length;
            ctx.Assert(len > 1000, "生成测试 GLB 应有内容，实际 " + len + " 字节");
            string genLine = LastLineWith(stdout, "GEN ");
            if (genLine != null) ctx.Log(genLine.Trim());
        }

        private static string LastLineWith(string text, string marker)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string best = null;
            foreach (var l in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (l.IndexOf(marker, StringComparison.Ordinal) >= 0) best = l;
            return best;
        }

        private static string Tail(string text, int lines)
        {
            if (string.IsNullOrEmpty(text)) return "(空)";
            string[] all = text.Split(new[] { '\n' });
            int start = Math.Max(0, all.Length - lines);
            var sb = new StringBuilder();
            for (int i = start; i < all.Length; i++) sb.AppendLine(all[i]);
            return sb.ToString();
        }

        /// <summary>跑一个 Blender 工具进程并捕获 stdout/stderr（异步读防死锁）</summary>
        private static int RunProcessCapture(string exe, string args, int timeoutMs, out string stdout, out string stderr)
        {
            stdout = "";
            stderr = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var p = new Process { StartInfo = psi })
                {
                    var so = new StringBuilder();
                    var se = new StringBuilder();
                    p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (se) se.AppendLine(e.Data); };
                    p.Start();
                    JobObject.Assign(p);
                    BlenderScheduler.TrackProcess(p);
                    try { p.BeginOutputReadLine(); p.BeginErrorReadLine(); } catch { }
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { if (!p.HasExited) p.Kill(); } catch { }
                        p.WaitForExit(5000);
                    }
                    p.WaitForExit();
                    int code;
                    try { code = p.ExitCode; } catch { code = int.MinValue; }
                    lock (so) stdout = so.ToString();
                    lock (se) stderr = se.ToString();
                    BlenderScheduler.UntrackProcess(p);
                    return code;
                }
            }
            catch (Exception ex)
            {
                stderr = "启动失败: " + ex.Message;
                return int.MinValue;
            }
        }

        /// <summary>在后台线程跑 RunAll，超时则 KillAll 并让用例失败（避免整个自测挂死）</summary>
        private static List<BlenderJobResult> RunAllWithWatchdog(SelfTestContext ctx, IReadOnlyList<BlenderJobSpec> specs,
            BlenderRunOptions options, CancellationToken cancel, Action<int, int> onProgress, int watchdogMs)
        {
            List<BlenderJobResult> results = null;
            Exception error = null;
            var th = new Thread(delegate ()
            {
                try { results = BlenderScheduler.RunAll(specs, options, cancel, onProgress); }
                catch (Exception ex) { error = ex; }
            });
            th.IsBackground = true;
            th.Name = "BlenderRunAllTest";
            th.Start();
            if (!th.Join(watchdogMs))
            {
                BlenderScheduler.KillAll();
                th.Join(20000);
                ctx.Assert(false, "RunAll 超过 " + (watchdogMs / 1000) + " 秒未返回，已 KillAll（疑似卡死）");
            }
            if (error != null) throw new Exception("RunAll 抛出异常: " + error, error);
            return results;
        }

        /// <summary>断言每个任务都成功且产物合理（文件存在、after&lt;before、GLB 里三角形数对得上）</summary>
        private static void AssertJobOutputs(SelfTestContext ctx, IList<BlenderJobSpec> specs, IList<BlenderJobResult> results)
        {
            for (int i = 0; i < specs.Count; i++)
            {
                BlenderJobSpec spec = specs[i];
                BlenderJobResult r = results[i];
                string label = "任务" + (i + 1) + "(" + Path.GetFileName(spec.Destination) + ")";
                ctx.Assert(r != null, label + " 结果不应为 null");
                ctx.Assert(r.Ok, label + " 应成功，错误=" + (r.Error ?? "(null)"));
                ctx.Assert(string.IsNullOrEmpty(r.Error), label + " 成功时不应有错误");
                ctx.Assert(r.BeforeTriangles > 0, label + " before 面数应 > 0");
                ctx.Assert(r.AfterTriangles > 0, label + " after 面数应 > 0");
                ctx.Assert(r.AfterTriangles < r.BeforeTriangles, label + " after 应小于 before（" + r.BeforeTriangles + " → " + r.AfterTriangles + "）");
                ctx.Assert(r.TargetTriangles > 0, label + " target 应 > 0");
                ctx.Assert(File.Exists(spec.Destination), label + " 输出文件应存在：" + spec.Destination);
                long len = new FileInfo(spec.Destination).Length;
                ctx.Assert(len > 100, label + " 输出 GLB 应非空，实际 " + len + " 字节");
                int exported = CountGlbTriangles(spec.Destination);
                ctx.Assert(exported > 0, label + " 导出的 GLB 应能数出三角形，实际=" + exported);
                // Blender 场景里的多边形数 vs 导出 GLB 的三角形数：允许极小的退化面差异（<1%）
                ctx.Assert(exported <= r.AfterTriangles, label + " 导出三角形数不应多于 Blender 统计（" + exported + " > " + r.AfterTriangles + "）");
                ctx.Assert(exported >= r.AfterTriangles - Math.Max(2, r.AfterTriangles / 100),
                           label + " 导出三角形数与 Blender 统计偏差过大（导出 " + exported + "，统计 " + r.AfterTriangles + "）");
                ctx.Log(label + "  ratio=" + spec.Ratio.ToString("0.###", CultureInfo.InvariantCulture) +
                        "  " + r.BeforeTriangles + " -> " + r.AfterTriangles +
                        "（目标 " + r.TargetTriangles + "，连通块 " + r.Blocks + "，简化块 " + r.AppliedBlocks + "，" +
                        len + " 字节）" + (string.IsNullOrEmpty(r.Warning) ? "" : "  警告:" + r.Warning));
            }
        }

        // ── GLB 三角形数（不依赖 Engine/Model 模块，自己读 glTF 的 JSON chunk） ──

        private static int CountGlbTriangles(string path)
        {
            try
            {
                byte[] b = File.ReadAllBytes(path);
                if (b.Length < 20) return -1;
                if (b[0] != (byte)'g' || b[1] != (byte)'l' || b[2] != (byte)'T' || b[3] != (byte)'F') return -1;
                int pos = 12;
                while (pos + 8 <= b.Length)
                {
                    int len = BitConverter.ToInt32(b, pos);
                    int type = BitConverter.ToInt32(b, pos + 4);
                    pos += 8;
                    if (len < 0 || pos + len > b.Length) return -1;
                    if (type == 0x4E4F534A)   // "JSON"
                    {
                        string jsonText = Encoding.UTF8.GetString(b, pos, len);
                        Dictionary<string, object> root = Json.ParseObjectSafe(jsonText);
                        if (root == null) return -1;
                        object accessorsObj, meshesObj;
                        if (!root.TryGetValue("accessors", out accessorsObj) || !root.TryGetValue("meshes", out meshesObj)) return 0;
                        var accessors = accessorsObj as List<object>;
                        var meshes = meshesObj as List<object>;
                        if (accessors == null || meshes == null) return 0;
                        int tris = 0;
                        foreach (object m in meshes)
                        {
                            var md = m as Dictionary<string, object>;
                            if (md == null) continue;
                            object primsObj;
                            if (!md.TryGetValue("primitives", out primsObj)) continue;
                            var prims = primsObj as List<object>;
                            if (prims == null) continue;
                            foreach (object pr in prims)
                            {
                                var pd = pr as Dictionary<string, object>;
                                if (pd == null) continue;
                                int mode = Json.GetInt(pd, "mode", 4);
                                if (mode != 4) continue;    // 只数三角形图元
                                object idxObj;
                                if (!pd.TryGetValue("indices", out idxObj)) continue;
                                int accIdx = ToInt(idxObj);
                                if (accIdx < 0 || accIdx >= accessors.Count) continue;
                                var acc = accessors[accIdx] as Dictionary<string, object>;
                                if (acc == null) continue;
                                tris += Json.GetInt(acc, "count") / 3;
                            }
                        }
                        return tris;
                    }
                    pos += len;
                }
            }
            catch { return -1; }
            return -1;
        }

        private static int ToInt(object v)
        {
            if (v == null) return -1;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return -1; }
        }

        // ── 进程残留检查 ──

        private static int CountBlenderProcesses()
        {
            try { return Process.GetProcessesByName("blender").Length; }
            catch { return -1; }
        }

        /// <summary>只统计「本进程启动的」blender（父进程 = 当前进程）；
        /// 多人/多模块并行跑自测时，别的 blender 实例不应影响本用例结论。</summary>
        private static int CountOwnedBlenderProcesses()
        {
            int mine = Process.GetCurrentProcess().Id;
            int n = 0;
            Process[] all;
            try { all = Process.GetProcessesByName("blender"); }
            catch { return -1; }
            foreach (var p in all)
            {
                try
                {
                    if (GetParentProcessId(p) == mine) n++;
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
            return n;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

        /// <summary>取父进程 PID；失败返回 -1</summary>
        private static int GetParentProcessId(Process p)
        {
            try
            {
                var pbi = new PROCESS_BASIC_INFORMATION();
                int len;
                int status = NtQueryInformationProcess(p.Handle, 0, ref pbi,
                    Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)), out len);
                if (status != 0) return -1;
                long pid = pbi.InheritedFromUniqueProcessId.ToInt64();
                return pid > int.MaxValue ? -1 : (int)pid;
            }
            catch { return -1; }
        }

        /// <summary>本次自测过程中是否观察到「不是本进程启动的」blender 实例
        /// （多人/多模块并行跑自测时会出现；此时不能断言全局计数为 0，否则误报）</summary>
        private static bool _foreignBlenderSeen;

        /// <summary>
        /// 断言没有残留 Blender：
        ///  - 调度器内部登记表必须为空（权威：本进程启动的进程都已退出）；
        ///  - 父进程 = 本进程的 blender 进程数必须为 0（权威，不受并行自测/用户实例干扰）；
        ///  - 用例开始前系统里本来没有 blender、且全程没见过外部 blender 时，额外要求全局计数也为 0（最严格）。
        /// </summary>
        private static void AssertNoResidualBlender(SelfTestContext ctx, int baseline, string when)
        {
            var sw = Stopwatch.StartNew();
            while (BlenderScheduler.LiveProcessCount > 0 && sw.ElapsedMilliseconds < 10000) Thread.Sleep(50);

            int owned = CountOwnedBlenderProcesses();
            var sw2 = Stopwatch.StartNew();
            while (owned != 0 && sw2.ElapsedMilliseconds < 10000)
            {
                Thread.Sleep(50);
                owned = CountOwnedBlenderProcesses();
            }

            int live = BlenderScheduler.LiveProcessCount;
            int sys = CountBlenderProcesses();
            if (sys > owned && !_foreignBlenderSeen)
            {
                _foreignBlenderSeen = true;
                ctx.Log("提示：检测到不是本进程启动的 blender 实例（" + (sys - owned) +
                        " 个，可能是并行运行的其他自测/用户实例），后续不再断言全局计数为 0");
            }
            ctx.Log(when + "残留检查：调度器登记=" + live + "，本进程启动的 blender=" + owned +
                    "，系统 blender=" + sys + "（用例开始时 " + baseline + "）");
            ctx.Assert(live == 0, when + "调度器进程登记表应为空，实际=" + live);
            ctx.Assert(owned == 0, when + "本进程启动的 blender 进程应全部退出，实际=" + owned);
            if (baseline == 0 && !_foreignBlenderSeen)
                ctx.Assert(sys == 0, when + "用例开始前系统没有 blender，结束时全局计数应为 0，实际=" + sys);
        }

        // ═══════════════════════════════ 辅助：生成 GLB 的 Python ═══════════════════════════════

        private const string GenPython =
@"import bpy, sys, os
argv = sys.argv
argv = argv[argv.index('--') + 1:] if '--' in argv else []
out = argv[0]
bpy.ops.wm.read_factory_settings(use_empty=True)
# 主对象：icosphere + cylinder join 成一个对象（=> 2 个松散连通块，触发 separate(LOOSE) 逐块预算路径）
bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=3, radius=1.0, location=(0.0, 0.0, 0.0))
main = bpy.context.active_object
main.name = 'Mesh_main'
bpy.ops.mesh.primitive_cylinder_add(vertices=64, radius=0.4, depth=2.0, location=(3.0, 0.0, 0.0))
bpy.ops.object.select_all(action='DESELECT')
for ob in list(bpy.context.scene.objects):
    if ob.type == 'MESH':
        ob.select_set(True)
bpy.context.view_layer.objects.active = main
bpy.ops.object.join()
bpy.context.active_object.name = 'Mesh_main'
# 内层部件（名字含 body）：闭合环面，验证内缩路径且不产生退化面
bpy.ops.mesh.primitive_torus_add(major_segments=64, minor_segments=12, location=(0.0, 0.0, 1.6))
bpy.context.active_object.name = 'Body_inner'
bpy.ops.export_scene.gltf(filepath=out, export_format='GLB', use_selection=False, export_apply=False)
tris = 0
for ob in bpy.context.scene.objects:
    if ob.type == 'MESH':
        tris += sum(len(p.vertices) - 2 for p in ob.data.polygons)
print('GEN objects=%d tris=%d size=%d' % (len([o for o in bpy.context.scene.objects if o.type == 'MESH']), tris, os.path.getsize(out)), flush=True)
";

        private const string GenBigPython =
@"import bpy, sys, os
argv = sys.argv
argv = argv[argv.index('--') + 1:] if '--' in argv else []
out = argv[0]
bpy.ops.wm.read_factory_settings(use_empty=True)
# 取消/超时用例用的大模型：约 2.3 万三角面、多个连通块
bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=5, radius=1.0, location=(0.0, 0.0, 0.0))
main = bpy.context.active_object
main.name = 'Mesh_main'
bpy.ops.mesh.primitive_torus_add(major_segments=48, minor_segments=12, location=(3.0, 0.0, 0.0))
bpy.ops.object.select_all(action='DESELECT')
for ob in list(bpy.context.scene.objects):
    if ob.type == 'MESH':
        ob.select_set(True)
bpy.context.view_layer.objects.active = main
bpy.ops.object.join()
bpy.context.active_object.name = 'Mesh_main'
bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=3, radius=0.5, location=(0.0, 0.0, 1.6))
bpy.context.active_object.name = 'Body_inner'
bpy.ops.export_scene.gltf(filepath=out, export_format='GLB', use_selection=False, export_apply=False)
tris = 0
for ob in bpy.context.scene.objects:
    if ob.type == 'MESH':
        tris += sum(len(p.vertices) - 2 for p in ob.data.polygons)
print('GEN objects=%d tris=%d size=%d' % (len([o for o in bpy.context.scene.objects if o.type == 'MESH']), tris, os.path.getsize(out)), flush=True)
";
    }
}
