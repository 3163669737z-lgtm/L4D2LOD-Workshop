using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LodStudio.Services
{
    internal static class WindowWorkArea
    {
        internal static void Track(Window window)
        {
            var preferredMin = new Size(window.MinWidth, window.MinHeight);
            window.SourceInitialized += (s, e) => Fit(window, preferredMin);
            window.DpiChanged += (s, e) => window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => Fit(window, preferredMin)));
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
    }
}
