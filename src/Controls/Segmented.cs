using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LodStudio.Services;

namespace LodStudio.Controls
{
    public sealed class SegOption
    {
        public string Text;
        public int Count = -1;
        public bool Enabled = true;
        public SegOption() { }
        public SegOption(string text, int count = -1) { Text = text; Count = count; }
        public string Display { get { return Count >= 0 ? Text + " " + Count : Text; } }
    }

    public enum SegVariant
    {
        /// <summary>页签：纯文字 + 底部平滑滑行信号光条（选中）</summary>
        Tabs,
        /// <summary>分段按钮：圆角玻璃容器 + 弹性滑行胶囊选中块</summary>
        Boxed
    }

    /// <summary>
    /// 现代化自绘分段控件（页签 / 胶囊分段按钮），支持平滑滑动指示器动画。
    /// </summary>
    public sealed class Segmented : FrameworkElement
    {
        private static readonly Typeface Cjk = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Typeface CjkBold = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        private readonly List<Rect> _rects = new List<Rect>();
        private int _hover = -1;
        private int _selected;

        private double _indX = double.NaN;
        private double _indW = double.NaN;
        private double _targetX;
        private double _targetW;
        private double _velX;
        private double _velW;

        private const double SpringStiffness = 1764;
        private const double SpringDamping = 62;
        private static readonly double SpringFrequency = Math.Sqrt(SpringStiffness - SpringDamping * SpringDamping / 4);

        private bool CanAnimate => IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation &&
                                   UiMotion.ActiveMotionTier > 0 && !UiMotion.SuppressContinuousAmbient;

        public IList<SegOption> Items { get; private set; } = new List<SegOption>();
        public SegVariant Variant { get; set; } = SegVariant.Tabs;
        public double FontSize { get; set; } = 12.5;
        public double CountFontSize { get; set; } = 10.5;

        /// <summary>计数数字的固定槽宽：0→10→100 变化时页签宽度不变，避免整行控件轻微位移。</summary>
        public double CountSlotWidth { get; set; } = 26;
        public double ItemPadding { get; set; } = 14;
        /// <summary>true = 各段等分控件宽度（铺满），false = 按文字自然宽度排列</summary>
        public bool EqualWidths { get; set; }

        public event EventHandler SelectedIndexChanged;

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                if (Items == null || Items.Count == 0) { _selected = 0; return; }
                int v = Math.Max(0, Math.Min(Items.Count - 1, value));
                if (v == _selected) return;
                _selected = v;
                UpdateIndicatorTarget(true);
                InvalidateVisual();
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public SegOption SelectedOption
        {
            get
            {
                if (Items == null || Items.Count == 0) return null;
                return Items[Math.Max(0, Math.Min(Items.Count - 1, _selected))];
            }
        }

        public Segmented()
        {
            Focusable = true;
            SnapsToDevicePixels = true;
            FocusVisualStyle = null;
            Cursor = Cursors.Hand;
            IsKeyboardFocusWithinChanged += delegate { InvalidateVisual(); };
            MouseEnter += delegate { if (IsKeyboardFocused) InvalidateVisual(); };
            MouseLeave += delegate { if (IsKeyboardFocused) InvalidateVisual(); };
            Loaded += delegate { UiMotion.AmbientSuppressionChanged += OnMotionChanged; };
            Unloaded += delegate
            {
                UiMotion.AmbientSuppressionChanged -= OnMotionChanged;
                SnapIndicator();
                InvalidateVisual();
            };
            IsVisibleChanged += delegate { SyncMotion(); };
        }

        public void SetItems(IList<SegOption> items, int selected)
        {
            Items = items ?? new List<SegOption>();
            _selected = Math.Max(0, Math.Min(Math.Max(0, Items.Count - 1), selected));
            _hover = -1;
            SnapIndicator();
            _indX = double.NaN;
            _indW = double.NaN;
            InvalidateMeasure();
            InvalidateVisual();
        }

        private void UpdateIndicatorTarget(bool animate)
        {
            if (_selected < 0 || _selected >= _rects.Count) return;
            Rect r = _rects[_selected];
            _targetX = r.X;
            _targetW = r.Width;
            if (!animate || double.IsNaN(_indX) || !CanAnimate)
            {
                SnapIndicator();
                return;
            }
            RenderFrameHub.Subscribe(OnAnimTick);
        }

        private void SnapIndicator()
        {
            _indX = _targetX;
            _indW = _targetW;
            _velX = _velW = 0;
            RenderFrameHub.Unsubscribe(OnAnimTick);
        }

        private void OnMotionChanged(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess()) SyncMotion();
            else Dispatcher.BeginInvoke(new Action(SyncMotion));
        }

        private void SyncMotion()
        {
            if (CanAnimate) return;
            SnapIndicator();
            InvalidateVisual();
        }

