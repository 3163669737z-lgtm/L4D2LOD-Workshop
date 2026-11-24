using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Globalization;

namespace LodStudio.Services
{
    /// <summary>
    /// 把窗口内容渲染成 PNG。用途：UI 审计、设计迭代、截图回归。
    /// 用 RenderTargetBitmap 离屏渲染（窗口放在屏幕外），不干扰用户桌面、不需要真实屏幕。
    /// 渲染前必须让布局与渲染都完成（Show + UpdateLayout + 泵消息），否则会截到空画面。
    /// </summary>
    public static class UiCapture
    {
        /// <summary>泵一轮 Dispatcher 队列（含渲染优先级），让布局/动画到位。</summary>
        public static void DoEvents(int rounds = 3)
        {
            for (int i = 0; i < rounds; i++)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate
                {
                    frame.Continue = false;
                }));
                Dispatcher.PushFrame(frame);
            }
        }

        /// <summary>
        /// 以指定的目标 DPI 渲染 PNG，保持窗口的逻辑布局尺寸。
        /// </summary>
        public static void CaptureWindow(Window window, string path, double scale = 1.0)
        {
            if (window == null) throw new ArgumentNullException("window");
            DoEvents();
            FrameworkElement root = window;
            root.UpdateLayout();
            double w = root.ActualWidth;
            double h = root.ActualHeight;
            if (w < 2 || h < 2) throw new InvalidOperationException("窗口内容尺寸非法: " + w + "x" + h);

            int pw = (int)Math.Round(w * scale);
            int ph = (int)Math.Round(h * scale);
            var rtb = new RenderTargetBitmap(pw, ph, 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32);
            rtb.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                encoder.Save(fs);
        }

        /// <summary>比较两张图：返回 (差异像素数, 总像素数, 最大通道差)。尺寸不同时按较大者计并全部视为差异。</summary>
        public static PixelDiff Compare(string pathA, string pathB)
        {
            var a = LoadPixels(pathA);
            var b = LoadPixels(pathB);
            var diff = new PixelDiff();
            if (a.Width != b.Width || a.Height != b.Height)
            {
                diff.Total = Math.Max(a.Width * a.Height, b.Width * b.Height);
                diff.Different = diff.Total;
                diff.MaxChannelDelta = 255;
                return diff;
            }
            diff.Total = a.Width * a.Height;
            int stride = a.Width * 4;
            for (int i = 0; i < a.Data.Length; i += 4)
            {
                int dr = Math.Abs(a.Data[i] - b.Data[i]);
                int dg = Math.Abs(a.Data[i + 1] - b.Data[i + 1]);
                int db = Math.Abs(a.Data[i + 2] - b.Data[i + 2]);
                int m = Math.Max(dr, Math.Max(dg, db));
                if (m != 0)
                {
                    diff.Different++;
                    if (m > diff.MaxChannelDelta) diff.MaxChannelDelta = m;
                }
            }
            GC.KeepAlive(stride);
            return diff;
        }

        public sealed class PixelDiff
        {
            public int Different;
            public int Total;
            public int MaxChannelDelta;
            public double Percent { get { return Total == 0 ? 0 : Different * 100.0 / Total; } }
            public override string ToString()
            {
                return "差异像素 " + Different + " / " + Total + " = " + Percent.ToString("0.000", CultureInfo.InvariantCulture) +
                       "%  最大通道差 " + MaxChannelDelta;
            }
        }

        private sealed class PixelBuffer
        {
            public int Width, Height;
            public byte[] Data;
        }

        private static PixelBuffer LoadPixels(string path)
        {
            BitmapSource src;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var dec = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                src = dec.Frames[0];
            }
            var conv = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            int stride = w * 4;
            var raw = new byte[stride * h];
            conv.CopyPixels(raw, stride, 0);
            return new PixelBuffer { Width = w, Height = h, Data = raw };
        }
    }
}
