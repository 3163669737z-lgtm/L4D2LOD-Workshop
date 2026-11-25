using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LodStudio.Controls;
using LodStudio.Engine;
using LodStudio.Services;
using LodStudio.ViewModels;

namespace LodStudio.Views.Pages
{
    public abstract class StudioPageBase : UserControl
    {
        protected ShellViewModel _vm;
        protected Action _onWakeSweep;
        private Action<bool> _onSaved;
        private FrameworkElement[] _panels;
        private Button[] _tabs;
        private FrameworkElement _tabTrack;
        private Border _indicator;
        private ScrollViewer _scroll;

        protected AppSettings Settings => _vm?.Settings;
        protected int MotionTier => Settings?.MotionTier ?? 3;
        protected bool AnimationsEnabled => Settings?.Animations ?? true;
        protected bool PrismEffectsEnabled => AnimationsEnabled && MotionTier >= 2;
        protected bool CanSave => Settings != null && !Syncing;
        protected bool Syncing { get; private set; }
        protected bool AuditRevealAllChildren { get; private set; }
        public int CurrentSubPageIndex { get; private set; }

        public void Attach(ShellViewModel vm, Action<bool> onSaved = null, Action onWakeSweep = null)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _onSaved = onSaved;
            _onWakeSweep = onWakeSweep;
            DataContext = vm;
            SyncFromSettings();
        }

        public void SyncFromSettings()
        {
            if (Settings == null) return;
            UiMotion.SetMotionTier(AnimationsEnabled ? MotionTier : 0);
            SyncControls(ReadSettings);
            RefreshChildren(false);
        }

        public void SelectSubPage(int index, bool animate)
        {
            if (index < 0 || index >= _panels.Length) return;

            int direction = index - CurrentSubPageIndex;
            CurrentSubPageIndex = index;
            if (direction != 0)
            {
                _panels[index - direction].Visibility = Visibility.Collapsed;
                _panels[index].Visibility = Visibility.Visible;
                SelectChoice(_tabs, index);
                _scroll.ScrollToTop();
                UiMotion.PlaySubPageTransition(_panels[index], direction, animate && AnimationsEnabled, MotionTier);
            }

            AlignSubTabIndicator(animate && direction != 0);
        }

        public void SetAuditRevealAllChildren(bool expand)
        {
            AuditRevealAllChildren = expand;
            RefreshChildren(false);
        }

        protected void InitializeStudio(Panel tabButtons, FrameworkElement tabTrack, Border indicator,
            ScrollViewer scroll, params FrameworkElement[] panels)
        {
            _panels = panels;
            _tabs = tabButtons.Children.OfType<Button>().ToArray();
            _tabTrack = tabTrack;
            _indicator = indicator;
            _scroll = scroll;

            for (int i = 0; i < _tabs.Length; i++)
            {
                int index = i;
                _tabs[i].Click += (s, e) => SelectSubPage(index, true);
                _tabs[i].SizeChanged += (s, e) => AlignSubTabIndicator(false);
            }
            SelectChoice(_tabs, 0);
            Loaded += (s, e) => AlignSubTabIndicator(false);
            IsVisibleChanged += (s, e) => AlignSubTabIndicator(false);
            AddHandler(CheckBox.ClickEvent, new RoutedEventHandler(AnimateToggle));
        }

        private void AlignSubTabIndicator(bool animate)
        {
            var tab = _tabs[CurrentSubPageIndex];
            if (!IsLoaded || !IsVisible || tab.ActualWidth <= 0) return;
            double x = tab.TranslatePoint(new Point(0, 0), _tabTrack).X;
            UiMotion.GlideHorizontalIndicator(_indicator, x, tab.ActualWidth,
                animate && AnimationsEnabled && MotionTier > 0);
        }

