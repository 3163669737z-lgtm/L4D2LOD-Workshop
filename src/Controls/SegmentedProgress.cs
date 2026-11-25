using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using LodStudio.Services;

namespace LodStudio.Controls
{
    /// <summary>
    /// 现代化流光分段能量进度条：圆角胶囊分节 + 柔和光晕 + 未知进度流光彗星波；失败变红；数值缓动。
    /// </summary>
    public sealed class SegmentedProgress : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            "Value", typeof(double), typeof(SegmentedProgress),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnAnimFlagChanged));

        public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
            "IsIndeterminate", typeof(bool), typeof(SegmentedProgress),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnAnimFlagChanged));

        public static readonly DependencyProperty FailedProperty = DependencyProperty.Register(
            "Failed", typeof(bool), typeof(SegmentedProgress),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnAnimFlagChanged));

        public static readonly DependencyProperty CellWidthProperty = DependencyProperty.Register(
            "CellWidth", typeof(double), typeof(SegmentedProgress),
            new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty CellGapProperty = DependencyProperty.Register(
            "CellGap", typeof(double), typeof(SegmentedProgress),
            new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>0..1</summary>
        public double Value { get { return (double)GetValue(ValueProperty); } set { SetValue(ValueProperty, value); } }
        public bool IsIndeterminate { get { return (bool)GetValue(IsIndeterminateProperty); } set { SetValue(IsIndeterminateProperty, value); } }
        public bool Failed { get { return (bool)GetValue(FailedProperty); } set { SetValue(FailedProperty, value); } }
        public double CellWidth { get { return (double)GetValue(CellWidthProperty); } set { SetValue(CellWidthProperty, value); } }
        public double CellGap { get { return (double)GetValue(CellGapProperty); } set { SetValue(CellGapProperty, value); } }

        private readonly Action<double> _onFrame;
        private double _shown;
        private double _phase;

        private bool CanAnimate => IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation &&
                                   UiMotion.ActiveMotionTier > 0 && !UiMotion.SuppressContinuousAmbient;
        private bool HasSheen => !IsIndeterminate && !Failed && Value > 0.01 && Value < 0.999;
        private double TargetValue => Value > 0 ? Math.Min(1, Value) : 0;

        public SegmentedProgress()
        {
            SnapsToDevicePixels = true;
            Height = 4;
            IsHitTestVisible = false;
            _onFrame = OnTick;
            Loaded += delegate
            {
                SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
                UiMotion.AmbientSuppressionChanged += OnMotionChanged;
                SyncTimer();
            };
            Unloaded += delegate
            {
                SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
                UiMotion.AmbientSuppressionChanged -= OnMotionChanged;
                RenderFrameHub.Unsubscribe(_onFrame);
                _phase = 0;
                _shown = TargetValue;
            };
            IsVisibleChanged += delegate { SyncTimer(); };
        }

        private static void OnAnimFlagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var progress = (SegmentedProgress)d;
            if (e.Property == IsIndeterminateProperty) progress._phase = 0;
            progress.SyncTimer();
        }

        private void OnSystemParametersChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) OnMotionChanged(sender, e);
        }

        private void OnMotionChanged(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess()) SyncTimer();
            else Dispatcher.BeginInvoke(new Action(SyncTimer));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = Math.Max(2, ActualHeight);
            if (w < 2) return;
            var sunken = TryFindResource("Brush.Sunken") as Brush ?? Brushes.Gray;
            var signal = TryFindResource("Brush.SignalGrad") as Brush ?? TryFindResource("Brush.Signal") as Brush ?? Brushes.Orange;
            var signalGlow = TryFindResource("Brush.SignalGlow") as Brush ?? Brushes.Transparent;
            var err = TryFindResource("Brush.DangerGrad") as Brush ?? TryFindResource("Brush.Err") as Brush ?? Brushes.Red;
            var fill = Failed ? err : signal;
            bool animate = CanAnimate;
            double r = h / 2.0;

            double cell = Math.Max(3, CellWidth);
            double gap = Math.Max(0, CellGap);
            int total = Math.Max(1, (int)Math.Floor((w + gap) / (cell + gap)));
            dc.DrawRoundedRectangle(sunken, null, new Rect(0, 0, w, h), r, r);

            void DrawCell(int i, double opacity = 1.0, Brush sheen = null)
            {
                double x = i * (cell + gap);
                double cw = Math.Min(cell, w - x);
                if (cw <= 0) return;
                var rect = new Rect(x, 0, cw, h);
                if (opacity < 0.99) dc.PushOpacity(opacity);
                dc.DrawRoundedRectangle(fill, null, rect, r, r);
                if (sheen != null) dc.DrawRoundedRectangle(sheen, null, rect, r, r);
                if (opacity < 0.99) dc.Pop();
            }

            if (IsIndeterminate)
            {
                const int span = 5;
                double head = animate ? _phase * (total + span) : 0;
                double headX = head * (cell + gap) + cell / 2;
                double haloRadius = cell + gap;
                if (headX - haloRadius < w)
                {
                    Color haloColor = (signalGlow as SolidColorBrush)?.Color ?? Colors.Transparent;
                    var halo = new RadialGradientBrush(haloColor, Color.FromArgb(0, haloColor.R, haloColor.G, haloColor.B));
                    dc.PushClip(new RectangleGeometry(new Rect(0, -h, w, h * 3)));
                    dc.DrawEllipse(halo, null, new Point(headX, r), haloRadius, h * 1.5);
                    dc.Pop();
                }
                for (int k = 0; k < span; k++)
                {
                    int idx = (int)Math.Floor(head) - k;
                    if (idx >= 0 && idx < total)
                    {
                        double alpha = 1.0 - (k / (double)span) * 0.78;
                        DrawCell(idx, alpha);
                    }
                }
                return;
            }

            if (TargetValue == 0) return;
            double shown = animate ? _shown : TargetValue;
            int filled = Math.Max(1, (int)Math.Round(shown * total));
            double tip = Math.Min(w, (filled - 1) * (cell + gap) + cell);
            Brush sheen = null;
            if (!Failed)
            {
                dc.DrawRoundedRectangle(signalGlow, null, new Rect(0, -1, tip, h + 2), r + 1, r + 1);
                if (animate && HasSheen && TryFindResource("Brush.ShimmerSweep") is LinearGradientBrush sweep)
                {
                    double width = cell * 4;
                    double x = _phase * (tip + width) - width;
                    sheen = new LinearGradientBrush(sweep.GradientStops, new Point(x, 0), new Point(x + width, 0))
                    {
                        MappingMode = BrushMappingMode.Absolute,
                        Opacity = 0.65
                    };
                }
            }
            for (int i = 0; i < filled; i++) DrawCell(i, sheen: sheen);
            if (!Failed)
            {
                var jewel = new Point(tip - Math.Min(r, tip / 2), r);
                dc.DrawEllipse(signalGlow, null, jewel, r + 1, r + 1);
                dc.DrawEllipse(Brushes.FloralWhite, null, jewel, r * 0.62, r * 0.62);
            }
        }

        private void SyncTimer()
        {
            double target = TargetValue;
            bool animate = CanAnimate;
            bool sweep = animate && (IsIndeterminate || HasSheen);
            if (!sweep) _phase = 0;
            if (!animate || target == 0 || target == 1 || Math.Abs(target - _shown) < 0.002)
                _shown = target;
            if (sweep || (animate && _shown != target)) RenderFrameHub.Subscribe(_onFrame);
            else RenderFrameHub.Unsubscribe(_onFrame);
            InvalidateVisual();
        }

        private void OnTick(double dt)
        {
            if (CanAnimate)
            {
                if (IsIndeterminate || HasSheen)
                    _phase = (_phase + dt * (IsIndeterminate ? 1.0 : 0.5)) % 1.0;
                if (!IsIndeterminate)
                    _shown += (TargetValue - _shown) * (1 - Math.Exp(-dt / 0.073));
            }
            SyncTimer();
        }
    }
}
