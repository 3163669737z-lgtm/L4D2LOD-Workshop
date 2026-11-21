using System;
using System.IO;

namespace LodStudio.Engine
{
    /// <summary>
    /// 启动时的临时目录清理：主程序被强杀时来不及删除的任务 JSON / 旧后端脚本会被清掉。
    /// 只清理本程序自己的目录（%TEMP%\L4D2LOD远景工坊），且只动超过安全时限的陈旧文件。
    /// </summary>
    public static class TempCleaner
    {
        private static bool _done;

        public static void CleanStale()
        {
            if (_done) return;
            _done = true;
            try
            {
                string root = AppInfo.TempRoot;
                if (!Directory.Exists(root)) return;
                DateTime now = DateTime.UtcNow;

                // 任务 JSON / jsonl / 结果文件：被强杀后残留（6 小时以上）
                SafeClean(root, "tasks_*", now, TimeSpan.FromHours(6));
                SafeClean(root, "backend_*.py", now, TimeSpan.FromHours(6));

                // 后端脚本：保留最近 7 天（当前版本会被立即复用）
                string backend = AppInfo.TempBackendDir;
                if (Directory.Exists(backend))
                    SafeClean(backend, "blender_backend_*.py", now, TimeSpan.FromDays(7));

                // SMD 中间文件（正常结束会删；强杀残留）
                string smd = AppInfo.TempSmdDir;
                if (Directory.Exists(smd)) SafeClean(smd, "*", now, TimeSpan.FromHours(6));

                // 运行目录（有的版本会把任务文件放在 run\ 下）
                string run = AppInfo.TempRunDir;
                if (Directory.Exists(run)) SafeClean(run, "*", now, TimeSpan.FromHours(6));

                // handoff 投递文件（超过 1 天）
                string handoff = Path.Combine(root, "handoff");
                if (Directory.Exists(handoff)) SafeClean(handoff, "pending_*", now, TimeSpan.FromDays(1));

                // 崩溃日志保留最近 30 天
                SafeClean(root, "crash_*.txt", now, TimeSpan.FromDays(30));
            }
            catch { }
        }

        private static void SafeClean(string dir, string pattern, DateTime nowUtc, TimeSpan olderThan)
        {
            try
            {
                foreach (var f in Directory.GetFiles(dir, pattern))
                {
                    try
                    {
                        if ((nowUtc - File.GetLastWriteTimeUtc(f)) > olderThan) File.Delete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
