using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using LodStudio.Engine;
using Pixels = System.Drawing;
using static LodStudio.Services.WindowWorkArea;

namespace LodStudio.Services
{
    internal static class SelfTestWindowWorkArea
    {
        [SelfTest("窗口工作区：四边任务栏、负坐标副屏及最大跟踪尺寸")]
        public static void MaximizedGeometry(SelfTestContext ctx)
        {
            var monitor = new Pixels.Rectangle(0, 0, 1920, 1080);
            var trackLimit = new Pixels.Size(1920, 1080);
            var fixedBars = new[]
            {
                new Pixels.Rectangle(0, 0, 1920, 1032),
                new Pixels.Rectangle(0, 48, 1920, 1032),
                new Pixels.Rectangle(48, 0, 1872, 1080),
                new Pixels.Rectangle(0, 0, 1872, 1080)
            };
            foreach (var work in fixedBars)
            {
                var result = ComputeMaximizedBounds(monitor, work, trackLimit, AppBarEdge.Bottom);
                ctx.Assert(result.Bounds == work && result.MaxTrackSize == trackLimit, $"固定任务栏工作区 {work} 应原样保留");
            }

            var autoHideBars = new[]
            {
                (AppBarEdge.Left, new Pixels.Rectangle(2, 0, 1918, 1080)),
                (AppBarEdge.Top, new Pixels.Rectangle(0, 2, 1920, 1078)),
                (AppBarEdge.Right, new Pixels.Rectangle(0, 0, 1918, 1080)),
                (AppBarEdge.Bottom, new Pixels.Rectangle(0, 0, 1920, 1078))
            };
            foreach (var (edge, expected) in autoHideBars)
            {
                var result = ComputeMaximizedBounds(monitor, monitor, trackLimit, edge);
                ctx.Assert(result.Bounds == expected, $"自动隐藏任务栏 {edge} 应内缩 2px，实际 {result.Bounds}");
            }
            ctx.Assert(ComputeMaximizedBounds(monitor, monitor, trackLimit).Bounds == monitor, "无任务栏时应占满显示器");

            var secondary = new Pixels.Rectangle(-2560, -1440, 2560, 1440);
            var secondaryWork = new Pixels.Rectangle(-2512, -1400, 2512, 1400);
            var secondaryResult = ComputeMaximizedBounds(secondary, secondaryWork, trackLimit);
            ctx.Assert(secondaryResult.Bounds == new Pixels.Rectangle(48, 40, 2512, 1400), "负坐标副屏的位置应相对于该显示器的原点");
            ctx.Assert(secondaryResult.MaxTrackSize == secondaryWork.Size, "最大跟踪尺寸应容纳比主屏更大的副屏");
        }

        [SelfTest("窗口工作区：真实 WPF 窗口最大化、还原及释放 HWND")]
        public static void NativeMaximize(SelfTestContext ctx)
        {
            ctx.Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "WPF 窗口测试需要 STA 线程");
            VerifyNativeWindow(ctx, WindowState.Normal);
            VerifyNativeWindow(ctx, WindowState.Maximized);
        }

        private static void VerifyNativeWindow(SelfTestContext ctx, WindowState initialState)
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowState = initialState,
                ShowInTaskbar = false,
                ShowActivated = initialState == WindowState.Maximized,
                Opacity = 0,
                Width = 400,
                Height = 300,
                MinWidth = 241,
                MinHeight = 173
            };
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 48,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0)
            });
            Track(window);
            var hwnd = IntPtr.Zero;
            window.SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(window).Handle;
                if (initialState == WindowState.Maximized) AssertMaximizedRect(ctx, hwnd);
            };

            try
            {
                window.Show();
                ctx.Assert(hwnd != IntPtr.Zero && IsWindow(hwnd), "应创建真实 HWND");
                if (initialState == WindowState.Normal) window.WindowState = WindowState.Maximized;
                var (actual, monitor, work) = AssertMaximizedRect(ctx, hwnd);

                var info = new MinMaxInfo { MaxTrackSize = new NativePoint(1, 1) };
                var result = SendMessage(hwnd, WM_GETMINMAXINFO, IntPtr.Zero, ref info);
                ctx.Assert(result == IntPtr.Zero, "处理 WM_GETMINMAXINFO 应返回 0");
                var messageRect = new Pixels.Rectangle(monitor.Left + info.MaxPosition.X,
                    monitor.Top + info.MaxPosition.Y, info.MaxSize.X, info.MaxSize.Y);
                ctx.Assert(messageRect == actual, $"Win32 最大化矩形 {actual} 应与 WM_GETMINMAXINFO 的 {messageRect} 一致");
                ctx.Assert(info.MaxTrackSize.X >= actual.Width && info.MaxTrackSize.Y >= actual.Height,
                    "最大跟踪尺寸应容纳最大化窗口");
                var toDevice = HwndSource.FromHwnd(hwnd).CompositionTarget.TransformToDevice;
                ctx.Assert(info.MinTrackSize.X == Math.Min(actual.Width, Math.Ceiling(window.MinWidth * toDevice.M11)) &&
                    info.MinTrackSize.Y == Math.Min(actual.Height, Math.Ceiling(window.MinHeight * toDevice.M22)),
                    "原生最小跟踪尺寸应遵守当前 DPI 下的 WPF 最小尺寸，并限制在最大化尺寸内");
                ctx.Log($"{initialState}: GetWindowRect={actual}, rcWork={work}, DPI={toDevice.M11 * 96:0}");

                window.WindowState = WindowState.Normal;
                ctx.Assert(!IsZoomed(hwnd), "窗口应恢复为普通状态");
                ctx.Assert(GetWindowRect(hwnd, out var restored), "还原后应能读取窗口矩形");
                ctx.Assert(restored.ToRectangle().Size != actual.Size, "还原后应恢复普通窗口大小");
            }
            finally
            {
                window.Close();
            }
            ctx.Assert(!IsWindow(hwnd), "窗口关闭后应释放 HWND");
        }

        private static (Pixels.Rectangle Actual, Pixels.Rectangle Monitor, Pixels.Rectangle Work) AssertMaximizedRect(
            SelfTestContext ctx, IntPtr hwnd)
        {
            ctx.Assert(IsZoomed(hwnd), "Win32 窗口应处于最大化状态");
            ctx.Assert(GetWindowRect(hwnd, out var rect), "应能读取实际 Win32 窗口矩形");
            ctx.Assert(TryGetMonitorBounds(hwnd, out var monitor, out var work), "应能读取所属显示器的工作区");
            var actual = rect.ToRectangle();
            ctx.Assert(work.Contains(actual), $"最大化窗口 {actual} 应完全位于工作区 {work} 内");
            if (work != monitor) ctx.Assert(actual == work, "有固定任务栏时应填满工作区");
            return (actual, monitor, work);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsZoomed(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, ref MinMaxInfo info);
    }
}
