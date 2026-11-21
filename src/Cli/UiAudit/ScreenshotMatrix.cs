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
            "min", "wide", "eight", "dialog_confirm", "dialog_info", "dialog_prompt", "toast", "drop"
        };

        /// <summary>布局审计覆盖的状态（docs/02 §8 要求）。</summary>
        public static readonly string[] LayoutStates = { "empty", "tasks", "running", "plan", "log", "dialog_confirm" };

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
                case "eight": return "8 级 LOD（角色·极致）";
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
                    s.Vm.TestSetLastRun(true);
                    s.Vm.TestSetStatus("全部完成", "写入 6 个 QC · 24 个模型 · 用时 2 分 14 秒", "Ok");
                    s.Vm.TestSetReadout("24", "个减面任务");
                    s.SelectFirstTask();
                    break;

                case "failed":
                    s.AddMixedTasks();
                    s.Vm.TestSetRunning(false);
                    s.Vm.TestSetProgress(true, 0.62, false, true);
                    s.Vm.TestSetLastRun(false);
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
                    s.Vm.LodPlan.LoadFrom(LodPlan.FromPreset(PresetLibrary.Builtins()[6]), "角色·极致（8级）");
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
                        "Blender：4.1.1（已探测）\n编译器 nekomdl：未设置（可选）\n权限：普通权限",
                        "配置文件：%APPDATA%\\L4D2LOD远景工坊\\settings.ini\n临时目录：%TEMP%\\L4D2LOD远景工坊\n日志：12 条 · 0 错误");
                    break;

                case "dialog_prompt":
                    s.AddMixedTasks();
                    s.OpenPrompt("保存为预设", "预设名称", "角色·8级 轻量");
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
            report.Section("1. 状态矩阵（16 状态 × 浅/暗）");
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
            report.Section("6. 三档缩放（100% / 125% / 150%）");
            report.Info("基准窗口 = 最小尺寸 1120×720（150% 时窗口 1680×1080，不超过本机窗口上限），" +
                        "因此三种缩放的逻辑布局尺寸都是 1120×720。");
            report.Info("做法：窗口尺寸 × scale + 根内容 LayoutTransform(scale)（TestApplyLayoutScale），" +
                        "再以 96dpi 渲染整个窗口 → 像素尺寸 = 1120×720 × scale，等价真实 100%/125%/150% DPI。");
            report.Info("注意：不能把 scale 再传给 UiCapture.CaptureWindow —— 它会给根内容再加一次 LayoutTransform(scale) " +
                        "并按 96×scale dpi 渲染，形成二次缩放（实测 125% 时图像被放大到约 1.56× 且右下裁切）。");

            const double baseW = 1120.0;
            const double baseH = 720.0;
            double[] scales = { 1.0, 1.25, 1.5 };
            int bad = 0;
            foreach (string state in new[] { "empty", "tasks" })
            {
                foreach (double scale in scales)
                {
                    s.ResetToRestState();
                    ApplyState(s, state);
                    s.SetSize(baseW * scale, baseH * scale);
                    s.Win.TestApplyLayoutScale(scale);
                    UiPump.For(240);

                    string pct = (scale * 100).ToString("0");
                    string name = state + "_scale" + pct + ".png";
                    string path = Path.Combine(outDir, name);
                    s.Capture(path);

                    int pw = 0, ph = 0;
                    PngSize(path, out pw, out ph);
                    int expW = (int)Math.Ceiling(baseW * scale);
                    int expH = (int)Math.Ceiling(baseH * scale);
                    bool sizeOk = Math.Abs(pw - expW) <= 2 && Math.Abs(ph - expH) <= 2;
                    report.Info(name + "  (" + pw + "×" + ph + "，期望 " + expW + "×" + expH + ")  " +
                                Label(state) + " · " + pct + "%");
                    if (!sizeOk)
                        report.Detail("像素尺寸与期望不符（窗口可能被系统窗口上限截断，或缩放链路失效）：实际 " + pw + "×" + ph);

                    var res = LayoutAuditor.Audit(s.Root, Label(state) + " " + pct + "%");
                    bool ok = res.Ok && sizeOk;
                    if (!res.Ok || !sizeOk) bad++;
                    report.Check("缩放 " + pct + "% · " + state + " 布局审计", ok,
                        res.Summary + (sizeOk ? "" : "；像素尺寸不符"));
                    if (!res.Ok) PrintDetails(report, res);

                    s.Win.TestClearLayoutScale();
                    s.SetSize(ShellAuditSession.DefaultWidth, ShellAuditSession.DefaultHeight);
                }
            }
            report.Check("三档缩放的布局审计全部 0/0", bad == 0, "6 组（empty/tasks × 100/125/150）");
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
