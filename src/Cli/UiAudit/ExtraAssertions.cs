using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LodStudio.Controls;
using LodStudio.Engine;
using LodStudio.Services;

namespace LodStudio.Cli.UiAudit
{
    /// <summary>
    /// 附加断言（docs/02 §8 与 00 方案 §5.6 的可机检条目）：
    ///  - Toast 与 HUD / 日志矩形不相交；
    ///  - 危险对话框默认焦点在「取消」、Enter 不触发确认、Esc 关闭且 Task 完成；
    ///  - 日志计数文本与行数一致；
    ///  - 8 级 LOD 在 1320×860 / 1280×840 下级别区不需要滚动（1120×720 只记录）；
    ///  - 搜索框有文字时清除按钮可见、无文字时隐藏；
    ///  - HUD 按钮随运行状态切换（扫描预览/开始生成 ↔ 停止）；
    ///  - 空状态 / 无匹配 / 计划视图三种画面各自出现。
    /// </summary>
    internal static class ExtraAssertions
    {
        public static void Run(ShellAuditSession s, AuditReport report)
        {
            report.Section("5. 附加断言");
            s.SetTheme(ThemeKind.Light);
            RestStateInvariants(s, report);
            HudButtons(s, report);
            ToastClearance(s, report);
            DialogFocusAndKeys(s, report);
            DialogButtonsVisible(s, report);
            LogCountText(s, report);
            LogBodyText(s, report);
            QueueRowText(s, report);
            EightLevelScroll(s, report);
            SearchClearButton(s, report);
            StateSwitches(s, report);
            PlanViewContent(s, report);
            s.ResetToRestState();
        }

        // ══════════════════════ 静息状态自检 ══════════════════════

        /// <summary>每次截图前都会 ResetToRestState；这里断言它真的把状态清干净了（防止状态串味到下一张截图）。</summary>
        private static void RestStateInvariants(ShellAuditSession s, AuditReport report)
        {
            s.ResetToRestState();
            int queue = s.Vm.Queue.Items.Count;
            int logs = s.Vm.Log.Lines.Count;
            int levels = s.Vm.LodPlan.Levels.Count;
            bool dialogOpen = s.Dialogs != null && s.Dialogs.IsOpen;
            bool ok = queue == 0 && logs == 0 && !s.Vm.IsPlanVisible && !s.Vm.IsRunning &&
                      !s.Vm.ProgressVisible && levels == 4 && !dialogOpen;
            report.Check("静息状态可复现（空队列 / 空日志 / 4 级默认预设 / 无浮层）", ok,
                "队列 " + queue + " 项，日志 " + logs + " 行，LOD " + levels + " 级，计划视图=" +
                s.Vm.IsPlanVisible + "，运行中=" + s.Vm.IsRunning + "，进度条=" + s.Vm.ProgressVisible +
                "，对话框=" + dialogOpen);
        }

        // ══════════════════════ HUD 按钮 ══════════════════════

        private static void HudButtons(ShellAuditSession s, AuditReport report)
        {
            s.ResetToRestState();
            s.AddMixedTasks();
            UiPump.For(120);
            var preview = s.Named("BtnPreview");
            var start = s.Named("BtnStart");
            var stop = s.Named("BtnStop");
            bool idle = preview != null && preview.IsVisible && start != null && start.IsVisible &&
                        (stop == null || !stop.IsVisible);
            string idleState = Visible(preview) + "/" + Visible(start) + "/" + Visible(stop);

            s.Vm.TestSetRunning(true);
            UiPump.For(160);
            bool running = stop != null && stop.IsVisible && (start == null || !start.IsVisible);
            string runState = Visible(preview) + "/" + Visible(start) + "/" + Visible(stop);
            s.Vm.TestSetRunning(false);
            UiPump.For(120);

            report.Check("HUD 按钮：未运行=扫描预览+开始生成，运行中=停止（互斥）", idle && running,
                "未运行[预览/开始/停止=" + idleState + "]  运行中[" + runState + "]");
        }

