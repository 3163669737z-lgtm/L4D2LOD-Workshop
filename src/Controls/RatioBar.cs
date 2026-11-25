using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LodStudio.Services;

namespace LodStudio.Controls
{
    /// <summary>
    /// 现代化保留比例滑条：圆角胶囊轨道 + 渐变能量填充 + 发光圆钮；
    /// 可拖动改比例（0.1% 步进），键盘 ←→ ±0.5%、Shift ±5%。
    /// </summary>
    public sealed class RatioBar : FrameworkElement
    {
        private const double HandleInset = 6.5;
        private readonly Action<double> _onBloomFrame;
        private double _ratio = 1.0;
        private double _activeLevel;
        private bool _hover;
        private bool _dragging;

        private double ActiveTarget => _hover || _dragging || IsKeyboardFocused ? 1 : 0;
        private bool CanAnimate => IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation &&
                                   UiMotion.ActiveMotionTier > 0 && !UiMotion.SuppressContinuousAmbient;

        public event EventHandler<double> RatioChanged;
        public event EventHandler DragCompleted;

        public static readonly DependencyProperty RatioProperty = DependencyProperty.Register(
            "Ratio", typeof(double), typeof(RatioBar),
            new FrameworkPropertyMetadata(1.0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                OnRatioPropertyChanged));

        public double Ratio
        {
            get { return (double)GetValue(RatioProperty); }
            set { SetValue(RatioProperty, Math.Max(0.001, Math.Min(Maximum, value))); }
        }

        /// <summary>允许拖到的最大比例（由上一级比例决定：本级不能超过上一级）。</summary>
        public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
            "Maximum", typeof(double), typeof(RatioBar), new PropertyMetadata(1.0, OnMaximumChanged));

        public double Maximum
        {
            get { return (double)GetValue(MaximumProperty); }
            set { SetValue(MaximumProperty, Math.Max(0.001, Math.Min(1.0, value))); }
        }

