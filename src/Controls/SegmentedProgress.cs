using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace LodStudio.Controls
{
    /// <summary>
    /// 分段进度条：10px 一格、2px 缝；未开始/未知时三格游走；失败变红；数值缓动。
    /// 只画，不抢事件；高度 3~4px。
    /// </summary>
    public sealed class SegmentedProgress : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            "Value", typeof(double), typeof(SegmentedProgress),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
            "IsIndeterminate", typeof(bool), typeof(SegmentedProgress),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnAnimFlagChanged));

        public static readonly DependencyProperty FailedProperty = DependencyProperty.Register(
            "Failed", typeof(bool), typeof(SegmentedProgress),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty CellWidthProperty = DependencyProperty.Register(
            "CellWidth", typeof(double), typeof(SegmentedProgress),
            new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty CellGapProperty = DependencyProperty.Register(
            "CellGap", typeof(double), typeof(SegmentedProgress),
            new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>0..1</summary>
        public double Value { get { return (double)GetValue(ValueProperty); } set { SetValue(ValueProperty, value); } }
        public bool IsIndeterminate { get { return (bool)GetValue(IsIndeterminateProperty); } set { SetValue(IsIndeterminateProperty, value); } }
        public bool Failed { get { return (bool)GetValue(FailedProperty); } set { SetValue(FailedProperty, value); } }
        public double CellWidth { get { return (double)GetValue(CellWidthProperty); } set { SetValue(CellWidthProperty, value); } }
        public double CellGap { get { return (double)GetValue(CellGapProperty); } set { SetValue(CellGapProperty, value); } }

        private readonly DispatcherTimer _timer;
        private double _shown;          // 缓动后的显示值
        private double _phase;          // 游走相位
        private int _lastCells = -1;
        private bool _animating;

        public SegmentedProgress()
        {
            SnapsToDevicePixels = true;
            Height = 3;
            IsHitTestVisible = false;
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
            _timer.Tick += OnTick;
        }

        private static void OnAnimFlagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var p = d as SegmentedProgress;
            if (p != null) p.SyncTimer();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = Math.Max(2, ActualHeight);
            if (w < 2) return;
            var sunken = TryFindResource("Brush.Sunken") as Brush ?? Brushes.Gray;
            var signal = TryFindResource("Brush.Signal") as Brush ?? Brushes.Orange;
            var err = TryFindResource("Brush.Err") as Brush ?? Brushes.Red;
            var fill = Failed ? err : signal;

            double cell = Math.Max(2, CellWidth);
            double gap = Math.Max(0, CellGap);
            int total = Math.Max(1, (int)Math.Floor((w + gap) / (cell + gap)));
            dc.DrawRectangle(sunken, null, new Rect(0, 0, w, h));

            void DrawCell(int i)
            {
                double x = i * (cell + gap);
                double cw = Math.Min(cell, w - x);
                if (cw <= 0) return;
                dc.DrawRectangle(fill, null, new Rect(x, 0, cw, h));
            }

            if (IsIndeterminate)
            {
                int span = 3;
                double p = _phase % 1.0;
                int maxStart = Math.Max(1, total - span);
                int start = (int)Math.Round(p * maxStart);
                for (int k = 0; k < span; k++)
                    if (start + k < total) DrawCell(start + k);
                return;
            }

            int filled = (int)Math.Round(Math.Max(0, Math.Min(1, _shown)) * total);
            for (int i = 0; i < filled; i++) DrawCell(i);
            if (_lastCells < 0) _lastCells = total;
        }

        private void SyncTimer()
        {
            bool need = IsIndeterminate || Math.Abs(Value - _shown) > 0.001;
            if (need && !_animating)
            {
                _animating = true;
                _shown = Math.Min(_shown, Value);
                if (IsIndeterminate) _phase = 0;
                _timer.Start();
            }
            else if (!need && _animating)
            {
                _animating = false;
                _timer.Stop();
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            bool changed = false;
            if (IsIndeterminate)
            {
                _phase += 0.035;
                if (_phase > 1.2) _phase = 0;
                changed = true;
            }
            else
            {
                double target = Math.Max(0, Math.Min(1, Value));
                if (Math.Abs(target - _shown) > 0.0005)
                {
                    _shown += (target - _shown) * 0.28;
                    if (Math.Abs(target - _shown) < 0.002) _shown = target;
                    changed = true;
                }
            }
            if (changed) InvalidateVisual();
            SyncTimer();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == ValueProperty) SyncTimer();
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            SyncTimer();
        }
    }
}