        private static string Visible(FrameworkElement fe)
        {
            return fe == null ? "无" : (fe.IsVisible ? "可见" : "隐藏");
        }

        // ══════════════════════ Toast 与 HUD / 日志 ══════════════════════

        private static void ToastClearance(ShellAuditSession s, AuditReport report)
        {
            s.ResetToRestState();
            s.AddMixedTasks();
            s.Win.TestShowToast("完成 · 6 项", "写入 6 个 QC · 用时 2 分 14 秒", ToastTone.Ok);
            UiPump.For(340);

            var toasts = s.Toasts;
            var hud = s.HudPanel();
            var log = s.Named("LogCard");
            Rect tr = s.RectOf(toasts);
            Rect hr = s.RectOf(hud);
            Rect lr = s.RectOf(log);
            double aHud = UiTree.IntersectArea(tr, hr);
            double aLog = UiTree.IntersectArea(tr, lr);
            bool above = !tr.IsEmpty && !hr.IsEmpty && tr.Bottom <= hr.Top + 0.5;

            report.Info("Toast 宿主 " + UiTree.RectText(tr) + " / HUD " + UiTree.RectText(hr) +
                        " / 日志 " + UiTree.RectText(lr));
            report.Check("Toast 与 HUD 矩形不相交（且在 HUD 上方）", aHud <= 0.5 && above,
                "相交面积 " + aHud.ToString("0.#", CultureInfo.InvariantCulture) + "px²，Toast 底边 " +
                (tr.IsEmpty ? "?" : tr.Bottom.ToString("0.#", CultureInfo.InvariantCulture)) + " ≤ HUD 顶边 " +
                (hr.IsEmpty ? "?" : hr.Top.ToString("0.#", CultureInfo.InvariantCulture)));
            report.Check("Toast 与日志矩形不相交", aLog <= 0.5,
                "相交面积 " + aLog.ToString("0.#", CultureInfo.InvariantCulture) + "px²");

            int shown = s.ToastCount;
            var toastWatch = System.Diagnostics.Stopwatch.StartNew();
            bool autoGone = s.WaitToastGone(9000);          // hold 4000（离屏软件渲染下 DispatcherTimer 实际约 1.7×，留足余量）
            toastWatch.Stop();
            report.Check("Toast 自动退场（约 4 秒后清空，不残留到后续界面）",
                shown > 0 && autoGone && !s.ToastForceCleared,
                "显示后 " + shown + " 条，实测 " + toastWatch.ElapsedMilliseconds + "ms 后 " + s.ToastCount +
                " 条（设计停留 4000ms），强制清理=" + s.ToastForceCleared);
        }

        // ══════════════════════ 对话框焦点 / Enter / Esc ══════════════════════

        private static void DialogFocusAndKeys(ShellAuditSession s, AuditReport report)
        {
            s.ResetToRestState();
            s.AddMixedTasks();
            UiPump.For(100);

            DialogHost host = s.Dialogs;
            Task<bool> task = s.OpenConfirmDanger("移除选中的 3 项任务？",
                "只会从列表里移除，不会删除任何文件；再次导入即可恢复。",
                "8人自动编译\n武器包\n道具包");
            bool opened = host != null && host.IsOpen;
            string focus = host != null ? host.DebugFocusText : "(无 DialogHost)";
            bool focusOk = opened && focus.IndexOf("取消", StringComparison.Ordinal) >= 0;
            report.Check("危险对话框默认焦点在「取消」", focusOk, "焦点=" + focus + "，IsOpen=" + opened);

            // Enter 不应触发危险确认（防误删）
            var target = Keyboard.FocusedElement as UIElement ?? (UIElement)host;
            s.RaiseKey(target, Key.Enter);
            UiPump.For(240);
            bool stillOpen = host != null && host.IsOpen;
            report.Check("危险确认对话框不响应 Enter（IsOpen 仍为 true）", stillOpen, "IsOpen=" + stillOpen);

            // Esc 关闭：模拟真实按键（PreviewKeyDown 从窗口隧道到对话框）
            target = Keyboard.FocusedElement as UIElement ?? (UIElement)host;
            s.RaiseKey(target, Key.Escape);
            bool closed = UiPump.Until(delegate { return host == null || !host.IsOpen; }, 1800);
            bool taskDone = UiPump.WaitTask(task, 900);
            report.Check("Esc 关闭对话框（IsOpen → false 且 Task 完成）", closed && taskDone,
                "IsOpen=" + (host == null ? "?" : host.IsOpen.ToString()) + "，Task 完成=" + taskDone);

            s.CloseDialogs();
        }

