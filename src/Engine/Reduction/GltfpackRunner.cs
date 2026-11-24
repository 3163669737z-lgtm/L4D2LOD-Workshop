using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using LodStudio.Engine.Platform;

namespace LodStudio.Engine
{
    /// <summary>
    /// 调用内置 gltfpack 做分连通块减面，并保留部件边界与轮廓支撑点。
    /// 按输出路径去重，各进程独立超时。单任务失败后继续处理其余任务。
    /// </summary>
    public static class GltfpackRunner
    {
        // 这里按文件面数制定目标，原生后端再为每个连通块分配预算。
        public const double ShapeRefFaces = 4000.0;    // >= 4000 面的部件按原目标比例减
        public const double ShapeMinWeight = 0.15;     // 最小减面权重
        public const string ShapeErrorFine = "0.01";   // LOD1/2：误差 <= 约 1% 包围盒
        public const string ShapeErrorMid = "0.012";
        public const string ShapeErrorCoarse = "0.015";
        public const double WarnOverRatio = 1.5;       // 实际面数超过目标 50% 以上时告警

        /// <summary>面数越少，减面权重按 sqrt(before/REF) 下降。</summary>
        public static double ShapeFirstRatio(int beforeTriangles, double requestedRatio)
        {
            double b = beforeTriangles;
            double r = requestedRatio;
            if (b <= 0 || r >= 0.995 || r <= 0.0) return r;
            double w = Math.Sqrt(b / ShapeRefFaces);
            w = Math.Min(1.0, Math.Max(ShapeMinWeight, w));
            double eff = 1.0 - (1.0 - r) * w;
            return Math.Min(1.0, Math.Max(r, eff));
        }

        /// <summary>Farther tiers give dense meshes more room to search for valid reductions.</summary>
        public static double AdaptiveRatio(int beforeTriangles, double requestedRatio)
        {
            double density = Math.Max(0.0, Math.Min(1.0, Math.Log(Math.Max(1, beforeTriangles) / 4096.0, 2) / 2));
            double distance = Math.Max(0.0, Math.Min(1.0, (0.58 - requestedRatio) / 0.35));
            return Math.Pow(requestedRatio, 1 + 0.65 * density * distance);
        }

        /// <summary>按目标保留比例选择归一化误差上限。</summary>
        public static string ShapeErrorForRatio(double requestedRatio)
        {
            if (requestedRatio <= 0.35) return ShapeErrorCoarse;
            if (requestedRatio <= 0.50) return ShapeErrorMid;
            return ShapeErrorFine;
        }

        public sealed class RunOptions
        {
            /// <summary>0 = 自动，按 CPU、空闲内存和任务数选择。</summary>
            public int Workers;
            public bool QualityFirst = true;
            public bool AdaptiveTriangleBudget = true;
            public bool AllowVertexUpdates;
            public bool OmitUnchangedOutputs;
            /// <summary>单任务进程超时基础分钟数；大文件按 ReductionScheduler.ResolveTimeoutMs 自动放宽</summary>
            public int TaskTimeoutMinutes = 20;
            /// <summary>引擎级日志（会转发到 IBatchObserver）</summary>
            public Action<string> Log;
            /// <summary>每完成一个任务回调（索引, 结果）；可能来自任意线程</summary>
            public Action<int, ReductionResult> OnJobCompleted;
        }

