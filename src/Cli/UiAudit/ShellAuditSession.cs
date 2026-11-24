using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LodStudio.Controls;
using LodStudio.Engine;
using LodStudio.Services;
using LodStudio.ViewModels;
using LodStudio.Views;
using TaskStatus = LodStudio.ViewModels.TaskStatus;

namespace LodStudio.Cli.UiAudit
{
    /// <summary>
    /// 一次 --ui-audit 的离屏主窗口会话：负责创建窗口、注入状态、切换主题、截图、清理对话框。
    /// 所有方法都在 UI 线程上调用（Run 本身就在 UI 线程）；不做阻塞等待（见 <see cref="UiPump"/>）。
    /// </summary>
    internal sealed class ShellAuditSession : IDisposable
    {
        public const double DefaultWidth = 1320.0;
        public const double DefaultHeight = 860.0;

        private double _width = DefaultWidth;
        private double _height = DefaultHeight;
        private Task _lastDialog;

        public ShellWindow Win { get; private set; }
        public ShellViewModel Vm { get; private set; }
        public string OutDir { get; private set; }

        /// <summary>关对话框时用了 ForceCloseForTest 兜底（说明 ui-widgets 的 Finish 还没修）。</summary>
        public bool UsedForceClose { get; private set; }

        // ══════════════════════ 创建 / 销毁 ══════════════════════

        public static ShellAuditSession Create(string outDir)
        {
            var win = new ShellWindow(false)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                Width = DefaultWidth,
                Height = DefaultHeight,
                WindowState = WindowState.Normal,
                Title = AppInfo.TitleText
            };
            win.Show();
            try { win.Activate(); } catch { }
            win.UpdateLayout();
            UiPump.For(420);

            var session = new ShellAuditSession
            {
                Win = win,
                Vm = win.Vm,
                OutDir = outDir
            };
            session.SettleEnvironment();
            session.PinEnvironment();
            session.ResetToRestState();
            return session;
        }

        public void Dispose()
        {
            try
            {
                CloseDialogs();
                if (Win != null) Win.Close();
            }
            catch { }
            UiPump.For(120);
        }

        // ══════════════════════ 常用元素 ══════════════════════

        /// <summary>布局审计的根：自绘窗口边框（窗口内容的可视根）。</summary>
        public FrameworkElement Root { get { return (FrameworkElement)Win.RootVisual; } }

        /// <summary>窗口内容根（包含主界面 + 覆盖层，用作跨覆盖层矩形比较的公共祖）。</summary>
        public FrameworkElement ContentRoot
        {
            get
            {
                try { return Win.Content as FrameworkElement; } catch { return null; }
            }
        }

        public DialogHost Dialogs
        {
            get { try { return Win.FindName("Dialogs") as DialogHost; } catch { return null; } }
        }

        public ToastHost Toasts
        {
            get { try { return Win.FindName("Toasts") as ToastHost; } catch { return null; } }
        }

        public FrameworkElement Named(string name)
        {
            return UiTree.Named(Win, name);
        }

        /// <summary>当前 Toast 条数（ToastHost 自绘、没有公开计数，用反射读私有 _items，仅供审计诊断）。</summary>
        public int ToastCount
        {
            get
            {
                try
                {
                    ToastHost host = Toasts;
                    if (host == null) return -1;
                    var field = typeof(ToastHost).GetField("_items",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field == null) return -1;
                    var list = field.GetValue(host) as System.Collections.ICollection;
                    return list == null ? -1 : list.Count;
                }
                catch { return -1; }
            }
        }

        /// <summary>等 Toast 完全退场（最多 timeoutMs）；返回是否已清零。</summary>
        public bool WaitToastGone(int timeoutMs = 6000)
        {
            if (ToastCount <= 0) return true;
            if (UiPump.Until(delegate { return ToastCount <= 0; }, timeoutMs)) return true;
            // 兜底：直接清空自绘项并停表（只用于审计截图，避免残留 Toast 污染下一张图）
            ForceClearToasts();
            ToastForceCleared = true;
            return ToastCount <= 0;
        }

        /// <summary>审计里用过"强行清空 Toast"兜底（说明 ToastHost 计时器没把条目自然退场）。</summary>
        public bool ToastForceCleared { get; private set; }

        private void ForceClearToasts()
        {
            try
            {
                ToastHost host = Toasts;
                if (host == null) return;
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var field = typeof(ToastHost).GetField("_items", flags);
                var list = field != null ? field.GetValue(host) as System.Collections.IList : null;
                if (list != null) list.Clear();
                var timerField = typeof(ToastHost).GetField("_timer", flags);
                var timer = timerField != null ? timerField.GetValue(host) as System.Windows.Threading.DispatcherTimer : null;
                if (timer != null) timer.Stop();
                host.InvalidateVisual();
            }
            catch { }
        }

