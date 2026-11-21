using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LodStudio
{
    /// <summary>
    /// 单实例支持：
    ///  - 第二个实例把要导入的路径写进 %TEMP%\L4D2LOD远景工坊\handoff\pending_*.txt；
    ///  - 已有实例在后台线程轮询该目录，把新路径加入队列；
    ///  - 第二个实例随后把已有实例的窗口提到前台并退出。
    /// 这样"双击一个 glb"不会开出两个界面。
    /// </summary>
    internal static class SingleInstance
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

        private const int SwRestore = 9;

        private static string HandoffDir
        {
            get
            {
                string d = Path.Combine(AppInfo.TempRoot, "handoff");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        /// <summary>把路径数组写成一个投递文件（原子重命名，避免读到半截）</summary>
        public static void DeliverToRunningInstance(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0) return;
            try
            {
                string tmp = Path.Combine(HandoffDir, "pending_" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllLines(tmp, paths.ToArray(), new UTF8Encoding(false));
                File.Move(tmp, Path.Combine(HandoffDir, "pending_" + DateTime.Now.Ticks + ".txt"));
            }
            catch { }
        }

        /// <summary>把同名进程的主窗口提到前台</summary>
        public static void ActivateExistingWindow()
        {
            try
            {
                string name = Process.GetCurrentProcess().ProcessName;
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.Id == Process.GetCurrentProcess().Id) continue;
                        IntPtr h = p.MainWindowHandle;
                        if (h == IntPtr.Zero) continue;
                        if (IsIconic(h)) ShowWindow(h, SwRestore);
                        SetForegroundWindow(h);
                        break;
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
        }

        /// <summary>
        /// 启动轮询（后台线程）。回调在后台线程触发，调用方负责封送 UI 线程。
        /// 返回停止函数（释放句柄用）。
        /// </summary>
        public static IDisposable StartHandoffWatcher(Action<string[]> onPaths)
        {
            var stop = new ManualResetEventSlim(false);
            var th = new Thread(delegate ()
            {
                while (!stop.IsSet)
                {
                    try
                    {
                        foreach (var f in Directory.GetFiles(HandoffDir, "pending_*.txt"))
                        {
                            string[] lines = null;
                            try { lines = File.ReadAllLines(f, Encoding.UTF8); } catch { }
                            try { File.Delete(f); } catch { }
                            if (lines != null && lines.Length > 0)
                            {
                                try { onPaths(lines.Where(x => x.Length > 0).ToArray()); } catch { }
                            }
                        }
                    }
                    catch { }
                    try { stop.Wait(700); } catch { }
                }
            });
            th.IsBackground = true;
            th.Name = "HandoffWatcher";
            th.Start();
            return new Stopper(stop);
        }

        private sealed class Stopper : IDisposable
        {
            private readonly ManualResetEventSlim _ev;
            public Stopper(ManualResetEventSlim ev) { _ev = ev; }
            public void Dispose() { try { _ev.Set(); } catch { } }
        }
    }
}