        /// <summary>
        /// 跑一批减面任务。取消时正在运行的 gltfpack 进程被杀掉，未开始的任务标「已取消」；
        /// 每个 gltfpack 进程都 Assign 进 JobObject，主程序被结束也不留孤儿。
        /// </summary>
        public static List<ReductionResult> RunAll(IReadOnlyList<ReductionJob> jobs, RunOptions options,
                                                    CancellationToken cancel, Action<int, int> onProgress)
        {
            int n = jobs == null ? 0 : jobs.Count;
            var results = new ReductionResult[n];
            options = options ?? new RunOptions();
            var log = options.Log ?? (Action<string>)(delegate { });
            if (n == 0) return new List<ReductionResult>();

            string extractError;
            string exe = GltfpackTool.EnsureExtracted(out extractError);
            if (exe == null)
            {
                string reason = "无法释放内置 gltfpack：" + (extractError ?? "未知错误");
                for (int i = 0; i < n; i++) results[i] = new ReductionResult { Error = reason };
                return new List<ReductionResult>(results);
            }
            log("内置 meshoptimizer：" + exe);

            List<int>[] followers;
            var owners = ReductionScheduler.DedupOwners(jobs, out followers);
            int workers = ReductionScheduler.ResolveWorkers(options.Workers, owners.Count);
            log("并行进程：" + (options.Workers > 0 ? workers + "（手动指定）" : workers + "（自动，按核数/空闲内存）"));

            int total = n;
            int next = -1;
            int done = 0;
            var threads = new Thread[workers];
            for (int w = 0; w < workers; w++)
            {
                var th = new Thread(delegate ()
                {
                    while (true)
                    {
                        int k = Interlocked.Increment(ref next);
                        if (k >= owners.Count) return;
                        int i = owners[k];
                        ReductionResult r;
                        if (cancel.IsCancellationRequested)
                        {
                            r = new ReductionResult { Error = "已取消" };
                        }
                        else
                        {
                            try { r = ExecuteOne(jobs[i], exe, options, cancel); }
                            catch (Exception ex) { r = new ReductionResult { Error = "未预期异常：" + ex.Message }; }
                        }
                        results[i] = r;
                        try { options.OnJobCompleted?.Invoke(i, r); } catch { }
                        int d = Interlocked.Increment(ref done);
                        try { onProgress?.Invoke(d, total); } catch { }

                        // follower 直接复用 owner 结果（同一输出只执行一次）
                        var fs = followers[i];
                        if (fs != null)
                        {
                            foreach (int f in fs)
                            {
                                results[f] = CopyResult(r);
                                try { options.OnJobCompleted?.Invoke(f, results[f]); } catch { }
                                int df = Interlocked.Increment(ref done);
                                try { onProgress?.Invoke(df, total); } catch { }
                            }
                        }
                    }
                });
                th.IsBackground = true;
                th.Name = "GltfpackWorker-" + (w + 1);
                threads[w] = th;
                th.Start();
            }
            foreach (var th in threads) th.Join();
            return new List<ReductionResult>(results);
        }

        private static ReductionResult CopyResult(ReductionResult r)
        {
            return new ReductionResult
            {
                Ok = r.Ok,
                BeforeTriangles = r.BeforeTriangles,
                AfterTriangles = r.AfterTriangles,
                TargetTriangles = r.TargetTriangles,
                BudgetSourceTriangles = r.BudgetSourceTriangles,
                BudgetTargetTriangles = r.BudgetTargetTriangles,
                BalancedRegions = r.BalancedRegions,
                MaximumRegionalSpread = r.MaximumRegionalSpread,
                MaximumAdjacentRegionalSpread = r.MaximumAdjacentRegionalSpread,
                RetriedComponents = r.RetriedComponents,
                RetainedComponents = r.RetainedComponents,
                ProtectedPrimitives = r.ProtectedPrimitives,
                VertexUpdateTrials = r.VertexUpdateTrials,
                VertexUpdateAccepted = r.VertexUpdateAccepted,
                VertexUpdateTrianglesSaved = r.VertexUpdateTrianglesSaved,
                WholePrimitiveTrianglesSaved = r.WholePrimitiveTrianglesSaved,
                Warning = r.Warning,
                Error = r.Error
            };
        }

        private static ReductionResult ExecuteOne(ReductionJob job, string exe, RunOptions options, CancellationToken cancel)
        {
            var r = new ReductionResult();
            string src = job.Source;
            string dst = job.Destination;
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) { r.Error = "源文件不存在：" + (src ?? "(空)"); return r; }
            if (string.IsNullOrEmpty(dst)) { r.Error = "缺少输出路径"; return r; }

            int before;
            try { before = GlbCodec.CountTriangles(src); }
            catch (Exception ex) { r.Error = "读取源 GLB 失败：" + ex.Message; return r; }
            if (before <= 0) { r.Error = "源 GLB 三角面数为 0"; return r; }

