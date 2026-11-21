using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>
    /// 启动性能打点（仅在环境变量 L4D2LOD_PERF=1 时写文件，正常使用零开销）。
    /// 用于验收"冷启动时间"与定位启动慢的环节。
    /// </summary>
    public static class PerfTrace
    {
        private static readonly Stopwatch Watch = Stopwatch.StartNew();
        private static readonly bool Enabled;
        private static readonly StringBuilder Buffer = new StringBuilder();
        private static readonly object Gate = new object();

        static PerfTrace()
        {
            try { Enabled = Environment.GetEnvironmentVariable("L4D2LOD_PERF") == "1"; } catch { Enabled = false; }
        }

        public static bool IsEnabled { get { return Enabled; } }

        public static void Mark(string name)
        {
            if (!Enabled) return;
            try
            {
                lock (Gate)
                {
                    Buffer.AppendLine(Watch.ElapsedMilliseconds.ToString() + " ms\t" + name);
                }
            }
            catch { }
        }

        public static void Flush()
        {
            if (!Enabled) return;
            try
            {
                string dir = AppInfo.TempRoot;
                Directory.CreateDirectory(dir);
                lock (Gate)
                {
                    File.WriteAllText(Path.Combine(dir, "perf.log"), Buffer.ToString(), new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