        // ══════════════════════ 对话框按钮可见性 ══════════════════════

        /// <summary>三种对话框的主/次按钮必须真的渲染出来（面板高度被 MaxHeight 夹住时会整排按钮消失）。</summary>
        private static void DialogButtonsVisible(ShellAuditSession s, AuditReport report)
        {
            s.ResetToRestState();
            s.AddMixedTasks();
            UiPump.For(80);

            s.OpenInfo("运行环境诊断", "Blender：4.1.1（已探测）\n编译器 nekomdl：未设置（可选）\n权限：普通权限",
                "配置文件：%APPDATA%\\L4D2LOD远景工坊\\settings.ini\n临时目录：%TEMP%\\L4D2LOD远景工坊\n日志：12 条 · 0 错误");
            bool infoOk = ButtonShown(s, "确定");
            string infoPanel = PanelSize(s);
            s.CloseDialogs();

            s.OpenConfirmDanger("移除选中的 3 项任务？", "只会从列表里移除，不会删除任何文件；再次导入即可恢复。",
                "8人自动编译\n武器包\n道具包");
            bool confirmOk = ButtonShown(s, "取消") && ButtonShown(s, "删除");
            string confirmPanel = PanelSize(s);
            s.CloseDialogs();

            s.OpenPrompt("保存为预设", "预设名称", "角色·8级 轻量");
            bool promptOk = ButtonShown(s, "取消") && ButtonShown(s, "确定");
            string promptPanel = PanelSize(s);
            s.CloseDialogs();

            report.Check("三种对话框的按钮在可视范围内（取消/确定/删除）", infoOk && confirmOk && promptOk,
                "info=" + infoOk + "(" + infoPanel + ")，confirm=" + confirmOk + "(" + confirmPanel +
                ")，prompt=" + promptOk + "(" + promptPanel + ")");
        }

        private static bool ButtonShown(ShellAuditSession s, string content)
        {
            var host = s.Dialogs;
            if (host == null) return false;
            var buttons = new List<Button>();
            UiTree.CollectAll<Button>(host, buttons);
            foreach (Button b in buttons)
            {
                if (string.Equals(b.Content as string, content, StringComparison.Ordinal))
                {
                    Rect r = s.RectOf(b);
                    return b.IsVisible && r.Width > 20 && r.Height > 20;
                }
            }
            return false;
        }

