using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace LodStudio.Services
{
    public enum SpringPreset { Snappy, Bouncy, Default, Gentle, Micro, Indicator }

    /// <summary>
    /// 现代化界面动效引擎：
    /// - 视图切换（平滑滑入 + 轻微缩放 + 透明淡入）；
    /// - 窗口/卡片弹性入场；
    /// - 模态/拖放时的背景毛玻璃动态虚化（BlurEffect 0 ↔ radius）；
    /// - 指针跟随玻璃高光（Spotlight Reveal）；
    /// - 所有动效均遵循用户设置与系统 ClientAreaAnimation 开关。
    /// </summary>
    internal static class UiMotion
    {
        private static readonly IEasingFunction EaseOutExpo = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        private static readonly IEasingFunction EaseOutCubic = new CubicEase { EasingMode = EasingMode.EaseOut };
        private static readonly IEasingFunction EaseInOutCubic = new CubicEase { EasingMode = EasingMode.EaseInOut };
        private static readonly IEasingFunction EaseInOutSine = new SineEase { EasingMode = EasingMode.EaseInOut };
        private static readonly IEasingFunction ReadoutEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

        private static readonly (double Duration, double[] Samples)[] SpringCurves =
        {
            SampleSpring(420, 30, 0.9),  // Snappy
            SampleSpring(380, 20, 1.0),  // Bouncy
            SampleSpring(380, 26, 1.0),  // Default
            SampleSpring(260, 26, 1.05), // Gentle
            SampleSpring(440, 24, 0.85), // Micro
            SampleSpring(430, 32, 0.9)   // Indicator
        };

        private static readonly IReadOnlyList<Color>[] DarkPrismSpectra =
        {
            Spectrum(0xF5FFFFFF, 0xFF4285F4, 0xFF9B72CB, 0xFFEA4335, 0xFFFBBC04),
            Spectrum(0xF2FFF6EE, 0xFFF6BD60, 0xFFFF8E57, 0xFFFF6B2B, 0xFFD95420),
            Spectrum(0xF5F0F9FF, 0xFF7CE2FF, 0xFF4285F4, 0xFF2563EB, 0xFF1B49B2),
            Spectrum(0xF5F5F3FF, 0xFFDDD6FE, 0xFFA58AF8, 0xFF8B5CF6, 0xFF6D28D9),
            Spectrum(0xF5FFFBEB, 0xFFFDE68A, 0xFFF2B84B, 0xFFF59E0B, 0xFFD97706)
        };

        private static readonly IReadOnlyList<Color>[] LightPrismSpectra =
        {
            Spectrum(0xFF2563EB, 0xFF7C3AED, 0xFFEA4335, 0xFFD97706, 0xFFF59E0B),
            Spectrum(0xFF9A3412, 0xFFC25934, 0xFFD97757, 0xFFD97706, 0xFFF59E0B),
            Spectrum(0xFF1D4ED8, 0xFF2563EB, 0xFF0284C7, 0xFF38BDF8, 0xFF7DD3FC),
            Spectrum(0xFF5B21B6, 0xFF7C3AED, 0xFF8B5CF6, 0xFFA855F7, 0xFFC084FC),
            Spectrum(0xFF92400E, 0xFFB45309, 0xFFD97706, 0xFFF59E0B, 0xFFFBBF24)
        };

        private const int EnumCurrentSettings = -1;
        private static readonly object DisplayRefreshLock = new object();
        private static int _displayRefreshRate;
        private static int _displayRefreshCheckedAt;

        // DEVMODEW has a fixed layout across 32- and 64-bit Windows.
        [StructLayout(LayoutKind.Explicit, Size = 220)]
        private struct DisplayMode
        {
            [FieldOffset(68)] internal ushort dmSize;
            [FieldOffset(184)] internal uint dmDisplayFrequency;
        }

        [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DisplayMode mode);

        internal static int DisplayRefreshRate
        {
            get
            {
                lock (DisplayRefreshLock)
                {
                    // Recheck on demand so display changes need no listener or timer at idle.
                    int now = Environment.TickCount;
                    if (_displayRefreshRate == 0 || unchecked((uint)(now - _displayRefreshCheckedAt)) >= 5000)
                    {
                        var mode = new DisplayMode { dmSize = (ushort)Marshal.SizeOf<DisplayMode>() };
                        _displayRefreshRate = EnumDisplaySettings(null, EnumCurrentSettings, ref mode)
                            ? (int)Math.Max(60u, Math.Min(240u, mode.dmDisplayFrequency))
                            : 60;
                        _displayRefreshCheckedAt = now;
                    }
                    return _displayRefreshRate;
                }
            }
        }

        internal static T WithRefreshFrameRate<T>(T timeline) where T : Timeline
        {
            Timeline.SetDesiredFrameRate(timeline, DisplayRefreshRate);
            return timeline;
        }

        private static bool _suppressContinuousAmbient;
        private static int _activePrismAccent;
        private static int _activeMotionTier = 3;
        internal static event EventHandler AmbientSuppressionChanged;
        internal static event EventHandler PrismAccentChanged;
        internal static event EventHandler<bool> TransitionStarted;

        internal static bool SuppressContinuousAmbient
        {
            get => _suppressContinuousAmbient;
            set
            {
                if (_suppressContinuousAmbient == value) return;
                _suppressContinuousAmbient = value;
                AmbientSuppressionChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        internal static int ActiveMotionTier
        {
            get => _activeMotionTier;
            set => SetMotionTier(value);
        }

        internal static void SetMotionTier(int tier)
        {
            tier = Math.Max(0, Math.Min(3, tier));
            if (_activeMotionTier == tier) return;
            _activeMotionTier = tier;
            AmbientSuppressionChanged?.Invoke(null, EventArgs.Empty);
        }

        internal static int ActivePrismAccent
        {
            get => _activePrismAccent;
            set => SetPrismAccent(value);
        }

        internal static void SetPrismAccent(int accent)
        {
            accent = Math.Max(0, Math.Min(4, accent));
            if (_activePrismAccent == accent) return;
            _activePrismAccent = accent;
            PrismAccentChanged?.Invoke(null, EventArgs.Empty);
        }

        internal static IReadOnlyList<Color> GetPrismSpectrum(int accent, bool dark)
        {
            var spectra = dark ? DarkPrismSpectra : LightPrismSpectra;
            return spectra[Math.Max(0, Math.Min(spectra.Length - 1, accent))];
        }

        internal static Color WithOpacity(Color color, double opacity) =>
            Color.FromArgb((byte)Math.Round(color.A * Math.Max(0, Math.Min(1, opacity))), color.R, color.G, color.B);

        internal static bool IsNonNegativeFinite(object value) => (double)value >= 0 && !double.IsInfinity((double)value);

        internal static Color GetPrismTone(string tone, Color fallback)
        {
            switch (tone)
            {
                case "Signal":
                case "Ok":
                case "Warn":
                case "Err":
                    Color color = ThemeService.GetColor("Color." + tone);
                    return color.A != 0 ? color : fallback;
                default:
                    return fallback;
            }
        }

        private static IReadOnlyList<Color> Spectrum(params uint[] argb) => Array.AsReadOnly(Array.ConvertAll(argb, value =>
            Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value)));

        private static (double Duration, double[] Samples) SampleSpring(double stiffness, double damping, double mass)
        {
            // Unit step response with zero initial displacement and velocity. All presets are underdamped.
            double decay = damping / (2 * mass);
            double naturalFrequencySquared = stiffness / mass;
            double frequency = Math.Sqrt(naturalFrequencySquared - decay * decay);

            double DisplacementError(double time) => Math.Exp(-decay * time) *
                (Math.Cos(frequency * time) + decay / frequency * Math.Sin(frequency * time));

            double Velocity(double time) => Math.Exp(-decay * time) *
                (naturalFrequencySquared / frequency) * Math.Sin(frequency * time);

            const int maxDurationMs = 900;
            int durationMs = 120;
            // Find the final threshold crossing so a quiet instant before another overshoot cannot end the curve.
            for (int ms = maxDurationMs - 1; ms >= durationMs; ms--)
            {
                double time = ms / 1000.0;
                if (Math.Abs(DisplacementError(time)) < 0.001 && Math.Abs(Velocity(time)) < 0.001) continue;
                durationMs = ms + 1;
                break;
            }

            var samples = new double[64];
            for (int i = 0; i < samples.Length - 1; i++)
                samples[i] = 1 - DisplacementError(durationMs / 1000.0 * i / (samples.Length - 1));
            samples[samples.Length - 1] = 1;
            return (durationMs, samples);
        }

        internal static DoubleAnimationUsingKeyFrames CreateSpringAnimation(
            double from, double to, SpringPreset preset, int delayMs = 0, double durationScale = 1.0)
        {
            var (duration, samples) = SpringCurves[(int)preset];
            duration *= durationScale > 0 && !double.IsInfinity(durationScale) ? durationScale : 1;
            delayMs = Math.Max(0, delayMs);
            var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            if (delayMs > 0) animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, TimeSpan.Zero));
            for (int i = 0; i < samples.Length; i++)
            {
                double time = delayMs + duration * i / (samples.Length - 1);
                double value = i == samples.Length - 1 ? to : from + (to - from) * samples[i];
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(value, TimeSpan.FromMilliseconds(time)));
            }
            return WithRefreshFrameRate(animation);
        }

        private sealed class MotionState
        {
            internal int Token;
            internal Action Cancel;

            // The instance target gives each active element a distinct delegate in RenderFrameHub.
            internal void KeepRendering(double _) { }
        }

        private static readonly ConditionalWeakTable<FrameworkElement, MotionState> ActiveMotions =
            new ConditionalWeakTable<FrameworkElement, MotionState>();

        private static bool CanTransition(bool enabled, int motionTier = 3) =>
            enabled && motionTier > 0 && ActiveMotionTier > 0 && !SuppressContinuousAmbient;

        internal static void CancelMotion(UIElement owner)
        {
            if (owner is FrameworkElement element && ActiveMotions.TryGetValue(element, out var state))
                state.Cancel?.Invoke();
        }

        internal static void TrackAnimation(UIElement owner, Timeline primary, Action<bool> cleanup = null)
        {
            var finish = owner is FrameworkElement element ? TrackMotion(element, cleanup) : cleanup;
            if (finish != null) primary.Completed += delegate { finish(true); };
        }

        private static Action<bool> TrackMotion(FrameworkElement element, Action<bool> cleanup)
        {
            var state = ActiveMotions.GetOrCreateValue(element);
            state.Cancel?.Invoke();
            int token = ++state.Token;
            var window = Window.GetWindow(element);
            Action<double> pulse = null;

            void OnUnloaded(object sender, RoutedEventArgs e) => Finish(true);

            void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
            {
                if (!element.IsVisible) Finish(true);
            }

            void OnWindowStateChanged(object sender, EventArgs e)
            {
                if (window.WindowState == WindowState.Minimized) Finish(true);
            }

            void OnSuppressionChanged(object sender, EventArgs e)
            {
                if (!SuppressContinuousAmbient && ActiveMotionTier > 0) return;
                if (element.Dispatcher.CheckAccess()) Finish(true);
                else element.Dispatcher.BeginInvoke(new Action(() => Finish(true)));
            }

            void Finish(bool settle)
            {
                if (state.Token != token || state.Cancel == null) return;
                state.Cancel = null;
                element.Unloaded -= OnUnloaded;
                AmbientSuppressionChanged -= OnSuppressionChanged;
                if (pulse != null)
                {
                    element.IsVisibleChanged -= OnVisibilityChanged;
                    if (window != null) window.StateChanged -= OnWindowStateChanged;
                    RenderFrameHub.Unsubscribe(pulse);
                }
                cleanup?.Invoke(settle);
            }

            element.Unloaded += OnUnloaded;
            AmbientSuppressionChanged += OnSuppressionChanged;
            state.Cancel = () => Finish(false);
            if (!SuppressContinuousAmbient && ActiveMotionTier > 0 && element.IsVisible &&
                window?.WindowState != WindowState.Minimized)
            {
                pulse = state.KeepRendering;
                element.IsVisibleChanged += OnVisibilityChanged;
                if (window != null) window.StateChanged += OnWindowStateChanged;
                RenderFrameHub.Subscribe(pulse);
            }
            return Finish;
        }

        private static Action<bool> StartTransformMotion(FrameworkElement element, Point origin,
            ScaleTransform scale, TranslateTransform offset, RotateTransform rotation, Action<bool> cleanup)
        {
            var previousTransform = element.RenderTransform;
            var previousOrigin = element.RenderTransformOrigin;
            var transforms = new TransformGroup();
            if (previousTransform != null && previousTransform != Transform.Identity)
                transforms.Children.Add(previousTransform);
            if (scale != null) transforms.Children.Add(scale);
            if (rotation != null) transforms.Children.Add(rotation);
            if (offset != null) transforms.Children.Add(offset);
            element.SetCurrentValue(UIElement.RenderTransformOriginProperty, origin);
            element.SetCurrentValue(UIElement.RenderTransformProperty, transforms);

            return TrackMotion(element, settle =>
            {
                cleanup?.Invoke(settle);
                if (ReferenceEquals(element.RenderTransform, transforms))
                {
                    element.SetCurrentValue(UIElement.RenderTransformProperty, previousTransform);
                    element.SetCurrentValue(UIElement.RenderTransformOriginProperty, previousOrigin);
                }
                if (scale != null) SetScale(scale, null);
                if (offset != null)
                {
                    offset.BeginAnimation(TranslateTransform.XProperty, null);
                    offset.BeginAnimation(TranslateTransform.YProperty, null);
                }
                rotation?.BeginAnimation(RotateTransform.AngleProperty, null);
            });
        }

        internal static void PlayPageTransition(FrameworkElement incoming, int direction, bool enabled, int motionTier = 3) =>
            PlayPageMotion(incoming, Math.Sign(direction) * 42, false, enabled, motionTier);

        internal static void PlaySubPageTransition(FrameworkElement incoming, int direction, bool enabled, int motionTier = 3) =>
            PlayPageMotion(incoming, Math.Sign(direction) * 54, true, enabled, motionTier);

        private static void PlayPageMotion(FrameworkElement incoming, double startOffset, bool isSubPage, bool enabled, int motionTier)
        {
            if (incoming == null) return;
            CancelMotion(incoming);
            bool animate = CanTransition(enabled, motionTier) && incoming.IsVisible &&
                Window.GetWindow(incoming)?.WindowState != WindowState.Minimized;
            if (animate)
            {
                TransitionStarted?.Invoke(incoming, isSubPage);
                EnterPageTransition();
            }

            Action<bool> finish = null;
            var cardFinishes = animate ? new List<Action<bool>>(8) : null;
            try
            {
                if (animate)
                {
                    var preset = isSubPage ? SpringPreset.Snappy : motionTier >= 3 ? SpringPreset.Bouncy : SpringPreset.Default;
                    var scale = new ScaleTransform();
                    var offset = new TranslateTransform();
                    finish = StartTransformMotion(incoming, new Point(0.5, 0.5), scale, offset, null, settle =>
                    {
                        try
                        {
                            foreach (var finishCard in cardFinishes) finishCard(settle);
                            incoming.BeginAnimation(UIElement.OpacityProperty, null);
                        }
                        finally { ExitPageTransition(); }
                    });

                    SetScale(scale, CreateSpringAnimation(isSubPage ? 0.972 : 0.958, 1, preset));
                    incoming.BeginAnimation(UIElement.OpacityProperty, EntranceAnimation(0, 1, 0, isSubPage ? 200 : 240, EaseOutCubic));
                    var slide = CreateSpringAnimation(startOffset, 0, preset);
                    slide.Completed += delegate { finish(true); };
                    offset.BeginAnimation(isSubPage ? TranslateTransform.XProperty : TranslateTransform.YProperty, slide);
                }

                var panel = FindFirstPanel(incoming);
                if (panel == null) return;
                int index = 0;
                foreach (UIElement child in panel.Children)
                {
                    if (!(child is FrameworkElement card) || card.Visibility != Visibility.Visible) continue;
                    CancelMotion(card);
                    if (animate) cardFinishes.Add(PlayCardEntrance(card, index * (isSubPage ? 28 : 34)));
                    if (++index == 8) break;
                }
            }
            catch
            {
                if (finish != null) finish(false);
                else if (animate) ExitPageTransition();
                throw;
            }
        }

        private static Panel FindFirstPanel(FrameworkElement element)
        {
            if (element is Panel panel) return panel;
            int visualCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < visualCount; i++)
            {
                if (!(VisualTreeHelper.GetChild(element, i) is FrameworkElement child)) continue;
                var found = FindFirstPanel(child);
                if (found != null) return found;
            }
            if (visualCount == 0 && element is ContentControl content && content.Content is FrameworkElement contentElement)
                return FindFirstPanel(contentElement);
            return null;
        }

        private static Action<bool> PlayCardEntrance(FrameworkElement card, int delayMs)
        {
            var offset = new TranslateTransform();
            var finish = StartTransformMotion(card, card.RenderTransformOrigin, null, offset, null, null);
            var slide = CreateSpringAnimation(18, 0, SpringPreset.Snappy, delayMs, 0.65);
            slide.Completed += delegate { finish(true); };
            offset.BeginAnimation(TranslateTransform.YProperty, slide);
            return finish;
        }

        internal static void AnimateParentChildReveal(FrameworkElement childPanel, bool show, bool enabled, int motionTier = 3)
        {
            if (childPanel == null) return;
            double fromOpacity = childPanel.Opacity;
            CancelMotion(childPanel);
            if (!CanTransition(enabled, motionTier) || (!show && childPanel.Visibility != Visibility.Visible))
            {
                childPanel.SetCurrentValue(UIElement.VisibilityProperty, show ? Visibility.Visible : Visibility.Collapsed);
                return;
            }

            if (show) childPanel.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
            var scale = new ScaleTransform();
            var offset = new TranslateTransform();
            var finish = StartTransformMotion(childPanel, new Point(0.5, 0), scale, offset, null, settle =>
            {
                if (settle && !show) childPanel.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
                childPanel.BeginAnimation(UIElement.OpacityProperty, null);
            });

            if (show)
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateSpringAnimation(0.86, 1, SpringPreset.Snappy));
                childPanel.BeginAnimation(UIElement.OpacityProperty, EntranceAnimation(0, 1, 0, 180, EaseOutCubic));
                var unfold = CreateSpringAnimation(-10, 0, SpringPreset.Snappy);
                unfold.Completed += delegate { finish(true); };
                offset.BeginAnimation(TranslateTransform.YProperty, unfold);
            }
            else
            {
                var duration = TimeSpan.FromMilliseconds(140);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, WithRefreshFrameRate(new DoubleAnimation(1, 0.88, duration)
                {
                    EasingFunction = EaseInOutCubic,
                    FillBehavior = FillBehavior.Stop
                }));
                var fade = WithRefreshFrameRate(new DoubleAnimation(fromOpacity, 0, duration)
                {
                    EasingFunction = EaseInOutCubic,
                    FillBehavior = FillBehavior.Stop
                });
                fade.Completed += delegate { finish(true); };
                childPanel.BeginAnimation(UIElement.OpacityProperty, fade);
            }
        }

        internal static void GlideNavIndicator(FrameworkElement indicator, double targetY, double targetHeight, bool enabled) =>
            GlideIndicator(indicator, targetY, targetHeight, enabled, horizontal: false);

        internal static void GlideHorizontalIndicator(FrameworkElement indicator, double targetX, double targetWidth, bool enabled) =>
            GlideIndicator(indicator, targetX, targetWidth, enabled, horizontal: true);

        private static void GlideIndicator(FrameworkElement indicator, double targetOffset, double targetSize, bool enabled, bool horizontal)
        {
            if (indicator == null) return;

            var sizeProperty = horizontal ? FrameworkElement.WidthProperty : FrameworkElement.HeightProperty;
            var offsetProperty = horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty;
            var scaleProperty = horizontal ? ScaleTransform.ScaleXProperty : ScaleTransform.ScaleYProperty;

            // Read the current visual geometry before cancellation restores the previous destination.
            var current = indicator.RenderTransform?.Value ?? Matrix.Identity;
            double fromOffset = horizontal ? current.OffsetX : current.OffsetY;
            double layoutSize = (double)indicator.GetValue(sizeProperty);
            if (double.IsNaN(layoutSize))
                layoutSize = horizontal ? indicator.ActualWidth : indicator.ActualHeight;
            double fromSize = layoutSize * (horizontal ? current.M11 : current.M22);

            CancelMotion(indicator);
            targetSize = Math.Max(0, targetSize);
            var offset = horizontal
                ? new TranslateTransform(targetOffset, current.OffsetY)
                : new TranslateTransform(current.OffsetX, targetOffset);
            indicator.SetCurrentValue(sizeProperty, targetSize);

            if (!CanTransition(enabled))
            {
                indicator.SetCurrentValue(UIElement.RenderTransformProperty, offset);
                return;
            }

            var origin = indicator.RenderTransformOrigin;
            ScaleTransform scale = null;
            TransformGroup transforms = null;
            if (targetSize > 0.5 && fromSize > 0.5 && Math.Abs(fromSize - targetSize) > 0.5)
            {
                scale = new ScaleTransform();
                transforms = new TransformGroup { Children = { scale, offset } };
                indicator.SetCurrentValue(UIElement.RenderTransformOriginProperty,
                    horizontal ? new Point(0, 0.5) : new Point(0.5, 0));
                indicator.SetCurrentValue(UIElement.RenderTransformProperty, transforms);

                var resize = CreateSpringAnimation(fromSize / targetSize, 1, SpringPreset.Indicator);
                foreach (DoubleKeyFrame sample in resize.KeyFrames)
                    sample.Value = Math.Max(0, sample.Value);
                scale.BeginAnimation(scaleProperty, resize);
            }
            else
            {
                indicator.SetCurrentValue(UIElement.RenderTransformProperty, offset);
            }

            var glide = CreateSpringAnimation(fromOffset, targetOffset, SpringPreset.Indicator);
            TrackAnimation(indicator, glide, _ =>
            {
                offset.BeginAnimation(offsetProperty, null);
                if (scale == null) return;
                scale.BeginAnimation(scaleProperty, null);
                if (ReferenceEquals(indicator.RenderTransform, transforms))
                {
                    indicator.SetCurrentValue(UIElement.RenderTransformProperty, offset);
                    indicator.SetCurrentValue(UIElement.RenderTransformOriginProperty, origin);
                }
            });
            offset.BeginAnimation(offsetProperty, glide);
        }

        internal static void AttachIconMicroMotion(FrameworkElement host, FrameworkElement icon, string motionKind, Func<bool> isEnabled)
        {
            if (host == null || icon == null) return;
            host.MouseEnter += delegate
            {
                switch (motionKind)
                {
                    case "gear": PlayIcon(58, 0, 1); break;
                    case "lift": PlayIcon(0, -2.5, 1.12); break;
                    case "tilt": PlayIcon(-12, 0, 1.10); break;
                    case "pulse": PlayIcon(0, 0, 1.18); break;
                }
            };
            host.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler((sender, e) => PlayIcon(0, 0, 0.88)), true);

            void PlayIcon(double angle, double y, double size)
            {
                CancelMotion(icon);
                if (!CanTransition(isEnabled?.Invoke() ?? true)) return;
                var scale = new ScaleTransform();
                var offset = new TranslateTransform();
                var rotation = new RotateTransform();
                var finish = StartTransformMotion(icon, new Point(0.5, 0.5), scale, offset, rotation, null);
                SetScale(scale, CreateSpringAnimation(size, 1, SpringPreset.Micro));
                if (y != 0) offset.BeginAnimation(TranslateTransform.YProperty, CreateSpringAnimation(y, 0, SpringPreset.Micro));
                var turn = CreateSpringAnimation(angle, 0, SpringPreset.Micro);
                turn.Completed += delegate { finish(true); };
                rotation.BeginAnimation(RotateTransform.AngleProperty, turn);
            }
        }

        internal static void PlayWindowEntrance(
            IReadOnlyList<(FrameworkElement Element, double OffsetX, double OffsetY, int DelayMs)> sequence, bool enabled)
        {
            foreach (var (element, offsetX, offsetY, delayMs) in sequence)
            {
                CancelMotion(element);
                element.BeginAnimation(UIElement.OpacityProperty, null);
                element.RenderTransform = Transform.Identity;
                if (!CanTransition(enabled)) continue;

                var scale = new ScaleTransform();
                var offset = new TranslateTransform();
                var finish = StartTransformMotion(element, new Point(0.5, 0.5), scale, offset, null,
                    _ => element.BeginAnimation(UIElement.OpacityProperty, null));
                var enter = EntranceAnimation(0.986, 1, delayMs, 460, ReadoutEase);
                enter.Completed += delegate { finish(true); };
                SetScale(scale, enter);
                offset.BeginAnimation(TranslateTransform.XProperty, EntranceAnimation(offsetX, 0, delayMs, 420, EaseOutCubic));
                offset.BeginAnimation(TranslateTransform.YProperty, EntranceAnimation(offsetY, 0, delayMs, 420, EaseOutCubic));
                element.BeginAnimation(UIElement.OpacityProperty, EntranceAnimation(0, 1, delayMs, 280, EaseOutCubic));
            }
        }

        private static DoubleAnimationUsingKeyFrames EntranceAnimation(
            double from, double to, int delayMs, int durationMs, IEasingFunction easing)
        {
            var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            // Hold the starting value during the stagger without changing the property's resting value.
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, TimeSpan.Zero));
            if (delayMs > 0) animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, TimeSpan.FromMilliseconds(delayMs)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, TimeSpan.FromMilliseconds(delayMs + durationMs), easing));
            return WithRefreshFrameRate(animation);
        }

        // Animation clocks and their transitions belong to the UI dispatcher that created them.
        [ThreadStatic] private static int _activePageTransitions;
        [ThreadStatic] private static Dictionary<FrameworkElement, (AnimationClock X, AnimationClock Y, AnimationClock Scale)> _auroraClocks;

        private static void EnterPageTransition()
        {
            if (++_activePageTransitions != 1 || _auroraClocks == null) return;
            foreach (var clocks in _auroraClocks.Values)
            {
                clocks.X.Controller.Pause();
                clocks.Y.Controller.Pause();
                clocks.Scale.Controller.Pause();
            }
        }

        private static void ExitPageTransition()
        {
            if (--_activePageTransitions != 0 || _auroraClocks == null) return;
            foreach (var clocks in _auroraClocks.Values)
            {
                clocks.X.Controller.Resume();
                clocks.Y.Controller.Resume();
                clocks.Scale.Controller.Resume();
            }
        }

        internal static void SetAmbientAurora(bool active, params FrameworkElement[] orbs)
        {
            const int fogFrameRate = 30;
            active &= !SuppressContinuousAmbient && ActiveMotionTier > 0;
            for (int i = 0; i < orbs.Length; i++)
            {
                var orb = orbs[i];
                var transforms = (TransformGroup)orb.RenderTransform;
                var scale = (ScaleTransform)transforms.Children[0];
                var drift = (TranslateTransform)transforms.Children[1];
                if (_auroraClocks != null && _auroraClocks.TryGetValue(orb, out var clocks))
                {
                    if (active) continue;

                    clocks.X.Controller.Remove();
                    clocks.Y.Controller.Remove();
                    clocks.Scale.Controller.Remove();
                    drift.ApplyAnimationClock(TranslateTransform.XProperty, null);
                    drift.ApplyAnimationClock(TranslateTransform.YProperty, null);
                    scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, null);
                    scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, null);
                    _auroraClocks.Remove(orb);
                    continue;
                }
                if (!active) continue;

                double direction = i % 2 == 0 ? 1 : -1;
                double distance = 13 + 3 * i;
                var x = SineLoop(0, distance * direction, 14 + 2 * i, fogFrameRate).CreateClock();
                var y = SineLoop(0, -distance * direction, 22 - 2 * i, fogFrameRate).CreateClock();
                var zoom = SineLoop(1, 1.04 + 0.012 * i, 16 + 2 * i, fogFrameRate).CreateClock();
                drift.ApplyAnimationClock(TranslateTransform.XProperty, x);
                drift.ApplyAnimationClock(TranslateTransform.YProperty, y);
                scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, zoom);
                scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, zoom);
                if (_activePageTransitions > 0)
                {
                    x.Controller.Pause();
                    y.Controller.Pause();
                    zoom.Controller.Pause();
                }

                if (_auroraClocks == null)
                    _auroraClocks = new Dictionary<FrameworkElement, (AnimationClock X, AnimationClock Y, AnimationClock Scale)>();
                _auroraClocks.Add(orb, (x, y, zoom));
            }
        }

        internal static void SetOrbitalEmblemActive(
            bool active, FrameworkElement outerRing, FrameworkElement innerRing, FrameworkElement hexRing, FrameworkElement jewelBox)
        {
            active &= !SuppressContinuousAmbient;
            var outer = (RotateTransform)outerRing.RenderTransform;
            if (active && outer.HasAnimatedProperties) return;

            var inner = (RotateTransform)innerRing.RenderTransform;
            var hex = (ScaleTransform)hexRing.RenderTransform;
            var jewel = (TranslateTransform)jewelBox.RenderTransform;
            outer.BeginAnimation(RotateTransform.AngleProperty, active ? RotationLoop(360, 36) : null);
            inner.BeginAnimation(RotateTransform.AngleProperty, active ? RotationLoop(-360, 24) : null);
            SetScale(hex, active ? SineLoop(0.965, 1.035, 4.2) : null);
            jewel.BeginAnimation(TranslateTransform.YProperty, active ? SineLoop(-3.5, 3.5, 3.6) : null);
        }

        private static DoubleAnimation SineLoop(double from, double to, double seconds, int? desiredFrameRate = null)
        {
            var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = EaseInOutSine
            };
            if (!desiredFrameRate.HasValue) return WithRefreshFrameRate(animation);

            Timeline.SetDesiredFrameRate(animation, desiredFrameRate.Value);
            return animation;
        }

        private static DoubleAnimation RotationLoop(double angle, double seconds)
        {
            return WithRefreshFrameRate(new DoubleAnimation(0, angle, TimeSpan.FromSeconds(seconds))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        }

        internal static void PlayReadoutPop(FrameworkElement target, bool enabled)
        {
            var scale = (ScaleTransform)target.RenderTransform;
            CancelMotion(target);
            if (!CanTransition(enabled))
            {
                SetScale(scale, null);
                return;
            }

            var pop = WithRefreshFrameRate(new DoubleAnimation(1.14, 1, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = ReadoutEase,
                FillBehavior = FillBehavior.Stop
            });
            TrackAnimation(target, pop, _ => SetScale(scale, null));
            SetScale(scale, pop);
        }

        internal static void PlayBrandSheen(FrameworkElement sheen, bool enabled)
        {
            var slide = (TranslateTransform)sheen.RenderTransform;
            CancelMotion(sheen);
            if (!CanTransition(enabled)) return;

            var delay = TimeSpan.FromMilliseconds(230);
            var fade = WithRefreshFrameRate(new DoubleAnimationUsingKeyFrames { BeginTime = delay, FillBehavior = FillBehavior.Stop });
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(0.72, TimeSpan.FromMilliseconds(280), EaseInOutSine));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, TimeSpan.FromMilliseconds(620), EaseInOutSine));
            TrackAnimation(sheen, fade, _ =>
            {
                slide.BeginAnimation(TranslateTransform.XProperty, null);
                sheen.BeginAnimation(UIElement.OpacityProperty, null);
            });
            slide.BeginAnimation(TranslateTransform.XProperty, WithRefreshFrameRate(new DoubleAnimation(-34, 34, TimeSpan.FromMilliseconds(620))
            {
                BeginTime = delay,
                EasingFunction = EaseInOutCubic,
                FillBehavior = FillBehavior.Stop
            }));
            sheen.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        internal static void AnimateOpacity(UIElement target, double toOpacity, bool enabled, int durationMs = 220)
        {
            if (!CanTransition(enabled))
            {
                CancelMotion(target);
                target.BeginAnimation(UIElement.OpacityProperty, null);
                target.Opacity = toOpacity;
                return;
            }
            if ((double)target.GetAnimationBaseValue(UIElement.OpacityProperty) == toOpacity) return;

            double from = target.Opacity;
            var fade = WithRefreshFrameRate(new DoubleAnimation(from, toOpacity, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = EaseInOutCubic,
                FillBehavior = FillBehavior.Stop
            });
            TrackAnimation(target, fade, _ => target.BeginAnimation(UIElement.OpacityProperty, null));
            target.Opacity = toOpacity;
            target.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private static void SetScale(ScaleTransform scale, DoubleAnimationBase animation)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        /// <summary>视图切换滑入动效（适用于 队列 ↔ 计划、设置页切换、自动/手动切换）。</summary>
        internal static void PlayViewEnter(FrameworkElement el, bool enabled, double offsetX = 0, double offsetY = 14, int durationMs = 220)
        {
            if (el == null) return;
            CancelMotion(el);
            if (!CanTransition(enabled))
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = 1;
                el.RenderTransform = null;
                return;
            }
            try
            {
                var offset = new TranslateTransform();
                var scale = new ScaleTransform();
                var dur = TimeSpan.FromMilliseconds(durationMs);
                var fade = WithRefreshFrameRate(new DoubleAnimation(0.68, 1.0, TimeSpan.FromMilliseconds(Math.Max(110, durationMs - 60)))
                {
                    EasingFunction = EaseOutCubic,
                    FillBehavior = FillBehavior.Stop
                });
                var size = WithRefreshFrameRate(new DoubleAnimation(0.992, 1.0, dur)
                {
                    EasingFunction = EaseOutExpo,
                    FillBehavior = FillBehavior.Stop
                });

                var finish = StartTransformMotion(el, new Point(0.5, 0.5), scale, offset, null,
                    _ => el.BeginAnimation(UIElement.OpacityProperty, null));
                var primary = fade.Duration.TimeSpan > dur ? fade : size;
                primary.Completed += delegate { finish(true); };
                el.Opacity = 1.0;
                el.BeginAnimation(UIElement.OpacityProperty, fade);
                SetScale(scale, size);

                if (Math.Abs(offsetX) > 0.1)
                    offset.BeginAnimation(TranslateTransform.XProperty, WithRefreshFrameRate(new DoubleAnimation(offsetX, 0.0, dur)
                    {
                        EasingFunction = EaseOutExpo,
                        FillBehavior = FillBehavior.Stop
                    }));
                if (Math.Abs(offsetY) > 0.1)
                    offset.BeginAnimation(TranslateTransform.YProperty, WithRefreshFrameRate(new DoubleAnimation(offsetY, 0.0, dur)
                    {
                        EasingFunction = EaseOutExpo,
                        FillBehavior = FillBehavior.Stop
                    }));
            }
            catch { CancelMotion(el); }
        }

        /// <summary>主题切换柔光过渡幕（仅交互切换时触发，不污染离屏审计）。</summary>
        internal static void PlayThemeVeil(Border veil, bool enabled)
        {
            if (veil == null) return;
            CancelMotion(veil);
            if (!CanTransition(enabled)) { veil.Visibility = Visibility.Collapsed; return; }
            try
            {
                veil.Visibility = Visibility.Visible;
                var anim = WithRefreshFrameRate(new DoubleAnimation(0.32, 0.0, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = EaseOutCubic,
                    FillBehavior = FillBehavior.Stop
                });
                TrackAnimation(veil, anim, settle =>
                {
                    veil.BeginAnimation(UIElement.OpacityProperty, null);
                    if (settle) veil.Visibility = Visibility.Collapsed;
                });
                veil.Opacity = 0;
                veil.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            catch
            {
                CancelMotion(veil);
                veil.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>旋转角度缓动（用于日志展开箭头等）。</summary>
        internal static void AnimateRotate(UIElement el, double toAngle, bool enabled, int durationMs = 220)
        {
            if (el == null) return;
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            var rt = el.RenderTransform as RotateTransform;
            if (rt == null || rt.IsFrozen)
            {
                rt = new RotateTransform(rt?.Angle ?? 0);
                el.RenderTransform = rt;
            }
            double from = rt.Angle;
            CancelMotion(el);
            if (!CanTransition(enabled) || from == toAngle)
            {
                rt.BeginAnimation(RotateTransform.AngleProperty, null);
                rt.Angle = toAngle;
                return;
            }

            var anim = WithRefreshFrameRate(new DoubleAnimation(from, toAngle, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = EaseOutCubic,
                FillBehavior = FillBehavior.Stop
            });
            TrackAnimation(el, anim, _ => rt.BeginAnimation(RotateTransform.AngleProperty, null));
            rt.Angle = toAngle;
            rt.BeginAnimation(RotateTransform.AngleProperty, anim);
        }

        /// <summary>动态毛玻璃背景模糊（打开模态对话框或拖放覆盖层时模糊底层工作区）。</summary>
        internal static void AnimateBackdropBlur(UIElement target, double toRadius, bool enabled, int durationMs = 180)
        {
            if (target == null) return;
            try
            {
                if (!enabled || toRadius <= 0.1)
                {
                    if (toRadius <= 0.1)
                    {
                        var existing = target.Effect as BlurEffect;
                        if (existing == null || !enabled)
                        {
                            target.Effect = null;
                            return;
                        }
                        var clear = WithRefreshFrameRate(new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(durationMs))
                        {
                            EasingFunction = EaseOutCubic,
                            FillBehavior = FillBehavior.Stop
                        });
                        clear.Completed += delegate { target.Effect = null; };
                        existing.BeginAnimation(BlurEffect.RadiusProperty, clear);
                        return;
                    }
                    target.Effect = new BlurEffect { Radius = toRadius, RenderingBias = RenderingBias.Performance };
                    return;
                }

                var blur = target.Effect as BlurEffect;
                if (blur == null)
                {
                    blur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
                    target.Effect = blur;
                }
                var anim = WithRefreshFrameRate(new DoubleAnimation(toRadius, TimeSpan.FromMilliseconds(durationMs))
                {
                    EasingFunction = EaseOutCubic
                });
                blur.BeginAnimation(BlurEffect.RadiusProperty, anim);
            }
            catch { }
        }

        /// <summary>为玻璃卡片挂载指针跟随柔光高光（Fluent Reveal / VisionOS 玻璃质感）。</summary>
        internal static void AttachCardSpotlight(FrameworkElement card, Border spotlightOverlay, Func<bool> isEnabled)
        {
            if (card == null || spotlightOverlay == null) return;

            card.MouseMove += (s, e) =>
            {
                if (isEnabled != null && !isEnabled()) return;
                try
                {
                    double w = card.ActualWidth, h = card.ActualHeight;
                    if (w < 10 || h < 10) return;
                    Point p = e.GetPosition(card);
                    Point rel = new Point(Math.Max(0, Math.Min(1, p.X / w)), Math.Max(0, Math.Min(1, p.Y / h)));
                    Color c = ThemeService.GetColor("Color.Spotlight");
                    var brush = new RadialGradientBrush
                    {
                        Center = rel,
                        GradientOrigin = rel,
                        RadiusX = 240.0 / w,
                        RadiusY = 240.0 / h
                    };
                    brush.GradientStops.Add(new GradientStop(c, 0.0));
                    brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1.0));
                    brush.Freeze();
                    spotlightOverlay.Background = brush;
                    if (spotlightOverlay.Opacity < 0.99)
                    {
                        spotlightOverlay.BeginAnimation(UIElement.OpacityProperty,
                            WithRefreshFrameRate(new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(140))));
                    }
                }
                catch { }
            };

            card.MouseLeave += (s, e) =>
            {
                try
                {
                    spotlightOverlay.BeginAnimation(UIElement.OpacityProperty,
                        WithRefreshFrameRate(new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(220))));
                }
                catch { }
            };
        }

        internal static void SetDualPulseActive(UIElement ring1, UIElement ring2, bool active, bool enabled)
        {
            bool animate = active && enabled && !SuppressContinuousAmbient;
            SetPulse(ring1, animate, 0);
            SetPulse(ring2, animate, 650);
        }

        private static void SetPulse(UIElement ring, bool active, int delayMs)
        {
            var scale = (ScaleTransform)ring.RenderTransform;
            if (active && scale.HasAnimatedProperties) return;

            SetScale(scale, active ? PulseAnimation(1, 2.8, delayMs) : null);
            ring.BeginAnimation(UIElement.OpacityProperty, active ? PulseAnimation(0.75, 0, delayMs) : null);
        }

        private static DoubleAnimation PulseAnimation(double from, double to, int delayMs)
        {
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(1300))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = EaseOutCubic,
                RepeatBehavior = RepeatBehavior.Forever
            };
        }
    }

    internal static class RenderFrameHub
    {
        // CompositionTarget.Rendering belongs to the calling UI dispatcher.
        [ThreadStatic] private static HashSet<Action<double>> _subscribers;
        [ThreadStatic] private static Action<double>[] _snapshot;
        [ThreadStatic] private static TimeSpan? _lastRenderingTime;
        [ThreadStatic] private static long _lastTimestamp;

        internal static int ActiveSubscriberCount => _subscribers?.Count ?? 0;

        internal static void Subscribe(Action<double> tick)
        {
            if (_subscribers == null) _subscribers = new HashSet<Action<double>>();
            if (!_subscribers.Add(tick)) return;
            _snapshot = null;
            if (_subscribers.Count != 1) return;

            _lastTimestamp = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnRendering;
        }

        internal static void Unsubscribe(Action<double> tick)
        {
            if (_subscribers == null || !_subscribers.Remove(tick)) return;
            _snapshot = null;
            if (_subscribers.Count == 0) CompositionTarget.Rendering -= OnRendering;
        }

        private static void OnRendering(object sender, EventArgs e)
        {
            if (e is RenderingEventArgs rendering)
            {
                if (rendering.RenderingTime == _lastRenderingTime) return;
                _lastRenderingTime = rendering.RenderingTime;
            }

            long now = Stopwatch.GetTimestamp();
            double deltaSeconds = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
            _lastTimestamp = now;
            deltaSeconds = Math.Max(0.0005, Math.Min(0.08, deltaSeconds));

            // Keep a stable iteration order within each frame and rebuild only when membership changes.
            var snapshot = _snapshot;
            if (snapshot == null)
            {
                snapshot = new Action<double>[_subscribers.Count];
                _subscribers.CopyTo(snapshot);
                _snapshot = snapshot;
            }
            foreach (var tick in snapshot)
                if (_subscribers.Contains(tick)) tick(deltaSeconds);
        }
    }

    internal sealed class AmbientFrameTimer
    {
        private readonly FrameworkElement _owner;
        private readonly Action _sync;
        private readonly Action<double> _onFrame;
        private Window _window;
        private bool _loaded;

        internal AmbientFrameTimer(FrameworkElement owner, Action sync, Action<double> tick)
        {
            _owner = owner;
            _sync = sync;
            owner.IsHitTestVisible = false;
            owner.Focusable = false;
            owner.SnapsToDevicePixels = true;
            _onFrame = deltaSeconds =>
            {
                if (!CanAnimate)
                {
                    Sync();
                    return;
                }
                tick(deltaSeconds);
                owner.InvalidateVisual();
            };
            owner.Loaded += OnLoaded;
            owner.Unloaded += OnUnloaded;
            owner.IsVisibleChanged += delegate { Sync(); };
        }

        internal bool CanAnimate => _loaded && _owner.IsVisible && UiMotion.ActiveMotionTier > 0 &&
            _window?.WindowState != WindowState.Minimized &&
            !UiMotion.SuppressContinuousAmbient && SystemParameters.ClientAreaAnimation;

        internal void Start()
        {
            if (CanAnimate) RenderFrameHub.Subscribe(_onFrame);
        }

        internal void Stop() => RenderFrameHub.Unsubscribe(_onFrame);

        internal void Sync()
        {
            if (!CanAnimate) Stop();
            _sync();
            _owner.InvalidateVisual();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _loaded = true;
            _window = Window.GetWindow(_owner);
            if (_window != null) _window.StateChanged += OnMotionChanged;
            UiMotion.AmbientSuppressionChanged += OnMotionChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            UiMotion.PrismAccentChanged += OnPaletteChanged;
            ThemeService.ThemeChanged += OnPaletteChanged;
            Sync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            if (_window != null) _window.StateChanged -= OnMotionChanged;
            _window = null;
            UiMotion.AmbientSuppressionChanged -= OnMotionChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            UiMotion.PrismAccentChanged -= OnPaletteChanged;
            ThemeService.ThemeChanged -= OnPaletteChanged;
            Sync();
        }

        private void OnSystemParametersChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) OnMotionChanged(sender, e);
        }

        private void OnMotionChanged(object sender, EventArgs e)
        {
            if (_owner.Dispatcher.CheckAccess()) Sync();
            else _owner.Dispatcher.BeginInvoke(new Action(Sync));
        }

        private void OnPaletteChanged(object sender, EventArgs e)
        {
            if (_owner.Dispatcher.CheckAccess()) _owner.InvalidateVisual();
            else _owner.Dispatcher.BeginInvoke(new Action(_owner.InvalidateVisual));
        }
    }
}
