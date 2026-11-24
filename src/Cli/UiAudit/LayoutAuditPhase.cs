using System;
using LodStudio.Services;

namespace LodStudio.Cli.UiAudit
{
    /// <summary>
    /// 布局审计覆盖默认、宽屏、最小和紧凑工作区。
    /// </summary>
    internal static class LayoutAuditPhase
    {
        private struct Size
        {
            public double W, H;
            public Size(double w, double h) { W = w; H = h; }
            public override string ToString()
            {
                return W.ToString("0") + "×" + H.ToString("0");
            }
        }

        public static void Run(ShellAuditSession s, AuditReport report, string outDir = null)
        {
            var sizes = new[] { new Size(1320, 860), new Size(1600, 900), new Size(1120, 720), new Size(960, 550) };
            report.Section("3. 布局审计（" + ScreenshotMatrix.LayoutStates.Length + " 状态 × 浅/暗 × " + sizes.Length + " 尺寸）");
            report.Info("判定：兄弟控件重叠 0 处 / 子元素越界 0 处（滚动区内容只查重叠；" +
                        "EmptyState/NoMatchState/LogBody/DropOverlay/DialogHost/ToastHost/Progress/" +
                        "TickStrip/CornerBrackets/Rule/SegmentedProgress 及 1px 分隔线、Canvas 子级不参与；" +
                        "内置控件模板内部（TextBox/ComboBox/ScrollBar/AdornerLayer）与非交互覆盖层不参与）。");

            int groups = 0, failed = 0;
            int totalOverlaps = 0, totalOutOfBounds = 0;

            foreach (ThemeKind theme in new[] { ThemeKind.Light, ThemeKind.Dark })
            {
                string themeName = theme == ThemeKind.Light ? "浅" : "暗";
                s.SetTheme(theme);
                foreach (Size size in sizes)
                {
                    foreach (string state in ScreenshotMatrix.LayoutStates)
                    {
                        s.ResetToRestState();
                        s.SetSize(size.W, size.H);
                        ScreenshotMatrix.ApplyState(s, state);
                        ScreenshotMatrix.SettleState(s, state);
                        s.Win.UpdateLayout();
                        UiPump.For(90);

                        string context = themeName + " " + size + " " + state;
                        if (!string.IsNullOrEmpty(outDir))
                        {
                            try
                            {
                                System.IO.File.AppendAllText(System.IO.Path.Combine(outDir, "progress.txt"),
                                    DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) +
                                    "  layout group: " + context + Environment.NewLine,
                                    new System.Text.UTF8Encoding(false));
                            }
                            catch { }
                        }
                        LayoutAuditResult res = LayoutAuditor.Audit(s.Root, context);
                        groups++;
                        totalOverlaps += res.Overlaps;
                        totalOutOfBounds += res.OutOfBounds;

                        bool ok = res.Ok;
                        if (!ok) failed++;
                        report.Info("  " + Pad(context, 30) + res.Summary + (ok ? "   PASS" : "   FAIL"));
                        if (!ok) ScreenshotMatrix.PrintDetails(report, res);
                        s.CloseDialogs();
                    }
                }
            }

            report.Info("");
            report.Check("布局审计全部 0/0（" + groups + " 组）", failed == 0,
                "重叠合计 " + totalOverlaps + " 处 / 越界合计 " + totalOutOfBounds + " 处 / 失败 " + failed + " 组");
        }

        private static string Pad(string s, int width)
        {
            int w = 0;
            foreach (char c in s ?? "") w += c > 0x2E7F ? 2 : 1;
            var sb = new System.Text.StringBuilder(s ?? "");
            for (int i = w; i < width; i++) sb.Append(' ');
            return sb.ToString();
        }
    }
}
