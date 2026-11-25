using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LodStudio.Services
{
    /// <summary>
    /// Windows 11/10 DWM 窗口玻璃与原生圆角集成：
    /// - Windows 11 上启用原生窗口圆角（DWMWCP_ROUND）与暗/浅沉浸式标题栏同步；
    /// - 离屏或旧系统上静默安全降级，绝不抛异常或干扰 RenderTargetBitmap 截图。
    /// </summary>
    internal static class WindowGlass
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        internal static void Attach(Window window)
        {
            if (window == null) return;
            EventHandler apply = delegate { Apply(window, ThemeService.Current == ThemeKind.Dark); };
            window.SourceInitialized += apply;
            EventHandler onTheme = delegate { Apply(window, ThemeService.Current == ThemeKind.Dark); };
            ThemeService.ThemeChanged += onTheme;
            window.Closed += delegate
            {
                try { ThemeService.ThemeChanged -= onTheme; } catch { }
            };
        }

        internal static void Apply(Window window, bool dark)
        {
            if (window == null) return;
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int darkVal = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkVal, sizeof(int));

                int round = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            }
            catch { }
        }
    }
}
