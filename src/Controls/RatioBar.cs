using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LodStudio.Controls
{
    /// <summary>
    /// 保留比例条：6px 细轨 + 竖把手；可拖动改比例（0.5% 步进），键盘 ←→ ±0.5%、Shift ±5%。
    /// 只在 hover/拖动时用信号色，静息用中性色（信号色只给"当前/交互"）。
    /// 事件：RatioChanged(新比例 0..1，拖动中实时)；DragCompleted。
    /// </summary>
    public sealed class RatioBar : FrameworkElement
    {
        private double _ratio = 1.0;
        private bool _hover;
        private bool _dragging;

        private double _dragStartX;
        private double _dragStartRatio;

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
            FocusVisualStyle = null;          // 不用系统默认虚线焦点框（自绘中性实线，仅键盘时）
            Cursor = Cursors.Hand;
            ToolTip = "拖动改比例（键盘 ←→ 0.5%，Shift ±5%）";
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 8 || h < 6) return;
            var track = TryFindResource("Brush.Sunken") as Brush ?? Brushes.LightGray;
            var lineStrong = TryFindResource("Brush.LineStrong") as Brush ?? Brushes.Gray;
            var signal = TryFindResource("Brush.Signal") as Brush ?? Brushes.Orange;
            var inkSoft = TryFindResource("Brush.InkSoft") as Brush ?? Brushes.Gray;
            var ink = TryFindResource("Brush.Ink") as Brush ?? Brushes.Black;

            double trackH = 6;
            double y = (h - trackH) / 2;
            double handleW = 8, handleH = 14;
            double usable = Math.Max(1, w - handleW);
            dc.DrawRectangle(track, null, new Rect(0, y, w, trackH));

            double fillW = usable * _ratio;
            Brush fill = (_hover || _dragging) ? signal : lineStrong;
            dc.DrawRectangle(fill, null, new Rect(0, y, Math.Max(0, fillW), trackH));

            double hx = fillW;
            var hb = (_hover || _dragging) ? signal : inkSoft;
            dc.DrawRectangle(hb, new Pen(ink, 0), new Rect(hx, (h - handleH) / 2, handleW, handleH));
            dc.DrawRectangle(null, new Pen((_hover || _dragging) ? ink : lineStrong, 0.8),
                new Rect(hx + 0.4, (h - handleH) / 2 + 0.4, handleW - 0.8, handleH - 0.8));

        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            _hover = true; InvalidateVisual(); base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _hover = false; InvalidateVisual(); base.OnMouseLeave(e);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            CaptureMouse();
            _dragging = true;
            _dragStartX = e.GetPosition(this).X;
            _dragStartRatio = _ratio;
            SetFromX(_dragStartX);
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
                InvalidateVisual();
                DragCompleted?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            base.OnMouseLeftButtonUp(e);
        }

        private void SetFromX(double x)
        {
            double handleW = 8;
            double usable = Math.Max(1, ActualWidth - handleW);
            double r = Math.Max(0.001, Math.Min(Maximum, x / usable));
            r = Math.Round(r * 1000.0) / 1000.0;   // 0.1% 步进
            r = Math.Min(Maximum, Math.Max(0.001, r));
            if (Math.Abs(r - _ratio) < 1e-9) return;
            // 写回 DP：TwoWay 绑定会把新值送到 ViewModel（百分比数字随之实时变化）
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
