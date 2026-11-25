using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using LodStudio.Services;

namespace LodStudio.Controls
{
    public sealed class CrystalGlintOverlay : FrameworkElement
    {
        public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
            nameof(Active), typeof(bool), typeof(CrystalGlintOverlay),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender,
                (d, e) => ((CrystalGlintOverlay)d)._frames.Sync()));

        private const double GoldenStep = 0.6180339887498949;
        private const double WakeDuration = 0.7;
        private static readonly (double X, double Y, double Size)[] GlintAnchors =
        {
            (0.14, 0.24, 4.6),
            (0.36, 0.78, 3.7),
            (0.64, 0.19, 4.1),
            (0.86, 0.65, 3.4)
        };

        private readonly AmbientFrameTimer _frames;
        private double _elapsed;
        private double _wakeAge = WakeDuration;

        public CrystalGlintOverlay()
        {
            _frames = new AmbientFrameTimer(this, SyncMotion, Tick);
            ClipToBounds = true;
        }

        public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }

        public void TriggerWakeSweep()
        {
            if (!Active || !_frames.CanAnimate) return;
            _wakeAge = 0;
            _frames.Start();
            InvalidateVisual();
        }

        private void SyncMotion()
        {
            if (Active && _frames.CanAnimate)
            {
                _frames.Start();
                return;
            }
            _frames.Stop();
            _elapsed = 0;
            _wakeAge = WakeDuration;
        }

        private void Tick(double elapsed)
        {
            _elapsed += elapsed;
            _wakeAge = Math.Min(WakeDuration, _wakeAge + elapsed);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (!Active || !_frames.CanAnimate || ActualWidth <= 0 || ActualHeight <= 0) return;

            bool dark = ThemeService.Current == ThemeKind.Dark;
            var spectrum = UiMotion.GetPrismSpectrum(UiMotion.ActivePrismAccent, dark);
            var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
            double corner = Math.Min(14, Math.Min(bounds.Width, bounds.Height) / 2);
            var clip = new RectangleGeometry(bounds, corner, corner);
            clip.Freeze();
            dc.PushClip(clip);
            if (_wakeAge < WakeDuration) DrawWake(dc, bounds, spectrum, dark);
            for (int i = 0; i < GlintAnchors.Length; i++)
            {
                var (x, y, size) = GlintAnchors[i];
                double phase = _elapsed / (5.4 + i * GoldenStep) + i * GoldenStep;
                double peak = Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * phase)), 16);
                if (peak < 0.005) continue;
                var center = new Point(x * bounds.Width, y * bounds.Height);
                DrawStar(dc, center, size * (0.65 + peak * 0.35), spectrum[i + 1], peak * (dark ? 0.95 : 0.72));
            }
            dc.Pop();
        }

        private void DrawWake(DrawingContext dc, Rect bounds, IReadOnlyList<Color> spectrum, bool dark)
        {
            var normal = new Vector(0.85, 0.53);
            normal.Normalize();
            double travel = bounds.Width * normal.X + bounds.Height * normal.Y;
            double band = Math.Max(12, Math.Min(48, Math.Min(bounds.Width, bounds.Height) * 0.22));
            double progress = _wakeAge / WakeDuration;
            double position = -band + (travel + 2 * band) * progress;
            var origin = new Point();
            var light = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = origin + normal * (position - band),
                EndPoint = origin + normal * (position + band),
                Opacity = Math.Sin(Math.PI * progress) * (dark ? 0.84 : 0.58)
            };
            light.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[3], 0), 0));
            light.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[3], 0.22), 0.25));
            light.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[2], 0.44), 0.44));
            light.GradientStops.Add(new GradientStop(Color.FromArgb(225, 230, 249, 255), 0.53));
            light.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[1], 0.58), 0.62));
            light.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(spectrum[4], 0), 1));
            light.Freeze();
            dc.DrawRectangle(light, null, bounds);
        }

        private static void DrawStar(DrawingContext dc, Point center, double size, Color color, double opacity)
        {
            var halo = new RadialGradientBrush(UiMotion.WithOpacity(color, opacity * 0.56), UiMotion.WithOpacity(color, 0));
            halo.Freeze();
            dc.DrawEllipse(halo, null, center, size * 1.65, size * 1.65);

            double inset = size * 0.20;
            double width = size * 0.82;
            var star = new StreamGeometry();
            using (var path = star.Open())
            {
                path.BeginFigure(center + new Vector(0, -size), true, true);
                path.LineTo(center + new Vector(inset, -inset), true, false);
                path.LineTo(center + new Vector(width, 0), true, false);
                path.LineTo(center + new Vector(inset, inset), true, false);
                path.LineTo(center + new Vector(0, size), true, false);
                path.LineTo(center + new Vector(-inset, inset), true, false);
                path.LineTo(center + new Vector(-width, 0), true, false);
                path.LineTo(center + new Vector(-inset, -inset), true, false);
            }
            var core = new RadialGradientBrush(UiMotion.WithOpacity(Colors.AliceBlue, opacity), UiMotion.WithOpacity(color, opacity));
            star.Freeze();
            core.Freeze();
            dc.DrawGeometry(core, null, star);
        }
    }
}