        private void OnAnimTick(double deltaSeconds)
        {
            if (CanAnimate)
            {
                StepSpring(ref _indX, ref _velX, _targetX, deltaSeconds);
                StepSpring(ref _indW, ref _velW, _targetW, deltaSeconds);
                if (Math.Abs(_indX - _targetX) < 0.35 && Math.Abs(_indW - _targetW) < 0.35 &&
                    Math.Abs(_velX) < 12 && Math.Abs(_velW) < 12)
                    SnapIndicator();
            }
            else SnapIndicator();
            InvalidateVisual();
        }

        private static void StepSpring(ref double position, ref double velocity, double target, double elapsed)
        {
            // Exact damped oscillator step for the elapsed frame time.
            double decay = SpringDamping / 2;
            double offset = position - target;
            double sine = Math.Sin(SpringFrequency * elapsed);
            double cosine = Math.Cos(SpringFrequency * elapsed);
            double envelope = Math.Exp(-decay * elapsed);
            double wave = offset * cosine + (velocity + decay * offset) * sine / SpringFrequency;
            velocity = envelope * (velocity * cosine -
                (SpringStiffness * offset + decay * velocity) * sine / SpringFrequency);
            position = target + envelope * wave;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = 0, h = 0;
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (int i = 0; i < Items.Count; i++)
            {
                var opt = Items[i];
                var ft = MakeText(opt, Cjk, dpi, false);
                var ftc = opt.Count >= 0 ? MakeCount(opt, dpi, false) : null;
                double iw = ft.Width + (ftc != null ? 5 + CountSlotWidth : 0) + ItemPadding * 2;
                w += iw;
                h = Math.Max(h, ft.Height + (Variant == SegVariant.Boxed ? 12 : 8));
            }
            if (Variant == SegVariant.Boxed) { w += 6; h += 4; }
            if (EqualWidths && !double.IsInfinity(availableSize.Width) && availableSize.Width > w)
                w = availableSize.Width;
            if (EqualWidths && !double.IsInfinity(availableSize.Height) && availableSize.Height > h)
                h = availableSize.Height;
            return new Size(w, h);
        }

        private double[] BuildWidths(double total)
        {
            int n = Math.Max(1, Items.Count);
            double[] widths = new double[n];
            if (!EqualWidths)
            {
                var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                for (int i = 0; i < Items.Count; i++)
                {
                    var opt = Items[i];
                    var ft = MakeText(opt, Cjk, dpi, false);
                    var ftc = opt.Count >= 0 ? MakeCount(opt, dpi, false) : null;
                    widths[i] = ft.Width + (ftc != null ? 5 + CountSlotWidth : 0) + ItemPadding * 2;
                }
                return widths;
            }
            double usable = Math.Max(1, total - (Variant == SegVariant.Boxed ? 6 : 0));
            double each = usable / n;
            for (int i = 0; i < n; i++) widths[i] = each;
            return widths;
        }