        private static void OnMaximumChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var bar = d as RatioBar;
            if (bar == null) return;
            double max = (double)e.NewValue;
            if (max < 0.001) max = 0.001;
            if (max > 1.0) max = 1.0;
            if (bar._ratio > max) bar.SetCurrentValue(RatioProperty, max);
        }

        private static void OnRatioPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var bar = d as RatioBar;
            if (bar == null) return;
            bar._ratio = (double)e.NewValue;
            bar.InvalidateVisual();
        }

        public RatioBar()
        {
            SnapsToDevicePixels = true;
            Height = 22;
            Focusable = true;
            FocusVisualStyle = null;
            Cursor = Cursors.Hand;
            ToolTip = "拖动改比例（键盘 ←→ 0.5%，Shift ±5%）";

            _onBloomFrame = OnBloomTick;
            IsKeyboardFocusedChanged += delegate { UpdateBloom(); };
            Loaded += delegate { UiMotion.AmbientSuppressionChanged += OnMotionChanged; };
            Unloaded += delegate
            {
                UiMotion.AmbientSuppressionChanged -= OnMotionChanged;
                RenderFrameHub.Unsubscribe(_onBloomFrame);
                _hover = false;
                _activeLevel = ActiveTarget;
            };
            IsVisibleChanged += delegate { UpdateBloom(); };
        }

        private void OnMotionChanged(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess()) UpdateBloom();
            else Dispatcher.BeginInvoke(new Action(UpdateBloom));
        }

        private void UpdateBloom()
        {
            if (CanAnimate && _activeLevel != ActiveTarget)
                RenderFrameHub.Subscribe(_onBloomFrame);
            else
            {
                RenderFrameHub.Unsubscribe(_onBloomFrame);
                _activeLevel = ActiveTarget;
            }
            InvalidateVisual();
        }

        private void OnBloomTick(double dt)
        {
            double target = ActiveTarget;
            double step = dt / 0.115;
            double remaining = target - _activeLevel;
            _activeLevel = Math.Abs(remaining) <= step ? target : _activeLevel + Math.Sign(remaining) * step;
            UpdateBloom();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 12 || h < 8) return;

            var track = TryFindResource("Brush.Sunken") as Brush ?? Brushes.LightGray;
            var line = TryFindResource("Brush.Line") as Brush ?? Brushes.Gray;
            var lineStrong = TryFindResource("Brush.LineStrong") as Brush ?? Brushes.Gray;
            var tick = TryFindResource("Brush.Tick") as Brush ?? lineStrong;
            var signalSolid = TryFindResource("Brush.Signal") as Brush ?? Brushes.Orange;
            var signal = TryFindResource("Brush.SignalGrad") as Brush ?? signalSolid;
            var signalGlow = TryFindResource("Brush.SignalGlow") as Brush ?? Brushes.Transparent;
            var panel = TryFindResource("Brush.Panel") as Brush ?? Brushes.White;

            double level = CanAnimate ? _activeLevel : ActiveTarget;
            double bloom = level * level * (3 - 2 * level);
            double trackH = 5 + bloom;
            double y = (h - trackH) / 2.0;
            double knobR = 5.5 + bloom;
            double usable = Math.Max(1, w - HandleInset * 2);

            // 背景轨道
            dc.DrawRoundedRectangle(track, new Pen(line, 0.8), new Rect(0, y, w, trackH), trackH / 2, trackH / 2);

            // 填充条
            double cx = HandleInset + usable * _ratio;
            double fillW = Math.Min(w, Math.Max(trackH, cx));
            var fillRect = new Rect(0, y, fillW, trackH);
            dc.DrawRoundedRectangle(lineStrong, null, fillRect, trackH / 2, trackH / 2);
            if (bloom > 0)
            {
                dc.PushOpacity(bloom);
                dc.DrawRoundedRectangle(signalGlow, null, new Rect(0, y - 1.5, fillW, trackH + 3), (trackH + 3) / 2, (trackH + 3) / 2);
                dc.DrawRoundedRectangle(signal, null, fillRect, trackH / 2, trackH / 2);
                dc.Pop();
            }

            var tickPen = new Pen(tick, 1);
            dc.PushOpacity(0.65);
            for (int i = 1; i <= 3; i++)
            {
                double x = HandleInset + usable * i / 4;
                dc.DrawLine(tickPen, new Point(x, y + trackH + 0.5), new Point(x, y + trackH + 3.5));
            }
            dc.Pop();

            // 圆形操控把手 + 外圈柔光
            var center = new Point(cx, h / 2.0);
            if (signalGlow is SolidColorBrush glow)
            {
                Color color = glow.Color;
                var aura = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B));
                double auraR = knobR + 2.5 + bloom * 2;
                dc.PushOpacity(0.42 + bloom * 0.58);
                dc.DrawEllipse(aura, null, center, auraR, auraR);
                dc.Pop();
            }
            dc.DrawEllipse(panel, new Pen(lineStrong, 1.4), center, knobR, knobR);
            const double coreRadius = 2.7;
            dc.DrawEllipse(lineStrong, null, center, coreRadius, coreRadius);
            if (bloom > 0)
            {
                dc.PushOpacity(bloom);
                dc.DrawEllipse(null, new Pen(signalSolid, 1.7), center, knobR, knobR);
                dc.DrawEllipse(signal, null, center, coreRadius, coreRadius);
                dc.Pop();
            }
            dc.PushOpacity(0.55 + bloom * 0.35);
            dc.DrawEllipse(Brushes.FloralWhite, null, new Point(cx - 0.65, center.Y - 0.8), 0.95, 0.95);
            dc.Pop();
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            _hover = true; UpdateBloom(); base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _hover = false; UpdateBloom(); base.OnMouseLeave(e);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            _dragging = CaptureMouse();
            UpdateBloom();
            SetFromX(e.GetPosition(this).X);
            e.Handled = true;
            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                SetFromX(e.GetPosition(this).X);
                e.Handled = true;
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_dragging)
            {
                _dragging = false;
                try { ReleaseMouseCapture(); } catch { }
                UpdateBloom();
                DragCompleted?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            base.OnMouseLeftButtonUp(e);
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            if (_dragging)
            {
                _dragging = false;
                UpdateBloom();
                DragCompleted?.Invoke(this, EventArgs.Empty);
            }
            base.OnLostMouseCapture(e);
        }

        private void SetFromX(double x)
        {
            double usable = Math.Max(1, ActualWidth - HandleInset * 2);
            double r = Math.Max(0.001, Math.Min(Maximum, (x - HandleInset) / usable));
            r = Math.Round(r * 1000.0) / 1000.0;
            r = Math.Min(Maximum, Math.Max(0.001, r));
            if (Math.Abs(r - _ratio) < 1e-9) return;
            SetCurrentValue(RatioProperty, r);
            var h = RatioChanged;
            if (h != null) h(this, r);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.05 : 0.005;
            double next = _ratio;
            if (e.Key == Key.Left) next -= step;
            else if (e.Key == Key.Right) next += step;
            else { base.OnKeyDown(e); return; }
            next = Math.Max(0.001, Math.Min(Maximum, Math.Round(next * 1000.0) / 1000.0));
            if (Math.Abs(next - _ratio) > 1e-9)
            {
                SetCurrentValue(RatioProperty, next);
                var h = RatioChanged;
                if (h != null) h(this, next);
                DragCompleted?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
        }
    }
}
