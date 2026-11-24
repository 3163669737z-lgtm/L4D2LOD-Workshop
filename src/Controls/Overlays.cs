using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LodStudio.Controls
{
    public enum ToastTone { Info, Ok, Warn, Err }

    /// <summary>
    /// 浮层提示条（右下角、HUD 上方）。
    ///
    /// 契约（不可变）：Show(title, subtitle, tone, holdMs=3200) —— 不抢焦点、不拦截鼠标；
    /// 最多同时 3 条，垂直堆叠；3.2 秒后淡出。
    ///
    /// 实现：自绘（不建子元素/不重建对象），DispatcherTimer 驱动；动画开关遵循
    /// SystemParameters.ClientAreaAnimation（关闭"显示动画效果"时立即显示/隐藏）。
    ///
    /// 堆叠约定：**新的在最下面**（最靠近 HUD，视线最先看到），旧的上移让位；超 3 条时丢掉最旧的一条。
    /// </summary>
    public sealed class ToastHost : FrameworkElement
    {
        private const double MinToastWidth = 250.0;
        private const double MaxToastWidth = 320.0;
        private const double SingleLineHeight = 44.0;
        private const double TwoLineHeight = 58.0;
        private const double BarWidth = 3.0;            // 左侧语义色条
        private const double TextLeft = 14.0;           // 文字距框左边（含 3px 色条）
        private const double TextRight = 14.0;
        private const double StackGap = 8.0;
        private const double HardShadowOffset = 3.0;    // 右下 3px 硬偏移色块（不用模糊阴影）
        private const int IntervalMs = 16;
        private const double EnterMs = 160.0;
        private const double LeaveMs = 160.0;
        private const int MaxToasts = 3;

        private static readonly Typeface FaceTitle = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private static readonly Typeface FaceSub = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        private sealed class ToastItem
        {
            public string Title;
            public string Subtitle;
            public ToastTone Tone;
            public double HoldMs;
            public double AgeMs;
            public double Width;
            public double Height;
            public double Y;
            public double TargetY;
            public double Alpha;
            public bool Leaving;
            public FormattedText TitleText;
            public FormattedText SubtitleText;
        }

        private readonly List<ToastItem> _items = new List<ToastItem>();   // [0] = 最新，画在最下面
        private DispatcherTimer _timer;
        private double _dpi = 1.0;
        private Brush _themeStamp;
        private Pen _borderPen;

        public ToastHost()
        {
            IsHitTestVisible = false;      // 不抢鼠标、不抢焦点
            Focusable = false;
            SnapsToDevicePixels = true;
            Width = 340;
            Height = 200;
            HorizontalAlignment = HorizontalAlignment.Right;
            VerticalAlignment = VerticalAlignment.Bottom;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>入场/出场是否走动画（系统"显示动画效果"关闭时立即显示/隐藏）。</summary>
        private static bool? _animationOverride;   // 测试/预览钩子：null = 跟随系统

        internal static void DebugSetAnimations(bool? enabled) { _animationOverride = enabled; }

        internal bool DebugAnimationsEnabled { get { return AnimationsEnabled; } }

        /// <summary>预览/审计用：最新一条的当前透明度（-1 = 没有提示）。</summary>
        internal double DebugFirstAlpha { get { return _items.Count == 0 ? -1 : _items[0].Alpha; } }

        /// <summary>预览/审计用：当前堆叠数量。</summary>
        internal int DebugItemCount { get { return _items.Count; } }

        private static bool AnimationsEnabled
        {
            get
            {
                if (_animationOverride.HasValue) return _animationOverride.Value;
                try { return SystemParameters.ClientAreaAnimation; } catch { return false; }
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Services.ThemeService.ThemeChanged -= OnThemeChanged;
            Services.ThemeService.ThemeChanged += OnThemeChanged;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Services.ThemeService.ThemeChanged -= OnThemeChanged;
            if (_timer != null) _timer.Stop();
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            _themeStamp = null;
            _borderPen = null;
            InvalidateVisual();
        }

        /// <summary>
        /// 弹出一条提示。标题必填、副行可选；tone 决定左侧 3px 语义色条颜色。
        /// 同一时刻最多 3 条（第 4 条会顶掉最旧的一条），新的排在下面。
        /// </summary>
        public void Show(string title, string sub, ToastTone tone, int holdMs = 3200)
        {
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(sub)) return;
            _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var item = new ToastItem
            {
                Title = title ?? string.Empty,
                Subtitle = string.IsNullOrEmpty(sub) ? null : sub,
                Tone = tone,
                HoldMs = holdMs <= 0 ? 3200 : holdMs
            };
            BuildItemVisual(item);
            while (_items.Count >= MaxToasts) _items.RemoveAt(_items.Count - 1);   // 超限：丢最旧
            _items.Insert(0, item);                                               // 最新的在底部
            // 入位置：最终位置贴着堆叠底部；动画开启时先下移 6px，由 tick 滑入
            double anchorBottom = Math.Max(0, ActualHeight);
            item.Y = anchorBottom - item.Height;
            item.TargetY = item.Y;
            if (AnimationsEnabled)
            {
                item.Y += 6.0;
                item.Alpha = 0.0;
            }
            else
            {
                // 系统关闭"显示动画效果"：立即以最终位置、全不透明显示（不播任何动画）
                item.Alpha = 1.0;
            }
            item.AgeMs = 0;
            StartTimer();
            InvalidateVisual();
        }

        private void BuildItemVisual(ToastItem item)
        {
            Brush ink = Res("Brush.Ink", Brushes.Black);
            Brush inkSoft = Res("Brush.InkSoft", Brushes.DimGray);
            double titleNatural = MeasureWidth(item.Title, FaceTitle, 12.5);
            double subNatural = item.Subtitle == null ? 0 : MeasureWidth(item.Subtitle, FaceSub, 10.5);
            double natural = Math.Max(titleNatural, subNatural) + BarWidth + TextLeft + TextRight;
            item.Width = Math.Max(MinToastWidth, Math.Min(MaxToastWidth, Math.Ceiling(natural)));
            double textMax = item.Width - BarWidth - TextLeft - TextRight;
            item.TitleText = FitSingleLine(item.Title, FaceTitle, 12.5, ink, textMax);
            item.SubtitleText = item.Subtitle == null ? null : FitSingleLine(item.Subtitle, FaceSub, 10.5, inkSoft, textMax);
            item.Height = item.Subtitle == null ? SingleLineHeight : TwoLineHeight;
        }

        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private long _lastTickMs;

        private void StartTimer()
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(IntervalMs) };
                _timer.Tick += OnTick;
            }
            _lastTickMs = _clock.ElapsedMilliseconds;
            if (!_timer.IsEnabled) _timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            // 用真实经过时间，而不是 tick 次数：DispatcherTimer 不保证准点，
            // 离屏/软件渲染下 tick 会慢 1.5~2 倍，按计数计时会让 Toast 停留时间明显变长。
            long now = _clock.ElapsedMilliseconds;
            double delta = now - _lastTickMs;
            if (delta < 1) delta = 1;
            if (delta > 200) delta = 200;
            _lastTickMs = now;

            bool animate = AnimationsEnabled;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                ToastItem it = _items[i];
                if (it.Leaving)
                {
                    if (animate)
                    {
                        it.Alpha -= delta / LeaveMs;
                        if (it.Alpha <= 0.01) { _items.RemoveAt(i); continue; }
                    }
                    else { _items.RemoveAt(i); continue; }
                }
                else
                {
                    it.AgeMs += delta;
                    if (animate) it.Alpha = Math.Min(1.0, it.Alpha + delta / EnterMs);
                    if (it.AgeMs >= it.HoldMs)
                    {
                        it.Leaving = true;
                        if (!animate) { _items.RemoveAt(i); continue; }
                    }
                }
            }

            // 堆叠：从底部往上排（[0] 在最下面），旧的向上让位
            double bottom = ActualHeight;
            for (int i = 0; i < _items.Count; i++)
            {
                ToastItem it = _items[i];
                it.TargetY = bottom - it.Height;
                bottom = it.TargetY - StackGap;
            }
            for (int i = 0; i < _items.Count; i++)
            {
                ToastItem it = _items[i];
                if (!animate) { it.Y = it.TargetY; continue; }
                double d = it.TargetY - it.Y;
                if (Math.Abs(d) < 0.4) it.Y = it.TargetY;
                else it.Y += d * 0.35;                                     // 连续 Show 时简单纵向位移重排
            }

            if (_items.Count == 0 && _timer != null) _timer.Stop();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_items.Count == 0) return;
            _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            Brush panel = Res("Brush.Panel", Brushes.White);
            Brush lineStrong = Res("Brush.LineStrong", Brushes.Gray);
            Brush hardShadow = Res("Brush.HardShadow", Brushes.Transparent);
            if (!ReferenceEquals(panel, _themeStamp))
            {
                // 主题换了：建文本时用的 Brush 实例失效 → 按当前主题重建（宽度/高度不变）
                _themeStamp = panel;
                for (int i = 0; i < _items.Count; i++) BuildItemVisual(_items[i]);
            }
            if (_borderPen == null)
            {
                _borderPen = new Pen(lineStrong, 1.0);
                if (_borderPen.CanFreeze) _borderPen.Freeze();
            }

            // 旧的先画、最新的最后画（视觉上压在最上面）
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                ToastItem it = _items[i];
                if (it.Alpha <= 0.005 || it.TitleText == null) continue;
                double x = ActualWidth - HardShadowOffset - it.Width;
                double y = it.Y;
                dc.PushOpacity(Math.Min(1.0, Math.Max(0.0, it.Alpha)));
                dc.DrawRectangle(hardShadow, null, new Rect(x + HardShadowOffset, y + HardShadowOffset, it.Width, it.Height));
                dc.DrawRectangle(panel, null, new Rect(x, y, it.Width, it.Height));
                dc.DrawRectangle(null, _borderPen, new Rect(x + 0.5, y + 0.5, Math.Max(0, it.Width - 1), Math.Max(0, it.Height - 1)));
                dc.DrawRectangle(ToneBrush(it.Tone), null, new Rect(x, y, BarWidth, it.Height));

                FormattedText t = it.TitleText;
                FormattedText s = it.SubtitleText;
                double total = t.Height + (s != null ? 2.0 + s.Height : 0.0);
                double ty = y + Math.Max(3.0, (it.Height - total) / 2.0);
                double tx = x + BarWidth + TextLeft;
                dc.DrawText(t, new Point(tx, ty));
                if (s != null) dc.DrawText(s, new Point(tx, ty + t.Height + 2.0));
                dc.Pop();
            }
        }

        private Brush ToneBrush(ToastTone tone)
        {
            switch (tone)
            {
                case ToastTone.Ok: return Res("Brush.Ok", Brushes.Green);
                case ToastTone.Warn: return Res("Brush.Warn", Brushes.Orange);
                case ToastTone.Err: return Res("Brush.Err", Brushes.Red);
                default: return Res("Brush.Signal", Brushes.OrangeRed);
            }
        }

        private Brush Res(string key, Brush fallback)
        {
            Brush b = TryFindResource(key) as Brush;
            return b ?? fallback;
        }

        private double MeasureWidth(string text, Typeface face, double size)
        {
            return Text(text, face, size, Brushes.Black).WidthIncludingTrailingWhitespace;
        }

        private FormattedText Text(string text, Typeface face, double size, Brush brush)
        {
            return new FormattedText(text ?? string.Empty, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                     face, size, brush ?? Brushes.Black, _dpi > 0 ? _dpi : 1.0);
        }

        /// <summary>单行截断：超宽时用 "…" 结尾（不换行、不裁切文字）。</summary>
        private FormattedText FitSingleLine(string text, Typeface face, double size, Brush brush, double maxWidth)
        {
            FormattedText ft = Text(text, face, size, brush);
            if (ft.WidthIncludingTrailingWhitespace <= maxWidth || string.IsNullOrEmpty(text)) return ft;
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                FormattedText cand = Text(text.Substring(0, mid) + "…", face, size, brush);
                if (cand.WidthIncludingTrailingWhitespace <= maxWidth) lo = mid; else hi = mid - 1;
            }
            return Text(text.Substring(0, lo) + "…", face, size, brush);
        }
    }

    /// <summary>对话框选项。</summary>
    public sealed class DialogOptions
    {
        public string Title;
        public string Message;
        public string Detail;
        public string OkText = "确定";
        public string CancelText = "取消";
        public bool Danger;
        public string InputLabel;
        public string InputInitial;
        public Func<string, string> Validate;   // 返回非空 = 错误文本
    }

    /// <summary>
    /// 窗口内模态浮层（半透明遮罩 + 居中面板）。契约（不可变，返回 Task，调用方 await）：
    ///   ConfirmAsync / ShowInfoAsync / PromptAsync。
    ///
    /// 挂载约定：铺满"窗口内容层"（不含自绘标题栏），放在内容区域最后一个 Grid 子元素里，
    /// 这样遮罩挡住内容交互，而标题栏拖拽仍然可用；打开时 Visibility=Visible，关闭时 Collapsed。
    ///
    /// 同时只允许一个对话框：重复打开直接忽略（返回默认值），避免两个 TCS 互相覆盖。
    /// 键盘：Esc = 取消/关闭；Enter = 主按钮；但 Danger 确认时 Enter 不触发确认（防止误删，
    ///       必须用鼠标点或 Tab 到红色按钮后按空格），Tab 在按钮间循环。
    /// </summary>
    public sealed class DialogHost : ContentControl
    {
        private enum DialogKind { Confirm, Info, Prompt }

        private const double PanelWidth = 460.0;
        private const double ContentMaxHeight = 330.0;
        private const double ButtonMinWidth = 92.0;

        private static readonly Typeface FaceCjk = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        private DialogKind _kind;
        private DialogOptions _options;
        private Action<object> _complete;
        private IInputElement _previousFocus;
        private bool _closing;
        private int _closeToken;

        private Grid _root;
        private Border _mask;
        private FrameworkElement _panelFrame;
        private Border _panel;
        private TranslateTransform _panelTransform;
        private Button _primaryButton;
        private Button _cancelButton;
        private Button _closeButton;
        private TextBox _input;
        private TextBlock _errorLine;

        /// <summary>当前是否有对话框打开。</summary>
        public bool IsOpen { get; private set; }


        public DialogHost()
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Focusable = false;
            Visibility = Visibility.Collapsed;
            SnapsToDevicePixels = true;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;
        }

        /// <summary>预览/审计用：当前键盘焦点的可读描述（证明默认焦点在哪）。</summary>
        internal string DebugFocusText
        {
            get
            {
                IInputElement fe = Keyboard.FocusedElement;
                if (fe == null) return "(none)";
                var b = fe as Button;
                if (b != null) return "Button「" + (b.Content as string ?? "") + "」";
                var t = fe as TextBox;
                if (t != null) return "TextBox「" + t.Text + "」";
                var el = fe as FrameworkElement;
                return el != null ? el.GetType().Name : fe.GetType().Name;
            }
        }

        /// <summary>预览/审计用：直接触发主按钮（用于截"校验失败行内报错"状态，不经过鼠标）。</summary>
        internal void DebugAcceptPrimary()
        {
            AcceptPrimary();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            FitPanel();
        }

        private void FitPanel()
        {
            if (_panel == null) return;
            _panel.MaxWidth = Math.Max(0, ActualWidth - 48);
            _panel.MaxHeight = Math.Max(0, ActualHeight - 48);
        }

        /// <summary>系统"显示动画效果"是否开启；关闭时所有动效即时完成。</summary>
        private static bool? _animationOverride;   // 测试/预览钩子：null = 跟随系统

        internal static void DebugSetAnimations(bool? enabled) { _animationOverride = enabled; }

        internal bool DebugAnimationsEnabled { get { return AnimationsEnabled; } }

        private static bool AnimationsEnabled
        {
            get
            {
                if (_animationOverride.HasValue) return _animationOverride.Value;
                try { return SystemParameters.ClientAreaAnimation; } catch { return false; }
            }
        }

        /// <summary>
        /// 预览/审计用：向对话框根元素投递一个按键，走与真实键盘完全相同的 PreviewKeyDown 处理路径
        /// （窗口真实键盘事件先隧道路由到根元素，这里等价）。返回是否投递成功。
        /// </summary>
        internal bool DebugKey(Key key)
        {
            if (!IsOpen || _root == null) return false;
            PresentationSource source = PresentationSource.FromVisual(_root);
            if (source == null) return false;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            _root.RaiseEvent(args);
            return true;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Services.ThemeService.ThemeChanged -= OnThemeChanged;
            Services.ThemeService.ThemeChanged += OnThemeChanged;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Services.ThemeService.ThemeChanged -= OnThemeChanged;
            if (IsOpen) Finish(null);       // 宿主卸载：别让 await 永远挂着
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            // 面板里的 Brush 全部是 DynamicResource（SetResourceReference），切换主题自动生效；
            // 自绘的硬偏移色块也是资源引用，无需重建。
        }

        // ══════════════════════════ 公开 API（签名不可变） ══════════════════════════

        internal void Dismiss() => RequestClose(CancelResult());
        internal void FocusDialog() => FocusInitial();

        /// <summary>确认对话框：返回 true=确定，false=取消/关闭。Danger 时主按钮为红色且默认焦点在"取消"。</summary>
        public Task<bool> ConfirmAsync(DialogOptions options)
        {
            if (IsOpen)
            {
                // 同时只允许一个：直接忽略新的请求（返回 false，绝不让调用方误以为已确认）
                System.Diagnostics.Debug.WriteLine("[dialog] 已有对话框打开，忽略 ConfirmAsync：" + (options == null ? "" : options.Title));
                return Task.FromResult(false);
            }
            var tcs = new TaskCompletionSource<bool>();
            Open(options, DialogKind.Confirm, delegate (object r) { tcs.TrySetResult(r is bool && (bool)r); });
            return tcs.Task;
        }

        /// <summary>信息对话框：单个「知道了」按钮。</summary>
        public Task ShowInfoAsync(DialogOptions options)
        {
            if (IsOpen)
            {
                System.Diagnostics.Debug.WriteLine("[dialog] 已有对话框打开，忽略 ShowInfoAsync：" + (options == null ? "" : options.Title));
                return Task.FromResult(0);
            }
            var tcs = new TaskCompletionSource<bool>();
            Open(options, DialogKind.Info, delegate (object r) { tcs.TrySetResult(true); });
            return tcs.Task;   // Task<bool> 直接当 Task 返回
        }

        /// <summary>文本输入对话框：返回输入值；取消/关闭返回 null。校验失败时行内报错且不关闭。</summary>
        public Task<string> PromptAsync(DialogOptions options)
        {
            if (IsOpen)
            {
                System.Diagnostics.Debug.WriteLine("[dialog] 已有对话框打开，忽略 PromptAsync：" + (options == null ? "" : options.Title));
                return Task.FromResult<string>(null);
            }
            var tcs = new TaskCompletionSource<string>();
            Open(options, DialogKind.Prompt, delegate (object r) { tcs.TrySetResult(r as string); });
            return tcs.Task;
        }

        /// <summary>
        /// 测试钩子（主控 ShellWindow.TestHooks / --ui-audit 用）：强制关闭当前对话框。
        /// 同步走真实关闭流程（Finish）：移除遮罩与面板、恢复打开前的焦点、让挂起的 Task 以
        /// "取消/默认"结果完成（Confirm→false、ShowInfo→完成、Prompt→null）。
        /// 不依赖动画时长（测试无需等待），可重复调用（没打开时是安全空操作），绝不抛异常、不卡死。
        /// </summary>
        public void ForceCloseForTest()
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ForceCloseForTest));
                    return;
                }
                if (!IsOpen) return;
                _closeToken++;            // 让进行中的关闭动画回调失效
                _closing = false;
                Finish(CancelResult());
            }
            catch
            {
                // 测试钩子绝不把异常抛给调用方（最坏情况也只是对话框留在屏幕上）
            }
        }

        // ══════════════════════════ 打开 / 构建 ══════════════════════════

        private void Open(DialogOptions options, DialogKind kind, Action<object> complete)
        {
            _options = options ?? new DialogOptions();
            _kind = kind;
            _complete = complete;
            _closing = false;
            _previousFocus = Keyboard.FocusedElement;
            Content = BuildVisual();
            IsOpen = true;
            Visibility = Visibility.Visible;
            // 关键：BuildVisual 里读 ActualHeight 时宿主还是 Collapsed（=0），会把面板夹到 200 高、按钮被裁。
            // 先显示并完成一次布局，再按宿主的可用空间限制面板。
            UpdateLayout();
            FitPanel();
            StartOpenAnimation();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusInitial));
        }

        private FrameworkElement BuildVisual()
        {
            _root = new Grid { Background = Brushes.Transparent };
            KeyboardNavigation.SetTabNavigation(_root, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(_root, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetDirectionalNavigation(_root, KeyboardNavigationMode.Contained);
            _root.PreviewKeyDown += OnRootPreviewKeyDown;

            _mask = new Border { Opacity = AnimationsEnabled ? 0.0 : 1.0 };
            ApplyStyle(_mask, "Widget.Dialog.Mask");
            _mask.SetResourceReference(Border.BackgroundProperty, "Brush.Mask");
            // 遮罩拦截底层鼠标；点遮罩本身不关闭（危险确认必须明确选择取消/确定）
            _root.Children.Add(_mask);

            // 面板：Grid 里叠"HARD 阴影（右下 3px 硬偏移）+ 面板"，容器 Margin 让阴影不被裁掉
            var center = new Grid { Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            var frame = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 3)
            };

            var shadow = new Border { Margin = new Thickness(3, 3, 0, 0) };
            shadow.SetResourceReference(Border.BackgroundProperty, "Brush.HardShadow");
            frame.Children.Add(shadow);

            _panel = new Border { Width = PanelWidth };
            ApplyStyle(_panel, "Widget.Dialog.Panel");
            _panel.SetResourceReference(Border.BackgroundProperty, "Brush.Panel");
            _panel.SetResourceReference(Border.BorderBrushProperty, "Brush.LineStrong");
            _panel.BorderThickness = new Thickness(1);
            _panel.Padding = new Thickness(20);
            _panel.Margin = new Thickness(0, 0, 3, 3);
            frame.Children.Add(_panel);

            _panelTransform = new TranslateTransform(0, AnimationsEnabled ? 8.0 : 0.0);
            frame.RenderTransform = _panelTransform;
            _panelFrame = frame;
            center.Children.Add(frame);
            _root.Children.Add(center);

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _panel.Child = layout;

            // 标题行：标题 + 右上角关闭 ×
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock();
            ApplyStyle(title, "Widget.Dialog.Title");
            title.FontFamily = FontOr("Font.Cjk", FaceCjk.FontFamily);
            title.FontSize = SizeOr("FS.PageTitle", 15.0);
            title.FontWeight = FontWeights.SemiBold;
            title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Ink");
            title.TextWrapping = TextWrapping.Wrap;
            title.Text = string.IsNullOrEmpty(_options.Title) ? "提示" : _options.Title;
            title.VerticalAlignment = VerticalAlignment.Top;
            title.Margin = new Thickness(0, 0, 12, 0);
            titleRow.Children.Add(title);

            _closeButton = new Button { Content = CloseGlyph(), ToolTip = "关闭（Esc）", Focusable = true, TabIndex = 4 };
            ApplyButtonStyle(_closeButton, "Btn.Icon");
            _closeButton.Width = 26;
            _closeButton.Height = 26;
            _closeButton.Padding = new Thickness(0);
            _closeButton.VerticalAlignment = VerticalAlignment.Top;
            _closeButton.Click += delegate { Dismiss(); };
            System.Windows.Automation.AutomationProperties.SetName(_closeButton, "关闭");
            Grid.SetColumn(_closeButton, 1);
            titleRow.Children.Add(_closeButton);
            layout.Children.Add(titleRow);

            var body = new StackPanel();
            var content = new ScrollViewer
            {
                Content = body, MaxHeight = ContentMaxHeight, Focusable = false, IsTabStop = false, TabIndex = 3,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            content.ScrollChanged += (s, e) => content.Focusable = content.IsTabStop = content.ScrollableHeight > 0;
            System.Windows.Automation.AutomationProperties.SetName(content, "详细内容");
            Grid.SetRow(content, 1);
            layout.Children.Add(content);

            // 正文
            if (!string.IsNullOrEmpty(_options.Message))
            {
                var msg = new TextBlock { Margin = new Thickness(0, 12, 0, 0) };
                ApplyStyle(msg, "Widget.Dialog.Body");
                msg.FontFamily = FontOr("Font.Cjk", FaceCjk.FontFamily);
                msg.FontSize = SizeOr("FS.Body", 12.5);
                msg.SetResourceReference(TextBlock.ForegroundProperty, "Brush.InkSoft");
                msg.TextWrapping = TextWrapping.Wrap;
                msg.Text = _options.Message;
                body.Children.Add(msg);
            }

            // 正文与详情共用滚动区，标题和确认按钮固定。
            if (!string.IsNullOrEmpty(_options.Detail))
            {
                var box = new Border { Margin = new Thickness(0, 12, 0, 0) };
                ApplyStyle(box, "Widget.Dialog.DetailBox");
                box.SetResourceReference(Border.BackgroundProperty, "Brush.Sunken");
                box.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
                box.BorderThickness = new Thickness(1);
                box.Padding = new Thickness(10);
                var detail = new TextBlock();
                ApplyStyle(detail, "Widget.Dialog.DetailText");
                detail.FontFamily = MonoFont();
                detail.FontSize = SizeOr("FS.Mono", 11.5);
                detail.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Ink");
                detail.TextWrapping = TextWrapping.Wrap;
                detail.Text = _options.Detail;
                box.Child = detail;
                body.Children.Add(box);
            }

            // 输入区（Prompt）
            if (_kind == DialogKind.Prompt)
            {
                var label = new TextBlock { Margin = new Thickness(0, 14, 0, 0) };
                ApplyStyle(label, "Widget.Dialog.Label");
                label.FontFamily = FontOr("Font.Cjk", FaceCjk.FontFamily);
                label.FontSize = SizeOr("FS.Small", 11.5);
                label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.InkSoft");
                label.Text = string.IsNullOrEmpty(_options.InputLabel) ? "请输入" : _options.InputLabel;
                body.Children.Add(label);

                _input = new TextBox { Text = _options.InputInitial ?? string.Empty, Margin = new Thickness(0, 6, 0, 0), TabIndex = 0 };
                ApplyStyle(_input, "Input.Text");
                _input.TextChanged += delegate
                {
                    if (_errorLine != null && _errorLine.Visibility == Visibility.Visible)
                    {
                        _errorLine.Visibility = Visibility.Collapsed;
                        _errorLine.Text = string.Empty;
                    }
                };
                body.Children.Add(_input);

                _errorLine = new TextBlock { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
                ApplyStyle(_errorLine, "Widget.Dialog.Error");
                _errorLine.FontFamily = FontOr("Font.Cjk", FaceCjk.FontFamily);
                _errorLine.FontSize = SizeOr("FS.Small", 11.5);
                _errorLine.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Err");
                _errorLine.TextWrapping = TextWrapping.Wrap;
                body.Children.Add(_errorLine);
            }

            // 底部按钮：右对齐，34 高
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0),
                Height = 34
            };
            if (_kind == DialogKind.Info)
            {
                _primaryButton = new Button { Content = string.IsNullOrEmpty(_options.OkText) ? "知道了" : _options.OkText, TabIndex = 1 };
                ApplyButtonStyle(_primaryButton, "Btn.Primary");
                _primaryButton.MinWidth = ButtonMinWidth;
                _primaryButton.IsDefault = true;
                _primaryButton.Click += delegate { RequestClose(SuccessResult()); };
                buttons.Children.Add(_primaryButton);
            }
            else
            {
                _cancelButton = new Button { Content = string.IsNullOrEmpty(_options.CancelText) ? "取消" : _options.CancelText, TabIndex = 1 };
                ApplyButtonStyle(_cancelButton, "Btn.Secondary");
                _cancelButton.MinWidth = ButtonMinWidth;
                _cancelButton.Click += delegate { RequestClose(CancelResult()); };
                buttons.Children.Add(_cancelButton);

                _primaryButton = new Button
                {
                    Content = string.IsNullOrEmpty(_options.OkText) ? "确定" : _options.OkText,
                    TabIndex = 2,
                    Margin = new Thickness(10, 0, 0, 0),
                    IsDefault = !_options.Danger
                };
                ApplyButtonStyle(_primaryButton, _options.Danger ? "Btn.Danger" : "Btn.Primary");
                _primaryButton.MinWidth = ButtonMinWidth;
                _primaryButton.Click += delegate { AcceptPrimary(); };
                buttons.Children.Add(_primaryButton);
            }
            Grid.SetRow(buttons, 2);
            layout.Children.Add(buttons);
            return _root;
        }

        private void FocusInitial()
        {
            if (!IsOpen) return;
            try
            {
                // 危险确认：默认焦点在"取消"（防止回车/误触直接执行删除）
                if (_kind == DialogKind.Confirm && _options.Danger && _cancelButton != null) _cancelButton.Focus();
                else if (_kind == DialogKind.Prompt && _input != null) { _input.Focus(); _input.SelectAll(); }
                else if (_primaryButton != null) _primaryButton.Focus();
            }
            catch { }
        }

        // ══════════════════════════ 键盘 ══════════════════════════

        private void OnRootPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsOpen) return;
            if (_closing) { e.Handled = true; return; }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                RequestClose(CancelResult());
                return;
            }
            if (e.Key == Key.Enter)
            {
                // 危险确认：Enter 不触发主按钮（避免"删库"式误操作）。Esc / 鼠标 / Tab+空格 仍可用。
                if (_kind == DialogKind.Confirm && _options.Danger)
                {
                    e.Handled = true;
                    return;
                }
                e.Handled = true;
                if (_cancelButton?.IsKeyboardFocused == true || _closeButton?.IsKeyboardFocused == true) Dismiss();
                else AcceptPrimary();
            }
        }

        // ══════════════════════════ 结果 ══════════════════════════

        private object CancelResult()
        {
            return _kind == DialogKind.Prompt ? null : (object)false;
        }

        private object SuccessResult()
        {
            if (_kind == DialogKind.Prompt) return _input != null ? _input.Text : string.Empty;
            return true;
        }

        private void AcceptPrimary()
        {
            if (!IsOpen || _closing) return;
            if (_kind == DialogKind.Prompt)
            {
                string value = _input != null ? _input.Text : string.Empty;
                string err = null;
                try { err = _options.Validate != null ? _options.Validate(value) : null; }
                catch (Exception ex) { err = "校验失败：" + ex.Message; }
                if (!string.IsNullOrEmpty(err))
                {
                    if (_errorLine != null) { _errorLine.Text = err; _errorLine.Visibility = Visibility.Visible; }
                    if (_input != null) _input.Focus();
                    return;                                   // 校验失败：不关闭
                }
            }
            RequestClose(SuccessResult());
        }

        private void RequestClose(object result)
        {
            if (!IsOpen || _closing) return;
            _closing = true;
            int token = ++_closeToken;
            if (!AnimationsEnabled)
            {
                Finish(result);
                return;
            }
            // 关闭动画：遮罩 120ms 淡出、面板 160ms 下移淡出；动画完成后才真正移除内容
            try
            {
                if (_mask != null)
                {
                    var fade = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(120)) { FillBehavior = FillBehavior.HoldEnd };
                    _mask.BeginAnimation(UIElement.OpacityProperty, fade);
                }
                if (_panelFrame != null)
                {
                    var panelFade = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.HoldEnd };
                    panelFade.Completed += delegate
                    {
                        if (token == _closeToken) Finish(result);
                    };
                    _panelFrame.BeginAnimation(UIElement.OpacityProperty, panelFade);
                }
                if (_panelTransform != null)
                {
                    var slide = new DoubleAnimation(6.0, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.HoldEnd, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                    _panelTransform.BeginAnimation(TranslateTransform.YProperty, slide);
                }
            }
            catch
            {
                Finish(result);
            }
        }

        private void Finish(object result)
        {
            _closeToken++;                 // 让迟到的动画回调失效
            IsOpen = false;
            _closing = false;
            Content = null;
            _root = null;
            _mask = null;
            _panel = null;
            _panelFrame = null;
            _panelTransform = null;
            _primaryButton = null;
            _cancelButton = null;
            _closeButton = null;
            _input = null;
            _errorLine = null;
            _options = null;
            Visibility = Visibility.Collapsed;

            // 恢复打开前的焦点
            try
            {
                if (_previousFocus is UIElement)
                {
                    var el = (UIElement)_previousFocus;
                    if (el.IsVisible && el.IsEnabled) Keyboard.Focus(el);
                }
            }
            catch { }
            _previousFocus = null;

            Action<object> complete = _complete;
            _complete = null;
            if (complete != null) complete(result);   // 在 UI 线程完成 Task（调用方 await 直接恢复）
        }

        // ══════════════════════════ 动画 / 样式工具 ══════════════════════════

        private void StartOpenAnimation()
        {
            if (!AnimationsEnabled) return;
            try
            {
                if (_mask != null)
                    _mask.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120)));
                if (_panelFrame != null)
                    _panelFrame.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160)));
                if (_panelTransform != null)
                {
                    var slide = new DoubleAnimation(8.0, 0.0, TimeSpan.FromMilliseconds(160))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    _panelTransform.BeginAnimation(TranslateTransform.YProperty, slide);
                }
            }
            catch { }
        }

        private void ApplyStyle(FrameworkElement element, string key)
        {
            if (element == null || string.IsNullOrEmpty(key)) return;
            Style style = TryFindResource(key) as Style;
            if (style == null && Application.Current != null) style = Application.Current.TryFindResource(key) as Style;
            if (style != null) element.Style = style;
        }

        /// <summary>
        /// 按钮样式：优先用 Widgets.xaml 里的命名样式；没有就从 Btn.* 基样式派生一个（保证主题/hover/press/焦点环都跟随全局）。
        /// </summary>
        private void ApplyButtonStyle(Button button, string baseStyleKey)
        {
            if (button == null || string.IsNullOrEmpty(baseStyleKey)) return;
            Style baseStyle = TryFindResource(baseStyleKey) as Style;
            if (baseStyle == null && Application.Current != null) baseStyle = Application.Current.TryFindResource(baseStyleKey) as Style;
            if (baseStyle != null)
            {
                var derived = new Style(typeof(Button), baseStyle);
                derived.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, ButtonMinWidth));
                derived.Seal();
                button.Style = derived;
            }
        }

        private FontFamily FontOr(string key, FontFamily fallback)
        {
            FontFamily f = TryFindResource(key) as FontFamily;
            return f ?? fallback;
        }

        private double SizeOr(string key, double fallback)
        {
            object v = TryFindResource(key);
            if (v is double) return (double)v;
            return fallback;
        }

        private FontFamily MonoFont()
        {
            FontFamily f = TryFindResource("Font.Mono") as FontFamily;
            return f ?? new FontFamily("Consolas");
        }

        /// <summary>关闭按钮的 × 字形（24 网格路径，和标题栏视觉一致）。</summary>
        private static UIElement CloseGlyph()
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M6,6 L18,18 M18,6 L6,18"),
                StrokeThickness = 1.5,
                Width = 11,
                Height = 11,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.InkSoft");
            return path;
        }
    }
}