        public FrameworkElement HudPanel()
        {
            var rootBorder = Named("RootBorder") as Border;
            if (rootBorder != null)
            {
                var grid = rootBorder.Child as Grid;
                if (grid != null && grid.Children.Count >= 3)
                {
                    var hud = grid.Children[2] as FrameworkElement;
                    if (hud != null) return hud;
                }
            }
            var borders = new List<Border>();
            UiTree.CollectAll<Border>(Root, borders);
            foreach (var b in borders)
            {
                try { if (Grid.GetRow(b) == 2) return b; }
                catch { }
            }
            return null;
        }

        public Rect RectOf(FrameworkElement fe)
        {
            if (fe == null) return Rect.Empty;
            FrameworkElement common = ContentRoot;
            if (common == null) return UiTree.Slot(fe, Root);
            return UiTree.Slot(fe, common);
        }

        // ══════════════════════ 主题 / 环境 ══════════════════════

        public void SetTheme(ThemeKind kind)
        {
            ThemeService.Apply(kind);
            Win.UpdateLayout();
            UiPump.For(110);              // DynamicResource 重绘 + 自绘控件 InvalidateVisual
            Win.UpdateLayout();
        }

        /// <summary>
        /// 等 Loaded 里那个异步环境检测跑完并稳定，
        /// 否则它会在截图过程中往日志/顶栏里写东西，主题往返就不可能逐像素一致。
        /// </summary>
        public void SettleEnvironment(int timeoutMs = 25000)
        {
            var sw = Stopwatch.StartNew();
            string last = null;
            int stable = 0;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                string cur = Vm.ReductionText + "|" + Vm.CompilerText + "|" + Vm.PermissionText + "|" +
                             Vm.Log.Lines.Count + "|" + Vm.Queue.Items.Count;
                if (cur == last)
                {
                    stable++;
                    if (stable >= 10) break;      // 连续 400ms 没有变化 → 认为稳定
                }
                else
                {
                    stable = 0;
                    last = cur;
                }
                UiPump.For(40);
            }
        }

        /// <summary>把顶栏三组只读读数固定成确定值，保证截图/往返可复现（不触发真实探测）。</summary>
        public void PinEnvironment()
        {
            try { Vm.TestSetEnvStrings("内置 meshoptimizer", "Ok", "未设置", "Faint", "普通权限", "Faint"); } catch { }
            UiPump.For(60);
        }

        // ══════════════════════ 尺寸 / 静息状态 ══════════════════════

        public void SetSize(double w, double h)
        {
            if (Math.Abs(_width - w) < 0.5 && Math.Abs(_height - h) < 0.5) return;
            _width = w;
            _height = h;
            try { Win.TestSetSize(w, h); } catch { }
            Win.UpdateLayout();
            UiPump.For(160);
        }

        /// <summary>回到"静息状态"：空队列、无日志、无对话框/Toast/拖放层、默认尺寸、默认 LOD 方案、1.0 缩放。</summary>
        public void ResetToRestState()
        {
            CloseDialogs();
            try { Win.TestShowDropOverlay(false); } catch { }
            SetSize(DefaultWidth, DefaultHeight);
            try { Win.TestSetLogExpanded(false); } catch { }
            Vm.TestResetState();
            // 复位到默认预设，避免上一张截图的档位和写入方式影响后续状态。
            try
            {
                Vm.LodPlan.LoadFrom(LodPlan.CreateDefault(), PresetLibrary.DefaultName);
                Vm.LodPlan.WriteMode = 0;
            }
            catch { }
            // TestResetState 不重发 CanStart/CanPreview（IsRunning 已经是 false 时 Set 不触发）：
            // 不补这一步，上一状态（有任务）的按钮可用态会残留到空队列截图里。
            try { Vm.RaiseReady(); } catch { }
            // 上一状态的 Toast 必须完全退场，否则会盖在下一张截图上
            // （离屏软件渲染下 DispatcherTimer 的实际 tick 间隔约 27ms，Toast 的 4000ms 要跑约 6.9s 真实时间）
            WaitToastGone(9000);
            PinEnvironment();
            try { Keyboard.ClearFocus(); } catch { }
            Win.UpdateLayout();
            UiPump.For(160);
        }

        /// <summary>6 个任务、混合状态（未扫描/待处理/完成/部分完成/失败/跳过）。</summary>
        public void AddMixedTasks()
        {
            const string root = @"E:\L4D2\求生之路2 MOD制作\工程文件";
            var items = new List<(TaskKind kind, string path, TaskStatus status, int qc, int models, string note)>
            {
                (TaskKind.Folder, root + @"\8人自动编译", TaskStatus.Pending, 0, 0, ""),
                (TaskKind.Qc,     root + @"\8人自动编译\qc\actor.qc", TaskStatus.Pending, 1, 3, ""),
                (TaskKind.Folder, root + @"\武器包", TaskStatus.Ok, 3, 12, ""),
                (TaskKind.Qc,     root + @"\武器包\qc\weapon.qc", TaskStatus.Partial, 1, 2, ""),
                (TaskKind.Qc,     root + @"\道具包\qc\prop_door.qc", TaskStatus.Failed, 1, 1, ""),
                (TaskKind.Folder, root + @"\道具包", TaskStatus.Skipped, 2, 6, "")
            };
            Vm.TestAddTasks(items);
            // TestAddTasks 会把所有项标成已扫描；把第 1 项改回"未扫描"，覆盖 8 态里的这一态
            if (Vm.Queue.Items.Count > 0) Vm.Queue.Items[0].Scanned = false;
            // 真实导入路径会调 UpdateReadiness（HUD 读数变 "6 项就绪"）；钩子不调，这里补上，避免截图上读数与队列不一致
            try { Vm.RaiseReady(); } catch { }
            Win.UpdateLayout();
            UiPump.For(130);
        }

        /// <summary>选中队列第一行（展示 RowSelected + 左边缘 2px 信号条）。</summary>
        public void SelectFirstTask()
        {
            var list = Named("QueueList") as ListBox;
            if (list == null || list.Items.Count == 0) return;
            try { list.SelectedIndex = 0; } catch { }
            UiPump.For(80);
        }

        // ══════════════════════ 截图 ══════════════════════

        public void Capture(string path, double scale = 1.0)
        {
            Win.UpdateLayout();
            UiPump.Frames(4);
            UiCapture.CaptureWindow(Win, path, scale);
        }

        // ══════════════════════ 对话框 ══════════════════════

        public Task<bool> OpenConfirmDanger(string title, string message, string detail)
        {
            _lastDialog = Win.TestConfirmAsync(title, message, true, detail);
            UiPump.For(320);                       // 遮罩 120ms + 面板 160ms + 焦点落到默认按钮
            return (Task<bool>)_lastDialog;
        }

        public Task OpenInfo(string title, string message, string detail)
        {
            _lastDialog = Win.TestInfoAsync(title, message, detail);
            UiPump.For(320);
            return _lastDialog;
        }

        public Task<string> OpenPrompt(string title, string label, string initial)
        {
            _lastDialog = Win.TestPromptAsync(title, label, initial);
            UiPump.For(320);
            return (Task<string>)_lastDialog;
        }

        /// <summary>上次打开的对话框 Task（用于确认关闭后 TCS 真的完成）。</summary>
        public Task LastDialogTask { get { return _lastDialog; } }

        /// <summary>
        /// 关闭当前对话框：优先"点取消按钮"（等价用户操作，走 RequestClose→Finish→TCS 完成）；
        /// 其次 Esc；最后才用 ForceCloseForTest 兜底（并记录 UsedForceClose）。
        /// </summary>
        public bool CloseDialogs(int timeoutMs = 1800)
        {
            DialogHost host = Dialogs;
            if (host == null) return true;
            if (!host.IsOpen)
            {
                // 可能正在关闭动画中
                UiPump.Until(delegate { return !host.IsOpen; }, 400);
                return true;
            }

            Button cancel = FindButtonByText(host, "取消") ?? FindButtonByText(host, "继续运行") ?? FindButtonByText(host, "知道了");
            if (cancel != null)
            {
                try { cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, cancel)); } catch { }
            }
            if (UiPump.Until(delegate { return !host.IsOpen; }, timeoutMs)) return true;

            // 兜底 2：Esc（走对话框自己的 PreviewKeyDown 隧道）
            var focused = Keyboard.FocusedElement as UIElement;
            RaiseKey(focused ?? (UIElement)host, Key.Escape);
            if (UiPump.Until(delegate { return !host.IsOpen; }, 1200)) return true;

            // 兜底 3：测试钩子（可能残留面板内容 / TCS 不完成 → 由 UsedForceClose 记录下来）
            try { Win.TestCloseDialog(); } catch { }
            UsedForceClose = true;
            return UiPump.Until(delegate { return !host.IsOpen; }, 800);
        }

        private static Button FindButtonByText(DependencyObject root, string text)
        {
            var buttons = new List<Button>();
            UiTree.CollectAll<Button>(root, buttons);
            foreach (Button b in buttons)
            {
                if (string.Equals(b.Content as string, text, StringComparison.Ordinal)) return b;
            }
            return null;
        }

        /// <summary>模拟一次键盘按键（走 PreviewKeyDown 隧道：Window → … → 焦点元素）。</summary>
        public void RaiseKey(UIElement target, Key key)
        {
            if (target == null) return;
            PresentationSource src = null;
            try { src = PresentationSource.FromVisual(target); } catch { }
            if (src == null)
            {
                try { src = PresentationSource.FromVisual(Win); } catch { }
            }
            if (src == null) return;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, src, Environment.TickCount, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            target.RaiseEvent(args);
        }

        /// <summary>Toast 显示 4000ms（test hook 固定）；等它完全退场，避免污染下一张截图。</summary>
        public void WaitToastExpiry()
        {
            UiPump.For(4400);
        }
    }
}
