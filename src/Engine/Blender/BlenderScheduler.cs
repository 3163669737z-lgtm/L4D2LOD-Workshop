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
    /// Blender 减面任务调度器（进程调度 + stdout/jsonl 结果协议解析）。
    ///
    /// 行为（与旧版 L4D2自动LOD工具/BlenderBackend.cs 的对外语义一致）：
    ///  - Dst 去重：同一输出路径只跑一次，其余任务（follower）直接复用结果并回调；
    ///  - 按 Weight（源文件字节数）降序轮流放入当前最轻的桶，每桶一个线程、一个 Blender 进程；
    ///  - Workers=0 时自动 = min(cpu/2, 空闲物理内存GB/1.2, 8, 任务数)，内存查询失败上限 3；
    ///  - 逐任务实时解析 RESULT 回调；进程崩溃/看门狗超时只失败「当前任务」，剩余任务换新进程继续
    ///    （每轮至少消掉一个任务，必然收敛）；
    ///  - 脚本没跑起来（整轮都没看到 JOBS=）→ 剩余任务全部失败并给可读原因；
    ///  - 看门狗是「空闲超时」：距上一条 BEGIN/RESULT/JOBS= 超过 20 分钟 + 每 10MB 源文件 5 分钟
    ///    （最多 +100 分钟）才判超时，200ms 轮询；
    ///  - 取消：CancellationToken 或 KillAll()（递增 epoch），立即杀全部子进程，未完成标「已取消」，
    ///    本批次不再启动新进程；
    ///  - 每个 Blender 进程都 Assign 进 JobObject（KILL_ON_JOB_CLOSE），主程序被结束也不留孤儿；
    ///  - 进程退出后补读 &lt;task&gt;.jsonl 里没被登记过的结果（stdout 因崩溃丢行时兜底，去重登记）。
    /// 返回列表与 jobs 一一对应（同序、同数量）。
    /// </summary>
    public static class BlenderScheduler
    {
        // 协议 / 调度常量
        private const int PollMs = 200;                 // 看门狗轮询间隔
        private const int KillWaitMs = 5000;            // Kill 后等待退出的上限
        private const int TailMaxLines = 60;            // 失败诊断用的尾部输出行数
        private const int AutoWorkersHardMax = 8;       // 自动并行上限（手动值不受此限制）
        private const long TenMegabytes = 10L * 1024 * 1024;
        private const long ExtraTimeoutPerTenMbMs = 5L * 60 * 1000;
        private const long MaxExtraTimeoutMs = 100L * 60 * 1000;
        private const long MinTimeoutMs = 60L * 1000;
        private const int DefaultTimeoutMinutes = 20;
        private const long StaleTaskHours = 6;          // 临时任务文件超过 6 小时视为残留

        private const string LogPrefix = "[Blender] ";

        // ───────────────────────────────── 进程登记 / 全局终止 ─────────────────────────────────

        private static readonly object ProcGate = new object();
        private static readonly HashSet<Process> LiveProcs = new HashSet<Process>();
        private static int _killEpoch;   // KillAll 每调用一次 +1；正在跑的批次据此停止且不再启动新进程

        /// <summary>当前登记在册（已启动、未注销）的 Blender 进程数；自测/诊断用。</summary>
        public static int LiveProcessCount
        {
            get { lock (ProcGate) return LiveProcs.Count; }
        }

        internal static void TrackProcess(Process p)
        {
            if (p == null) return;
            lock (ProcGate) LiveProcs.Add(p);
        }

        internal static void UntrackProcess(Process p)
        {
            if (p == null) return;
            lock (ProcGate) LiveProcs.Remove(p);
        }

        /// <summary>
        /// 立即结束本进程启动的所有 Blender（取消 / 关闭窗口时调用）。
        /// 正在运行的 RunAll 会把未完成任务标为「已取消」并尽快返回，不会再重启 Blender。
        /// </summary>
        public static void KillAll()
        {
            Interlocked.Increment(ref _killEpoch);
            List<Process> list;
            lock (ProcGate) list = new List<Process>(LiveProcs);
            foreach (var p in list)
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
            }
            // 不逐个等 3 秒（UI 线程停止时会被拖住）：给一个总预算，谁没退就留给 Job Object
            int waited = 0;
            while (waited < 1500)
            {
                bool anyAlive = false;
                foreach (var p in list)
                {
                    try { if (!p.HasExited) { anyAlive = true; break; } }
                    catch { }
                }
                if (!anyAlive) break;
                try { System.Threading.Thread.Sleep(50); } catch { }
                waited += 50;
            }
        }

        private static void KillAndWait(Process p, int waitMs)
        {
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch { }
            try { p.WaitForExit(waitMs); } catch { }
        }

        // ───────────────────────────────── 自动并行数 / 空闲内存 ─────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        /// <summary>空闲物理内存（GB）；查询失败返回 -1（此时自动并行数上限按 3 处理）</summary>
        public static double FreePhysicalGb
        {
            get
            {
                try
                {
                    var ms = new MEMORYSTATUSEX();
                    ms.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                    if (!GlobalMemoryStatusEx(ref ms)) return -1;
                    return ms.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
                }
                catch { return -1; }
            }
        }

        /// <summary>
        /// 解析实际并行数：requested&gt;0 直接夹到 [1, 任务数]；
        /// requested=0 自动 = min(max(1,cpu/2), floor(空闲GB/1.2), 8, 任务数)，内存查询失败上限 3。
        /// </summary>
        public static int ResolveWorkers(int requested, int jobCount)
        {
            if (jobCount <= 0) return 0;
            if (requested > 0) return Math.Max(1, Math.Min(requested, jobCount));

            int byCpu = Math.Max(1, Environment.ProcessorCount / 2);
            double gb = FreePhysicalGb;
            int byMem = gb < 0 ? 3 : Math.Max(1, (int)Math.Floor(gb / 1.2));
            int w = Math.Min(byCpu, byMem);
            w = Math.Min(w, AutoWorkersHardMax);
            w = Math.Min(w, jobCount);
            return Math.Max(1, w);
        }

        // ───────────────────────────────── 纯逻辑辅助（自测可直接调） ─────────────────────────────────

        /// <summary>
        /// 按 Dst 去重：返回要真正执行的任务下标（owner），followers[owner] = 复用其结果的其余任务下标。
        /// 键 = GetFullPath(dst).ToLowerInvariant()；Dst 为空的任务不参与去重（各自执行）。
        /// </summary>
        public static List<int> DedupOwners(IReadOnlyList<BlenderJobSpec> jobs, out List<int>[] followers)
        {
            int n = jobs == null ? 0 : jobs.Count;
            followers = new List<int>[n];
            var owners = new List<int>();
            var firstByDst = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                BlenderJobSpec j = jobs[i];
                string key = DstKey(j == null ? null : j.Destination);
                int first;
                if (key != null && firstByDst.TryGetValue(key, out first))
                {
                    if (followers[first] == null) followers[first] = new List<int>();
                    followers[first].Add(i);
                    continue;
                }
                if (key != null) firstByDst[key] = i;
                owners.Add(i);
            }
            return owners;
        }

        private static string DstKey(string dst)
        {
            if (string.IsNullOrEmpty(dst)) return null;
            try { return Path.GetFullPath(dst).ToLowerInvariant(); }
            catch { return dst.ToLowerInvariant(); }
        }

        /// <summary>
        /// 加权分桶：owner 按 Weight 降序（相等按原顺序），逐个放进当前负载最小的桶（相等取靠前桶）。
        /// 负载累加 Weight&gt;0 ? Weight : 1。
        /// </summary>
        public static List<List<int>> PlanBuckets(IReadOnlyList<BlenderJobSpec> jobs, IReadOnlyList<int> ownerIndices, int workers)
        {
            var buckets = new List<List<int>>();
            if (ownerIndices == null || ownerIndices.Count == 0) return buckets;
            if (workers < 1) workers = 1;
            if (workers > ownerIndices.Count) workers = ownerIndices.Count;

            var loads = new long[workers];
            for (int i = 0; i < workers; i++) buckets.Add(new List<int>());

            var ordered = new List<int>(ownerIndices);
            // 稳定排序：权重降序（List.Sort 不稳定，这里显式用下标做次键保证可复现）
            ordered.Sort(delegate (int a, int b)
            {
                long wa = WeightOf(jobs, a), wb = WeightOf(jobs, b);
                if (wa != wb) return wb.CompareTo(wa);
                return a.CompareTo(b);
            });

            foreach (int i in ordered)
            {
                int pick = 0;
                for (int k = 1; k < workers; k++) if (loads[k] < loads[pick]) pick = k;
                buckets[pick].Add(i);
                loads[pick] += WeightOf(jobs, i) > 0 ? WeightOf(jobs, i) : 1;
            }
            return buckets;
        }

        private static long WeightOf(IReadOnlyList<BlenderJobSpec> jobs, int i)
        {
            if (jobs == null || i < 0 || i >= jobs.Count) return 0;
            BlenderJobSpec j = jobs[i];
            return j == null ? 0 : j.Weight;
        }

        /// <summary>
        /// 单任务看门狗时长（空闲超时）：base + min(100min, floor(size/10MB) × 5min)，
        /// size = Weight&gt;0 ? Weight : 源文件字节数（读不到按 0）。
        /// </summary>
        public static long ResolveTimeoutMs(BlenderJobSpec job, int taskTimeoutMinutes)
        {
            int minutes = taskTimeoutMinutes > 0 ? taskTimeoutMinutes : DefaultTimeoutMinutes;
            long baseMs = Math.Max(MinTimeoutMs, minutes * 60000L);
            long size = 0;
            if (job != null)
            {
                size = job.Weight;
                if (size <= 0)
                {
                    try { size = new FileInfo(job.Source).Length; }
                    catch { size = 0; }
                }
            }
            long extra = Math.Min(MaxExtraTimeoutMs, (size / TenMegabytes) * ExtraTimeoutPerTenMbMs);
            return baseMs + extra;
        }

        // ───────────────────────────────── RunAll ─────────────────────────────────

        /// <summary>
        /// 跑一批减面任务：
        ///  - 按 Dst 去重（同一输出只跑一次）；
        ///  - 多进程并行（每进程一组任务，按权重摊平）；逐任务实时回传结果；
        ///  - 单任务崩溃/超时只失败它自己，剩余任务换新进程继续；
        ///  - 取消：立即杀全部 Blender，未完成的标「已取消」；
        ///  - 所有子进程进 Job Object；本进程退出时不留孤儿。
        /// 返回列表与 jobs 一一对应（同序、同数量）。
        /// </summary>
        public static List<BlenderJobResult> RunAll(IReadOnlyList<BlenderJobSpec> jobs, BlenderRunOptions options,
                                                   CancellationToken cancel, Action<int, int> onProgress)
        {
            var output = new List<BlenderJobResult>();
            if (jobs == null || jobs.Count == 0) return output;
            if (options == null) options = new BlenderRunOptions();

            int n = jobs.Count;
            var normalized = new List<BlenderJobSpec>(n);
            for (int i = 0; i < n; i++) normalized.Add(jobs[i] ?? new BlenderJobSpec());   // 不改调用方列表

            var ctx = new RunContext
            {
                Jobs = normalized,
                Total = n,
                Res = new BlenderJobResult[n],
                OnDone = options.OnJobCompleted,
                Progress = onProgress,
                Log = options.Log,
                Cancel = cancel,
                Epoch = Volatile.Read(ref _killEpoch),
                Quality = options.QualityFirst,
                BaseTimeoutMinutes = options.TaskTimeoutMinutes > 0 ? options.TaskTimeoutMinutes : DefaultTimeoutMinutes
            };

            // 1) Dst 去重
            List<int>[] followers;
            List<int> owners = DedupOwners(normalized, out followers);
            ctx.Followers = followers;
            int dupCount = n - owners.Count;
            if (dupCount > 0) ctx.L(LogPrefix + "跳过 " + dupCount + " 个输出路径重复的任务（复用同一结果）");

            // 2) 每个任务的看门狗时长（含 followers，便于日志/诊断）
            ctx.TimeoutMs = new long[n];
            for (int i = 0; i < n; i++) ctx.TimeoutMs[i] = ResolveTimeoutMs(normalized[i], ctx.BaseTimeoutMinutes);

            // 3) 前置检查
            string fatal = null;
            string exe = options.BlenderPath;
            string script = null;
            if (cancel.IsCancellationRequested || ctx.Stopped) fatal = "已取消";
            else
            {
                if (string.IsNullOrEmpty(exe) || !SafeFileExists(exe))
                {
                    string found = BlenderLocator.Find(null);
                    if (!string.IsNullOrEmpty(found)) exe = found;
                }
                if (string.IsNullOrEmpty(exe) || !SafeFileExists(exe)) fatal = "找不到 Blender";
                else
                {
                    string err;
                    script = BackendScript.EnsureExtracted(out err);   // 开线程前释放一次，避免并行竞态
                    if (script == null) fatal = "无法释放后端脚本: " + err;
                }
            }
            if (fatal != null)
            {
                ctx.L(LogPrefix + fatal);
                foreach (int i in owners) ctx.Set(i, ctx.Fail(i, fatal));
                return ctx.Collect();
            }
            ctx.BlenderExe = exe;
            ctx.ScriptPath = script;
            CleanupStaleTaskFilesOnce();

            // 4) 分桶
            int workers = ResolveWorkers(options.Workers, owners.Count);
            var buckets = PlanBuckets(normalized, owners, workers);
            ctx.L(LogPrefix + "并行 " + workers + " 个进程，唯一任务 " + owners.Count + " 个" +
                  (dupCount > 0 ? "（另有 " + dupCount + " 个重复输出复用结果）" : "") +
                  "，质量模式=" + (ctx.Quality ? "是" : "否"));

            // 5) 执行：每桶一个线程
            if (workers <= 1)
            {
                SafeRunBucket(ctx, buckets.Count > 0 ? buckets[0] : new List<int>(), "");
            }
            else
            {
                var threads = new List<Thread>();
                for (int w = 0; w < buckets.Count; w++)
                {
                    List<int> b = buckets[w];
                    if (b.Count == 0) continue;
                    string tag = "[并行 " + (w + 1) + "/" + workers + "] ";
                    var th = new Thread(delegate ()
                    {
                        ctx.L(tag + "开始，本批 " + b.Count + " 个任务");
                        SafeRunBucket(ctx, b, tag);
                    });
                    th.IsBackground = true;
                    th.Name = "BlenderWorker" + (w + 1);
                    threads.Add(th);
                    th.Start();
                }
                foreach (var t in threads) t.Join();
            }

            // 6) 兜底：任何没拿到结果的任务
            string missing = ctx.Stopped ? "已取消" : "Blender 未返回该任务的结果";
            foreach (int i in owners) if (!ctx.HasResult(i)) ctx.Set(i, ctx.Fail(i, missing));

            // 7) 与输入同序同数量
            return ctx.Collect();
        }

        private static bool SafeFileExists(string path)
        {
            try { return !string.IsNullOrEmpty(path) && File.Exists(path); } catch { return false; }
        }

        // ───────────────────────────────── 一次 RunAll 的共享状态 ─────────────────────────────────

        private sealed class RunContext
        {
            public List<BlenderJobSpec> Jobs;
            public BlenderJobResult[] Res;
            public List<int>[] Followers;
            public long[] TimeoutMs;
            public string BlenderExe;
            public string ScriptPath;
            public bool Quality;
            public int BaseTimeoutMinutes;
            public Action<string> Log;
            public Action<int, BlenderJobResult> OnDone;
            public Action<int, int> Progress;
            public CancellationToken Cancel;
            public int Epoch;
            public int Total;
            public int Done;   // Interlocked
            private readonly object _gate = new object();

            /// <summary>取消或 KillAll 之后为 true；本批次不再启动新进程</summary>
            public bool Stopped
            {
                get { return Cancel.IsCancellationRequested || Volatile.Read(ref _killEpoch) != Epoch; }
            }

            public bool HasResult(int i)
            {
                lock (_gate) return Res[i] != null;
            }

            public void L(string s)
            {
                if (Log == null) return;
                try { Log(s); } catch { }
            }

            public BlenderJobResult Fail(int i, string error)
            {
                return new BlenderJobResult { Ok = false, Error = error };
            }

            /// <summary>登记结果（每任务只登记一次），followers 复制结果；回调在锁外触发</summary>
            public bool Set(int i, BlenderJobResult r)
            {
                var emit = new List<KeyValuePair<int, BlenderJobResult>>(2);
                lock (_gate)
                {
                    if (Res[i] != null) return false;
                    Res[i] = r;
                    emit.Add(new KeyValuePair<int, BlenderJobResult>(i, r));
                    var fs = Followers == null ? null : Followers[i];
                    if (fs != null)
                    {
                        foreach (int f in fs)
                        {
                            if (Res[f] != null) continue;
                            BlenderJobResult c = CopyResult(r);
                            Res[f] = c;
                            emit.Add(new KeyValuePair<int, BlenderJobResult>(f, c));
                        }
                    }
                }
                foreach (var kv in emit)
                {
                    if (OnDone != null)
                    {
                        try { OnDone(kv.Key, kv.Value); }
                        catch (Exception ex) { L("[警告] OnJobCompleted 回调异常: " + ex.Message); }
                    }
                    if (Progress != null)
                    {
                        try { Progress(Interlocked.Increment(ref Done), Total); }
                        catch (Exception ex) { L("[警告] onProgress 回调异常: " + ex.Message); }
                    }
                }
                return true;
            }

            public List<BlenderJobResult> Collect()
            {
                var list = new List<BlenderJobResult>(Total);
                lock (_gate)
                {
                    string missing = Stopped ? "已取消" : "未执行";
                    for (int i = 0; i < Total; i++) list.Add(Res[i] ?? Fail(i, missing));
                }
                return list;
            }
        }

        private static BlenderJobResult CopyResult(BlenderJobResult r)
        {
            if (r == null) return new BlenderJobResult { Ok = false, Error = "Blender 报告失败" };
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

        // ───────────────────────────────── 单桶执行（崩溃/超时续跑） ─────────────────────────────────

        private static void SafeRunBucket(RunContext ctx, List<int> items, string tag)
        {
            try { RunBucket(ctx, items, tag); }
            catch (Exception ex)
            {
                ctx.L("[错误] " + tag + "批次异常: " + ex.Message);
                foreach (int i in items) if (!ctx.HasResult(i)) ctx.Set(i, ctx.Fail(i, "批次异常: " + ex.Message));
            }
        }

        private static void RunBucket(RunContext ctx, List<int> items, string tag)
        {
            var pending = new List<int>(items);
            while (pending.Count > 0)
            {
                if (ctx.Stopped) return;

                ProcOutcome o = RunProcess(ctx, pending, tag);
                var still = new List<int>();
                foreach (int i in pending) if (!ctx.HasResult(i)) still.Add(i);
                pending = still;
                if (pending.Count == 0) return;
                if (o.Cancelled || ctx.Stopped) return;

                if (o.StartFailed)
                {
                    foreach (int i in pending) ctx.Set(i, ctx.Fail(i, o.StartError));
                    return;
                }

                string code = FormatExitCode(o.ExitCode);
                if (!o.SawStart)
                {
                    // 脚本根本没跑起来（Blender 启动即崩、被安全软件拦截、Python 语法错误、启动阶段卡死等）：
                    // 重试也没用；不能按"单任务失败续跑"处理，否则每个任务都要等满一次超时。
                    string why = (o.TimedOut ? "Blender 启动后没有任何响应（超时），可能被安全软件拦截或显卡驱动异常" :
                                               "Blender 后端脚本未能启动，退出码 " + code + LastErrorLine(o.Tail));
                    ctx.L("[错误] " + tag + why);
                    foreach (int i in pending) ctx.Set(i, ctx.Fail(i, why));
                    return;
                }

                int culprit = (o.Current >= 0 && pending.Contains(o.Current)) ? o.Current : pending[0];
                string msg = o.TimedOut
                    ? "Blender 在处理该任务时超时（超过 " + (o.TimeoutMs / 60000) + " 分钟没有进展），已终止进程，退出码 " + code
                    : "Blender 在处理该任务时崩溃，退出码 " + code + LastErrorLine(o.Tail);
                ctx.L("[错误] " + tag + Path.GetFileName(ctx.Jobs[culprit].Source ?? "") + ": " + msg);
                ctx.Set(culprit, ctx.Fail(culprit, msg));
                pending.Remove(culprit);
                if (pending.Count > 0)
                    ctx.L(tag + "重新启动 Blender 继续剩余 " + pending.Count + " 个任务");
            }
        }

        private static string FormatExitCode(int code)
        {
            if (code == int.MinValue) return "未知";
            if (code < 0 || code > 0xFFFF) return code + " (0x" + code.ToString("X8", CultureInfo.InvariantCulture) + ")";
            return code.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>从尾部输出里找最后一条像错误的信息（给用户看的中文原因补充）</summary>
        private static string LastErrorLine(string tail)
        {
            if (string.IsNullOrEmpty(tail)) return "";
            string[] raw = tail.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int k = raw.Length - 1; k >= 0; k--)
            {
                string s = raw[k].Trim();
                if (s.Length == 0) continue;
                if (s.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.StartsWith("FAIL", StringComparison.Ordinal) ||
                    s.IndexOf("EXCEPTION_", StringComparison.Ordinal) >= 0)
                {
                    return "（" + (s.Length > 200 ? s.Substring(0, 200) + "…" : s) + "）";
                }
            }
            return "";
        }

        // ───────────────────────────────── 一次 Blender 进程 ─────────────────────────────────

        private sealed class ProcOutcome
        {
            public bool StartFailed;
            public string StartError;
            public bool SawStart;      // 看到了 JOBS= 行（脚本已开始执行）
            public bool Cancelled;
            public bool TimedOut;
            public int ExitCode = int.MinValue;
            public int Current = -1;   // 崩溃/超时时正在处理的任务（全局下标）
            public long TimeoutMs;
            public string Tail = "";
        }

        private sealed class ProcState
        {
            public long LastActivityMs;   // 最近一条 BEGIN/RESULT/JOBS= 的时刻（相对 Stopwatch）
            public int CurrentLocal = -1; // 最近一条 BEGIN 的批次内下标
            public int SawStart;
        }

        private static string TaskDir()
        {
            string d = AppInfo.TempRunDir;
            Directory.CreateDirectory(d);
            return d;
        }

        private static bool _staleTaskCleaned;

        /// <summary>每个进程清理一次临时目录里超过 6 小时的残留任务文件（主程序被强杀时来不及删的）</summary>
        private static void CleanupStaleTaskFilesOnce()
        {
            if (_staleTaskCleaned) return;
            _staleTaskCleaned = true;
            try
            {
                string dir = TaskDir();
                DateTime now = DateTime.UtcNow;
                foreach (var f in Directory.GetFiles(dir, "tasks_*"))
                {
                    try
                    {
                        if ((now - File.GetLastWriteTimeUtc(f)).TotalHours > StaleTaskHours) FileUtil.TryDelete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static ProcOutcome RunProcess(RunContext ctx, List<int> batch, string tag)
        {
            var o = new ProcOutcome();
            string taskPath = null;
            string jsonlPath = null;
            try
            {
                string dir = TaskDir();
                taskPath = Path.Combine(dir, "tasks_" + Guid.NewGuid().ToString("N") + ".json");
                jsonlPath = taskPath + ".jsonl";
                var specs = new List<BlenderJobSpec>(batch.Count);
                foreach (int i in batch) specs.Add(ctx.Jobs[i]);
                WriteTaskFile(taskPath, specs, ctx.Quality);
            }
            catch (Exception ex)
            {
                o.StartFailed = true;
                o.StartError = "写任务文件失败: " + ex.Message;
                CleanupTaskFiles(taskPath, jsonlPath);
                return o;
            }

            // gltfpack 作为嵌入资源随 exe 交付；每个 Blender 进程通过环境变量拿到释放后的路径。
            // 释放失败时整批直接失败：不能悄悄回退到旧的全量焊接减面，否则袜子会再次粘连。
            string gltfpack = null;
            string gltfpackError = null;
            try { gltfpack = GltfpackTool.EnsureExtracted(out gltfpackError); }
            catch (Exception ex) { gltfpackError = ex.Message; }
            if (gltfpack == null)
            {
                o.StartFailed = true;
                o.StartError = "无法释放内置 gltfpack: " + (gltfpackError ?? "未知错误");
                CleanupTaskFiles(taskPath, jsonlPath);
                return o;
            }

            var st = new ProcState();
            var sw = Stopwatch.StartNew();
            var tail = new Queue<string>();
            Action<string> addTail = delegate (string s)
            {
                if (s == null) return;
                lock (tail) { tail.Enqueue(s); while (tail.Count > TailMaxLines) tail.Dequeue(); }
            };

            Process p = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ctx.BlenderExe,
                    // --python-exit-code 必须在 --python 之前：脚本未捕获异常时进程以 2 退出
                    Arguments = "--background --factory-startup --python-exit-code 2 --python \"" + ctx.ScriptPath +
                                "\" -- \"" + taskPath + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                // 后端脚本优先用这个内置 gltfpack 做拓扑感知减面
                psi.EnvironmentVariables["L4D2LOD_GLTFPACK"] = gltfpack;
                p = new Process { StartInfo = psi };
                p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    string line = e.Data;
                    if (line == null) return;
                    try
                    {
                        if (line.StartsWith("RESULT ", StringComparison.Ordinal))
                        {
                            addTail(line);
                            HandleRecord(ctx, batch, line.Substring(7));
                            Volatile.Write(ref st.LastActivityMs, sw.ElapsedMilliseconds);
                            return;
                        }
                        addTail(line);
                        if (line.StartsWith("BEGIN ", StringComparison.Ordinal))
                        {
                            int k;
                            if (int.TryParse(line.Substring(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out k))
                                Volatile.Write(ref st.CurrentLocal, k);
                            Volatile.Write(ref st.LastActivityMs, sw.ElapsedMilliseconds);
                        }
                        else if (line.StartsWith("JOBS=", StringComparison.Ordinal))
                        {
                            Volatile.Write(ref st.SawStart, 1);
                            Volatile.Write(ref st.LastActivityMs, sw.ElapsedMilliseconds);
                            ctx.L(LogPrefix + "后端已启动：" + line.Substring(5).Trim());
                        }
                        else if (line.StartsWith("OK", StringComparison.Ordinal) ||
                                 line.StartsWith("FAIL", StringComparison.Ordinal) ||
                                 line.StartsWith("WARN", StringComparison.Ordinal) ||
                                 line.StartsWith("DONE", StringComparison.Ordinal))
                        {
                            ctx.L("  " + tag + "[Blender] " + line);
                        }
                    }
                    catch (Exception ex) { addTail("输出处理异常: " + ex.Message); }
                };
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { addTail(e.Data); };

                try { p.Start(); }
                catch (Exception ex)
                {
                    o.StartFailed = true;
                    o.StartError = "启动 Blender 失败: " + ex.Message;
                    return o;
                }
                if (!JobObject.Assign(p))
                    ctx.L("[警告] 无法把 Blender 放入作业对象（" + JobObject.LastError + "），主程序异常退出时可能残留进程");
                TrackProcess(p);
                if (ctx.Stopped) KillAndWait(p, KillWaitMs);   // 与 KillAll/取消 的竞态：刚启动就该停
                try { p.BeginOutputReadLine(); } catch { }
                try { p.BeginErrorReadLine(); } catch { }

                // 看门狗：200ms 轮询；空闲超时（距上一条 BEGIN/RESULT/JOBS=）才判超时
                while (!p.WaitForExit(PollMs))
                {
                    if (ctx.Stopped)
                    {
                        o.Cancelled = true;
                        KillAndWait(p, KillWaitMs);
                        break;
                    }
                    int cl = Volatile.Read(ref st.CurrentLocal);
                    int cur = (cl >= 0 && cl < batch.Count) ? batch[cl] : FirstPending(ctx, batch);
                    long limit = cur >= 0 ? ctx.TimeoutMs[cur] : ctx.BaseTimeoutMinutes * 60000L;
                    long idle = sw.ElapsedMilliseconds - Volatile.Read(ref st.LastActivityMs);
                    if (idle > limit)
                    {
                        o.TimedOut = true;
                        o.TimeoutMs = limit;
                        ctx.L("[警告] " + tag + "超过 " + (limit / 60000) + " 分钟没有进度，终止 Blender");
                        KillAndWait(p, KillWaitMs);
                        break;
                    }
                }

                // WaitForExit(ms) 返回 true 不保证异步输出已读完；进程已退出时再无参等待一次把输出排空
                try { if (p.HasExited) p.WaitForExit(); } catch { }
                try { o.ExitCode = p.ExitCode; } catch { }
                if (ctx.Stopped) o.Cancelled = true;
            }
            catch (Exception ex)
            {
                ctx.L("[错误] " + tag + "Blender 进程通信异常: " + ex.Message);
            }
            finally
            {
                if (p != null)
                {
                    UntrackProcess(p);
                    try { p.Dispose(); } catch { }
                }

                // jsonl 兜底：stdout 可能因崩溃/被杀丢了最后几行
                try
                {
                    if (jsonlPath != null && File.Exists(jsonlPath))
                    {
                        foreach (var line in ReadAllLinesShared(jsonlPath))
                            if (!string.IsNullOrEmpty(line) && line.Trim().Length > 0) HandleRecord(ctx, batch, line);
                    }
                }
                catch (Exception ex) { ctx.L("[警告] 读取 jsonl 结果失败: " + ex.Message); }
                CleanupTaskFiles(taskPath, jsonlPath);
            }

            o.SawStart = Volatile.Read(ref st.SawStart) != 0;
            int cfinal = Volatile.Read(ref st.CurrentLocal);
            o.Current = (cfinal >= 0 && cfinal < batch.Count) ? batch[cfinal] : -1;
            lock (tail) o.Tail = string.Join("\n", tail.ToArray());

            if (!o.Cancelled && !o.TimedOut && o.ExitCode != 0 && o.ExitCode != int.MinValue)
            {
                bool allDone = true;
                foreach (int i in batch) if (!ctx.HasResult(i)) { allDone = false; break; }
                if (allDone)
                    ctx.L("[警告] " + tag + "Blender 退出码 " + FormatExitCode(o.ExitCode) + "（所有任务均已返回结果）");
            }
            return o;
        }

        private static int FirstPending(RunContext ctx, List<int> batch)
        {
            foreach (int i in batch) if (!ctx.HasResult(i)) return i;
            return -1;
        }

        private static void CleanupTaskFiles(string taskPath, string jsonlPath)
        {
            FileUtil.TryDelete(taskPath);
            FileUtil.TryDelete(jsonlPath);
            if (taskPath != null) FileUtil.TryDelete(taskPath + ".result");
        }

        private static string[] ReadAllLinesShared(string path)
        {
            var lines = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string l;
                while ((l = sr.ReadLine()) != null) lines.Add(l);
            }
            return lines.ToArray();
        }

        // ───────────────────────────────── RESULT 解析 ─────────────────────────────────

        /// <summary>解析一条 Python 回传的结果 JSON 并登记（每任务只登记一次，重复的忽略）</summary>
        private static void HandleRecord(RunContext ctx, List<int> batch, string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            Dictionary<string, object> o = Json.ParseObjectSafe(json);   // 截断的最后一行 → null → 忽略
            if (o == null) return;

            int local = Json.GetInt(o, "idx", -1);
            int gi = -1;
            if (local >= 0 && local < batch.Count)
            {
                gi = batch[local];
            }
            else
            {
                string dst = Json.GetString(o, "dst");
                if (!string.IsNullOrEmpty(dst))
                {
                    foreach (int i in batch)
                    {
                        if (string.Equals(ctx.Jobs[i].Destination, dst, StringComparison.OrdinalIgnoreCase)) { gi = i; break; }
                    }
                }
                if (gi < 0)
                {
                    string src = Json.GetString(o, "src");
                    if (!string.IsNullOrEmpty(src))
                    {
                        foreach (int i in batch)
                        {
                            if (string.Equals(ctx.Jobs[i].Source, src, StringComparison.OrdinalIgnoreCase)) { gi = i; break; }
                        }
                    }
                }
            }
            if (gi < 0) return;

            var r = new BlenderJobResult
            {
                Ok = Json.GetBool(o, "ok"),
                BeforeTriangles = Json.GetInt(o, "before"),
                AfterTriangles = Json.GetInt(o, "after"),
                TargetTriangles = Json.GetInt(o, "target"),
                Blocks = Json.GetInt(o, "blocks"),
                AppliedBlocks = Json.GetInt(o, "applied"),
                Warning = Json.GetString(o, "warn"),
                Error = Json.GetString(o, "error")
            };
            if (!r.Ok && string.IsNullOrEmpty(r.Error)) r.Error = "Blender 报告失败";
            if (ctx.Set(gi, r) && !string.IsNullOrEmpty(r.Warning))
            {
                string dstName = "";
                try { dstName = Path.GetFileName(ctx.Jobs[gi].Destination ?? ""); } catch { }
                ctx.L("[警告] " + dstName + ": " + r.Warning);
            }        }

        // ───────────────────────────────── 任务 JSON（落盘格式逐字兼容旧版） ─────────────────────────────────

        /// <summary>
        /// 写任务 JSON（UTF-8 无 BOM，按旧版逐字格式）：
        /// 首三行 "{" / " \"quality\": true," / " \"jobs\": ["；每行前缀两空格；字段序 src,dst,ratio,
        /// shrinkInner,minFaces,guardPct[,quality]；ratio/shrinkInner 用 "0.########"、guardPct 用
        /// "0.####"、全部 InvariantCulture；quality 覆盖只在有值时写。
        /// </summary>
        public static void WriteTaskFile(string path, IReadOnlyList<BlenderJobSpec> jobs, bool quality)
        {
            if (path == null) throw new ArgumentNullException("path");
            if (jobs == null) jobs = new BlenderJobSpec[0];
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append(" \"quality\": ").Append(quality ? "true" : "false").Append(",\r\n");
            sb.Append(" \"jobs\": [\r\n");
            for (int i = 0; i < jobs.Count; i++)
            {
                BlenderJobSpec j = jobs[i] ?? new BlenderJobSpec();
                sb.Append("  {\"src\": ").Append(Json.Escape(j.Source))
                  .Append(", \"dst\": ").Append(Json.Escape(j.Destination))
                  .Append(", \"ratio\": ").Append(Num(j.Ratio, "0.########", inv))
                  .Append(", \"shrinkInner\": ").Append(Num(j.ShrinkInner, "0.########", inv))
                  .Append(", \"minFaces\": ").Append(j.MinFaces.ToString(inv))
                  .Append(", \"guardPct\": ").Append(Num(j.GuardPercent, "0.####", inv));
                if (j.QualityOverride.HasValue)
                    sb.Append(", \"quality\": ").Append(j.QualityOverride.Value ? "true" : "false");
                sb.Append("}");
                sb.Append(i < jobs.Count - 1 ? ",\r\n" : "\r\n");
            }
            sb.Append(" ]\r\n");
            sb.Append("}\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static string Num(double v, string fmt, IFormatProvider inv)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            return v.ToString(fmt, inv);
        }
    }
}
