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
    /// 无 Blender 减面后端：直接调用内置 gltfpack 做拓扑感知减面，
    /// 需要时用 <see cref="GlbInnerShrink"/> 在原始 JSON+BIN 上做 Body 内缩（不做模型级往返）。
    /// 与 Blender 后端共用 <see cref="BlenderJobSpec"/> / <see cref="BlenderJobResult"/> 契约：
    /// 按 Dst 去重、多进程并行、可取消、单任务超时、单任务失败不拖垮整批（失败继续）。
    /// 找不到内置 gltfpack 时整批报错——本后端内部绝不静默回退 Blender。
    /// 注意：minFaces / guardPercent / quality 是 Blender 连通块减面的参数，本后端不使用。
    /// </summary>
    public static class GltfpackRunner
    {
        // ── 形状优先参数：与 src/Assets/blender_backend.py（7.0.3）保持一致，改动要两边同步 ──
        public const double ShapeRefFaces = 4000.0;    // >= 4000 面的部件按原目标比例减
        public const double ShapeMinWeight = 0.15;     // 最小减面权重
        public const string ShapeErrorFine = "0.01";   // LOD1/2：误差 <= 约 1% 包围盒
        public const string ShapeErrorMid = "0.015";   // LOD3：略放宽
        public const string ShapeErrorCoarse = "0.02"; // LOD4：远景级
        public const double WarnOverRatio = 1.5;       // 实际面数超过目标 50% 以上时告警

        /// <summary>python shape_first_ratio 的 C# 版：面数越少，减面权重按 sqrt(before/REF) 下降。</summary>
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

        /// <summary>python shape_error_for_ratio 的 C# 版：按 LOD 级别选择误差上限。</summary>
        public static string ShapeErrorForRatio(double requestedRatio)
        {
            if (requestedRatio <= 0.35) return ShapeErrorCoarse;
            if (requestedRatio <= 0.50) return ShapeErrorMid;
            return ShapeErrorFine;
        }

        public sealed class RunOptions
        {
            /// <summary>0 = 自动（与 Blender 后端同一公式：min(cpu/2, 空闲GB/1.2, 8, 任务数)）</summary>
            public int Workers;
            /// <summary>单任务进程超时基础分钟数；大文件按 BlenderScheduler.ResolveTimeoutMs 自动放宽</summary>
            public int TaskTimeoutMinutes = 20;
            /// <summary>引擎级日志（会转发到 IBatchObserver）</summary>
            public Action<string> Log;
            /// <summary>每完成一个任务回调（索引, 结果）；可能来自任意线程</summary>
            public Action<int, BlenderJobResult> OnJobCompleted;
        }

        /// <summary>
        /// 跑一批减面任务。取消时正在运行的 gltfpack 进程被杀掉，未开始的任务标「已取消」；
        /// 每个 gltfpack 进程都 Assign 进 JobObject，主程序被结束也不留孤儿。
        /// </summary>
        public static List<BlenderJobResult> RunAll(IReadOnlyList<BlenderJobSpec> jobs, RunOptions options,
                                                    CancellationToken cancel, Action<int, int> onProgress)
        {
            int n = jobs == null ? 0 : jobs.Count;
            var results = new BlenderJobResult[n];
            options = options ?? new RunOptions();
            var log = options.Log ?? (Action<string>)(delegate { });
            if (n == 0) return new List<BlenderJobResult>();

            string extractError;
            string exe = GltfpackTool.EnsureExtracted(out extractError);
            if (exe == null)
            {
                string reason = "无法释放内置 gltfpack：" + (extractError ?? "未知错误");
                for (int i = 0; i < n; i++) results[i] = new BlenderJobResult { Error = reason };
                return new List<BlenderJobResult>(results);
            }
            log("gltfpack（无 Blender 后端）：" + exe);

            List<int>[] followers;
            var owners = BlenderScheduler.DedupOwners(jobs, out followers);
            int workers = BlenderScheduler.ResolveWorkers(options.Workers, owners.Count);
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
                        BlenderJobResult r;
                        if (cancel.IsCancellationRequested)
                        {
                            r = new BlenderJobResult { Error = "已取消" };
                        }
                        else
                        {
                            try { r = ExecuteOne(jobs[i], exe, options, cancel); }
                            catch (Exception ex) { r = new BlenderJobResult { Error = "未预期异常：" + ex.Message }; }
                        }
                        results[i] = r;
                        try { options.OnJobCompleted?.Invoke(i, r); } catch { }
                        int d = Interlocked.Increment(ref done);
                        try { onProgress?.Invoke(d, total); } catch { }

                        // follower 直接复用 owner 结果（同一输出不重复执行，与 Blender 后端一致）
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
            return new List<BlenderJobResult>(results);
        }

        private static BlenderJobResult CopyResult(BlenderJobResult r)
        {
            return new BlenderJobResult
            {
                Ok = r.Ok,
                BeforeTriangles = r.BeforeTriangles,
                AfterTriangles = r.AfterTriangles,
                TargetTriangles = r.TargetTriangles,
                Blocks = r.Blocks,
                AppliedBlocks = r.AppliedBlocks,
                Warning = r.Warning,
                Error = r.Error
            };
        }

        private static BlenderJobResult ExecuteOne(BlenderJobSpec job, string exe, RunOptions options, CancellationToken cancel)
        {
            var r = new BlenderJobResult();
            string src = job.Source;
            string dst = job.Destination;
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) { r.Error = "源文件不存在：" + (src ?? "(空)"); return r; }
            if (string.IsNullOrEmpty(dst)) { r.Error = "缺少输出路径"; return r; }

            int before;
            try { before = GlbCodec.CountTriangles(src); }
            catch (Exception ex) { r.Error = "读取源 GLB 失败：" + ex.Message; return r; }
            if (before <= 0) { r.Error = "源 GLB 三角面数为 0"; return r; }

            double effRatio = ShapeFirstRatio(before, job.Ratio);
            string se = ShapeErrorForRatio(job.Ratio);
            string tmp = Path.Combine(
                Path.GetDirectoryName(dst) ?? "",
                Path.GetFileNameWithoutExtension(dst) + ".gp_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".glb");
            try
            {
                string output, tail;
                bool timedOut, cancelled;
                int code = RunGltfpack(exe, src, tmp, effRatio, se, job, options, cancel, out output, out timedOut, out cancelled);
                if (cancelled) { r.Error = "已取消"; return r; }
                if (timedOut)
                {
                    r.Error = "gltfpack 超时（超过 " +
                        (BlenderScheduler.ResolveTimeoutMs(job, options.TaskTimeoutMinutes) / 60000.0).ToString("0.#", CultureInfo.InvariantCulture) +
                        " 分钟），已终止进程";
                    return r;
                }
                tail = TailOf(output, 8);
                if (code != 0) { r.Error = "gltfpack 失败（exit=" + code + "）：" + (tail ?? "(无输出)"); return r; }
                if (!File.Exists(tmp)) { r.Error = "gltfpack 未生成输出文件：" + tmp; return r; }
                long size = new FileInfo(tmp).Length;
                if (size <= 20) { r.Error = "gltfpack 输出文件异常（" + size + " 字节）：" + tmp; return r; }

                if (cancel.IsCancellationRequested) { r.Error = "已取消"; return r; }

                // Body 内缩：原始 JSON+BIN 定点修改（骨骼/权重/UV/材质/图片/其余 JSON 字段不经过模型往返）
                if (job.ShrinkInner > 0)
                {
                    try
                    {
                        byte[] data = File.ReadAllBytes(tmp);
                        var shr = GlbInnerShrink.Apply(data, job.ShrinkInner, Path.GetFileName(src));
                        if (shr.Changed) File.WriteAllBytes(tmp, shr.Data);
                    }
                    catch (Exception ex)
                    {
                        r.Error = "Body 内缩失败：" + ex.Message + "（该模型可改用 Blender 后端处理）";
                        return r;
                    }
                }

                int after;
                try { after = GlbCodec.CountTriangles(tmp); }
                catch { after = before; }

                string dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(dst)) FileUtil.TryDelete(dst);
                File.Move(tmp, dst);

                r.Ok = true;
                r.BeforeTriangles = before;
                r.AfterTriangles = after;
                r.TargetTriangles = (int)Math.Round(before * effRatio);
                r.Blocks = 0;
                r.AppliedBlocks = after < before ? 1 : 0;
                if (effRatio < 0.99 && after >= before)
                    r.Warning = "gltfpack 未减面：模型可能为硬边/分离面（每面独立顶点），已保持原面数";
                else if (r.TargetTriangles > 0 && after > r.TargetTriangles * WarnOverRatio)
                    r.Warning = string.Format(CultureInfo.InvariantCulture,
                        "实际面数 {0} 超出目标 {1} 达 {2:F0}%（gltfpack 受边界/拓扑约束提前停止）",
                        after, r.TargetTriangles, (after / (double)r.TargetTriangles - 1.0) * 100.0);
                var log = options == null ? null : options.Log;
                if (log != null)
                    log("[" + Path.GetFileName(src) + " → " + Path.GetFileName(dst) + "]  " +
                        before + " → " + after + "（目标 " + r.TargetTriangles + "，形状比 " +
                        effRatio.ToString("0.###", CultureInfo.InvariantCulture) + "/se" + se + "）" +
                        (job.ShrinkInner > 0 ? "  内缩" + job.ShrinkInner.ToString("0.#####", CultureInfo.InvariantCulture) : "") +
                        (r.Warning != null ? "  警告:" + r.Warning : ""));
                return r;
            }
            finally
            {
                FileUtil.TryDelete(tmp);
            }
        }

        /// <summary>跑一个 gltfpack 进程：捕获输出、超时/取消即杀进程；返回退出码。</summary>
        private static int RunGltfpack(string exe, string src, string dst, double effRatio, string se,
            BlenderJobSpec job, RunOptions options, CancellationToken cancel,
            out string output, out bool timedOut, out bool cancelled)
        {
            timedOut = false;
            cancelled = false;
            string ratioText = effRatio.ToString("0.######", CultureInfo.InvariantCulture);
            string args = "-i " + Quote(src) + " -o " + Quote(dst) +
                          " -si " + ratioText + " -se " + se + " -slb -noq -kn -km -kv -v";
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
                BlenderScheduler.TrackProcess(p);
                try
                {
                    try { p.BeginOutputReadLine(); p.BeginErrorReadLine(); } catch { }
                    long timeoutMs = BlenderScheduler.ResolveTimeoutMs(job,
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
                    BlenderScheduler.UntrackProcess(p);
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
