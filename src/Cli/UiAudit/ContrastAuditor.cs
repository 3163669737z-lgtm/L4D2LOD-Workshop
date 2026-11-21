using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LodStudio.Services;

namespace LodStudio.Cli.UiAudit
{
    /// <summary>一行对比度检查。</summary>
    internal sealed class ContrastRow
    {
        public string Group = "";        // 正文 / 信号 / 语义 / 非文本
        public string Name = "";         // 组合名（"Ink on Panel"）
        public string FgKey = "";        // 令牌键（Color.Ink）
        public string BgKey = "";
        public double Threshold = 4.5;
        public bool AnnotationOnly;      // 允许标注（不计失败），如阶梯图辅助网格
        public string Note = "";         // 附加说明（为什么允许标注）

        public ContrastRow(string group, string name, string fgKey, string bgKey, double threshold, bool annotationOnly = false, string note = "")
        {
            Group = group; Name = name; FgKey = fgKey; BgKey = bgKey;
            Threshold = threshold; AnnotationOnly = annotationOnly; Note = note;
        }
    }

    /// <summary>
    /// 对比度审计（WCAG 2.x 相对亮度公式）：
    ///   正文 ≥4.5、弱文字/非文本 ≥3（docs/02 §7、docs/02 §2）。
    /// 令牌值从 Application.Current.Resources 读（切换主题后再读），不硬编码色值。
    /// </summary>
    internal static class ContrastAuditor
    {
        /// <summary>两套主题都必须定义的令牌（docs/02 §2 的完整清单）。</summary>
        private static readonly string[] TokenKeys =
        {
            "Color.Canvas", "Color.Panel", "Color.Sunken", "Color.SunkenHigh",
            "Color.Line", "Color.LineStrong",
            "Color.Ink", "Color.InkSoft", "Color.InkFaint",
            "Color.Signal", "Color.SignalInk", "Color.SignalHover", "Color.SignalPress", "Color.SignalSoft",
            "Color.Ok", "Color.Warn", "Color.Err",
            "Color.OkSoft", "Color.WarnSoft", "Color.ErrSoft",
            "Color.RowHover", "Color.RowSelected",
            "Color.ChartBand", "Color.ChartBandActive", "Color.ChartGrid", "Color.Tick",
            "Color.ScrollThumb", "Color.ScrollThumbHover",
            "Color.WindowBorder", "Color.Mask", "Color.HardShadow", "Color.FocusRing"
        };

        public static void Run(ShellAuditSession session, AuditReport report)
        {
            report.Section("4. 对比度审计（WCAG 2.x；正文 ≥4.5、弱文字/非文本 ≥3）");

            CheckTokenCompleteness(report);

            var rows = BuildRows();
            ThemeKind restore = ThemeService.Current;
            int fail = 0, note = 0, pass = 0;

            foreach (ThemeKind theme in new[] { ThemeKind.Light, ThemeKind.Dark })
            {
                if (session != null) session.SetTheme(theme);
                else ThemeService.Apply(theme);

                string themeName = theme == ThemeKind.Light ? "浅色" : "暗色";
                report.Info("");
                report.Info(themeName + "主题（令牌来自 Application.Current.Resources）");
                report.Info(Pad("组合", 34) + Pad("前景", 12) + Pad("背景", 12) + Pad("比值", 9) + Pad("阈值", 7) + "判定");

                foreach (ContrastRow row in rows)
                {
                    Color fg = ReadColor(row.FgKey);
                    Color bg = ReadColor(row.BgKey);
                    if (fg == Colors.Transparent && row.FgKey != "Color.Mask")
                        report.Note("令牌缺失或不是颜色：" + row.FgKey);
                    double ratio = Ratio(fg, bg);
                    bool ok = ratio + 1e-9 >= row.Threshold;
                    string detail = "#" + Hex(fg) + " on #" + Hex(bg) + " = " +
                                    ratio.ToString("0.00", CultureInfo.InvariantCulture) + ":1  ≥" +
                                    row.Threshold.ToString("0.##", CultureInfo.InvariantCulture) +
                                    (string.IsNullOrEmpty(row.Note) ? "" : "  (" + row.Note + ")");
                    report.Info(Pad(themeName + " " + row.Name, 34) + Pad("#" + Hex(fg), 12) + Pad("#" + Hex(bg), 12) +
                                Pad(ratio.ToString("0.00", CultureInfo.InvariantCulture) + ":1", 9) +
                                Pad("≥" + row.Threshold.ToString("0.##", CultureInfo.InvariantCulture), 7) +
                                (ok ? "PASS" : "FAIL") + (row.AnnotationOnly ? "（允许标注）" : ""));

                    string checkName = themeName + "对比度 · " + row.Group + " · " + row.Name;
                    if (row.AnnotationOnly)
                    {
                        if (report.Annotation(checkName, ok, detail)) pass++;
                        else note++;
                    }
                    else
                    {
                        if (report.Check(checkName, ok, detail)) pass++;
                        else fail++;
                    }
                }
            }

            if (session != null) session.SetTheme(restore);
            else ThemeService.Apply(restore);

            report.Info("");
            report.Check("对比度表全部达标", fail == 0,
                rows.Count * 2 + " 组（浅色 + 暗色）：PASS " + pass + " / FAIL " + fail + " / NOTE " + note);
        }

        // ══════════════════════ 表 ══════════════════════

