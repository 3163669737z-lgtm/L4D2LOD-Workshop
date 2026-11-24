using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LodStudio.Controls;
using LodStudio.Engine;
using LodStudio.Services;
using LodStudio.ViewModels;

namespace LodStudio.Cli.UiAudit
{
    /// <summary>
    /// 状态矩阵 / 主题往返 / 三档缩放：把主窗口推到各个状态并截图。
    /// 状态注入只用 ShellWindow.TestHooks + ShellViewModel.TestHooks（不写用户文件、不启动引擎）。
    /// </summary>
    internal static class ScreenshotMatrix
    {
        /// <summary>状态矩阵（浅/暗各一套）。</summary>
        public static readonly string[] MatrixStates =
        {
            "empty", "tasks", "running", "done", "failed", "nomatch", "plan", "log",
            "min", "wide", "eight", "auto_plan", "stale_plan", "cancelled", "dialog_confirm", "dialog_info", "dialog_prompt", "toast", "drop"
        };

        /// <summary>布局审计覆盖的状态（docs/02 §8 要求）。</summary>
        public static readonly string[] LayoutStates = { "empty", "tasks", "running", "plan", "auto_plan", "stale_plan", "log", "dialog_confirm" };

        public static string Label(string state)
        {
            switch (state)
            {
                case "empty": return "空队列";
                case "tasks": return "有任务（6 项混合状态）";
                case "running": return "运行中（35% · 正在生成 3/8）";
                case "done": return "完成态（进度满格 · 报告/目录按钮）";
                case "failed": return "失败态（进度条红 · Err）";
                case "nomatch": return "搜索无匹配";
                case "plan": return "计划视图";
                case "log": return "日志展开（22 行 · 含错误）";
                case "min": return "最小窗口 1120×720";
                case "wide": return "宽屏 1600×900";
                case "eight": return LodPlan.MaxLevels + " 级 LOD";
                case "auto_plan": return "自动挡与计划";
                case "stale_plan": return "过期预览与展开的日志";
                case "cancelled": return "已停止";
                case "dialog_confirm": return "危险确认框";
                case "dialog_info": return "信息框（带详情）";
                case "dialog_prompt": return "文本输入框";
                case "toast": return "Toast（Ok 语义）";
                case "drop": return "拖放高亮";
                default: return state;
            }
        }

        // ══════════════════════ 状态注入 ══════════════════════

        public static void ApplyState(ShellAuditSession s, string state)
        {
            switch (state)
            {
                case "empty":
                    break;

                case "tasks":
                    s.AddMixedTasks();
                    s.SelectFirstTask();
                    break;

                case "running":
                    s.AddMixedTasks();
                    if (s.Vm.Queue.Items.Count > 1) s.Vm.Queue.Items[1].Status = TaskStatus.Running;
                    s.Vm.TestSetRunning(true);
                    s.Vm.TestSetStatus("正在生成 3 / 8", "已用 1 分 12 秒 · 剩余约 2 分钟", "Signal");
                    s.Vm.TestSetProgress(true, 0.35, false, false);
                    s.Vm.TestSetReadout("3", "个减面任务");
                    s.SelectFirstTask();
                    break;

                case "done":
                    s.AddMixedTasks();
                    s.Vm.TestSetRunning(false);
                    s.Vm.TestSetProgress(true, 1.0, false, false);
                    s.Vm.TestSetLastRun(true, Path.Combine(s.OutDir, "progress.txt"), s.OutDir);
                    s.Vm.TestSetStatus("全部完成", "写入 6 个 QC · 24 个模型 · 用时 2 分 14 秒", "Ok");
                    s.Vm.TestSetReadout("24", "个减面任务");
                    s.SelectFirstTask();
                    break;

                case "failed":
                    s.AddMixedTasks();
                    s.Vm.TestSetRunning(false);
                    s.Vm.TestSetProgress(true, 0.62, false, true);
                    s.Vm.TestSetLastRun(false, Path.Combine(s.OutDir, "progress.txt"), null);
                    s.Vm.TestSetStatus("运行失败", "5 个完成 · 1 个失败 · 详见运行日志", "Err");
                    s.Vm.TestSetReadout("5", "个减面任务");
                    s.SelectFirstTask();
                    break;

                case "nomatch":
                    s.AddMixedTasks();
                    s.Vm.TestSetSearch("zzz");
                    break;

                case "plan":
                    s.AddMixedTasks();
                    s.Vm.TestSetPlanPreview(6, 12, 24);
                    break;

                case "auto_plan":
                    s.AddMixedTasks();
                    s.Vm.LodPlan.SelectedPresetIndex = PresetLibrary.AutomaticIndex;
                    s.Vm.TestSetPlanPreview(3, 18, 50);
                    s.Win.TestSetLogExpanded(true);
                    break;

                case "stale_plan":
                    s.AddMixedTasks();
                    s.Vm.LodPlan.LoadFrom(WidgetPreviewRunner.MaxLevelPlan(), PresetLibrary.CustomName);
                    s.Vm.TestSetPlanPreview(6, 12, 42);
                    s.Vm.OnWriteModeChanged(1);
                    s.Vm.TestFillLogs(22, false);
                    s.Win.TestSetLogExpanded(true);
                    break;

                case "cancelled":
                    s.AddMixedTasks();
                    s.Vm.Queue.Items[0].Status = TaskStatus.Cancelled;
                    s.Vm.TestSetLastRun(false, Path.Combine(s.OutDir, "progress.txt"), null);
                    s.Vm.TestSetStatus("已停止", "1 项未完成 · 用时 3 秒", "Warn");
                    s.Vm.TestSetReadout("0", "项完成");
                    break;

                case "log":
                    s.AddMixedTasks();
                    s.Win.TestSetLogExpanded(true);
                    s.Vm.TestFillLogs(22, true);
                    break;

                case "min":
                    s.AddMixedTasks();
                    s.SelectFirstTask();
                    s.SetSize(1120, 720);
                    break;

                case "wide":
                    s.AddMixedTasks();
                    s.SelectFirstTask();
                    s.SetSize(1600, 900);
                    break;

                case "eight":
                    s.AddMixedTasks();
                    s.Vm.LodPlan.LoadFrom(WidgetPreviewRunner.MaxLevelPlan(), PresetLibrary.CustomName);
                    break;

                case "dialog_confirm":
                    s.AddMixedTasks();
                    s.OpenConfirmDanger("移除选中的 3 项任务？",
                        "只会从列表里移除，不会删除任何文件；再次导入即可恢复。",
                        "8人自动编译\n武器包\n道具包\n\n（队列里的 3 项会被移除）");
                    break;

                case "dialog_info":
                    s.AddMixedTasks();
                    s.OpenInfo("运行环境诊断",
                        "减面后端：内置 meshoptimizer\n编译器 nekomdl：未设置（可选）\n权限：普通权限",
                        "配置文件：%APPDATA%\\L4D2LOD远景工坊\\settings.ini\n临时目录：%TEMP%\\L4D2LOD远景工坊\n日志：12 条 · 0 错误");
                    break;

                case "dialog_prompt":
                    s.AddMixedTasks();
                    s.OpenPrompt("输入名称", "名称", "测试名称");
                    break;

                case "toast":
                    s.AddMixedTasks();
                    s.Win.TestShowToast("完成 · 6 项", "写入 6 个 QC · 用时 2 分 14 秒", ToastTone.Ok);
                    break;

                case "drop":
                    s.AddMixedTasks();
                    s.Win.TestShowDropOverlay(true);
                    break;

                default:
                    throw new ArgumentException("未知状态：" + state, "state");
            }
        }

