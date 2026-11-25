using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using LodStudio.Services;

namespace LodStudio.Controls
{
    public sealed class LiquidVial : FrameworkElement
    {
        public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
            nameof(Level), typeof(double), typeof(LiquidVial),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnLevelChanged, CoerceLevel));

        public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
            nameof(Tone), typeof(string), typeof(LiquidVial),
            new FrameworkPropertyMetadata("Normal", FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
            nameof(IsActive), typeof(bool), typeof(LiquidVial),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
                (d, e) => ((LiquidVial)d)._frames.Sync()));

        private const int NodeCount = 24;
        private const double TimeStep = 1.0 / 120;
        private const double WaveCoupling = 1300;
        private const double SurfaceSpring = 34;
        private const double Viscosity = 6.2;

        private readonly AmbientFrameTimer _frames;
        private readonly double[] _heights = new double[NodeCount];
        private readonly double[] _prevHeights = new double[NodeCount];
        private readonly double[] _velocities = new double[NodeCount];
        private readonly Point[] _surface = new Point[NodeCount];
        private double _shownLevel;
        private double _prevShownLevel;
        private double _accumulator;
        private double _phase;

        static LiquidVial()
        {
            WidthProperty.OverrideMetadata(typeof(LiquidVial), new FrameworkPropertyMetadata(28.0));
            HeightProperty.OverrideMetadata(typeof(LiquidVial), new FrameworkPropertyMetadata(40.0));
        }

        public LiquidVial()
        {
            _frames = new AmbientFrameTimer(this, SyncMotion, Tick);
        }

        public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
        public string Tone { get => (string)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
        public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

        private bool IsDriven => IsActive && Level > 0 && Level < 1;

        private static object CoerceLevel(DependencyObject d, object value)
        {
            double level = (double)value;
            return double.IsNaN(level) ? 0.0 : Math.Max(0, Math.Min(1, level));
        }

        private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var vial = (LiquidVial)d;
            if (vial._frames.CanAnimate)
            {
                double impulse = (double)e.NewValue - (double)e.OldValue;
                for (int i = 0; i < NodeCount; i++)
                {
                    double angle = Math.PI * (i + 0.5) / NodeCount;
                    vial._velocities[i] += impulse * (0.88 * Math.Cos(angle) + 0.36 * Math.Cos(2 * angle));
                }
            }
            vial._frames.Sync();
        }

        private void SyncMotion()
        {
            if (_frames.CanAnimate && (IsDriven || !IsCalm())) _frames.Start();
            else SnapToRest();
        }

        private void SnapToRest()
        {
            _frames.Stop();
            _prevShownLevel = _shownLevel = Level;
            _accumulator = _phase = 0;
            Array.Clear(_heights, 0, NodeCount);
            Array.Clear(_prevHeights, 0, NodeCount);
            Array.Clear(_velocities, 0, NodeCount);
        }

        private void Tick(double elapsed)
        {
            // Bound catch-up after a blocked dispatcher while keeping the integration step constant.
            _accumulator = Math.Min(0.08, _accumulator + elapsed);
            while (_accumulator >= TimeStep)
            {
                StepSurface();
                _accumulator -= TimeStep;
            }
            if (!IsDriven && IsCalm()) SnapToRest();
        }

        private void StepSurface()
        {
            Array.Copy(_heights, _prevHeights, NodeCount);
            _prevShownLevel = _shownLevel;
            _shownLevel += (Level - _shownLevel) * (1 - Math.Exp(-TimeStep / 0.13));
            _phase = (_phase + TimeStep * 4.2) % (2 * Math.PI);
            double drive = IsDriven ? 2.3 * Math.Sin(_phase) : 0;
            double limit = Math.Min(0.13, Math.Min(_shownLevel, 1 - _shownLevel));

            // Reflect at both glass walls. Advance all velocities before updating the heights.
            for (int i = 0; i < NodeCount; i++)
            {
                double height = _heights[i];
                double left = i == 0 ? height : _heights[i - 1];
                double right = i == NodeCount - 1 ? height : _heights[i + 1];
                double slosh = drive * Math.Cos(Math.PI * (i + 0.5) / NodeCount);
                double acceleration = WaveCoupling * (left + right - 2 * height) - SurfaceSpring * height -
                    Viscosity * _velocities[i] + slosh;
                _velocities[i] += acceleration * TimeStep;
            }

            for (int i = 0; i < NodeCount; i++)
            {
                double height = _heights[i] + _velocities[i] * TimeStep;
                double clamped = Math.Max(-limit, Math.Min(limit, height));
                if (clamped != height) _velocities[i] *= -0.20;
                _heights[i] = clamped;
            }
        }

        private bool IsCalm()
        {
            if (Math.Abs(Level - _shownLevel) >= 0.0005) return false;
            for (int i = 0; i < NodeCount; i++)
            {
                if (Math.Abs(_heights[i]) >= 0.0005 || Math.Abs(_velocities[i]) >= 0.004) return false;
            }
            return true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth < 4 || ActualHeight < 4) return;
            bool dark = ThemeService.Current == ThemeKind.Dark;
            var spectrum = UiMotion.GetPrismSpectrum(UiMotion.ActivePrismAccent, dark);
            Color tone = UiMotion.GetPrismTone(Tone, spectrum[1]);
            Color crystal = dark ? Colors.AliceBlue : Color.FromRgb(73, 106, 158);
            var bounds = new Rect(2, 1, ActualWidth - 4, ActualHeight - 2);
            double corner = Math.Min(bounds.Width, bounds.Height) / 2;
            var glass = new RectangleGeometry(bounds, corner, corner);
            glass.Freeze();

            var chamber = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            chamber.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(crystal, dark ? 0.25 : 0.24), 0));
            chamber.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(crystal, 0.045), 0.44));
            chamber.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[2], dark ? 0.30 : 0.18), 1));
            chamber.Freeze();
            dc.DrawGeometry(chamber, null, glass);

            dc.PushClip(glass);
            DrawFluid(dc, bounds, spectrum, tone, _frames.CanAnimate);
            DrawSpecular(dc, bounds, crystal, dark);
            dc.Pop();

            var wall = new LinearGradientBrush
            {
                StartPoint = new Point(0.1, 0.05),
                EndPoint = new Point(0.9, 1)
            };
            wall.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(crystal, 0.75), 0));
            wall.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(crystal, 0.26), 0.56));
            wall.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[1], 0.64), 1));
            var outline = new Pen(wall, 0.85);
            outline.Freeze();
            dc.DrawGeometry(null, outline, glass);
        }

        private void DrawFluid(DrawingContext dc, Rect bounds, IReadOnlyList<Color> spectrum, Color tone, bool moving)
        {
            double alpha = moving ? Math.Max(0, Math.Min(1, _accumulator / TimeStep)) : 1.0;
            double level = moving ? _prevShownLevel + (_shownLevel - _prevShownLevel) * alpha : Level;
            if (level <= 0) return;
            for (int i = 0; i < NodeCount; i++)
            {
                double x = i / (double)(NodeCount - 1);
                double wave = moving ? _prevHeights[i] + (_heights[i] - _prevHeights[i]) * alpha : 0;
                double capillary = 0.028 * Math.Pow(Math.Abs(2 * x - 1), 6) * Math.Min(1, level * 6);
                double surfaceLevel = Math.Max(0, Math.Min(1, level + wave + capillary));
                _surface[i] = new Point(bounds.Left + bounds.Width * x, bounds.Bottom - bounds.Height * surfaceLevel);
            }

            double fillTop = bounds.Bottom - bounds.Height * Math.Min(1, level + 0.06);
            var fill = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(bounds.Left, fillTop),
                EndPoint = new Point(bounds.Left + 0.24 * bounds.Width, bounds.Bottom)
            };
            fill.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[1], 0.72), 0));
            fill.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(tone, 0.92), 0.35));
            fill.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[2], 0.88), 0.72));
            fill.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[4], 0.92), 1));
            fill.Freeze();
            dc.DrawGeometry(fill, null, SurfaceGeometry(bounds, true));

            var sheen = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = bounds.TopLeft,
                EndPoint = bounds.TopRight
            };
            sheen.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[1], 0.2), 0));
            sheen.GradientStops.Add(new GradientStop(Colors.AliceBlue, 0.42));
            sheen.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[3], 0.76), 0.8));
            sheen.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[4], 0.35), 1));
            sheen.Freeze();
            var glow = new Pen(sheen, 2.6);
            glow.Freeze();
            var edge = new Pen(sheen, 0.95);
            edge.Freeze();

            var surface = SurfaceGeometry(bounds, false);
            dc.PushOpacity(0.24);
            dc.DrawGeometry(null, glow, surface);
            dc.Pop();
            dc.DrawGeometry(null, edge, surface);
        }

        private StreamGeometry SurfaceGeometry(Rect bounds, bool filled)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(_surface[0], filled, filled);
                for (int i = 1; i < NodeCount - 1; i++)
                {
                    Point midpoint = _surface[i] + (_surface[i + 1] - _surface[i]) * 0.5;
                    path.QuadraticBezierTo(_surface[i], midpoint, true, true);
                }
                path.LineTo(_surface[NodeCount - 1], true, true);
                if (filled)
                {
                    path.LineTo(bounds.BottomRight, true, false);
                    path.LineTo(bounds.BottomLeft, true, false);
                }
            }
            geometry.Freeze();
            return geometry;
        }

        private static void DrawSpecular(DrawingContext dc, Rect bounds, Color crystal, bool dark)
        {
            var shine = new SolidColorBrush(UiMotion.WithOpacity(crystal, dark ? 0.72 : 0.64));
            var tick = new Pen(shine, 0.65);
            tick.Freeze();
            var rimHighlight = new Pen(new SolidColorBrush(UiMotion.WithOpacity(crystal, 0.29)), 0.8);
            rimHighlight.Freeze();
            double left = bounds.Left + bounds.Width * 0.20;
            dc.DrawEllipse(shine, null, new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height * 0.115),
                bounds.Width * 0.24, 0.9);
            dc.DrawLine(rimHighlight,
                new Point(left, bounds.Top + bounds.Height * 0.28), new Point(left, bounds.Top + bounds.Height * 0.71));
            for (int i = 0; i < 5; i++)
            {
                double y = bounds.Top + bounds.Height * (0.28 + 0.11 * i);
                double right = bounds.Right - bounds.Width * 0.10;
                double length = Math.Min(bounds.Width * 0.20, i == 2 ? 3.8 : 2.6);
                dc.DrawLine(tick, new Point(right - length, y), new Point(right, y));
            }
        }
    }
}