        private void AnimateToggle(object sender, RoutedEventArgs e)
        {
            if (!AnimationsEnabled || MotionTier == 0 || UiMotion.SuppressContinuousAmbient ||
                !SystemParameters.ClientAreaAnimation || !(e.OriginalSource is CheckBox toggle) ||
                !(toggle.Template?.FindName("StudioToggleThumb", toggle) is Border thumb)) return;

            var offset = thumb.RenderTransform as TranslateTransform;
            double currentX = offset?.X ?? 0;
            if (offset == null || offset.IsFrozen)
            {
                offset = new TranslateTransform(0, 0);
                thumb.RenderTransform = offset;
            }

            double travel = 18 * (toggle.IsChecked == true ? -1 : 1);
            var animation = UiMotion.WithRefreshFrameRate(new DoubleAnimation(currentX + travel, 0,
                TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
            UiMotion.TrackAnimation(thumb, animation, _ => offset.BeginAnimation(TranslateTransform.XProperty, null));
            offset.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        protected void MountPrismCards(params Border[] cards)
        {
            foreach (var card in cards)
            {
                var content = card.Child;
                card.Child = null;
                var surface = new Grid();
                surface.Children.Add(content);

                var margin = new Thickness(
                    -card.Padding.Left - card.BorderThickness.Left, -card.Padding.Top - card.BorderThickness.Top,
                    -card.Padding.Right - card.BorderThickness.Right, -card.Padding.Bottom - card.BorderThickness.Bottom);
                var rim = new PrismRimOverlay { CornerRadius = card.CornerRadius.TopLeft, Margin = margin };
                var ripple = new PrismRippleLayer { Margin = margin, ClipToBounds = true };
                surface.Children.Add(ripple);
                surface.Children.Add(rim);
                card.Child = surface;

                PrismRimOverlay.Attach(card, rim, () => PrismEffectsEnabled);
                PrismRippleLayer.AttachClickRipple(card, ripple, () => PrismEffectsEnabled);
            }
        }

        protected void RevealChild(FrameworkElement child, bool show, bool animate)
        {
            UiMotion.AnimateParentChildReveal(child, show || AuditRevealAllChildren,
                animate && AnimationsEnabled, MotionTier);
        }

        protected static void SelectChoice(Button[] choices, int index)
        {
            for (int i = 0; i < choices.Length; i++)
                choices[i].Tag = i == index ? "Selected" : null;
        }

        protected void SyncControls(Action sync)
        {
            bool wasSyncing = Syncing;
            Syncing = true;
            try { sync(); }
            finally { Syncing = wasSyncing; }
        }

        protected void BindTextBox(TextBox box, Action<string> commit)
        {
            void Commit()
            {
                if (CanSave) commit(box.Text.Trim());
            }

            box.LostKeyboardFocus += (s, e) => Commit();
            box.KeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                Commit();
            };
        }

        protected void Save(bool warn = false)
        {
            if (!CanSave) return;
            _vm.OnSettingsChanged();
            _onSaved?.Invoke(warn);
        }

        protected void OpenSettingsFolder()
        {
            if (_vm == null) return;
            try { _vm.OpenSettingsFolder(); }
            catch (Exception ex) { _vm.Toast("打开配置文件夹失败", ex.Message, ToastTone.Err); }
        }

        protected abstract void ReadSettings();
        protected virtual void RefreshChildren(bool animate) { }
    }

    public partial class StrategyPage : StudioPageBase
    {
        private const string L4d2Hands = "ValveBiped.Bip01_L_Hand|ValveBiped.Bip01_R_Hand";
        private readonly Button[] _profiles;
        private readonly Button[] _facialChoices;
        private readonly Button[] _shadowModes;
        private readonly Button[] _shadowReuseChoices;
        private string _lastCollapseBones = "";

        public StrategyPage()
        {
            InitializeComponent();
            InitializeStudio(SubTabButtons, SubTabTrack, SubTabIndicator, SubPageScroll,
                QualityPanel, EnginePanel, WritingPanel);
            MountPrismCards(ReductionCard, AdaptiveCard, VertexCard, KeepCard,
                ShadowCard, FacialCard, BonesCard, SkipCard, WorkersCard, WriteCard, PopupCard);
            _profiles = ProfileChoices.Children.OfType<Button>().ToArray();
            _facialChoices = FacialChoices.Children.OfType<Button>().ToArray();
            _shadowModes = ShadowModeChoices.Children.OfType<Button>().ToArray();
            _shadowReuseChoices = ShadowReuseChoices.Children.OfType<Button>().ToArray();

            BindNumber(AutoTargetBox, 1000, 200000, () => Settings.AutoTargetTriangles, v =>
            {
                Settings.AutoTargetTriangles = v;
                Save();
                if (Settings.AutoTargetTriangles != v)
                    _vm.Toast("目标面数已调整", $"当前版本可保存 2,000 到 100,000 三角面，已设为 {Settings.AutoTargetTriangles:N0}。", ToastTone.Warn);
            });
            BindNumber(AutoSavingsBox, 1, 50, () => Settings.AutoMinSavingsPercent, v =>
            {
                Settings.AutoMinSavingsPercent = v;
                Save();
            });
            BindNumber(MinTrianglesBox, 0, 2000000, () => Settings.MinTriangles, v =>
            {
                Settings.MinTriangles = v;
                Save();
            });
            BindNumber(ShadowRatioBox, 5, 60, ReadShadowPercent, SetShadowPercent);
            BindNumber(ShadowReuseLevelBox, 1, QcWriter.MaxVisibleLodsWithShadow,
                () => Settings.ShadowLodReuseLevel, SetShadowReuseLevel, zeroIsAutomatic: true);
            BindNumber(FacialLevelBox, 1, LodPlan.MaxLevels, () => Settings.NoFacialFromLevel, SetFacialLevel);
            BindNumber(BonesLevelBox, 1, LodPlan.MaxLevels, () => Settings.CollapseBonesFromLevel, v =>
            {
                Settings.CollapseBonesFromLevel = v;
                Save();
            });
            BindNumber(WorkersBox, 0, 16, () => Settings.Workers, SetWorkers);

            BindTextBox(KeepModelsBox, text =>
            {
                if (text == (Settings.KeepOriginalModels ?? "")) return;
                Settings.KeepOriginalModels = text;
                Save();
            });
            BindTextBox(BonesBox, SetCollapseBones);
            ShadowSlider.ValueChanged += (s, e) =>
            {
                if (CanSave) SetShadowPercent((int)e.NewValue);
            };
            WorkersSlider.ValueChanged += (s, e) =>
            {
                if (CanSave) SetWorkers((int)e.NewValue);
            };
        }

        protected override void ReadSettings()
        {
            SelectChoice(_profiles, Settings.EffectiveReductionProfile());
            AdaptiveToggle.IsChecked = Settings.AdaptiveTriangleBudget;
            VertexToggle.IsChecked = Settings.AllowVertexUpdates;
            KeepModelsBox.Text = Settings.KeepOriginalModels ?? "";
            AutoTargetBox.Text = Number(Settings.AutoTargetTriangles);
            AutoSavingsBox.Text = Number(Settings.AutoMinSavingsPercent);
            MinTrianglesBox.Text = Number(Settings.MinTriangles);

            ShadowToggle.IsChecked = Settings.GenerateShadowLod;
            FacialToggle.IsChecked = Settings.NoFacial;
            ReadShadowControls();
            ReadFacialControls();
            string bones = Settings.CollapseBones ?? "";
            if (!string.IsNullOrWhiteSpace(bones)) _lastCollapseBones = bones;
            BonesToggle.IsChecked = !string.IsNullOrWhiteSpace(bones);
            BonesBox.Text = bones;
            BonesLevelBox.Text = Number(ClampLevel(Settings.CollapseBonesFromLevel));
            SkipToggle.IsChecked = Settings.SkipMainModel;
            ReadSkipCount();

            ReadWorkerControls();
            OverwriteToggle.IsChecked = Settings.ForceOverwriteLod;
            CompileToggle.IsChecked = Settings.CompileAfter;
            SuccessToggle.IsChecked = Settings.PopupOnSuccess;
        }

        protected override void RefreshChildren(bool animate)
        {
            bool adaptive = AdaptiveToggle.IsChecked == true;
            RevealChild(AutomaticBudget, adaptive, animate);
            RevealChild(SmallModelBudget, !adaptive, animate);
            RevealChild(VertexDetails, VertexToggle.IsChecked == true, animate);
            RevealChild(ShadowDetails, ShadowToggle.IsChecked == true, animate);
            RefreshShadowPanels();
            RevealChild(FacialDetails, FacialToggle.IsChecked == true, animate);
            RevealChild(BonesDetails, BonesToggle.IsChecked == true, animate);
            RevealChild(SkipDetails, SkipToggle.IsChecked == true, animate);
        }

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static int ClampLevel(int value) => Math.Max(1, Math.Min(LodPlan.MaxLevels, value));

        private void BindNumber(TextBox box, int min, int max, Func<int> read, Action<int> write,
            bool zeroIsAutomatic = false)
        {
            BindTextBox(box, text =>
            {
                string Display(int value) => zeroIsAutomatic && value == 0 ? "" : Number(Math.Max(min, Math.Min(max, value)));
                int before = read();
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int typed) &&
                    (before == Math.Max(min, Math.Min(max, before)) || text != Display(before)))
                {
                    int value = Math.Max(min, Math.Min(max, typed));
                    if (value != before) write(value);
                }
                box.Text = Display(read());
            });
        }