        /// <summary>等到该状态的动画/布局到达稳态再截图。</summary>
        public static void SettleState(ShellAuditSession s, string state)
        {
            switch (state)
            {
                case "toast": UiPump.For(260); break;          // 入场 160ms + 堆叠重排
                case "log": UiPump.For(340); break;            // 展开 180ms 缓动 + 滚到底
                case "eight": UiPump.For(280); break;
                case "min":
                case "wide": UiPump.For(260); break;
                case "dialog_confirm":
                case "dialog_info":
                case "dialog_prompt": UiPump.For(600); break;   // 打开动画 160ms + 面板尺寸/焦点稳定
                default: UiPump.For(160); break;
            }
            s.Win.UpdateLayout();
        }

        // ══════════════════════ ① 状态矩阵 ══════════════════════

        public static void RunMatrix(ShellAuditSession s, AuditReport report, string outDir)
        {
            report.Section("1. 状态矩阵（" + MatrixStates.Length + " 状态 × 浅/暗）");
            var files = new List<string>();
            int shots = 0;
            int bad = 0;

            foreach (ThemeKind theme in new[] { ThemeKind.Light, ThemeKind.Dark })
            {
                string themeName = theme == ThemeKind.Light ? "light" : "dark";
                s.SetTheme(theme);
                foreach (string state in MatrixStates)
                {
                    s.ResetToRestState();
                    ApplyState(s, state);
                    SettleState(s, state);
                    string file = state + "_" + themeName + ".png";
                    string path = Path.Combine(outDir, file);
                    int toastBefore = s.ToastCount;
                    try
                    {
                        s.Capture(path);
                    }
                    catch (Exception ex)
                    {
                        bad++;
                        report.Detail("截图失败 " + file + " : " + ex.Message);
                        s.CloseDialogs();
                        continue;
                    }
                    long len = 0;
                    int pw = 0, ph = 0;
                    try
                    {
                        var fi = new FileInfo(path);
                        len = fi.Exists ? fi.Length : 0;
                        PngSize(path, out pw, out ph);
                    }
                    catch { }
                    bool ok = len > 2048 && pw > 1000;
                    if (state != "toast" && toastBefore > 0)
                    {
                        // 非 toast 状态不该有 Toast：说明上一条没退场，会污染截图
                        ok = false;
                        report.Detail(file + " 截图前有 " + toastBefore + " 条 Toast 未退场");
                    }
                    if (!ok) bad++;
                    shots++;
                    files.Add(file + "  (" + pw + "×" + ph + ", " + (len / 1024) + " KB)  " + Label(state) +
                              (toastBefore > 0 ? "  [toast=" + toastBefore + "]" : ""));
                    s.CloseDialogs();
                    s.WaitToastGone(9500);
                }
            }

            foreach (string f in files) report.Info(f);
            report.Info("");
            report.Check("状态矩阵截图（" + MatrixStates.Length + " 状态 × 浅/暗）", bad == 0,
                shots + " 张 PNG，失败 " + bad + " 张");
        }

