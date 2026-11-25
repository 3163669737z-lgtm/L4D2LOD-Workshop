using System;
using System.Windows;
using System.Windows.Media;
using LodStudio.Services;

namespace LodStudio.Controls
{
    public sealed class HorizonAura : FrameworkElement
    {
        public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
            nameof(Tone), typeof(string), typeof(HorizonAura),
            new FrameworkPropertyMetadata("Normal", FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(
            nameof(IsRunning), typeof(bool), typeof(HorizonAura),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
                (d, e) => ((HorizonAura)d)._frames.Sync()));

        public static readonly DependencyProperty IntensityProperty = DependencyProperty.Register(
            nameof(Intensity), typeof(double), typeof(HorizonAura),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender,
                (d, e) => ((HorizonAura)d)._frames.Sync()), UiMotion.IsNonNegativeFinite);

        public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
            nameof(CornerRadius), typeof(double), typeof(HorizonAura),
            new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender), UiMotion.IsNonNegativeFinite);

        private readonly AmbientFrameTimer _frames;
        private double _phase;
        private double _motion;

        public HorizonAura()
        {
            _frames = new AmbientFrameTimer(this, SyncMotion, Tick);
        }

        public string Tone { get => (string)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
        public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }
        public double Intensity { get => (double)GetValue(IntensityProperty); set => SetValue(IntensityProperty, value); }
        public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

        private void SyncMotion()
        {
            if (_frames.CanAnimate && Intensity > 0 && (IsRunning || _motion > 0)) _frames.Start();
            else ResetMotion();
        }

        private void ResetMotion()
        {
            _frames.Stop();
            _motion = _phase = 0;
        }

        private void Tick(double elapsed)
        {
            _phase = (_phase + elapsed * 2 * Math.PI / 4.8) % (2 * Math.PI);
            double target = IsRunning ? 1 : 0;
            _motion += (target - _motion) * (1 - Math.Exp(-elapsed / 0.23));
            if (!IsRunning && _motion < 0.005) ResetMotion();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0 || Intensity == 0) return;
            bool dark = ThemeService.Current == ThemeKind.Dark;
            var spectrum = UiMotion.GetPrismSpectrum(UiMotion.ActivePrismAccent, dark);
            Color tone = UiMotion.GetPrismTone(Tone, spectrum[1]);
            double motion = _frames.CanAnimate ? _motion : 0;
            double drift = Math.Sin(_phase) * motion;
            double breath = (0.58 + 0.42 * Math.Cos(_phase)) * motion;
            double strength = Intensity * (dark ? 1 : 0.65);
            double reach = Math.Min(height * 0.7, 58 + 12 * breath);
            double corner = Math.Min(CornerRadius, Math.Min(width, height) / 2);

            var clip = new RectangleGeometry(new Rect(0, 0, width, height), corner, corner);
            clip.Freeze();
            dc.PushClip(clip);
            DrawLobe(dc, tone, width * (0.50 + drift * 0.09), height, width * 0.59, reach, strength * (0.16 + 0.20 * breath));
            DrawLobe(dc, spectrum[2], width * (0.26 + drift * 0.14), height, width * 0.35, reach * 0.78, strength * (0.04 + 0.24 * motion));
            DrawLobe(dc, spectrum[4], width * (0.72 - drift * 0.13), height, width * 0.36, reach * 0.64, strength * (0.04 + 0.21 * motion));
            DrawLobe(dc, tone, width * (0.50 + drift * 0.08), height, width * 0.44, reach * 0.32, strength * (0.22 + 0.19 * breath));
            DrawLobe(dc, spectrum[0], width * (0.50 + drift * 0.10), height, width * 0.37, 2.8, strength * (0.16 + 0.35 * motion));
            dc.Pop();
        }

        private static void DrawLobe(DrawingContext dc, Color color, double x, double y, double radiusX, double radiusY, double opacity)
        {
            var light = new RadialGradientBrush();
            light.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(color, opacity), 0));
            light.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(color, opacity * 0.37), 0.52));
            light.GradientStops.Add(new GradientStop(UiMotion.WithOpacity(color, 0), 1));
            light.Freeze();
            dc.DrawEllipse(light, null, new Point(x, y), radiusX, radiusY);
        }
    }
}