        private static string PanelSize(ShellAuditSession s)
        {
            try
            {
                var host = s.Dialogs;
                if (host == null) return "?";
                var field = host.GetType().GetField("_panel",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var panel = field != null ? field.GetValue(host) as FrameworkElement : null;
                return panel == null ? "?" : panel.ActualWidth.ToString("0.#") + "×" + panel.ActualHeight.ToString("0.#");
            }
            catch { return "?"; }
        }

        // ══════════════════════ 日志计数 ══════════════════════

        private static void LogCountText(ShellAuditSession s, AuditReport report)
        {
            s.ResetToRestState();
            s.AddMixedTasks();
            s.Win.TestSetLogExpanded(true);
            s.Vm.TestFillLogs(22, true);
            UiPump.For(260);

            string text = s.Vm.Log.CountText;
            int lines = s.Vm.Log.Lines.Count;
            int declared = LeadingInt(text);
            report.Check("日志计数文本非空且与行数一致", !string.IsNullOrEmpty(text) && declared == lines,
                "CountText=\"" + text + "\"  行数=" + lines);
        }

        private static int LeadingInt(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            int i = 0;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            if (i == 0) return -1;
            int v;
            return int.TryParse(text.Substring(0, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        /// <summary>日志正文必须真的渲染出来（WPF 绑定只能绑属性；绑到字段会静默失败成空白）。</summary>
        private static void LogBodyText(ShellAuditSession s, AuditReport report)
        {
            s.ResetToRestState();
            s.AddMixedTasks();
            s.Win.TestSetLogExpanded(true);
            s.Vm.TestFillLogs(22, true);
            UiPump.For(300);

            var logList = s.Named("LogList");
            var texts = new List<TextBlock>();
            if (logList != null) UiTree.CollectAll<TextBlock>(logList, texts);
            int body = 0;
            string sample = "";
            foreach (TextBlock t in texts)
            {
                if (t == null || t.Visibility != Visibility.Visible || !t.IsVisible) continue;
                if (string.IsNullOrEmpty(t.Text) || t.Text.Length <= 8 || t.ActualWidth <= 4) continue;
                body++;
                if (sample.Length == 0) sample = t.Text;
            }
            int lines = s.Vm.Log.Lines.Count;
            report.Check("日志正文在界面上可见（LogLineVm.Text 绑定生效）", body >= 3,
                "渲染出正文的文本块 " + body + " 个 / 日志 " + lines + " 行" +
                (sample.Length > 0 ? "，示例=\"" + Clip(sample) + "\"" : "，正文文本块为空（绑定失败）"));
        }

        /// <summary>队列行的名称/类型文本必须真的渲染出来（同样的绑定失败会静默成空白）。</summary>
        private static void QueueRowText(ShellAuditSession s, AuditReport report)
        {
            s.ResetToRestState();
            s.AddMixedTasks();
            UiPump.For(220);

            var queueList = s.Named("QueueList");
            var texts = new List<TextBlock>();
            if (queueList != null) UiTree.CollectAll<TextBlock>(queueList, texts);
            bool foundName = false, foundPath = false, foundKind = false;
            foreach (TextBlock t in texts)
            {
                if (t == null || t.Visibility != Visibility.Visible || string.IsNullOrEmpty(t.Text)) continue;
                if (t.Text.IndexOf("actor.qc", StringComparison.Ordinal) >= 0) foundName = true;
                if (t.Text.IndexOf("E:\\L4D2", StringComparison.Ordinal) >= 0) foundPath = true;
                if (t.Text == "工程" || t.Text == "QC") foundKind = true;
            }
            report.Check("队列行文本可见（名称 / 路径 / 类型绑定生效）", foundName && foundPath && foundKind,
                "名称=" + foundName + "，路径=" + foundPath + "，类型=" + foundKind);
        }

        // ══════════════════════ 计划视图内容 / 覆盖层 z 序 ══════════════════════

        /// <summary>
        /// 计划视图两项机检：
        ///  1) 计划行必须有文字（PlanQcRowVm 的字段若不是属性，WPF 绑定会静默失败 → 6 行全空白）；
        ///  2) 计划覆盖层（PlanOverlay，不透明）之后声明的兄弟（队列页脚 + 1px 分隔线）不能压在它上面
        ///     （WPF 里后声明的兄弟 z 序更高，页脚会画在计划视图底部按钮上）。
        /// </summary>
        private static void PlanViewContent(ShellAuditSession s, AuditReport report)
        {
            s.ResetToRestState();
            s.AddMixedTasks();
            s.Vm.TestSetPlanPreview(6, 12, 24);
            UiPump.For(360);

            var overlay = s.Named("PlanOverlay");
            int rows = s.Vm.PlanPreview != null ? s.Vm.PlanPreview.Rows.Count : -1;
            bool overlayVisible = overlay != null && overlay.IsVisible;
            report.Check("计划视图出现且计划行已装载", overlayVisible && rows == 6,
                "PlanOverlay 可见=" + overlayVisible + "，Rows=" + rows);

            int nonEmpty = 0;
            string sample = "";
            if (overlay != null)
            {
                var texts = new List<TextBlock>();
                UiTree.CollectAll<TextBlock>(overlay, texts);
                foreach (TextBlock t in texts)
                {
                    if (t == null || !t.IsVisible || string.IsNullOrEmpty(t.Text)) continue;
                    nonEmpty++;
                    if (sample.Length == 0 && t.Text.Length > 2) sample = t.Text;
                }
            }
            report.Check("计划行文本可见（PlanQcRowVm 绑定生效）", nonEmpty >= 12,
                "非空文本块 " + nonEmpty + " 个（6 行 × 至少 2 列）" +
                (sample.Length > 0 ? "，示例=\"" + Clip(sample) + "\"" : "，计划行全空白（绑定失败）"));

            // 覆盖层 z 序：PlanOverlay 之后声明的可见兄弟不应与它相交
            string hit = "";
            if (overlay != null)
            {
                var parent = UiTree.ParentOf(overlay);
                if (parent != null)
                {
                    int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
                    int index = -1;
                    for (int i = 0; i < count; i++)
                    {
                        if (ReferenceEquals(System.Windows.Media.VisualTreeHelper.GetChild(parent, i), overlay)) { index = i; break; }
                    }
                    Rect overlayRect = s.RectOf(overlay);
                    for (int i = index + 1; i < count; i++)
                    {
                        var sibling = System.Windows.Media.VisualTreeHelper.GetChild(parent, i) as FrameworkElement;
                        if (sibling == null || !sibling.IsVisible) continue;
                        Rect r = s.RectOf(sibling);
                        double area = UiTree.IntersectArea(overlayRect, r);
                        if (area > 1.0)
                            hit += UiTree.Describe(sibling) + " " + UiTree.RectText(r) + " 相交 " +
                                   area.ToString("0.#", CultureInfo.InvariantCulture) + "px²；";
                    }
                }
            }
            report.Check("计划视图不被队列页脚覆盖（PlanOverlay 与页脚 z 序正确）", hit.Length == 0,
                hit.Length == 0 ? "覆盖层之后的兄弟无相交" : "压住计划视图的兄弟：" + hit);
        }

        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 24 ? s : s.Substring(0, 24) + "…";
        }

        // ══════════════════════ 8 级 LOD 滚动 ══════════════════════

        private static void EightLevelScroll(ShellAuditSession s, AuditReport report)
        {
            s.ResetToRestState();
            s.AddMixedTasks();
            s.Vm.LodPlan.LoadFrom(LodPlan.FromPreset(PresetLibrary.Builtins()[6]), "角色·极致（8级）");
            UiPump.For(260);

            int levelCount = s.Vm.LodPlan.Levels.Count;
            report.Check("8 级 LOD 已装载（8 级 · 「添加一级」隐藏）",
                levelCount == 8 && !s.Vm.LodPlan.CanAddLevel, "级别数=" + levelCount + "，CanAddLevel=" + s.Vm.LodPlan.CanAddLevel);

            foreach (var size in new[] { new { W = 1320.0, H = 860.0 }, new { W = 1280.0, H = 840.0 }, new { W = 1120.0, H = 720.0 } })
            {
                s.SetSize(size.W, size.H);
                UiPump.For(300);
                var levels = s.Named("LevelList");
                var sep = s.Named("WriteSeparator");
                var items = levels as ItemsControl;
                int rows = items != null ? items.Items.Count : 0;
                FrameworkElement lastRow = rows > 0 && items != null
                    ? items.ItemContainerGenerator.ContainerFromIndex(rows - 1) as FrameworkElement : null;
                Rect last = lastRow != null ? UiTree.Slot(lastRow, s.Root) : Rect.Empty;
                Rect sepRect = sep != null ? UiTree.Slot(sep, s.Root) : Rect.Empty;
                bool noScroll = UiTree.FindAncestor<ScrollViewer>(levels) == null;
                double rowHeight = lastRow != null ? lastRow.ActualHeight : -1;
                report.Check("8 级 LOD · " + size.W.ToString("0") + "×" + size.H.ToString("0") + " 行高固定 40（不铺满，4–7 级正常留空）",
                    noScroll && rows == 8 && Math.Abs(rowHeight - 40.0) <= 1.0,
                    "行数=" + rows + "，最后一行高=" + Num(rowHeight) + "，ScrollViewer=" + !noScroll +
                    "，表格底到分隔线=" + Num(!last.IsEmpty && !sepRect.IsEmpty ? sepRect.Top - last.Bottom : -999));

                var bottomFrame = s.Named("LodBottomFrame");
                Rect bf = bottomFrame != null ? UiTree.Slot(bottomFrame, s.Root) : Rect.Empty;
                var topFrame = s.Named("LodTopFrame");
                Rect tf = topFrame != null ? UiTree.Slot(topFrame, s.Root) : Rect.Empty;
                report.Check("8 级 LOD · " + size.W.ToString("0") + "×" + size.H.ToString("0") + " 外框上下边线可见",
                    bottomFrame != null && topFrame != null && bf.Height >= 0.5 && bf.Width > 100 &&
                    tf.Height >= 0.5 && tf.Width > 100,
                    "上边线=" + Num(tf.Top) + "/h" + Num(tf.Height) + "/w" + Num(tf.Width) +
                    "，下边线=" + Num(bf.Top) + "/h" + Num(bf.Height) + "/w" + Num(bf.Width));
            }
        }

        private static string Num(double v)
        {
            return v < 0 ? "?" : v.ToString("0.#", CultureInfo.InvariantCulture);
        }

        // ══════════════════════ 搜索清空按钮 ══════════════════════

        private static void SearchClearButton(ShellAuditSession s, AuditReport report)
        {
            s.ResetToRestState();
            s.AddMixedTasks();
            UiPump.For(120);
            var clear = s.Named("BtnClearSearch");
            bool hiddenEmpty = clear != null && !clear.IsVisible;

            s.Vm.TestSetSearch("zzz");
            UiPump.For(220);
            bool visibleWithText = clear != null && clear.IsVisible;

            s.Vm.TestSetSearch("");
            UiPump.For(220);
            bool hiddenAgain = clear != null && !clear.IsVisible;

            report.Check("搜索框清除按钮：无文字隐藏 / 有文字可见 / 清空后隐藏",
                hiddenEmpty && visibleWithText && hiddenAgain,
                "无文字=" + (clear == null ? "未找到" : (hiddenEmpty ? "隐藏" : "可见")) +
                "，有文字=" + (visibleWithText ? "可见" : "隐藏") +
                "，清空后=" + (hiddenAgain ? "隐藏" : "可见"));
        }

        // ══════════════════════ 三种空画面 ══════════════════════

        private static void StateSwitches(ShellAuditSession s, AuditReport report)
        {
            s.ResetToRestState();
            var empty = s.Named("EmptyState");
            var noMatch = s.Named("NoMatchState");
            var plan = s.Named("PlanOverlay");
            bool r1 = empty != null && empty.IsVisible && (noMatch == null || !noMatch.IsVisible) &&
                      (plan == null || !plan.IsVisible);

            s.AddMixedTasks();
            s.Vm.TestSetSearch("zzz");
            UiPump.For(220);
            bool r2 = noMatch != null && noMatch.IsVisible && (empty == null || !empty.IsVisible);

            s.ResetToRestState();
            s.AddMixedTasks();
            s.Vm.TestSetPlanPreview(2, 4, 8);
            UiPump.For(220);
            bool r3 = plan != null && plan.IsVisible;

            report.Check("三种画面各自出现：空队列 / 无匹配 / 计划视图", r1 && r2 && r3,
                "空状态=" + r1 + "，无匹配=" + r2 + "，计划视图=" + r3);
        }
    }
}
