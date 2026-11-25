using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LodStudio.Services;

namespace LodStudio.Controls
{
    public sealed class PrismRippleLayer : FrameworkElement
    {
        private const int MaxRipples = 4;
        private const double Lifetime = 0.460;
        private readonly List<Ripple> _ripples = new List<Ripple>(MaxRipples);
        private readonly AmbientFrameTimer _frames;

        private sealed class Ripple
        {
            internal Point Origin;
            internal double Radius;
            internal double Age;
        }

        public PrismRippleLayer()
        {
            _frames = new AmbientFrameTimer(this, SyncMotion, Tick);
            ClipToBounds = true;
        }

        public void SpawnRipple(Point origin, double maxRadius = 110)
        {
            if (!_frames.CanAnimate || !(maxRadius > 0) || double.IsInfinity(maxRadius)) return;
            if (_ripples.Count == MaxRipples) _ripples.RemoveAt(0);
            _ripples.Add(new Ripple { Origin = origin, Radius = maxRadius });
            _frames.Start();
            InvalidateVisual();
        }

        public static void AttachClickRipple(FrameworkElement host, PrismRippleLayer layer, Func<bool> isEnabled)
        {
            if (host == null || layer == null) return;
            host.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((sender, e) =>
            {
                if (isEnabled?.Invoke() ?? true) layer.SpawnRipple(e.GetPosition(layer));
            }), true);
        }

        private void SyncMotion()
        {
            if (!_frames.CanAnimate) _ripples.Clear();
        }

        private void Tick(double elapsed)
        {
            for (int i = _ripples.Count - 1; i >= 0; i--)
            {
                _ripples[i].Age += elapsed;
                if (_ripples[i].Age >= Lifetime) _ripples.RemoveAt(i);
            }
            if (_ripples.Count == 0) _frames.Stop();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (!_frames.CanAnimate || _ripples.Count == 0) return;
            bool dark = ThemeService.Current == ThemeKind.Dark;
            var spectrum = UiMotion.GetPrismSpectrum(UiMotion.ActivePrismAccent, dark);
            foreach (var ripple in _ripples)
            {
                double rest = 1 - ripple.Age / Lifetime;
                double radius = Math.Max(1, ripple.Radius * (1 - rest * rest * rest));
                double opacity = rest * rest * (dark ? 0.83 : 0.66);
                var light = new LinearGradientBrush { Opacity = opacity };
                for (int i = 0; i < spectrum.Count; i++)
                    light.GradientStops.Add(new GradientStop(spectrum[i], i / (double)(spectrum.Count - 1)));

                light.Freeze();
                var halo = new Pen(light, 4.8);
                halo.Freeze();
                var ring = new Pen(light, 0.8 + rest * 0.9);
                ring.Freeze();

                dc.PushOpacity(0.18);
                dc.DrawEllipse(null, halo, ripple.Origin, radius, radius);
                dc.Pop();
                dc.DrawEllipse(null, ring, ripple.Origin, radius, radius);
            }
        }
    }
}