        protected override void OnRender(DrawingContext dc)
        {
            _rects.Clear();
            var signal = Res("Brush.Signal");
            var signalGlow = Res("Brush.SignalGlow");
            var signalSoft = Res("Brush.SignalSoft");
            var line = Res("Brush.Line");
            var prismBorder = Res("Brush.PrismBorder");
            var glassPanel = Res("Brush.GlassElevated");
            var glassSunken = Res("Brush.GlassSunken");
            var hoverFill = Res("Brush.RowHover");
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            double h = ActualHeight;
            double x = Variant == SegVariant.Boxed ? 3 : 0;

            if (Variant == SegVariant.Boxed)
            {
                dc.DrawRoundedRectangle(glassSunken, new Pen(line, 1),
                    new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, h - 1)), 9, 9);
            }

            double[] widths = BuildWidths(ActualWidth);
            for (int i = 0; i < Items.Count; i++)
            {
                double iw = widths[i];
                _rects.Add(new Rect(x, 0, iw, h));
                x += iw;
            }

            if (_selected >= 0 && _selected < _rects.Count)
            {
                var selected = _rects[_selected];
                if (double.IsNaN(_indX) || selected.X != _targetX || selected.Width != _targetW)
                    UpdateIndicatorTarget(false);
            }

            // 选中滑行胶囊（Boxed 在文字下层绘制）
            if (Variant == SegVariant.Boxed && _selected >= 0 && _selected < _rects.Count && !double.IsNaN(_indX))
            {
                double padY = 3;
                var pillRect = new Rect(_indX + 0.5, padY, Math.Max(2, _indW - 1), Math.Max(2, h - padY * 2));
                dc.DrawRoundedRectangle(glassPanel, new Pen(prismBorder, 1), pillRect, 6.5, 6.5);
                // 底部微弱信号强调线
                double barW = Math.Min(22, Math.Max(8, pillRect.Width * 0.34));
                dc.DrawRoundedRectangle(signal, null,
                    new Rect(pillRect.X + (pillRect.Width - barW) / 2, pillRect.Bottom - 2.5, barW, 2), 1, 1);
            }

            for (int i = 0; i < Items.Count; i++)
            {
                var opt = Items[i];
                bool sel = i == _selected;
                var r = _rects[i];
                var ft = MakeText(opt, sel ? CjkBold : Cjk, dpi, sel);
                FormattedText ftc = opt.Count >= 0 ? MakeCount(opt, dpi, sel) : null;

                if (Variant == SegVariant.Tabs)
                {
                    if (i == _hover && opt.Enabled && !sel)
                    {
                        dc.DrawRoundedRectangle(hoverFill, null, new Rect(r.X + 2, 3, Math.Max(0, r.Width - 4), Math.Max(0, h - 8)), 6, 6);
                    }
                    double ty = (h - ft.Height) / 2 - 1.5;
                    double tx = r.X + ItemPadding;
                    dc.DrawText(ft, new Point(tx, ty));
                    if (ftc != null)
                    {
                        double cx = tx + ft.Width + 5;
                        var badgeRect = new Rect(cx, ty + 1, CountSlotWidth, Math.Max(14, ft.Height - 2));
                        dc.DrawRoundedRectangle(sel ? signalSoft : glassSunken, null, badgeRect, 6, 6);
                        dc.DrawText(ftc, new Point(cx + (CountSlotWidth - ftc.Width) / 2, ty + ft.Baseline - ftc.Baseline));
                    }
                }
                else
                {
                    if (i == _hover && !sel && opt.Enabled)
                    {
                        dc.DrawRoundedRectangle(hoverFill, null, new Rect(r.X + 1, 3, Math.Max(0, r.Width - 2), Math.Max(0, h - 6)), 6, 6);
                    }
                    double ty = (h - ft.Height) / 2 - 0.5;
                    double tx = r.X + (r.Width - ft.Width - (ftc != null ? 5 + CountSlotWidth : 0)) / 2;
                    dc.DrawText(ft, new Point(tx, ty));
                    if (ftc != null)
                        dc.DrawText(ftc, new Point(tx + ft.Width + 5 + (CountSlotWidth - ftc.Width) / 2, ty + ft.Baseline - ftc.Baseline));
                }
            }

            // Tabs 底部滑行信号光条
            if (Variant == SegVariant.Tabs && _selected >= 0 && _selected < _rects.Count && !double.IsNaN(_indX))
            {
                double left = _indX + Math.Max(4, ItemPadding - 4);
                double width = Math.Max(10, _indW - Math.Max(8, (ItemPadding - 4) * 2));
                dc.PushOpacity(0.45);
                dc.DrawRoundedRectangle(signalGlow, null, new Rect(left - 4, h - 6, width + 8, 6), 3, 3);
                dc.Pop();
                dc.DrawRoundedRectangle(signalGlow, null, new Rect(left - 2, h - 4.5, width + 4, 4.5), 2.25, 2.25);
                dc.DrawRoundedRectangle(signal, null, new Rect(left, h - 3.0, width, 2.5), 1.25, 1.25);
            }
        }

        private Brush Res(string key)
        {
            var b = TryFindResource(key) as Brush;
            return b ?? Brushes.Black;
        }

        private FormattedText MakeText(SegOption opt, Typeface face, double dpi, bool selected)
        {
            Brush brush = selected ? Res("Brush.Ink") : Res("Brush.InkSoft");
            return new FormattedText(opt.Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face,
                FontSize, brush, dpi);
        }

        private FormattedText MakeCount(SegOption opt, double dpi, bool selected)
        {
            var tf = new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal,
                selected ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
            Brush brush = selected ? Res("Brush.Signal") : Res("Brush.InkFaint");
            return new FormattedText(opt.Count.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, tf, CountFontSize, brush, dpi);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = HitTest(e.GetPosition(this));
            if (idx != _hover)
            {
                _hover = idx;
                Cursor = idx >= 0 && (idx >= Items.Count || Items[idx].Enabled) ? Cursors.Hand : Cursors.Arrow;
                InvalidateVisual();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (_hover != -1) { _hover = -1; InvalidateVisual(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            int idx = HitTest(e.GetPosition(this));
            if (idx >= 0 && idx < Items.Count && Items[idx].Enabled) SelectedIndex = idx;
            e.Handled = true;
            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int n = Items.Count;
            if (n == 0) { base.OnKeyDown(e); return; }
            if (e.Key == Key.Left)
            {
                int i = _selected - 1;
                while (i >= 0 && !Items[i].Enabled) i--;
                if (i >= 0) SelectedIndex = i;
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                int i = _selected + 1;
                while (i < n && !Items[i].Enabled) i++;
                if (i < n) SelectedIndex = i;
                e.Handled = true;
            }
            else if (e.Key == Key.Home) { SelectedIndex = 0; e.Handled = true; }
            else if (e.Key == Key.End) { SelectedIndex = n - 1; e.Handled = true; }
            base.OnKeyDown(e);
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < _rects.Count; i++)
                if (_rects[i].Contains(p)) return i;
            return -1;
        }
    }
}
