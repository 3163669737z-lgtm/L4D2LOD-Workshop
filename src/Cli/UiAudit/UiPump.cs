using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Threading;
using LodStudio.Services;

namespace LodStudio.Cli.UiAudit
{
    /// <summary>
    /// 审计专用的消息泵。
    ///
    /// 铁律：整个 --ui-audit 都在 UI 线程上跑，**永远不用 Task.Wait()/Thread.Join 阻塞 UI 线程**
    /// （一旦阻塞，DialogHost 的 TCS 永远不会完成、动画永远不走）。
    ///
    /// 实现要点：等待一段时间时，进一个 <see cref="DispatcherFrame"/> 让 Dispatcher **连续**跑满这段时间，
    /// 由 DispatcherTimer 负责到时关闭 frame；而不是"泵一轮 + Sleep"地切片等。
    /// 切片等待会让 DispatcherTimer 的 tick 频率明显低于真实时间（Toast 4 秒退场要拖到 6 秒+），
    /// 导致动画/超时判断全部失真。
    /// </summary>
    internal static class UiPump
    {
        /// <summary>泵 n 轮 Dispatcher 队列（含渲染/动画优先级）。</summary>
        public static void Frames(int n)
        {
            UiCapture.DoEvents(Math.Max(1, n));
        }

        /// <summary>让 Dispatcher 连续运行至少 ms 毫秒（动画/DispatcherTimer 按真实时间推进）。</summary>
        public static void For(int ms)
        {
            if (ms <= 0) { UiCapture.DoEvents(1); return; }
            var frame = new DispatcherFrame(false);
            var closer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms))
            };
            closer.Tick += delegate
            {
                closer.Stop();
                frame.Continue = false;
            };
            closer.Start();
            try
            {
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                closer.Stop();
            }
            UiCapture.DoEvents(1);
        }

        /// <summary>边泵边看条件；返回条件是否在超时前成立（condition 为 null 视为成立）。</summary>
        public static bool Until(Func<bool> condition, int timeoutMs, int chunkMs = 20)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                For(chunkMs);
                if (condition == null || condition()) return true;
                if (sw.ElapsedMilliseconds >= timeoutMs) return false;
            }
        }

        /// <summary>泵到 Task 完成（不阻塞 UI 线程）。返回是否已完成。</summary>
        public static bool WaitTask(Task task, int timeoutMs)
        {
            if (task == null) return true;
            return Until(delegate { return task.IsCompleted; }, timeoutMs);
        }
    }
}