        // ══════════════════════ ② 主题往返 ══════════════════════

        public static void RunThemeRoundTrip(ShellAuditSession s, AuditReport report, string outDir)
        {
            report.Section("2. 主题往返（浅 → 暗 → 浅，逐像素）");

            s.ResetToRestState();
            s.SetTheme(ThemeKind.Light);
            UiPump.For(220);
            string a = Path.Combine(outDir, "theme_roundtrip_light.png");
            string b = Path.Combine(outDir, "theme_roundtrip_dark.png");
            string c = Path.Combine(outDir, "theme_roundtrip_light_again.png");

            s.Capture(a);
            s.SetTheme(ThemeKind.Dark);
            UiPump.For(220);
            s.Capture(b);
            s.SetTheme(ThemeKind.Light);
            UiPump.For(220);
            s.Capture(c);

            int aw, ah;
            PngSize(a, out aw, out ah);
            report.Info("静息状态 = empty（无 Toast / 无动画 / 日志清空 / 环境读数固定）  " + aw + "×" + ah);
            report.Info("A = theme_roundtrip_light.png / B = theme_roundtrip_dark.png / C = theme_roundtrip_light_again.png");

            var roundTrip = UiCapture.Compare(a, c);
            var themeDiff = UiCapture.Compare(a, b);
            report.Info("A→C : " + roundTrip);
            report.Info("A→B : " + themeDiff);

            report.Check("主题往返 · 浅→暗→浅 逐像素一致（差异像素 0 / 最大通道差 0）",
                roundTrip.Different == 0 && roundTrip.MaxChannelDelta == 0, roundTrip.ToString());
            report.Check("主题切换 · 浅色 vs 暗色差异 > 90%（证明整体换色）",
                themeDiff.Percent > 90.0, themeDiff.ToString());
        }

        // ══════════════════════ ⑥ 三档缩放 ══════════════════════

        public static void RunScaling(ShellAuditSession s, AuditReport report, string outDir)
        {
            report.Section("6. 三档离屏缩放（100% / 125% / 150%）");
            report.Info("按目标 DPI 渲染 PNG，逻辑尺寸不变。覆盖两种主题、默认和最小窗口。");
            report.Info("原生窗口 DPI 为 " + System.Windows.Media.VisualTreeHelper.GetDpi(s.Win).PixelsPerDip.ToString("P0") + "。");

            var sizes = new[] { (W: 1320, H: 860, Label: "default"), (W: 1120, H: 720, Label: "min") };
            var states = new[] { "empty", "eight", "auto_plan", "stale_plan", "dialog_confirm" };
            double[] scales = { 1.0, 1.25, 1.5 };
            int groups = 0, bad = 0;
            foreach (ThemeKind theme in new[] { ThemeKind.Light, ThemeKind.Dark })
            {
                s.SetTheme(theme);
                foreach (var size in sizes)
                {
                    foreach (string state in states)
                    {
                        s.ResetToRestState();
                        s.SetSize(size.W, size.H);
                        ApplyState(s, state);
                        SettleState(s, state);
                        string tag = state + "_" + size.Label + "_" + theme.ToString().ToLowerInvariant();
                        var layout = LayoutAuditor.Audit(s.Root, tag);
                        if (!layout.Ok) PrintDetails(report, layout);
                        foreach (double scale in scales)
                        {
                            string pct = (scale * 100).ToString("0");
                            string name = tag + "_scale" + pct + ".png";
                            string path = Path.Combine(outDir, name);
                            s.Capture(path, scale);
                            PngSize(path, out int pw, out int ph);
                            bool sizeOk = Math.Abs(pw - size.W * scale) <= 1 && Math.Abs(ph - size.H * scale) <= 1;
                            bool ok = layout.Ok && sizeOk;
                            groups++;
                            if (!ok) bad++;
                            report.Check(name, ok, pw + "×" + ph + " · " + layout.Summary);
                        }
                        s.CloseDialogs();
                    }
                }
            }
            report.Check("三档缩放布局与截图尺寸", bad == 0, groups + " 组，失败 " + bad + " 组");
        }

        // ══════════════════════ 工具 ══════════════════════

        public static void PrintDetails(AuditReport report, LayoutAuditResult res)
        {
            foreach (string d in res.OverlapDetails) report.Detail(d);
            foreach (string d in res.OutOfBoundsDetails) report.Detail(d);
        }

        public static void PngSize(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var dec = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    width = dec.Frames[0].PixelWidth;
                    height = dec.Frames[0].PixelHeight;
                }
            }
            catch { }
        }
    }
}
