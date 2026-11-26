using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Pixels = System.Drawing;

namespace LodStudio.Services
{
    internal static class WindowWorkArea
    {
        internal const int WM_GETMINMAXINFO = 0x0024;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint ABM_GETSTATE = 0x00000004;
        private const uint ABM_GETAUTOHIDEBAREX = 0x0000000B;
        private const ulong ABS_AUTOHIDE = 0x00000001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int AutoHideInset = 2;

        internal enum AppBarEdge : uint { Left, Top, Right, Bottom }

        internal static void Track(Window window, bool constrainMaximized = true)
        {
            var preferredMin = new Size(window.MinWidth, window.MinHeight);
            HwndSource source = null;
            HwndSourceHook hook = OnWindowMessage;
            window.SourceInitialized += OnSourceInitialized;
            window.DpiChanged += OnDpiChanged;
            window.Closed += OnClosed;

            void OnSourceInitialized(object sender, EventArgs e)
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (constrainMaximized)
                {
                    source = HwndSource.FromHwnd(hwnd);
                    source?.AddHook(hook);
                }

                Fit(window, preferredMin);
                // An HWND created with WS_MAXIMIZE can already have its bounds before the hook is installed.
                if (source != null && window.WindowState == WindowState.Maximized &&
                    TryGetMonitorBounds(hwnd, out var monitor, out var work))
                {
                    var (bounds, _) = ComputeMaximizedBounds(monitor, work, Pixels.Size.Empty, FindAutoHideEdge(monitor, work));
                    SetWindowPos(hwnd, IntPtr.Zero, monitor.Left + bounds.X, monitor.Top + bounds.Y,
                        bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE);
                }
            }

