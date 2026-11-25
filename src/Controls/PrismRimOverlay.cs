using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LodStudio.Services;

namespace LodStudio.Controls
{
    public sealed class PrismRimOverlay : FrameworkElement
    {
        public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
            nameof(CornerRadius), typeof(double), typeof(PrismRimOverlay),
            new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender), UiMotion.IsNonNegativeFinite);

        public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
            nameof(StrokeThickness), typeof(double), typeof(PrismRimOverlay),
            new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender), UiMotion.IsNonNegativeFinite);

        public static readonly DependencyProperty ProximityRadiusProperty = DependencyProperty.Register(
            nameof(ProximityRadius), typeof(double), typeof(PrismRimOverlay),
            new FrameworkPropertyMetadata(220.0, FrameworkPropertyMetadataOptions.AffectsRender), UiMotion.IsNonNegativeFinite);

        private const double IdleThreshold = 0.015;
        private readonly AmbientFrameTimer _frames;
        private Point _pointer;
        private double _strength;
        private bool _hasPointer;

        public PrismRimOverlay()
        {
            _frames = new AmbientFrameTimer(this, SyncMotion, Tick);
            Loaded += (sender, e) => UiMotion.TransitionStarted += OnTransitionStarted;
            Unloaded += (sender, e) => UiMotion.TransitionStarted -= OnTransitionStarted;
            SizeChanged += delegate
            {
                if (_hasPointer || _strength > 0) _frames.Start();
            };
        }

        public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
        public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
        public double ProximityRadius { get => (double)GetValue(ProximityRadiusProperty); set => SetValue(ProximityRadiusProperty, value); }

        public static void Attach(FrameworkElement host, PrismRimOverlay overlay, Func<bool> isEnabled)
        {
            if (host == null || overlay == null) return;
            host.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler((sender, e) =>
            {
                if (!overlay._frames.CanAnimate || !(isEnabled?.Invoke() ?? true))
                {
                    overlay.ReleasePointer();
                    return;
                }
                overlay._pointer = e.GetPosition(overlay);
                overlay._hasPointer = true;
                overlay._frames.Start();
                overlay.InvalidateVisual();
            }), true);
            host.MouseLeave += delegate { overlay.ReleasePointer(); };
        }

        private void ReleasePointer()
        {
            _hasPointer = false;
            if (_strength > 0) _frames.Start();
        }

        private void OnTransitionStarted(object sender, bool isSubPage)
        {
            if (!Dispatcher.CheckAccess() || _hasPointer || _strength <= 0) return;
            _strength = 0;
            _frames.Stop();
            InvalidateVisual();
        }

        private void SyncMotion()
        {
            if (_frames.CanAnimate) return;
            _hasPointer = false;
            _strength = 0;
        }

        private void Tick(double elapsed)
        {
            double target = 0;
            if (_hasPointer && ProximityRadius > 0)
            {
                double halfWidth = ActualWidth / 2;
                double halfHeight = ActualHeight / 2;
                double radius = Math.Min(CornerRadius, Math.Min(halfWidth, halfHeight));
                double x = Math.Abs(_pointer.X - halfWidth) - halfWidth + radius;
                double y = Math.Abs(_pointer.Y - halfHeight) - halfHeight + radius;
                var outside = new Vector(Math.Max(0, x), Math.Max(0, y));
                double edgeDistance = Math.Abs(outside.Length + Math.Min(Math.Max(x, y), 0) - radius);
                target = Math.Max(0, 1 - edgeDistance / ProximityRadius);
            }
            if (target < IdleThreshold) target = 0;

            double timeConstant = target > _strength ? 0.045 : 0.085;
            _strength += (target - _strength) * (1 - Math.Exp(-elapsed / timeConstant));
            if (Math.Abs(target - _strength) >= IdleThreshold) return;
            _strength = target;
            _frames.Stop();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double thickness = StrokeThickness;
            double width = ActualWidth - thickness;
            double height = ActualHeight - thickness;
            if (!_frames.CanAnimate || _strength < IdleThreshold || ProximityRadius <= 0 ||
                thickness <= 0 || width <= 0 || height <= 0) return;

            bool dark = ThemeService.Current == ThemeKind.Dark;
            var spectrum = UiMotion.GetPrismSpectrum(UiMotion.ActivePrismAccent, dark);
            var refraction = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = _pointer,
                GradientOrigin = _pointer,
                RadiusX = ProximityRadius,
                RadiusY = ProximityRadius,
                Opacity = _strength * (dark ? 1 : 0.82)
            };
            refraction.GradientStops.Add(new GradientStop(spectrum[0], 0));
            refraction.GradientStops.Add(new GradientStop(spectrum[1], 0.18));
            refraction.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[2], 0.95), 0.37));
            refraction.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[3], 0.78), 0.55));
            refraction.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[4], 0.52), 0.72));
            refraction.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[4], 0), 1));

            var bounds = new Rect(thickness / 2, thickness / 2, width, height);
            double corner = Math.Max(0, Math.Min(CornerRadius - thickness / 2, Math.Min(width, height) / 2));
            var rim = new Pen(refraction, thickness);
            rim.Freeze();
            dc.DrawRoundedRectangle(null, rim, bounds, corner, corner);
        }
    }
}