        private void OnProfileClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            int profile = Array.IndexOf(_profiles, (Button)sender);
            if (Settings.ReductionProfile == profile && Settings.QualityFirst == (profile != 2)) return;
            Settings.ReductionProfile = profile;
            Settings.QualityFirst = profile != 2;
            SelectChoice(_profiles, profile);
            Save();
        }

        private void OnAdaptiveClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            bool enabled = Settings.AdaptiveTriangleBudget = AdaptiveToggle.IsChecked == true;
            RevealChild(AutomaticBudget, enabled, true);
            RevealChild(SmallModelBudget, !enabled, true);
            Save();
        }

        private void OnVertexClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            Settings.AllowVertexUpdates = VertexToggle.IsChecked == true;
            RevealChild(VertexDetails, Settings.AllowVertexUpdates, true);
            Save();
        }

        private void OnShadowClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            Settings.GenerateShadowLod = ShadowToggle.IsChecked == true;
            RevealChild(ShadowDetails, Settings.GenerateShadowLod, true);
            Save();
        }

        private int ReadShadowPercent() =>
            (int)Math.Round(Settings.ShadowLodRatio * 100, MidpointRounding.AwayFromZero);

        private void ReadShadowControls()
        {
            int percent = ReadShadowPercent();
            ShadowSlider.Value = percent;
            ShadowRatioBox.Text = Number(percent);
            SelectChoice(_shadowModes, Settings.ShadowLodMode);
            int reuseLevel = Settings.ShadowLodReuseLevel;
            SelectChoice(_shadowReuseChoices, reuseLevel);
            ShadowReuseLevelBox.Text = reuseLevel == 0 ? "" : Number(reuseLevel);

            string description = !ReusesShadowLod ? $"独立阴影网格目标保留 {percent}% 面数。无法生成时沿用最远可见档。"
                : reuseLevel == 0 ? "使用实际写入的最远可见 LOD 作为阴影网格。"
                : $"使用实际写入的第 {reuseLevel} 档作为阴影网格。档位不足时自动回退到最远可见档。";
            ShadowReadout.Text = description + " 上限为原模型 + 6 级可见 LOD + 1 级阴影，共 8 级。";
        }

        private bool ReusesShadowLod => Settings?.ShadowLodMode == (int)ShadowLodMode.ReuseExistingLod;

        private void RefreshShadowPanels()
        {
            ShadowDedicatedPanel.Visibility = !ReusesShadowLod || AuditRevealAllChildren ? Visibility.Visible : Visibility.Collapsed;
            ShadowReusePanel.Visibility = ReusesShadowLod || AuditRevealAllChildren ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnShadowModeClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            int mode = Array.IndexOf(_shadowModes, (Button)sender);
            if (mode == Settings.ShadowLodMode) return;
            Settings.ShadowLodMode = mode;
            SyncControls(ReadShadowControls);
            RefreshShadowPanels();
            Save();
        }

        private void OnShadowReuseClick(object sender, RoutedEventArgs e)
        {
            if (CanSave) SetShadowReuseLevel(Array.IndexOf(_shadowReuseChoices, (Button)sender));
        }

        private void SetShadowReuseLevel(int level)
        {
            if (Settings.ShadowLodReuseLevel == level) return;
            Settings.ShadowLodReuseLevel = level;
            SyncControls(ReadShadowControls);
            Save();
        }

        private void SetShadowPercent(int percent)
        {
            if (percent == ReadShadowPercent()) return;
            Settings.ShadowLodRatio = percent / 100.0;
            SyncControls(ReadShadowControls);
            Save();
        }

        private void OnFacialClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            Settings.NoFacial = FacialToggle.IsChecked == true;
            RevealChild(FacialDetails, Settings.NoFacial, true);
            Save();
        }

        private void OnFacialChoiceClick(object sender, RoutedEventArgs e)
        {
            if (CanSave) SetFacialLevel(Array.IndexOf(_facialChoices, (Button)sender) + 1);
        }

        private void ReadFacialControls()
        {
            int level = ClampLevel(Settings.NoFacialFromLevel);
            FacialLevelBox.Text = Number(level);
            SelectChoice(_facialChoices, Math.Min(2, level - 1));
        }

        private void SetFacialLevel(int level)
        {
            if (Settings.NoFacialFromLevel == level) return;
            Settings.NoFacialFromLevel = level;
            ReadFacialControls();
            Save();
        }

        private void OnBonesClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            string bones = BonesToggle.IsChecked != true ? "" :
                (string.IsNullOrWhiteSpace(_lastCollapseBones) ? L4d2Hands : _lastCollapseBones);
            SetCollapseBones(bones);
        }

        private void OnFillBonesClick(object sender, RoutedEventArgs e)
        {
            if (CanSave) SetCollapseBones(L4d2Hands);
        }

        private void SetCollapseBones(string bones)
        {
            string previous = Settings.CollapseBones ?? "";
            bool wasEnabled = !string.IsNullOrWhiteSpace(previous);
            bool enabled = !string.IsNullOrWhiteSpace(bones);
            if (enabled) _lastCollapseBones = bones;
            else if (wasEnabled) _lastCollapseBones = previous;

            BonesBox.Text = bones;
            BonesToggle.IsChecked = enabled;
            if (previous == bones) return;
            Settings.CollapseBones = bones;
            if (wasEnabled != enabled) RevealChild(BonesDetails, enabled, true);
            Save();
        }

        private void OnSkipClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            Settings.SkipMainModel = SkipToggle.IsChecked == true;
            RevealChild(SkipDetails, Settings.SkipMainModel, true);
            Save(Settings.SkipMainModel);
        }

        private void ReadSkipCount()
        {
            int count = Settings.SkipMainQcs?.Count ?? 0;
            SkipReadout.Text = count == 0 ? "还没有选中任何 QC" : $"已选 {count} 个 QC";
        }

        private void OnPickSkipClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            try
            {
                var dialog = new SkipMainPickerWindow(Window.GetWindow(this), _vm, _vm.Settings);
                if (dialog.ShowDialog() != true) return;
                Settings.SkipMainQcs = dialog.SelectedPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                ReadSkipCount();
                Save();
            }
            catch (Exception ex)
            {
                _vm.Toast("无法打开跳过清单", ex.Message, ToastTone.Err);
            }
        }

        private void ReadWorkerControls()
        {
            int workers = Settings.Workers;
            WorkersSlider.Value = workers;
            WorkersBox.Text = Number(workers);
            WorkersReadout.Text = workers == 0
                ? "智能自动（推荐）· 按 CPU 与内存自动决定"
                : $"手动固定为 {workers} 个并行进程";
        }

        private void SetWorkers(int workers)
        {
            if (Settings.Workers == workers) return;
            Settings.Workers = workers;
            SyncControls(ReadWorkerControls);
            Save();
        }

        private void OnOverwriteClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            Settings.ForceOverwriteLod = OverwriteToggle.IsChecked == true;
            Save(Settings.ForceOverwriteLod);
        }

        private void OnCompileClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            Settings.CompileAfter = CompileToggle.IsChecked == true;
            bool warn = Settings.CompileAfter && string.IsNullOrWhiteSpace(Settings.NekomdlPath);
            Save(warn);
            if (warn)
                _vm.Toast("已开启试编译", "请先到「维护与环境」设置编译器路径，再开始生成。", ToastTone.Warn);
        }

        private void OnSuccessClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            Settings.PopupOnSuccess = SuccessToggle.IsChecked == true;
            Save();
        }
    }
}
