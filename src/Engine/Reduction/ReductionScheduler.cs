using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace LodStudio.Engine
{
    internal static class ReductionScheduler
    {
        private const int AutoWorkersHardMax = 8;       // 自动并行上限（手动值不受此限制）
        private const long TenMegabytes = 10L * 1024 * 1024;
        private const long ExtraTimeoutPerTenMbMs = 5L * 60 * 1000;
        private const long MaxExtraTimeoutMs = 100L * 60 * 1000;
        private const long MinTimeoutMs = 60L * 1000;
        private const int DefaultTimeoutMinutes = 20;
        private static readonly object ProcGate = new object();
        private static readonly HashSet<Process> LiveProcs = new HashSet<Process>();

        /// <summary>当前登记在册（已启动、未注销）的减面进程数；自测/诊断用。</summary>
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
        /// 立即结束本进程启动的所有减面进程（取消 / 关闭窗口时调用）。
        /// </summary>
        public static void KillAll()
        {
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
        public static List<int> DedupOwners(IReadOnlyList<ReductionJob> jobs, out List<int>[] followers)
        {
            int n = jobs == null ? 0 : jobs.Count;
            followers = new List<int>[n];
            var owners = new List<int>();
            var firstByDst = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                ReductionJob j = jobs[i];
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
        /// 单任务进程超时：base + min(100min, floor(size/10MB) × 5min)，
        /// size = Weight&gt;0 ? Weight : 源文件字节数（读不到按 0）。
        /// </summary>
        public static long ResolveTimeoutMs(ReductionJob job, int taskTimeoutMinutes)
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
    }
}