            void OnDpiChanged(object sender, DpiChangedEventArgs e)
            {
                window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Fit(window, preferredMin)));
            }

            void OnClosed(object sender, EventArgs e)
            {
                source?.RemoveHook(hook);
                source = null;
                window.SourceInitialized -= OnSourceInitialized;
                window.DpiChanged -= OnDpiChanged;
                window.Closed -= OnClosed;
            }

            IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                if (message != WM_GETMINMAXINFO || lParam == IntPtr.Zero || source?.CompositionTarget == null ||
                    !TryGetMonitorBounds(hwnd, out var monitor, out var work)) return IntPtr.Zero;

                var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                var (bounds, maxTrackSize) = ComputeMaximizedBounds(monitor, work,
                    new Pixels.Size(info.MaxTrackSize.X, info.MaxTrackSize.Y), FindAutoHideEdge(monitor, work));
                info.MaxPosition = new NativePoint(bounds.X, bounds.Y);
                info.MaxSize = new NativePoint(bounds.Width, bounds.Height);
                info.MaxTrackSize = new NativePoint(maxTrackSize.Width, maxTrackSize.Height);

                // Completing the message here skips WPF's tracking-size calculation and DefWindowProc.
                var toDevice = source.CompositionTarget.TransformToDevice;
                info.MinTrackSize.X = (int)Math.Min(bounds.Width,
                    Math.Max(info.MinTrackSize.X, Math.Ceiling(window.MinWidth * toDevice.M11)));
                info.MinTrackSize.Y = (int)Math.Min(bounds.Height,
                    Math.Max(info.MinTrackSize.Y, Math.Ceiling(window.MinHeight * toDevice.M22)));
                Marshal.StructureToPtr(info, lParam, false);
                handled = true;
                return IntPtr.Zero;
            }
        }

        internal static (Pixels.Rectangle Bounds, Pixels.Size MaxTrackSize) ComputeMaximizedBounds(
            Pixels.Rectangle monitor, Pixels.Rectangle work, Pixels.Size maxTrackSize, AppBarEdge? autoHideEdge = null)
        {
            if (work == monitor)
            {
                int left = autoHideEdge == AppBarEdge.Left ? AutoHideInset : 0;
                int top = autoHideEdge == AppBarEdge.Top ? AutoHideInset : 0;
                int right = autoHideEdge == AppBarEdge.Right ? AutoHideInset : 0;
                int bottom = autoHideEdge == AppBarEdge.Bottom ? AutoHideInset : 0;
                work = Pixels.Rectangle.FromLTRB(work.Left + left, work.Top + top, work.Right - right, work.Bottom - bottom);
            }

            var bounds = new Pixels.Rectangle(work.Left - monitor.Left, work.Top - monitor.Top, work.Width, work.Height);
            return (bounds, new Pixels.Size(Math.Max(maxTrackSize.Width, bounds.Width), Math.Max(maxTrackSize.Height, bounds.Height)));
        }

        internal static bool TryGetMonitorBounds(IntPtr hwnd, out Pixels.Rectangle monitor, out Pixels.Rectangle work)
        {
            monitor = work = default;
            var handle = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (handle == IntPtr.Zero || !GetMonitorInfo(handle, ref info)) return false;
            monitor = info.Monitor.ToRectangle();
            work = info.Work.ToRectangle();
            return true;
        }

        private static AppBarEdge? FindAutoHideEdge(Pixels.Rectangle monitor, Pixels.Rectangle work)
        {
            if (work != monitor) return null;
            var appBar = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>() };
            if ((SHAppBarMessage(ABM_GETSTATE, ref appBar).ToUInt64() & ABS_AUTOHIDE) == 0) return null;

            for (var edge = AppBarEdge.Left; edge <= AppBarEdge.Bottom; edge++)
            {
                appBar.Edge = edge;
                appBar.Rect = new NativeRect { Left = monitor.Left, Top = monitor.Top, Right = monitor.Right, Bottom = monitor.Bottom };
                if (SHAppBarMessage(ABM_GETAUTOHIDEBAREX, ref appBar) != UIntPtr.Zero) return edge;
            }
            return null;
        }

        private static void Fit(Window window, Size preferredMin)
        {
            var source = PresentationSource.FromVisual(window);
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || source?.CompositionTarget == null) return;

            var area = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
            var fromDevice = source.CompositionTarget.TransformFromDevice;
            double workWidth = Math.Floor(area.Width * fromDevice.M11);
            double workHeight = Math.Floor(area.Height * fromDevice.M22);
            window.MinWidth = Math.Min(preferredMin.Width, workWidth);
            window.MinHeight = Math.Min(preferredMin.Height, workHeight);
            window.Width = Math.Max(window.MinWidth, Math.Min(window.Width, workWidth));
            window.Height = Math.Max(window.MinHeight, Math.Min(window.Height, workHeight));

            if (window.WindowState != WindowState.Normal || window.WindowStartupLocation != WindowStartupLocation.Manual) return;
            var topLeft = fromDevice.Transform(new Point(area.Left, area.Top));
            var bottomRight = fromDevice.Transform(new Point(area.Right, area.Bottom));
            window.Left = Math.Max(topLeft.X, Math.Min(window.Left, bottomRight.X - window.Width));
            window.Top = Math.Max(topLeft.Y, Math.Min(window.Top, bottomRight.Y - window.Height));
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint
        {
            internal int X;
            internal int Y;

            internal NativePoint(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MinMaxInfo
        {
            internal NativePoint Reserved;
            internal NativePoint MaxSize;
            internal NativePoint MaxPosition;
            internal NativePoint MinTrackSize;
            internal NativePoint MaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;

            internal Pixels.Rectangle ToRectangle() => Pixels.Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            internal uint Size;
            internal NativeRect Monitor;
            internal NativeRect Work;
            internal uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AppBarData
        {
            internal uint Size;
            internal IntPtr Hwnd;
            internal uint CallbackMessage;
            internal AppBarEdge Edge;
            internal NativeRect Rect;
            internal IntPtr LParam;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("shell32.dll")]
        private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
    }
}