            double effRatio = options.AdaptiveTriangleBudget
                ? AdaptiveRatio(before, job.Ratio)
                : ShapeFirstRatio(before, job.Ratio);
            string se = ShapeErrorForRatio(job.Ratio);
            if (!options.QualityFirst)
                se = (double.Parse(se, CultureInfo.InvariantCulture) * 1.5).ToString("0.####", CultureInfo.InvariantCulture);
            string tmp = Path.Combine(
                Path.GetDirectoryName(dst) ?? "",
                Path.GetFileNameWithoutExtension(dst) + ".gp_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".glb");
            try
            {
                if (effRatio < 1.0)
                {
                    string output;
                    bool timedOut, cancelled;
                    int code = RunGltfpack(exe, src, tmp, effRatio, se, job, options, cancel, out output, out timedOut, out cancelled);
                    if (cancelled) { r.Error = "已取消"; return r; }
                    if (timedOut)
                    {
                        r.Error = "gltfpack 超时（超过 " +
                            (ReductionScheduler.ResolveTimeoutMs(job, options.TaskTimeoutMinutes) / 60000.0).ToString("0.#", CultureInfo.InvariantCulture) +
                            " 分钟），已终止进程";
                        return r;
                    }
                    if (code != 0) { r.Error = "gltfpack 失败（exit=" + code + "）：" + (TailOf(output, 8) ?? "(无输出)"); return r; }
                    if (!File.Exists(tmp)) { r.Error = "gltfpack 未生成输出文件：" + tmp; return r; }
                    ReadAppearanceStats(output, r);
                }

                if (cancel.IsCancellationRequested) { r.Error = "已取消"; return r; }

                int after = effRatio >= 1.0 ? before : GlbCodec.CountTriangles(tmp);
                if (after <= 0) { r.Error = "减面输出没有可用三角形：" + tmp; return r; }
                if (cancel.IsCancellationRequested) { r.Error = "已取消"; return r; }

                string dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (after >= before)
                {
                    // Keep original bytes when optimization offers no triangle savings.
                    if (!options.OmitUnchangedOutputs) FileUtil.CopyAtomic(src, dst);
                    after = before;
                }
                else if (File.Exists(dst))
                {
                    try { File.Replace(tmp, dst, null); }
                    catch (IOException) { FileUtil.CopyAtomic(tmp, dst); }
                }
                else File.Move(tmp, dst);

                r.Ok = true;
                r.BeforeTriangles = before;
                r.AfterTriangles = after;
                r.TargetTriangles = (int)Math.Round(before * effRatio);
                if (r.BudgetSourceTriangles > 0)
                {
                    int allocatable = Math.Min(before, r.BudgetSourceTriangles);
                    double retention = Math.Min(1.0, r.BudgetTargetTriangles / (double)r.BudgetSourceTriangles);
                    r.TargetTriangles = before - allocatable + (int)Math.Round(allocatable * retention);
                }
                if (effRatio < 0.99 && after >= before)
                    r.Warning = options.OmitUnchangedOutputs
                        ? "没有找到合格的减面结果，沿用源网格"
                        : "部件边界、轮廓或误差限制阻止进一步减面，已保留原面数";
                else if (r.TargetTriangles > 0 && after > r.TargetTriangles * WarnOverRatio)
                    r.Warning = string.Format(CultureInfo.InvariantCulture,
                        "实际面数 {0} 超出目标 {1} 达 {2:F0}%（gltfpack 受边界/拓扑约束提前停止）",
                        after, r.TargetTriangles, (after / (double)r.TargetTriangles - 1.0) * 100.0);
                var log = options == null ? null : options.Log;
                if (log != null)
                    log("[" + Path.GetFileName(src) + " → " + Path.GetFileName(job.DisplayDestination ?? dst) + "]  " +
                        before + " → " + after + "（目标 " + r.TargetTriangles + "，保留目标 " +
                        (r.TargetTriangles / (double)before).ToString("P0", CultureInfo.InvariantCulture) + "/se" + se + "）" +
                        AppearanceSummary(r) +
                        (r.Warning != null ? "  警告:" + r.Warning : ""));
                return r;
            }
            finally
            {
                FileUtil.TryDelete(tmp);
            }
        }

