using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LodStudio.Controls;
using LodStudio.Services;

namespace LodStudio.Views.Pages
{
    public partial class PrismStudioPage : StudioPageBase
    {
        private static readonly double[] InjectionLevels = { 0.32, 0.76, 0.54, 0.90 };

        private readonly Button[] _themeChoices;
        private readonly Button[] _accentChoices;
        private readonly Button[] _motionChoices;
        private readonly Button[] _auraChoices;
        private readonly DispatcherTimer _wakeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(850) };
        private readonly DispatcherTimer _vialTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        private int _nextInjection;

        private bool CanAnimateSandbox => AnimationsEnabled && MotionTier >= 1 &&
            !UiMotion.SuppressContinuousAmbient && SystemParameters.ClientAreaAnimation;

        public PrismStudioPage()
        {
            InitializeComponent();
            InitializeStudio(SubTabButtons, SubTabTrack, SubTabIndicator, SubPageScroll, OpticsPanel, HabitsPanel);
            MountPrismCards(ThemeCard, AccentCard, MotionCard, HabitsCard, ShortcutsCard, AboutCard);

            _themeChoices = ThemeChoices.Children.OfType<Button>().ToArray();
            _accentChoices = AccentChoices.Children.OfType<Button>().ToArray();
            _motionChoices = MotionChoices.Children.OfType<Button>().ToArray();
            _auraChoices = new[] { IdleAuraButton, RunningAuraButton, WarningAuraButton };

            var paletteSwatches = new[] { ClassicSwatch, EmberSwatch, CobaltSwatch, IrisSwatch, AmberSwatch };
            for (int i = 0; i < paletteSwatches.Length; i++)
                paletteSwatches[i].Background = CreateSwatch(i, dark: true);
            RefreshSandboxSwatch();

            PrismRimOverlay.Attach(SandboxCard, SandboxRim, () => AnimationsEnabled && MotionTier >= 1);
            PrismRippleLayer.AttachClickRipple(SandboxCard, SandboxRipple, () => AnimationsEnabled && MotionTier >= 1);
            SandboxLevel.ValueChanged += (s, e) => SandboxVial.Level = e.NewValue;

            // Active glints draw continuously. Enable them only for the wake preview.
            SandboxGlint.Active = false;
            _wakeTimer.Tick += (s, e) => StopWakePreview();
            _vialTimer.Tick += (s, e) => StopVialDrive();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        protected override void ReadSettings()
        {
            UiMotion.SetPrismAccent(Settings.PrismAccent);
            int tier = Settings.Animations ? Settings.MotionTier : 0;
            UiMotion.SetMotionTier(tier);

            SelectChoice(_themeChoices, Settings.ThemeMode);
            SelectChoice(_accentChoices, Settings.PrismAccent);
            RestoreTasksToggle.IsChecked = Settings.RestoreTasks;
            RefreshSandboxSwatch();
            RefreshMotionSelection(tier);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ThemeService.ThemeChanged += OnThemeChanged;
            SyncFromSettings();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ThemeService.ThemeChanged -= OnThemeChanged;
            StopWakePreview();
            StopVialDrive();
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            if (Settings != null) SelectChoice(_themeChoices, Settings.ThemeMode);
            RefreshSandboxSwatch();
        }

        private void OnThemeClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            int mode = Array.IndexOf(_themeChoices, (Button)sender);
            Settings.ThemeMode = mode;
            _vm.SetThemeMode(mode);
            SelectChoice(_themeChoices, mode);
            RefreshSandboxSwatch();
        }

        private void OnAccentClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            int accent = Array.IndexOf(_accentChoices, (Button)sender);
            Settings.PrismAccent = accent;
            UiMotion.SetPrismAccent(accent);
            Save();
            SelectChoice(_accentChoices, accent);
            RefreshSandboxSwatch();
            TriggerWakeSweep();
        }

        private void OnMotionClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            int tier = _motionChoices.Length - 1 - Array.IndexOf(_motionChoices, (Button)sender);
            Settings.MotionTier = tier;
            Settings.Animations = tier > 0;
            UiMotion.SetMotionTier(tier);
            Save();
            RefreshMotionSelection(tier);
        }

        private void RefreshMotionSelection(int tier)
        {
            SelectChoice(_motionChoices, _motionChoices.Length - 1 - tier);
            SandboxAura.IsRunning = tier > 0 && SandboxAura.Tone != "Normal";
            if (tier > 0) return;
            StopWakePreview();
            StopVialDrive();
        }

        private void RefreshSandboxSwatch() => SandboxSwatch.Background =
            CreateSwatch(UiMotion.ActivePrismAccent, ThemeService.Current == ThemeKind.Dark);

        private static LinearGradientBrush CreateSwatch(int accent, bool dark)
        {
            var spectrum = UiMotion.GetPrismSpectrum(accent, dark);
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            // The first color is the optical highlight. Show the four spectrum stops.
            for (int i = 1; i < spectrum.Count; i++)
                brush.GradientStops.Add(new GradientStop(spectrum[i], (i - 1.0) / (spectrum.Count - 2)));
            brush.Freeze();
            return brush;
        }

        private void OnAuraClick(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            SelectChoice(_auraChoices, Array.IndexOf(_auraChoices, button));
            SandboxAura.Tone = (string)button.CommandParameter;
            SandboxAura.IsRunning = AnimationsEnabled && MotionTier >= 1 && SandboxAura.Tone != "Normal";
        }

        private void OnWakeClick(object sender, RoutedEventArgs e) => TriggerWakeSweep();

        private void TriggerWakeSweep()
        {
            if (CanAnimateSandbox)
            {
                SandboxGlint.Active = true;
                _wakeTimer.Stop();
                _wakeTimer.Start();
            }
            SandboxGlint.TriggerWakeSweep();
            _onWakeSweep?.Invoke();
        }

        private void StopWakePreview()
        {
            _wakeTimer.Stop();
            SandboxGlint.Active = false;
        }

        private void OnInjectClick(object sender, RoutedEventArgs e)
        {
            if (CanAnimateSandbox)
            {
                SandboxVial.IsActive = true;
                _vialTimer.Stop();
                _vialTimer.Start();
            }
            SandboxLevel.Value = InjectionLevels[_nextInjection];
            _nextInjection = (_nextInjection + 1) % InjectionLevels.Length;
        }

        private void StopVialDrive()
        {
            _vialTimer.Stop();
            SandboxVial.IsActive = false;
        }

        private void OnRestoreTasksClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            Settings.RestoreTasks = RestoreTasksToggle.IsChecked == true;
            Save();
        }

        private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsFolder();
        private void OnHelpClick(object sender, RoutedEventArgs e) => _vm?.Commands.Help.Execute(null);
        private void OnManualClick(object sender, RoutedEventArgs e) => _vm?.Commands.OpenManual.Execute(null);
    }
}