        private static List<ContrastRow> BuildRows()
        {
            var rows = new List<ContrastRow>();
            // 正文
            rows.Add(new ContrastRow("正文", "Ink on Canvas", "Color.Ink", "Color.Canvas", 4.5));
            rows.Add(new ContrastRow("正文", "Ink on Panel", "Color.Ink", "Color.Panel", 4.5));
            rows.Add(new ContrastRow("正文", "Ink on Sunken", "Color.Ink", "Color.Sunken", 4.5));
            rows.Add(new ContrastRow("正文", "InkSoft on Canvas", "Color.InkSoft", "Color.Canvas", 4.5));
            rows.Add(new ContrastRow("正文", "InkSoft on Panel", "Color.InkSoft", "Color.Panel", 4.5));
            rows.Add(new ContrastRow("弱文字", "InkFaint on Canvas", "Color.InkFaint", "Color.Canvas", 3));
            rows.Add(new ContrastRow("弱文字", "InkFaint on Panel", "Color.InkFaint", "Color.Panel", 3));
            rows.Add(new ContrastRow("弱文字", "InkFaint on Sunken", "Color.InkFaint", "Color.Sunken", 3));
            rows.Add(new ContrastRow("弱文字", "InkFaint on RowHover", "Color.InkFaint", "Color.RowHover", 3));
            // 信号色
            rows.Add(new ContrastRow("信号", "Signal on Panel（文本）", "Color.Signal", "Color.Panel", 4.5));
            rows.Add(new ContrastRow("信号", "Signal on Canvas（文本）", "Color.Signal", "Color.Canvas", 4.5));
            rows.Add(new ContrastRow("信号", "SignalInk on Signal", "Color.SignalInk", "Color.Signal", 4.5));
            // 语义色
            rows.Add(new ContrastRow("语义", "Ok on Panel", "Color.Ok", "Color.Panel", 4.5));
            rows.Add(new ContrastRow("语义", "Warn on Panel", "Color.Warn", "Color.Panel", 4.5));
            rows.Add(new ContrastRow("语义", "Err on Panel", "Color.Err", "Color.Panel", 4.5));
            rows.Add(new ContrastRow("语义", "Ok on OkSoft", "Color.Ok", "Color.OkSoft", 4.5));
            rows.Add(new ContrastRow("语义", "Warn on WarnSoft", "Color.Warn", "Color.WarnSoft", 4.5));
            rows.Add(new ContrastRow("语义", "Err on ErrSoft", "Color.Err", "Color.ErrSoft", 4.5));
            // 非文本
            rows.Add(new ContrastRow("非文本", "LineStrong on Panel", "Color.LineStrong", "Color.Panel", 3));
            rows.Add(new ContrastRow("非文本", "LineStrong on Canvas", "Color.LineStrong", "Color.Canvas", 3));
            rows.Add(new ContrastRow("非文本", "Signal on Panel（非文本）", "Color.Signal", "Color.Panel", 3));
            rows.Add(new ContrastRow("非文本", "ChartGrid on ChartBand", "Color.ChartGrid", "Color.ChartBand", 3,
                true, "阶梯图辅助网格，规范允许标注"));
            rows.Add(new ContrastRow("非文本", "ChartGrid on ChartBandActive", "Color.ChartGrid", "Color.ChartBandActive", 3,
                true, "阶梯图辅助网格，规范允许标注"));
            return rows;
        }

        // ══════════════════════ 令牌完整性 ══════════════════════

        private static void CheckTokenCompleteness(AuditReport report)
        {
            ResourceDictionary light = LoadTokenDictionary(ThemeKind.Light);
            ResourceDictionary dark = LoadTokenDictionary(ThemeKind.Dark);
            var missing = new List<string>();
            foreach (string key in TokenKeys)
            {
                if (!light.Contains(key)) missing.Add("浅色 " + key);
                if (!dark.Contains(key)) missing.Add("暗色 " + key);
            }
            report.Check("令牌双主题齐全（" + TokenKeys.Length + " 个 Color.*）", missing.Count == 0,
                missing.Count == 0 ? "浅色 / 暗色都定义了全部令牌" : "缺：" + string.Join(", ", missing.ToArray()));
        }

        private static ResourceDictionary LoadTokenDictionary(ThemeKind kind)
        {
            try
            {
                return new ResourceDictionary
                {
                    Source = new Uri(kind == ThemeKind.Dark ? "/Themes/Tokens.Dark.xaml" : "/Themes/Tokens.Light.xaml",
                                     UriKind.Relative)
                };
            }
            catch
            {
                return new ResourceDictionary();
            }
        }

        // ══════════════════════ 计算 ══════════════════════

        private static Color ReadColor(string key)
        {
            try
            {
                object v = Application.Current != null ? Application.Current.TryFindResource(key) : null;
                var brush = v as SolidColorBrush;
                if (brush != null) return brush.Color;
                if (v is Color) return (Color)v;
            }
            catch { }
            return Colors.Transparent;
        }

        /// <summary>WCAG 相对亮度。</summary>
        public static double Luminance(Color c)
        {
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        private static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        /// <summary>对比度比值（1..21）；忽略 alpha。</summary>
        public static double Ratio(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }

        public static string Hex(Color c)
        {
            return c.R.ToString("X2", CultureInfo.InvariantCulture) +
                   c.G.ToString("X2", CultureInfo.InvariantCulture) +
                   c.B.ToString("X2", CultureInfo.InvariantCulture);
        }

        private static string Pad(string s, int width)
        {
            // 中文按 2 宽度算，保证表格在等宽字体里能对齐
            int w = 0;
            foreach (char c in s ?? "") w += c > 0x2E7F ? 2 : 1;
            var sb = new System.Text.StringBuilder(s ?? "");
            for (int i = w; i < width; i++) sb.Append(' ');
            return sb.ToString();
        }
    }
}