        private static string AppearanceSummary(ReductionResult result)
        {
            string text = "";
            if (result.RetriedComponents != 0 || result.RetainedComponents != 0 || result.ProtectedPrimitives != 0)
                text = "，外观重试 " + result.RetriedComponents + "，保留连通块 " + result.RetainedComponents +
                       "，受保护图元 " + result.ProtectedPrimitives;
            if (result.VertexUpdateTrials != 0)
                text += "，顶点微调 " + result.VertexUpdateAccepted + "/" + result.VertexUpdateTrials +
                        " 个区域通过，额外减少 " + result.VertexUpdateTrianglesSaved + " 面";
            if (result.WholePrimitiveTrianglesSaved > 0)
                text += "，整块候选额外减少 " + result.WholePrimitiveTrianglesSaved + " 面";
            if (result.BudgetSourceTriangles > 0)
                text += "，区域均摊调整 " + result.BalancedRegions + " 区，相邻区域最大差 " +
                        (result.MaximumAdjacentRegionalSpread / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + " 个百分点";
            return text;
        }

        private static void ReadAppearanceStats(string output, ReductionResult result)
        {
            const string appearance = "lodstudio-appearance ";
            const string vertexUpdate = "lodstudio-vertex-update ";
            const string budget = "lodstudio-density-budget ";
            const string whole = "lodstudio-whole-primitive ";
            foreach (var line in (output ?? "").Split('\n'))
            {
                if (line.StartsWith(whole, StringComparison.Ordinal))
                {
                    var counts = line.Substring(whole.Length).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (counts.Length == 2 && int.TryParse(counts[0], out int before) &&
                        int.TryParse(counts[1], out int after) && before > after && after > 0)
                        result.WholePrimitiveTrianglesSaved += before - after;
                    continue;
                }
                bool update = line.StartsWith(vertexUpdate, StringComparison.Ordinal);
                bool density = line.StartsWith(budget, StringComparison.Ordinal);
                if (!update && !density && !line.StartsWith(appearance, StringComparison.Ordinal)) continue;
                int prefixLength = update ? vertexUpdate.Length : density ? budget.Length : appearance.Length;
                string[] parts = line.Substring(prefixLength).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int first, second, third;
                if (parts.Length != (density ? 5 : 3) ||
                    !int.TryParse(parts[0], out first) || !int.TryParse(parts[1], out second) ||
                    !int.TryParse(parts[2], out third)) continue;
                if (density)
                {
                    int spread, adjacentSpread;
                    if (!int.TryParse(parts[3], out spread) || !int.TryParse(parts[4], out adjacentSpread)) continue;
                    result.BudgetSourceTriangles += first;
                    result.BudgetTargetTriangles += second;
                    result.BalancedRegions += third;
                    result.MaximumRegionalSpread = Math.Max(result.MaximumRegionalSpread, spread);
                    result.MaximumAdjacentRegionalSpread = Math.Max(result.MaximumAdjacentRegionalSpread, adjacentSpread);
                }
                else if (update)
                {
                    result.VertexUpdateTrials += first;
                    result.VertexUpdateAccepted += second;
                    result.VertexUpdateTrianglesSaved += third;
                }
                else
                {
                    result.RetriedComponents += first;
                    result.RetainedComponents += second;
                    result.ProtectedPrimitives += third;
                }
            }
        }

        /// <summary>跑一个 gltfpack 进程：捕获输出、超时/取消即杀进程；返回退出码。</summary>
        private static int RunGltfpack(string exe, string src, string dst, double effRatio, string se,
            ReductionJob job, RunOptions options, CancellationToken cancel,
            out string output, out bool timedOut, out bool cancelled)
        {
            timedOut = false;
            cancelled = false;
            string ratioText = effRatio.ToString("0.######", CultureInfo.InvariantCulture);
            string args = "-i " + Quote(src) + " -o " + Quote(dst) +
                          " -si " + ratioText + " -se " + se + " -slb -noq -kn -km -kv -ke -v";
            if (options.AdaptiveTriangleBudget)
            {
                args += " -lodb";
                if (job.Distance > 0)
                {
                    // 10–60 HU covers the usual near to far character LODs.
                    double progress = Math.Max(0, Math.Min(1, (job.Distance - 10) / 50));
                    args += " -lodp " + progress.ToString("0.######", CultureInfo.InvariantCulture);
                }
            }
            if (job.RequireFixedVertices) args += " -lodfw";
            else if (options.AllowVertexUpdates) args += " -lodvu";
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
            var so = new StringBuilder();
            var se2 = new StringBuilder();
            int exitCode = -1;
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (se2) se2.AppendLine(e.Data); };
                p.Start();
                if (!JobObject.Assign(p) && options != null && options.Log != null)
                    options.Log("[警告] 无法把 gltfpack 放入作业对象（" + JobObject.LastError + "），主程序异常退出时可能残留进程");
                ReductionScheduler.TrackProcess(p);
                try
                {
                    try { p.BeginOutputReadLine(); p.BeginErrorReadLine(); } catch { }
                    long timeoutMs = ReductionScheduler.ResolveTimeoutMs(job,
                        options == null || options.TaskTimeoutMinutes <= 0 ? 20 : options.TaskTimeoutMinutes);
                    var sw = Stopwatch.StartNew();
                    while (true)
                    {
                        if (p.WaitForExit(200)) break;
                        if (cancel.IsCancellationRequested) { TryKill(p); cancelled = true; break; }
                        if (sw.ElapsedMilliseconds > timeoutMs) { TryKill(p); timedOut = true; break; }
                    }
                    try { p.WaitForExit(); } catch { }   // 等异步输出读完
                    try { exitCode = p.ExitCode; } catch { exitCode = -1; }
                }
                finally
                {
                    ReductionScheduler.UntrackProcess(p);
                }
            }
            string a, b;
            lock (so) a = so.ToString();
            lock (se2) b = se2.ToString();
            output = a + b;
            return exitCode;
        }

        private static void TryKill(Process p)
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        private static string Quote(string path) { return "\"" + path + "\""; }

        private static string TailOf(string text, int lines)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] all = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int start = Math.Max(0, all.Length - lines);
            var sb = new StringBuilder();
            for (int i = start; i < all.Length; i++)
            {
                if (sb.Length > 0) sb.Append(" / ");
                sb.Append(all[i].Trim());
                if (sb.Length > 800) break;
            }
            return sb.ToString();
        }
    }
}
