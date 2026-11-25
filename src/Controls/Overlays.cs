using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace LodStudio.Controls
{
    public enum ToastTone { Info, Ok, Warn, Err }

    /// <summary>
    /// 现代化毛玻璃浮层提示条（右下角、HUD 上方）。
    /// 特性：圆角磨砂卡片 + 柔和阴影 + 发光语义竖条 + 底部剩余时长进度细线 + 弹性滑入。
    /// 契约不变：Show(title, subtitle, tone, holdMs=3200) —— 不抢焦点、不拦截鼠标、最多 3 条。
    /// </summary>
    public sealed class ToastHost : FrameworkElement
    {
        private const double MinToastWidth = 256.0;
        private const double MaxToastWidth = 330.0;
        private const double SingleLineHeight = 48.0;
        private const double TwoLineHeight = 62.0;
        private const double BarWidth = 3.5;
        private const double TextLeft = 16.0;
        private const double TextRight = 16.0;
        private const double StackGap = 10.0;
        private const double CardRadius = 12.0;
        private const double EnterMs = 180.0;
        private const double LeaveMs = 180.0;
        private const int MaxToasts = 3;

        private static readonly Typeface FaceTitle = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private static readonly Typeface FaceSub = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        private sealed class ToastItem
        {
            public string Title;
            public string Subtitle;
            public ToastTone Tone;
            public double HoldMs;
            public double ShownAtMs;
            public double AgeMs;
            public double Width;
            public double Height;
            public double Y;
            public double TargetY;
            public double SlideX;
            public double Alpha;
            public bool Leaving;
            public FormattedText TitleText;
            public FormattedText SubtitleText;
        }

        private readonly List<ToastItem> _items = new List<ToastItem>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _dpi = 1.0;
        private Brush _themeStamp;
        private Pen _borderPen;

        public ToastHost()
        {
            IsHitTestVisible = false;
            Focusable = false;
            SnapsToDevicePixels = true;
            Width = 350;
            Height = 200;
            HorizontalAlignment = HorizontalAlignment.Right;
            VerticalAlignment = VerticalAlignment.Bottom;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private static bool? _animationOverride;

        internal static void DebugSetAnimations(bool? enabled) { _animationOverride = enabled; }
        internal bool DebugAnimationsEnabled { get { return AnimationsEnabled; } }
        internal double DebugFirstAlpha { get { return _items.Count == 0 ? -1 : _items[0].Alpha; } }
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
            if (_items.Count > 0) Services.RenderFrameHub.Subscribe(OnTick);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Services.ThemeService.ThemeChanged -= OnThemeChanged;
            Services.RenderFrameHub.Unsubscribe(OnTick);
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            _themeStamp = null;
            _borderPen = null;
            InvalidateVisual();
        }

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
            while (_items.Count >= MaxToasts) _items.RemoveAt(_items.Count - 1);
            _items.Insert(0, item);
            double anchorBottom = Math.Max(0, ActualHeight);
            item.Y = anchorBottom - item.Height;
            item.TargetY = item.Y;
            if (AnimationsEnabled)
            {
                item.Y += 8.0;
                item.SlideX = 24.0;
                item.Alpha = 0.0;
            }
            else
            {
                item.SlideX = 0.0;
                item.Alpha = 1.0;
            }
            item.ShownAtMs = _clock.Elapsed.TotalMilliseconds;
            if (IsLoaded) Services.RenderFrameHub.Subscribe(OnTick);
            InvalidateVisual();
        }

        private void BuildItemVisual(ToastItem item)
        {
            Brush ink = Res("Brush.Ink", Brushes.Black);
            Brush inkSoft = Res("Brush.InkSoft", Brushes.DimGray);
            double titleNatural = MeasureWidth(item.Title, FaceTitle, 12.5);
            double subNatural = item.Subtitle == null ? 0 : MeasureWidth(item.Subtitle, FaceSub, 11.0);
            double natural = Math.Max(titleNatural, subNatural) + BarWidth + TextLeft + TextRight;
            item.Width = Math.Max(MinToastWidth, Math.Min(MaxToastWidth, Math.Ceiling(natural)));
            double textMax = item.Width - BarWidth - TextLeft - TextRight;
            item.TitleText = FitSingleLine(item.Title, FaceTitle, 12.5, ink, textMax);
            item.SubtitleText = item.Subtitle == null ? null : FitSingleLine(item.Subtitle, FaceSub, 11.0, inkSoft, textMax);
            item.Height = item.Subtitle == null ? SingleLineHeight : TwoLineHeight;
        }

        private void OnTick(double deltaSeconds)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            double deltaMs = deltaSeconds * 1000;
            bool animate = AnimationsEnabled;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                ToastItem it = _items[i];
                if (it.Leaving)
                {
                    if (animate)
                    {
                        it.Alpha -= deltaMs / LeaveMs;
                        it.SlideX += deltaMs * 0.08;
                        if (it.Alpha <= 0.01) { _items.RemoveAt(i); continue; }
                    }
                    else { _items.RemoveAt(i); continue; }
                }
                else
                {
                    it.AgeMs = now - it.ShownAtMs;
                    if (animate)
                    {
                        it.Alpha = Math.Min(1.0, it.Alpha + deltaMs / EnterMs);
                        it.SlideX *= Math.Exp(-deltaMs / 58.0);
                        if (Math.Abs(it.SlideX) < 0.3) it.SlideX = 0;
                    }
                    if (it.AgeMs >= it.HoldMs)
                    {
                        it.Leaving = true;
                        if (!animate) { _items.RemoveAt(i); continue; }
                    }
                }
            }

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
                else it.Y += d * (1 - Math.Exp(-deltaMs / 41.5));
            }

            if (_items.Count == 0) Services.RenderFrameHub.Unsubscribe(OnTick);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_items.Count == 0) return;
            _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            Brush panel = Res("Brush.GlassElevated", Res("Brush.Panel", Brushes.White));
            Brush border = Res("Brush.GlassBorder", Res("Brush.LineStrong", Brushes.Gray));
            Brush shadow = Res("Brush.HardShadow", Brushes.Transparent);
            Brush highlight = Res("Brush.GlassHighlight", Brushes.Transparent);

            if (!ReferenceEquals(panel, _themeStamp))
            {
                _themeStamp = panel;
                for (int i = 0; i < _items.Count; i++) BuildItemVisual(_items[i]);
            }
            if (_borderPen == null)
            {
                _borderPen = new Pen(border, 1.0);
                if (_borderPen.CanFreeze) _borderPen.Freeze();
            }

            for (int i = _items.Count - 1; i >= 0; i--)
            {
                ToastItem it = _items[i];
                if (it.Alpha <= 0.005 || it.TitleText == null) continue;
                double x = ActualWidth - 6 - it.Width + it.SlideX;
                double y = it.Y;
                Brush tone = ToneBrush(it.Tone);
                dc.PushOpacity(Math.Min(1.0, Math.Max(0.0, it.Alpha)));

                // 柔和分层投影
                dc.PushOpacity(0.26);
                dc.DrawRoundedRectangle(shadow, null, new Rect(x + 1, y + 4, it.Width, it.Height), CardRadius, CardRadius);
                dc.Pop();

                // 磨砂玻璃主体
                var cardRect = new Rect(x, y, it.Width, it.Height);
                dc.DrawRoundedRectangle(panel, _borderPen, cardRect, CardRadius, CardRadius);

                // 顶部高光细线
                dc.DrawRoundedRectangle(highlight, null, new Rect(x + 14, y + 0.8, Math.Max(0, it.Width - 28), 1.2), 0.6, 0.6);

                // 左侧胶囊语义光条
                dc.DrawRoundedRectangle(tone, null, new Rect(x + 6, y + 9, BarWidth, Math.Max(8, it.Height - 18)), 2, 2);

                // 底部剩余时长进度细线
                double remain = Math.Max(0.0, Math.Min(1.0, 1.0 - it.AgeMs / Math.Max(1.0, it.HoldMs)));
                double progW = Math.Max(0, (it.Width - 28) * remain);
                if (progW > 1)
                {
                    dc.PushOpacity(0.55);
                    dc.DrawRoundedRectangle(tone, null, new Rect(x + 14, y + it.Height - 3.0, progW, 1.8), 0.9, 0.9);
                    dc.Pop();
                }

                FormattedText t = it.TitleText;
                FormattedText s = it.SubtitleText;
                double total = t.Height + (s != null ? 2.0 + s.Height : 0.0);
                double ty = y + Math.Max(4.0, (it.Height - total) / 2.0 - 1.0);
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
        public Func<string, string> Validate;
    }

    /// <summary>
    /// 现代化毛玻璃模态对话框浮层：
    /// 柔和遮罩 + 居中圆角玻璃面板 + 顶部镜面折光 + 弹性缩放淡入/淡出 + 可选背景动态模糊通知。
    /// </summary>
    public sealed class DialogHost : ContentControl
    {
        private enum DialogKind { Confirm, Info, Prompt }

        private const double PanelWidth = 468.0;
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
        private ScaleTransform _panelScale;
        private Button _primaryButton;
        private Button _cancelButton;
        private Button _closeButton;
        private TextBox _input;
        private TextBlock _errorLine;

        /// <summary>当前是否有对话框打开。</summary>
        public bool IsOpen { get; private set; }

        /// <summary>打开/关闭模态状态变化（宿主可借此对底层内容施加平滑毛玻璃虚化）。</summary>
        public event EventHandler<bool> OpenStateChanged;

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

        internal void DebugAcceptPrimary() { AcceptPrimary(); }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e) { FitPanel(); }

        private void FitPanel()
        {
            if (_panel == null) return;
            _panel.MaxWidth = Math.Max(0, ActualWidth - 48);
            _panel.MaxHeight = Math.Max(0, ActualHeight - 48);
        }

        private static bool? _animationOverride;

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
            if (IsOpen) Finish(null);
        }

        private void OnThemeChanged(object sender, EventArgs e) { }

        // ══════════════════════════ 公开 API ══════════════════════════

        internal void Dismiss() => RequestClose(CancelResult());
        internal void FocusDialog() => FocusInitial();

        public Task<bool> ConfirmAsync(DialogOptions options)
        {
            if (IsOpen) return Task.FromResult(false);
            var tcs = new TaskCompletionSource<bool>();
            Open(options, DialogKind.Confirm, delegate (object r) { tcs.TrySetResult(r is bool && (bool)r); });
            return tcs.Task;
        }

        public Task ShowInfoAsync(DialogOptions options)
        {
            if (IsOpen) return Task.FromResult(0);
            var tcs = new TaskCompletionSource<bool>();
            Open(options, DialogKind.Info, delegate (object r) { tcs.TrySetResult(true); });
            return tcs.Task;
        }

        public Task<string> PromptAsync(DialogOptions options)
        {
            if (IsOpen) return Task.FromResult<string>(null);
            var tcs = new TaskCompletionSource<string>();
            Open(options, DialogKind.Prompt, delegate (object r) { tcs.TrySetResult(r as string); });
            return tcs.Task;
        }

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
                _closeToken++;
                _closing = false;
                Finish(CancelResult());
            }
            catch { }
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
            UpdateLayout();
            FitPanel();
            OpenStateChanged?.Invoke(this, true);
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
            _root.Children.Add(_mask);

            var center = new Grid { Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            var frame = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };

            _panel = new Border { Width = PanelWidth, CornerRadius = new CornerRadius(16) };
            ApplyStyle(_panel, "Widget.Dialog.Panel");
            _panel.SetResourceReference(Border.BackgroundProperty, "Brush.GlassElevated");
            _panel.SetResourceReference(Border.BorderBrushProperty, "Brush.GlassBorder");
            _panel.BorderThickness = new Thickness(1);
            _panel.Padding = new Thickness(24);
            _panel.Effect = new DropShadowEffect
            {
                BlurRadius = 32,
                ShadowDepth = 10,
                Direction = 270,
                Opacity = 0.42,
                Color = Colors.Black
            };
            frame.Children.Add(_panel);

            // 顶部玻璃高光细线
            var sheen = new Border
            {
                Height = 1.5,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(24, 0.8, 24, 0),
                CornerRadius = new CornerRadius(1),
                IsHitTestVisible = false
            };
            sheen.SetResourceReference(Border.BackgroundProperty, "Brush.GlassHighlight");
            frame.Children.Add(sheen);

            _panelScale = new ScaleTransform(AnimationsEnabled ? 0.94 : 1.0, AnimationsEnabled ? 0.94 : 1.0);
            _panelTransform = new TranslateTransform(0, AnimationsEnabled ? 12.0 : 0.0);
            var tg = new TransformGroup();
            tg.Children.Add(_panelScale);
            tg.Children.Add(_panelTransform);
            frame.RenderTransform = tg;
            _panelFrame = frame;
            center.Children.Add(frame);
            _root.Children.Add(center);

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _panel.Child = layout;

            // 标题行
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var accentPill = new Border
            {
                Width = 4,
                Height = 18,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            accentPill.SetResourceReference(Border.BackgroundProperty, _options.Danger ? "Brush.Err" : "Brush.Signal");
            titleRow.Children.Add(accentPill);

            var title = new TextBlock();
            ApplyStyle(title, "Widget.Dialog.Title");
            title.FontFamily = FontOr("Font.Cjk", FaceCjk.FontFamily);
            title.FontSize = SizeOr("FS.PageTitle", 15.0);
            title.FontWeight = FontWeights.SemiBold;
            title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Ink");
            title.TextWrapping = TextWrapping.Wrap;
            title.Text = string.IsNullOrEmpty(_options.Title) ? "提示" : _options.Title;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(title, 1);
            titleRow.Children.Add(title);

            _closeButton = new Button { Content = CloseGlyph(), ToolTip = "关闭（Esc）", Focusable = true, TabIndex = 4 };
            ApplyButtonStyle(_closeButton, "Btn.Icon");
            _closeButton.Width = 28;
            _closeButton.Height = 28;
            _closeButton.Padding = new Thickness(0);
            _closeButton.VerticalAlignment = VerticalAlignment.Top;
            _closeButton.Click += delegate { Dismiss(); };
            System.Windows.Automation.AutomationProperties.SetName(_closeButton, "关闭");
            Grid.SetColumn(_closeButton, 2);
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

            if (!string.IsNullOrEmpty(_options.Message))
            {
                var msg = new TextBlock { Margin = new Thickness(0, 14, 0, 0) };
                ApplyStyle(msg, "Widget.Dialog.Body");
                msg.FontFamily = FontOr("Font.Cjk", FaceCjk.FontFamily);
                msg.FontSize = SizeOr("FS.Body", 12.5);
                msg.SetResourceReference(TextBlock.ForegroundProperty, "Brush.InkSoft");
                msg.TextWrapping = TextWrapping.Wrap;
                msg.Text = _options.Message;
                body.Children.Add(msg);
            }

            if (!string.IsNullOrEmpty(_options.Detail))
            {
                var box = new Border { Margin = new Thickness(0, 12, 0, 0), CornerRadius = new CornerRadius(10) };
                ApplyStyle(box, "Widget.Dialog.DetailBox");
                box.SetResourceReference(Border.BackgroundProperty, "Brush.GlassSunken");
                box.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
                box.BorderThickness = new Thickness(1);
                box.Padding = new Thickness(12);
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

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 22, 0, 0),
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
                    return;
                }
            }
            RequestClose(SuccessResult());
        }

        private void RequestClose(object result)
        {
            if (!IsOpen || _closing) return;
            _closing = true;
            int token = ++_closeToken;
            Services.UiMotion.CancelMotion(this);
            OpenStateChanged?.Invoke(this, false);
            if (!AnimationsEnabled)
            {
                Finish(result);
                return;
            }
            try
            {
                if (_mask != null)
                {
                    var fade = Services.UiMotion.WithRefreshFrameRate(new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.HoldEnd });
                    _mask.BeginAnimation(UIElement.OpacityProperty, fade);
                }
                if (_panelFrame != null)
                {
                    var panelFade = Services.UiMotion.WithRefreshFrameRate(new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.HoldEnd });
                    Services.UiMotion.TrackAnimation(this, panelFade, settle =>
                    {
                        if (settle && token == _closeToken) Finish(result);
                    });
                    _panelFrame.BeginAnimation(UIElement.OpacityProperty, panelFade);
                }
                if (_panelTransform != null)
                {
                    var slide = Services.UiMotion.WithRefreshFrameRate(new DoubleAnimation(10.0, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.HoldEnd, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
                    _panelTransform.BeginAnimation(TranslateTransform.YProperty, slide);
                }
                if (_panelScale != null)
                {
                    var s = Services.UiMotion.WithRefreshFrameRate(new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.HoldEnd, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
                    _panelScale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
                    _panelScale.BeginAnimation(ScaleTransform.ScaleYProperty, s);
                }
            }
            catch
            {
                Finish(result);
            }
        }

        private void Finish(object result)
        {
            Services.UiMotion.CancelMotion(this);
            _closeToken++;
            IsOpen = false;
            _closing = false;
            OpenStateChanged?.Invoke(this, false);
            Content = null;
            _root = null;
            _mask = null;
            _panel = null;
            _panelFrame = null;
            _panelTransform = null;
            _panelScale = null;
            _primaryButton = null;
            _cancelButton = null;
            _closeButton = null;
            _input = null;
            _errorLine = null;
            _options = null;
            Visibility = Visibility.Collapsed;

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
            if (complete != null) complete(result);
        }

        // ══════════════════════════ 动画 / 样式工具 ══════════════════════════

        private void StartOpenAnimation()
        {
            if (!AnimationsEnabled) return;
            try
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                if (_mask != null)
                    _mask.BeginAnimation(UIElement.OpacityProperty,
                        Services.UiMotion.WithRefreshFrameRate(new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(140))));
                if (_panelFrame != null)
                    _panelFrame.BeginAnimation(UIElement.OpacityProperty,
                        Services.UiMotion.WithRefreshFrameRate(new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease }));
                if (_panelTransform != null)
                {
                    var slide = Services.UiMotion.WithRefreshFrameRate(new DoubleAnimation(12.0, 0.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
                    Services.UiMotion.TrackAnimation(this, slide);
                    _panelTransform.BeginAnimation(TranslateTransform.YProperty, slide);
                }
                if (_panelScale != null)
                {
                    var s = Services.UiMotion.WithRefreshFrameRate(new DoubleAnimation(0.94, 1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
                    _panelScale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
                    _panelScale.BeginAnimation(ScaleTransform.ScaleYProperty, s);
                }
            }
            catch { Services.UiMotion.CancelMotion(this); }
        }

        private void ApplyStyle(FrameworkElement element, string key)
        {
            if (element == null || string.IsNullOrEmpty(key)) return;
            Style style = TryFindResource(key) as Style;
            if (style == null && Application.Current != null) style = Application.Current.TryFindResource(key) as Style;
            if (style != null) element.Style = style;
        }

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
