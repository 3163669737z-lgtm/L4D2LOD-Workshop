using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LodStudio.Controls;
using LodStudio.Engine;
using LodStudio.Services;
using LodStudio.ViewModels;
using LodStudio.Views.Pages;

namespace LodStudio.Views
{
    /// <summary>
    /// 主窗口：标题栏、工作室导航、四个页面，以及共用的 HUD、拖放和对话框。
    /// 视图职责：布局与交互；所有业务状态在 ShellViewModel 里。
    /// </summary>
    public partial class ShellWindow : Window, IShellInteraction
    {
        private ShellViewModel _vm;
        private AppSettings _settings;
        private bool _forceClose;
        private bool _closingRequested;
        private IDisposable _handoff;
        private ScrollViewer _logScroll;
        private bool _logFollowQueued;
        private bool _atelierLoaded;
        private int _currentMainPage;
        private int _lastWakePrismAccent;
        private readonly FrameworkElement[] _mainPages;
        private readonly Button[] _navButtons;
        private readonly DispatcherTimer _wakeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(780) };
        private readonly (FrameworkElement Element, double OffsetX, double OffsetY, int DelayMs)[] _entranceSequence;

        private ContextMenu _openMenu;
        private string _lastFolderPickDir;
        private SettingsWindow _settingsWindow;
        private bool _syncingPreset;

        public ShellWindow() : this(true) { }

        internal ShellWindow(bool fitWorkArea)
        {
            Engine.PerfTrace.Mark("shell-ctor-start");
            InitializeComponent();
            _mainPages = new FrameworkElement[] { PageWorkbench, StrategyView, ToolkitView, PrismView };
            _navButtons = new[] { BtnNavWorkbench, BtnNavStrategy, BtnNavToolkit, BtnNavPrism };
            _wakeTimer.Tick += (s, e) => StopGlobalWakeSweep();
            _entranceSequence = new (FrameworkElement, double, double, int)[]
            {
                (CaptionBar, 0, -6, 0),
                (NavRail, -10, 0, 40),
                (QueueCard, -10, 6, 72),
                (LodCard, 10, 7, 120),
                (LogCard, -7, 8, 156),
                (HudCard, 0, 9, 192)
            };
            Engine.PerfTrace.Mark("shell-xaml");
            try
            {
                Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
            }
            catch { }

            _settings = SettingsStore.Load();
            UiMotion.SetPrismAccent(_settings.PrismAccent);
            UiMotion.SetMotionTier(_settings.Animations ? _settings.MotionTier : 0);
            _lastWakePrismAccent = _settings.PrismAccent;
            Engine.PerfTrace.Mark("shell-settings");
            _vm = new ShellViewModel(_settings, this);
            Engine.PerfTrace.Mark("shell-vm");
            DataContext = _vm;
            StrategyView.Attach(_vm, OnStudioSettingSaved, TriggerGlobalWakeSweep);
            ToolkitView.Attach(_vm, OnStudioSettingSaved, TriggerGlobalWakeSweep);
            PrismView.Attach(_vm, OnStudioSettingSaved, TriggerGlobalWakeSweep);

            SetupSegments();
            SetupPresets();
            HookLodPlan();
            HookVm();
            ApplyWindowGeometry();
            UpdateThemeIcon();
            ApplyLogExpanded(_settings.LogExpanded, false);
            Engine.PerfTrace.Mark("shell-setup");

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Closed += OnClosed;
            WindowGlass.Attach(this);
            if (fitWorkArea) WindowWorkArea.Track(this);
            RootBorder.SizeChanged += OnWorkspaceSizeChanged;
            NavTrack.SizeChanged += (s, e) => AlignNavIndicator(false);
            Closing += OnClosing;
            StateChanged += OnStateChanged;
            if (fitWorkArea && WindowState == WindowState.Maximized) OnStateChanged(this, EventArgs.Empty);
            PreviewKeyDown += OnWindowPreviewKeyDown;
            PreviewMouseDown += OnWindowPreviewMouseDownForSearch;

            // 玻璃卡片指针跟随高光 + 模态对话框背景毛玻璃动态虚化
            UiMotion.AttachCardSpotlight(QueueCard, QueueSpotlight, () => AtelierMotionEnabled);
            UiMotion.AttachCardSpotlight(LodCard, LodSpotlight, () => AtelierMotionEnabled);
            UiMotion.AttachCardSpotlight(LogCard, LogSpotlight, () => AtelierMotionEnabled);
            UiMotion.AttachCardSpotlight(HudCard, HudSpotlight, () => AtelierMotionEnabled);
            AttachPrismOptics();
            Dialogs.OpenStateChanged += (s, open) =>
                UiMotion.AnimateBackdropBlur(RootBorder, open ? 6.0 : 0.0, AnimationsEnabled, 180);

            DragEnter += OnDragEnterWindow;
            DragOver += OnDragOverWindow;
            DragLeave += OnDragLeaveWindow;
            Drop += OnDropWindow;

            _handoff = SingleInstance.StartHandoffWatcher(paths =>
            {
                if (paths == null || paths.Length == 0) return;
                Dispatcher.BeginInvoke(new Action(async () => await _vm.AddPathsAsync(paths)));
            });
        }

        // ═══════════════════ 初始化 / 生命周期 ═══════════════════

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Engine.PerfTrace.Mark("window-loaded");
            if (!_atelierLoaded)
            {
                _atelierLoaded = true;
                ThemeService.ThemeChanged += OnThemeChanged;
                SystemParameters.StaticPropertyChanged += OnMotionPreferenceChanged;
                UiMotion.AmbientSuppressionChanged += OnAmbientSuppressionChanged;
                UiMotion.TransitionStarted += OnTransitionStarted;
                UiMotion.PlayWindowEntrance(_entranceSequence, AtelierMotionEnabled);
                UiMotion.PlayBrandSheen(BrandSheen, AtelierMotionEnabled);
                AlignNavIndicator(false);
                SyncAtelierMotion();
            }
            try
            {
                // 第一帧渲染完成（用于验收冷启动时间）
                EventHandler once = null;
                once = delegate
                {
                    try { CompositionTarget.Rendering -= once; } catch { }
                    Engine.PerfTrace.Mark("first-frame");
                    Engine.PerfTrace.Flush();
                };
                CompositionTarget.Rendering += once;
            }
            catch { }
            FollowLog();
            try { await _vm.RunEnvironmentCheckAsync(false); } catch { }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => StopAtelierMotion();

        private void OnClosed(object sender, EventArgs e) => StopAtelierMotion();

        private void StopAtelierMotion()
        {
            if (!_atelierLoaded) return;
            _atelierLoaded = false;
            ThemeService.ThemeChanged -= OnThemeChanged;
            SystemParameters.StaticPropertyChanged -= OnMotionPreferenceChanged;
            UiMotion.AmbientSuppressionChanged -= OnAmbientSuppressionChanged;
            UiMotion.TransitionStarted -= OnTransitionStarted;
            SyncAtelierMotion();
        }

        private bool AtelierMotionEnabled => AnimationsEnabled && _settings.MotionTier > 0 && !UiMotion.SuppressContinuousAmbient;

        private void AttachPrismOptics()
        {
            Func<bool> prismEffectsEnabled = () => AtelierMotionEnabled && _settings.MotionTier >= 2;
            foreach (var (card, rim, ripple) in new[]
            {
                (QueueCard, QueueRim, QueueRipple),
                (LodCard, LodRim, LodRipple),
                (LogCard, LogRim, LogRipple),
                (HudCard, HudRim, HudRipple),
                (NavRail, NavRailRim, NavRailRipple)
            })
            {
                PrismRimOverlay.Attach(card, rim, prismEffectsEnabled);
                PrismRippleLayer.AttachClickRipple(card, ripple, prismEffectsEnabled);
            }

            Func<bool> microMotionEnabled = () => AtelierMotionEnabled;
            UiMotion.AttachIconMicroMotion(BtnNavWorkbench, NavIcon0, "lift", microMotionEnabled);
            UiMotion.AttachIconMicroMotion(BtnNavStrategy, NavIcon1, "gear", microMotionEnabled);
            UiMotion.AttachIconMicroMotion(BtnNavToolkit, NavIcon2, "tilt", microMotionEnabled);
            UiMotion.AttachIconMicroMotion(BtnNavPrism, NavIcon3, "pulse", microMotionEnabled);
            UiMotion.AttachIconMicroMotion(BtnTheme, BtnTheme.Content as Icon, "pulse", microMotionEnabled);
            UiMotion.AttachIconMicroMotion(BtnEnv, EnvIcon, "tilt", microMotionEnabled);
        }

        private void SyncAtelierMotion()
        {
            UiMotion.SetMotionTier(_settings.Animations ? _settings.MotionTier : 0);
            UiMotion.SetPrismAccent(_settings.PrismAccent);
            bool enabled = _atelierLoaded && AtelierMotionEnabled;
            bool emptyWorkbench = _currentMainPage == 0 && _vm.Queue.IsEmpty && !_vm.IsPlanVisible;
            bool opticsEnabled = enabled && _settings.MotionTier >= 2;
            EmptyJewelGlint.Active = opticsEnabled && emptyWorkbench;
            if (!opticsEnabled) StopGlobalWakeSweep();
            UiMotion.SetAmbientAurora(opticsEnabled, AuroraOrb1, AuroraOrb2, AuroraOrb3, AuroraOrb4);
            UiMotion.SetOrbitalEmblemActive(opticsEnabled && emptyWorkbench,
                EmptyOuterRing, EmptyInnerRing, EmptyHexRing, EmptyJewelBox);

            bool hudActive = _atelierLoaded && (_vm.IsRunning || _vm.IsBusy);
            UiMotion.SetDualPulseActive(StatusPulseRing, StatusPulseRing2, hudActive, enabled);
            UiMotion.AnimateOpacity(HudActiveGlowBorder, hudActive ? 1 : 0, enabled);

            if (!enabled)
            {
                UiMotion.PlayWindowEntrance(_entranceSequence, false);
                UiMotion.PlayReadoutPop(HudReadoutMain, false);
                UiMotion.PlayBrandSheen(BrandSheen, false);
            }
        }

        private void TriggerGlobalWakeSweep() => TriggerGlobalWakeSweep(pageChanged: false);

        private void TriggerGlobalWakeSweep(bool pageChanged)
        {
            // 光效试验台也会调用此回调。只有切换页面或配色时才扫过整个窗口。
            int accent = _settings.PrismAccent;
            bool accentChanged = accent != _lastWakePrismAccent;
            _lastWakePrismAccent = accent;
            if ((!pageChanged && !accentChanged) || !_atelierLoaded || !AtelierMotionEnabled || _settings.MotionTier < 2)
                return;

            GlobalWakeGlint.Active = true;
            GlobalWakeGlint.TriggerWakeSweep();
            _wakeTimer.Stop();
            _wakeTimer.Start();
        }

        private void OnTransitionStarted(object sender, bool isSubPage)
        {
            if (isSubPage && Dispatcher.CheckAccess() &&
                sender is DependencyObject page && Window.GetWindow(page) == this)
                StopGlobalWakeSweep();
        }

        private void StopGlobalWakeSweep()
        {
            _wakeTimer.Stop();
            GlobalWakeGlint.Active = false;
        }

        private void OnStudioSettingSaved(bool warn)
        {
            SyncAtelierMotion();
            if (warn) ShowToast("设置已保存", "生成前可以先预览，确认写入结果符合预期。", ToastTone.Warn);
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            UpdateThemeIcon();
            SyncAtelierMotion();
        }

        private void OnMotionPreferenceChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
            OnAmbientSuppressionChanged(sender, EventArgs.Empty);
        }

        private void OnAmbientSuppressionChanged(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess()) SyncAtelierMotion();
            else Dispatcher.BeginInvoke(new Action(SyncAtelierMotion));
        }

        private void HookVm()
        {
            _vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_vm.IsLogExpanded))
                    ApplyLogExpanded(_vm.IsLogExpanded, true);
                if (e.PropertyName == nameof(_vm.IsPlanVisible))
                {
                    if (_vm.IsPlanVisible)
                        UiMotion.PlayViewEnter(PlanOverlay, AnimationsEnabled, 18, 0, 240);
                    else
                        UiMotion.PlayViewEnter(QueuePane, AnimationsEnabled, -18, 0, 240);
                    if (_atelierLoaded) SyncAtelierMotion();
                }
                // OnSettingsChanged always refreshes AutoBudgetHint, even for an animation-only change.
                if (_atelierLoaded && (e.PropertyName == nameof(_vm.IsRunning) || e.PropertyName == nameof(_vm.IsBusy) ||
                    e.PropertyName == nameof(_vm.AutoBudgetHint)))
                    SyncAtelierMotion();
                if (_atelierLoaded && e.PropertyName == nameof(_vm.ReadoutMainText))
                    UiMotion.PlayReadoutPop(HudReadoutMain, AtelierMotionEnabled);
                if (e.PropertyName == nameof(_vm.ProgressVisible) || e.PropertyName == nameof(_vm.ProgressValue) ||
                    e.PropertyName == nameof(_vm.IsRunning))
                    SyncHudLiquidLevel();
            };
            _vm.Queue.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_vm.Queue.CountAll))
                {
                    UpdateFilterItems();
                    SyncHudLiquidLevel();
                }
                if (e.PropertyName == nameof(_vm.Queue.FilterIndex)) SegFilter.SelectedIndex = _vm.Queue.FilterIndex;
                if (_atelierLoaded && e.PropertyName == nameof(_vm.Queue.IsEmpty)) SyncAtelierMotion();
            };
            _vm.Log.Lines.CollectionChanged += (s, e) => FollowLog();
            _vm.Log.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_vm.Log.Filter)) SegLogFilter.SelectedIndex = _vm.Log.Filter;
            };
            SyncHudLiquidLevel();
        }

        private void SyncHudLiquidLevel() => HudLiquidVial.Level = _vm.ProgressVisible
            ? Math.Max(0.08, _vm.ProgressValue)
            : (_vm.Queue.HasItems ? 0.28 : 0.14);

        private void OnMainNavClick(object sender, RoutedEventArgs e) =>
            SelectMainPage(Array.IndexOf(_navButtons, (Button)sender));

        public void SelectMainPage(int index, int subPageIndex = -1, bool animate = true)
        {
            index = Math.Max(0, Math.Min(_mainPages.Length - 1, index));
            int direction = index - _currentMainPage;
            var targetPage = _mainPages[index];
            if (direction != 0)
            {
                _mainPages[_currentMainPage].Visibility = Visibility.Collapsed;
                _currentMainPage = index;
                targetPage.Visibility = Visibility.Visible;
            }

            for (int i = 0; i < _navButtons.Length; i++)
                _navButtons[i].Tag = i == index ? "Selected" : null;
            AlignNavIndicator(animate && direction != 0);

            if (targetPage is StudioPageBase studioPage)
            {
                studioPage.SyncFromSettings();
                if (subPageIndex >= 0) studioPage.SelectSubPage(subPageIndex, false);
            }

            if (!animate) StopGlobalWakeSweep();
            if (direction != 0 || !animate)
                UiMotion.PlayPageTransition(targetPage, direction, animate && AtelierMotionEnabled, _settings.MotionTier);
            if (direction != 0 && animate) TriggerGlobalWakeSweep(pageChanged: true);

            SyncAtelierMotion();
        }

        private void AlignNavIndicator(bool animate)
        {
            var selected = _navButtons[_currentMainPage];
            if (selected.ActualHeight <= 0) return;

            const double targetHeight = 22;
            double targetY = selected.TranslatePoint(new Point(0, 0), NavTrack).Y + (selected.ActualHeight - targetHeight) / 2;
            UiMotion.GlideNavIndicator(NavActiveIndicator, targetY, targetHeight,
                animate && AtelierMotionEnabled && _settings.MotionTier > 0);
        }

        private void ApplyWindowGeometry()
        {
            try
            {
                if (_settings.WinWidth >= MinWidth && _settings.WinHeight >= MinHeight)
                {
                    Width = _settings.WinWidth;
                    Height = _settings.WinHeight;
                }
                if (_settings.WinLeft != int.MinValue && _settings.WinTop != int.MinValue)
                {
                    double l = _settings.WinLeft, t = _settings.WinTop;
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = l;
                    Top = t;
                }
                if (_settings.Maximized) WindowState = WindowState.Maximized;
            }
            catch { }
        }

        private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
            {
                double width = e.NewSize.Width;
                double windowWidth = ActualWidth > 0 ? ActualWidth : width;
                bool compact = windowWidth < 1100;
                double navOuterWidth = compact ? 56 : 162;
                NavRailColumn.Width = new GridLength(navOuterWidth);
                NavRail.Margin = compact ? new Thickness(12, 10, 0, 10) : new Thickness(18, 12, 0, 12);
                PageStack.Margin = compact ? new Thickness(10, 10, 12, 10) : new Thickness(14, 12, 18, 12);
                Visibility navTextVisibility = compact ? Visibility.Collapsed : Visibility.Visible;
                NavRailEyebrow.Visibility = NavThemePill.Visibility = navTextVisibility;
                NavLabel0.Visibility = NavLabel1.Visibility = NavLabel2.Visibility = NavLabel3.Visibility = navTextVisibility;
                foreach (var button in _navButtons)
                {
                    button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
                    button.Padding = compact ? new Thickness(0) : new Thickness(12, 0, 12, 0);
                }
                HudLiquidVial.Visibility = windowWidth < 1060 ? Visibility.Collapsed : Visibility.Visible;

                double outerPadding = PageStack.Margin.Left + PageStack.Margin.Right;
                double workbenchWidth = Math.Max(420, width - navOuterWidth - outerPadding);
                double planWidth = Math.Max(318, Math.Min(400, workbenchWidth * 0.365));
                PlanColumn.Width = new GridLength(planWidth);
                LodLatinTag.Visibility = planWidth >= 360 ? Visibility.Visible : Visibility.Collapsed;
                CmbPreset.Width = planWidth < 350 ? 128 : 148;
                Toasts.Width = Math.Min(360, planWidth - 8);
                double queueWidth = workbenchWidth - planWidth - 14;
                bool stackSearch = queueWidth < 610;
                Grid.SetRow(SearchBox, stackSearch ? 1 : 0);
                Grid.SetColumn(SearchBox, stackSearch ? 0 : 1);
                Grid.SetColumnSpan(SearchBox, stackSearch ? 2 : 1);
                SearchBox.HorizontalAlignment = stackSearch ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
                SearchBox.Margin = stackSearch ? new Thickness(0, 0, 0, 8) : new Thickness(12, 0, 0, 0);
                SearchBox.Width = stackSearch ? double.NaN : 204;
                SearchBox.MaxWidth = double.PositiveInfinity;
            }
            if (e.HeightChanged && _vm.IsLogExpanded) ApplyLogExpanded(true, false);
        }

        private void SaveWindowGeometry()
        {
            try
            {
                _settings.Maximized = WindowState == WindowState.Maximized;
                if (WindowState == WindowState.Normal)
                {
                    _settings.WinWidth = (int)Math.Round(Width);
                    _settings.WinHeight = (int)Math.Round(Height);
                    _settings.WinLeft = (int)Math.Round(Left);
                    _settings.WinTop = (int)Math.Round(Top);
                }
            }
            catch { }
        }

        private async void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_forceClose) return;
            if (_closingRequested || Dialogs.IsOpen)
            {
                e.Cancel = true;
                if (Dialogs.IsOpen && !_closingRequested) Dialogs.Dismiss();
                return;
            }
            if (!_vm.CanEdit)
            {
                e.Cancel = true;
                _closingRequested = true;
                try
                {
                    if (_vm.IsRunning && !_vm.IsStopping &&
                        !await ConfirmAsync("停止并关闭", "当前步骤收尾后关闭窗口，已发布的结果会保留。",
                            "停止并关闭", "继续生成", true, null))
                        return;
                    _vm.ForceCancel();
                    while (!_vm.CanEdit) await Task.Delay(75);
                    CleanupAndSave();
                    _forceClose = true;
                    await Dispatcher.InvokeAsync(Close);
                }
                finally { _closingRequested = false; }
                return;
            }
            CleanupAndSave();
        }

        private void CleanupAndSave()
        {
            try { if (_handoff != null) _handoff.Dispose(); } catch { }
            try
            {
                SaveWindowGeometry();
                _vm.SaveSettings();
            }
            catch { }
        }

        public void ApplyCliPaths(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0) return;
            Dispatcher.BeginInvoke(new Action(async () => await _vm.AddPathsAsync(paths)));
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            try
            {
                if (WindowState == WindowState.Maximized)
                {
                    // WindowChrome 最大化时去掉边框并让内容贴合工作区，避免超出屏幕
                    RootBorder.BorderThickness = new Thickness(0);
                    RootBorder.Margin = new Thickness(0);
                    var icon = BtnMax.Content as Icon;
                    if (icon != null) icon.Kind = IconKind.Restore;
                    BtnMax.ToolTip = "还原";
                }
                else
                {
                    RootBorder.BorderThickness = new Thickness(1);
                    RootBorder.Margin = new Thickness(0);
                    var icon = BtnMax.Content as Icon;
                    if (icon != null) icon.Kind = IconKind.Maximize;
                    BtnMax.ToolTip = "最大化";
                }
            }
            catch { }
        }

        // ═══════════════════ 分段控件 ═══════════════════

        private void SetupSegments()
        {
            UpdateFilterItems();
            SegFilter.SelectedIndexChanged += (s, e) => _vm.Queue.FilterIndex = SegFilter.SelectedIndex;

            SegLogFilter.SetItems(new List<SegOption> { new SegOption("全部"), new SegOption("错误") }, _vm.Log.Filter);
            SegLogFilter.SelectedIndexChanged += (s, e) =>
            {
                _vm.Log.Filter = SegLogFilter.SelectedIndex;
                _vm.Log.AutoFollow = true;
                FollowLog();
            };

            SegWriteMode.SetItems(new List<SegOption>
            {
                new SegOption("原 QC"),
                new SegOption("独立 _LOD.qc"),
                new SegOption("仅合集")
            }, _vm.LodPlan.WriteMode);
            SegWriteMode.SelectedIndexChanged += (s, e) =>
            {
                if (SegWriteMode.SelectedIndex != _vm.LodPlan.WriteMode)
                    _vm.OnWriteModeChanged(SegWriteMode.SelectedIndex);
            };
        }

        private void UpdateFilterItems()
        {
            SegFilter.SetItems(new List<SegOption>
            {
                new SegOption("全部", _vm.Queue.CountAll),
                new SegOption("未完成", _vm.Queue.CountUnfinished),
                new SegOption("完成", _vm.Queue.CountDone),
                new SegOption("异常", _vm.Queue.CountIssue),
                new SegOption("跳过", _vm.Queue.CountSkipped)
            }, _vm.Queue.FilterIndex);
        }

        // ═══════════════════ 预设下拉 ═══════════════════

        private void SetupPresets()
        {
            CmbPreset.ItemsSource = _vm.LodPlan.PresetNames;
            SyncPresetCombo();
        }

        /// <summary>把 ViewModel 的预设选择同步到 ComboBox（不触发生效逻辑）。</summary>
        private void SyncPresetCombo()
        {
            try
            {
                _syncingPreset = true;
                int idx = _vm.LodPlan.SelectedPresetIndex;
                if (idx >= 0 && idx < CmbPreset.Items.Count) CmbPreset.SelectedIndex = idx;
                else CmbPreset.SelectedIndex = -1;
            }
            catch { }
            finally { _syncingPreset = false; }
        }

        private void OnPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingPreset) return;
            int idx = CmbPreset.SelectedIndex;
            if (idx < 0) return;
            _vm.LodPlan.SelectedPresetIndex = idx;
            SyncPresetCombo();
        }

        // ═══════════════════ LOD 方案接线 ═══════════════════

        private void HookLodPlan()
        {
            _vm.LodPlan.PropertyChanged += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(_vm.LodPlan.SelectedPresetIndex))
                    Dispatcher.BeginInvoke(new Action(SyncPresetCombo));
                if (e.PropertyName == nameof(_vm.LodPlan.WriteMode))
                    SegWriteMode.SelectedIndex = _vm.LodPlan.WriteMode;
                if (e.PropertyName == nameof(_vm.LodPlan.IsAutomatic))
                {
                    if (_vm.LodPlan.IsAutomatic)
                        UiMotion.PlayViewEnter(AutomaticHint, AnimationsEnabled, 0, 12, 220);
                    else
                        UiMotion.PlayViewEnter(LodTableHost, AnimationsEnabled, 0, 12, 220);
                }
            };
        }

        // ═══════════════════ 日志展开/滚动 ═══════════════════

        private void OnToggleLogClick(object sender, RoutedEventArgs e)
        {
            _vm.IsLogExpanded = !_vm.IsLogExpanded;
        }

        private void ApplyLogExpanded(bool expanded, bool animate)
        {
            double target = expanded ? Math.Min(240, Math.Max(108, (RootBorder.ActualHeight - 200) * 0.34)) : 46;
            double from = LogCard.ActualHeight > 0 ? LogCard.ActualHeight : 46;
            UiMotion.CancelMotion(BtnToggleLog);
            LogCard.BeginAnimation(HeightProperty, null);
            LogCard.Height = target;
            bool useMotion = animate && AtelierMotionEnabled;
            UiMotion.AnimateRotate(LogChevron, expanded ? 180 : 0, useMotion, 220);
            if (!expanded && LogBody.IsKeyboardFocusWithin) BtnToggleLog.Focus();
            if (expanded) LogBody.Visibility = Visibility.Visible;

            if (!useMotion || Math.Abs(from - target) < 1)
            {
                if (!expanded) LogBody.Visibility = Visibility.Collapsed;
                FollowLog();
                return;
            }

            var anim = UiMotion.WithRefreshFrameRate(new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
            UiMotion.TrackAnimation(BtnToggleLog, anim, settle =>
            {
                LogCard.BeginAnimation(HeightProperty, null);
                if (settle && !_vm.IsLogExpanded) LogBody.Visibility = Visibility.Collapsed;
            });
            LogCard.BeginAnimation(HeightProperty, anim);
            if (expanded)
                UiMotion.PlayViewEnter(LogBody, AnimationsEnabled, 0, 8, 200);
            FollowLog();
        }

        private void FollowLog()
        {
            if (_logFollowQueued || !_vm.IsLogExpanded) return;
            _logFollowQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _logFollowQueued = false;
                if (!_vm.IsLogExpanded) return;
                if (_logScroll == null)
                {
                    _logScroll = FindChild<ScrollViewer>(LogList);
                    if (_logScroll != null) _logScroll.ScrollChanged += OnLogScrollChanged;
                }
                if (_vm.Log.AutoFollow && LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
            }));
        }

        private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange == 0 || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) return;
            _vm.Log.AutoFollow = _logScroll.VerticalOffset >= _logScroll.ScrollableHeight - 0.5;
        }

        private void OnResumeFollowClick(object sender, RoutedEventArgs e)
        {
            _vm.Log.AutoFollow = true;
            FollowLog();
            LogList.Focus();
        }

        private void OnCopyLogClick(object sender, RoutedEventArgs e) { _vm.Commands.CopyLog.Execute(null); }
        private void OnClearLogClick(object sender, RoutedEventArgs e) { _vm.Log.Clear(); }

        private void OnOpenErrorLogClick(object sender, RoutedEventArgs e)
        {
            if (_currentMainPage != 0) SelectMainPage(0);
            SegLogFilter.SelectedIndex = 1;
            _vm.IsLogExpanded = true;
            _vm.Log.AutoFollow = true;
            FollowLog();
            LogList.Focus();
        }

        private async void OnLogKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || !(LogList.SelectedItem is LogLineVm line)) return;
            e.Handled = true;
            await ShowInfoAsync("运行日志 · " + line.TimeText, "", line.Text);
        }

        // ═══════════════════ 队列交互 ═══════════════════

        private void OnQueueSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _vm.Queue.Selected = QueueList.SelectedItems.Cast<TaskItemVm>().ToList();
        }

        private async void OnQueueDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var item = FindAncestorTaskItem(e.OriginalSource as DependencyObject);
            if (item != null) await _vm.ShowTaskDetailsAsync(item);
        }

        private TaskItemVm FindAncestorTaskItem(DependencyObject d)
        {
            return d == null ? null : (ItemsControl.ContainerFromElement(QueueList, d) as ListBoxItem)?.DataContext as TaskItemVm;
        }

        private void OnQueueRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = FindAncestorTaskItem(e.OriginalSource as DependencyObject);
            if (item != null && !QueueList.SelectedItems.Contains(item)) QueueList.SelectedItem = item;
            ShowQueueMenu(PlacementMode.MousePoint);
            e.Handled = true;
        }

        private void OnQueueKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
            {
                ShowQueueMenu(PlacementMode.Bottom);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete) { _vm.RemoveSelected(); if (_vm.Queue.IsEmpty) FocusQueue(); e.Handled = true; }
            else if (e.Key == Key.Enter && !(e.Source is Button) && QueueList.SelectedItem is TaskItemVm item)
            {
                _ = _vm.ShowTaskDetailsAsync(item);
                e.Handled = true;
            }
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                QueueList.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.Control) { _vm.MoveSelected(-1); e.Handled = true; }
            else if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.Control) { _vm.MoveSelected(1); e.Handled = true; }
        }

        private void OnRevealTaskClick(object sender, RoutedEventArgs e)
        {
            var t = (sender as FrameworkElement)?.Tag as TaskItemVm;
            if (t != null) RevealInExplorer(t.Path);
            e.Handled = true;
        }

        private void OnRemoveTaskClick(object sender, RoutedEventArgs e)
        {
            var t = (sender as FrameworkElement)?.Tag as TaskItemVm;
            if (t == null) return;
            _vm.RemoveTask(t);
            FocusQueue();
            e.Handled = true;
        }

        private void ShowQueueMenu(PlacementMode placement)
        {
            var menu = NewMenu();
            AddImportMenuItems(menu);
            menu.Items.Add(new Separator { Style = (Style)FindResource("Menu.Separator") });
            var sel = QueueList.SelectedItem as TaskItemVm;
            menu.Items.Add(NewMenuItem("查看详情", IconKind.Info, "Enter", async (s, e) => await _vm.ShowTaskDetailsAsync(sel), sel != null));
            menu.Items.Add(NewMenuItem("在资源管理器中显示", IconKind.ExternalLink, null, (s, e) => RevealInExplorer(sel?.Path), sel != null));
            bool hasSelection = _vm.CanEdit && sel != null;
            menu.Items.Add(NewMenuItem("上移", IconKind.ArrowUp, "Ctrl+↑", (s, e) => _vm.MoveSelected(-1), hasSelection));
            menu.Items.Add(NewMenuItem("下移", IconKind.ArrowDown, "Ctrl+↓", (s, e) => _vm.MoveSelected(1), hasSelection));
            menu.Items.Add(NewMenuItem("移除选中项", IconKind.Trash, "Delete", (s, e) => _vm.RemoveSelected(), hasSelection));
            menu.Items.Add(new Separator { Style = (Style)FindResource("Menu.Separator") });
            menu.Items.Add(NewMenuItem("清空队列…", IconKind.Trash, null, async (s, e) =>
            {
                await _vm.ClearQueueAsync();
                if (_vm.Queue.IsEmpty) FocusQueue();
            }, _vm.CanEdit && _vm.Queue.HasItems));
            menu.PlacementTarget = QueueList;
            menu.Placement = placement;
            menu.IsOpen = true;
        }

        // ═══════════════════ 导入 / 计划 / LOD 面板 ═══════════════════

        private void OnImportMenuClick(object sender, RoutedEventArgs e)
        {
            var menu = NewMenu();
            AddImportMenuItems(menu);
            menu.PlacementTarget = BtnImport;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void AddImportMenuItems(ContextMenu menu)
        {
            menu.Items.Add(NewMenuItem("导入文件夹…", IconKind.Folder, "Ctrl+Shift+O", OnImportFolderClick, _vm.CanEdit));
            menu.Items.Add(NewMenuItem("导入文件…", IconKind.File, "Ctrl+O", OnImportFilesClick, _vm.CanEdit));
        }

        // 用 BeginInvoke 推迟到按钮完成"按下/抬起"状态处理之后再弹模态窗口，
        // 否则系统文件对话框的模态循环会让按钮的 IsPressed 卡住（关闭后仍显示内凹）。
        private void OnImportFolderClick(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => _vm.Commands.AddFolder.Execute(null)), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void OnImportFilesClick(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => _vm.Commands.AddFiles.Execute(null)), System.Windows.Threading.DispatcherPriority.Input);
        }
        private void OnClearSearchClick(object sender, RoutedEventArgs e)
        {
            ClearSearch();
            FocusSearch();
        }

        private void ClearSearch()
        {
            TxtSearch.Clear();
            _vm.Queue.SearchText = "";
        }

        private void OnResetFilterClick(object sender, RoutedEventArgs e)
        {
            SegFilter.SelectedIndex = 0;
            ClearSearch();
            FocusQueue();
        }

        private async void OnPreviewClick(object sender, RoutedEventArgs e)
        {
            if (_currentMainPage != 0) SelectMainPage(0, -1, true);
            await _vm.PreviewPlanAsync();
            if (FocusWasOnOperation()) (BtnClosePlan.IsVisible ? BtnClosePlan : BtnPreview).Focus();
        }

        private async void OnStartClick(object sender, RoutedEventArgs e) => await StartFromShellAsync();

        private async Task StartFromShellAsync()
        {
            if (!_vm.CanStart) return;
            if (_currentMainPage != 0) SelectMainPage(0, -1, true);
            await _vm.StartRunAsync();
            if (!FocusWasOnOperation()) return;
            if (BtnStart.IsEnabled) BtnStart.Focus();
            else if (BtnClosePlan.IsVisible) BtnClosePlan.Focus();
            else FocusQueue();
        }

        private bool FocusWasOnOperation()
        {
            if (!IsActive || Dialogs.IsOpen) return false;
            var focus = Keyboard.FocusedElement as UIElement;
            return focus == null || ReferenceEquals(focus, this) || !focus.IsVisible || !focus.IsEnabled ||
                   ReferenceEquals(focus, BtnStop);
        }
        private void OnStopClick(object sender, RoutedEventArgs e) { _vm.Commands.Stop.Execute(null); }
        private void OnOpenReportClick(object sender, RoutedEventArgs e) { _vm.Commands.OpenReport.Execute(null); }
        private void OnOpenFolderClick(object sender, RoutedEventArgs e) { _vm.Commands.OpenFolder.Execute(null); }
        private void OnRestorePresetClick(object sender, RoutedEventArgs e) { _vm.Commands.RestorePreset.Execute(null); }
        private void OnAddLevelClick(object sender, RoutedEventArgs e) { _vm.Commands.AddLevel.Execute(null); }
        private void OnAdvancedClick(object sender, RoutedEventArgs e) => SelectMainPage(1, 0, true);
        private void OnEnvClick(object sender, RoutedEventArgs e) => SelectMainPage(2, 1, true);

        // ── LOD 级别数字框：输入即生效、失焦整理格式、Enter 结束输入 ──
        private void OnLevelNumberGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var box = sender as TextBox;
            if (box != null) box.SelectAll();
        }

        private void OnLevelNumberLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            NormalizeLevelText(sender as TextBox);
        }

        private void OnLevelNumberKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (_vm.LodPlan.IsValid) NormalizeLevelText(sender as TextBox);
                e.Handled = true;
            }
        }

        private void NormalizeLevelText(TextBox box)
        {
            if (box == null) return;
            var level = box.Tag as LodLevelVm;
            if (level == null) return;
            try { _vm.LodPlan.NormalizeLevelText(level.Number - 1); } catch { }
        }
        private void OnClosePlanClick(object sender, RoutedEventArgs e)
        {
            _vm.ClosePlan();
            if (BtnPreview.IsEnabled) BtnPreview.Focus();
            else FocusQueue();
        }

        private async void OnPlanDetailsClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is PlanQcRowVm row) await ShowPlanDetailsAsync(row);
        }

        private async void OnPlanListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || e.Source is Button || !(PlanList.SelectedItem is PlanQcRowVm row)) return;
            e.Handled = true;
            await ShowPlanDetailsAsync(row);
        }

        private Task ShowPlanDetailsAsync(PlanQcRowVm row) =>
            ShowInfoAsync(row.ActionText + " · " + row.QcName, row.QcPath, row.Detail);

        private void OnRemoveLevelClick(object sender, RoutedEventArgs e)
        {
            var lv = (sender as FrameworkElement)?.Tag as LodLevelVm;
            if (lv == null) return;
            _vm.RemoveLevel(lv.Number - 1);
            FocusLodLevel(Math.Min(lv.Number - 1, _vm.LodPlan.Count - 1));
        }

        // ═══════════════════ 标题栏 / 菜单 / 主题 ═══════════════════

        private void OnMinClick(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }

        private void OnMaxClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) { Close(); }

        private void OnThemeClick(object sender, RoutedEventArgs e)
        {
            UiMotion.PlayThemeVeil(ThemeVeil, AtelierMotionEnabled);
            _vm.ToggleTheme();
        }

        private void UpdateThemeIcon()
        {
            try
            {
                bool dark = ThemeService.Current == ThemeKind.Dark;
                (BtnTheme.Content as Icon).Kind = dark ? IconKind.Sun : IconKind.Moon;
                NavThemeText.Text = dark ? "深色主题" : "浅色主题";
            }
            catch { }
        }

        private void OnMenuClick(object sender, RoutedEventArgs e)
        {

            // 键盘（空格/回车）激活按钮时走这里；鼠标点击在 OnWindowPreviewMouseDown 里统一处理
            ToggleMainMenu();
        }

        private void ToggleMainMenu()
        {
            if (_openMenu != null && _openMenu.IsOpen) { _openMenu.IsOpen = false; return; }
            ShowMainMenu();
        }

        /// <summary>点列表空白处：取消当前所有选中行。</summary>
        private void OnQueueListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                ListBoxItem item = ItemsControl.ContainerFromElement(QueueList, e.OriginalSource as DependencyObject) as ListBoxItem;
                if (item == null) QueueList.UnselectAll();
            }
            catch { }
        }

        /// <summary>点输入框外的空白区域时结束文本编辑。</summary>
        private void OnWindowPreviewMouseDownForSearch(object sender, MouseButtonEventArgs e)
        {
            try
            {
                DependencyObject src = e.OriginalSource as DependencyObject;
                if (Dialogs.IsOpen) return;
                var focusedBox = Keyboard.FocusedElement as TextBox;
                if (TxtSearch.IsKeyboardFocusWithin && IsWithin(SearchBox, src)) return;
                if (focusedBox == null) return;
                DependencyObject boundary = FindAncestorBorder(focusedBox) ?? (DependencyObject)focusedBox;
                if (boundary != null && IsWithin(boundary, src)) return;
                Keyboard.ClearFocus();
            }
            catch { }
        }

        /// <summary>向上找最近的 Border（输入框所在的单元格/搜索框外框），用来判断"点在自己格子里"。</summary>
        private static DependencyObject FindAncestorBorder(DependencyObject node)
        {
            while (node != null)
            {
                if (node is Border) return node;
                DependencyObject parent = null;
                try { parent = VisualTreeHelper.GetParent(node); } catch { }
                if (parent == null) { try { parent = LogicalTreeHelper.GetParent(node); } catch { } }
                node = parent;
            }
            return null;
        }

        private static bool IsWithin(DependencyObject ancestor, DependencyObject node)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, ancestor)) return true;
                DependencyObject parent = null;
                try { parent = VisualTreeHelper.GetParent(node); } catch { }
                if (parent == null) { try { parent = LogicalTreeHelper.GetParent(node); } catch { } }
                node = parent;
            }
            return false;
        }

        /// <summary>菜单打开时点窗口任意处：关闭菜单（这一下点击被 catcher 吃掉，符合下拉菜单的常规行为）。</summary>
        private void OnMenuCatcherClick(object sender, MouseButtonEventArgs e)
        {
            CloseMainMenu();
            e.Handled = true;
        }

        private void CloseMainMenu()
        {
            MenuCatcher.Visibility = Visibility.Collapsed;
            if (_openMenu != null && _openMenu.IsOpen) _openMenu.IsOpen = false;
            _openMenu = null;
        }

        private void ShowMainMenu()
        {
            if (_openMenu != null) { try { _openMenu.IsOpen = false; } catch { } _openMenu = null; }
            var menu = NewMenu();
            AddImportMenuItems(menu);
            menu.Items.Add(new Separator { Style = (Style)FindResource("Menu.Separator") });
            menu.Items.Add(NewMenuItem("打开策略中心", IconKind.Settings, "Ctrl+,", (s, e) => SelectMainPage(1, 0)));
            menu.Items.Add(NewMenuItem("打开维护与环境", IconKind.Shield, "Ctrl+3", (s, e) => SelectMainPage(2, 0)));
            menu.Items.Add(NewMenuItem("外观与光效设置", IconKind.Sun, "Ctrl+4", (s, e) => SelectMainPage(3, 0)));
            menu.Items.Add(NewMenuItem("独立设置窗口…", IconKind.Settings, null, (s, e) => OpenSettings(0)));
            menu.Items.Add(NewMenuItem("运行环境体检…", IconKind.Gauge, null, (s, e) => _vm.Commands.Diagnostics.Execute(null)));
            menu.Items.Add(new Separator { Style = (Style)FindResource("Menu.Separator") });
            menu.Items.Add(NewMenuItem("一键还原原始 QC…", IconKind.Refresh, null, async (s, e) =>
            {
                CloseMainMenu();
                await _vm.RestoreQcBackupsAsync();
            }, _vm.CanEdit));
            menu.Items.Add(NewMenuItem("清理未引用的旧 LOD 文件…", IconKind.Trash, null, async (s, e) =>
            {
                CloseMainMenu();
                await _vm.CleanOrphanLodFilesAsync();
            }, _vm.CanEdit));
            menu.Items.Add(new Separator { Style = (Style)FindResource("Menu.Separator") });

            menu.Items.Add(NewMenuItem("查看使用说明", IconKind.Report, null, (s, e) => _vm.Commands.OpenManual.Execute(null)));
            menu.Items.Add(NewMenuItem("查看快捷键…", IconKind.Terminal, "F1", (s, e) => _vm.Commands.Help.Execute(null)));
            menu.Items.Add(NewMenuItem("关于本工具…", IconKind.Info, null, (s, e) => _vm.Commands.About.Execute(null)));
            menu.Items.Add(new Separator { Style = (Style)FindResource("Menu.Separator") });
            menu.Items.Add(NewMenuItem("打开设置文件夹", IconKind.Folder, null, (s, e) => RevealInExplorer(AppInfo.SettingsDir)));
            if (!RuntimeContext.IsAdmin)
            {
                menu.Items.Add(new Separator { Style = (Style)FindResource("Menu.Separator") });
                menu.Items.Add(NewMenuItem("以管理员身份重启", IconKind.Shield, null, (s, e) => RestartAsAdmin()));
            }

            // 系统不自动关闭（StaysOpen=true），关闭由 catcher / 菜单项 / Esc 统一控制，避免"先关又开"。
            menu.StaysOpen = true;
            menu.Closed += delegate
            {
                if (ReferenceEquals(_openMenu, menu)) { _openMenu = null; MenuCatcher.Visibility = Visibility.Collapsed; }
            };
            menu.PlacementTarget = BtnMenu;
            menu.Placement = PlacementMode.Bottom;
            MenuCatcher.Visibility = Visibility.Visible;
            menu.IsOpen = true;
            _openMenu = menu;
        }

        private void RestartAsAdmin()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = System.Reflection.Assembly.GetExecutingAssembly().Location,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                var args = new System.Text.StringBuilder();
                foreach (var p in Vm.Queue.Items.Take(8)) args.Append('"').Append(p.Path).Append("\" ");
                psi.Arguments = args.ToString();
                Process.Start(psi);
                Close();
            }
            catch (Exception ex)
            {
                ShowToast("无法以管理员身份重启", ex.Message, ToastTone.Err);
            }
        }

        private ContextMenu NewMenu()
        {
            return new ContextMenu { Style = (Style)FindResource("Menu.Base") };
        }

        private MenuItem NewMenuItem(string header, IconKind icon, string shortcut, RoutedEventHandler onClick, bool enabled = true)
        {
            var item = new MenuItem
            {
                Header = header,
                IsEnabled = enabled,
                Style = (Style)FindResource("Menu.Item"),
                InputGestureText = shortcut,
                Icon = new Icon { Kind = icon, Size = 16 }
            };
            if (onClick != null) item.Click += onClick;
            return item;
        }

        private MenuItem NewCheckItem(string header, bool isChecked, RoutedEventHandler onClick)
        {
            var item = new MenuItem
            {
                Header = header,
                Style = (Style)FindResource("Menu.Item"),
                IsCheckable = true,
                IsChecked = isChecked,
                StaysOpenOnClick = false
            };
            if (onClick != null) item.Click += onClick;
            return item;
        }

        // ═══════════════════ 拖放 ═══════════════════

        private void SetDropOverlayVisible(bool show)
        {
            if (show)
            {
                if (DropOverlay.Visibility != Visibility.Visible)
                {
                    DropOverlay.Visibility = Visibility.Visible;
                    UiMotion.AnimateBackdropBlur(RootBorder, 7.0, AnimationsEnabled, 160);
                    UiMotion.PlayViewEnter(DropCard, AnimationsEnabled, 0, 14, 200);
                }
            }
            else if (DropOverlay.Visibility == Visibility.Visible)
            {
                DropOverlay.Visibility = Visibility.Collapsed;
                if (!Dialogs.IsOpen)
                    UiMotion.AnimateBackdropBlur(RootBorder, 0.0, AnimationsEnabled, 160);
            }
        }

        private void OnDragEnterWindow(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (!_vm.CanEdit || Dialogs.IsOpen) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            SetDropOverlayVisible(true);
            try
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                DropHint.Text = files != null && files.Length > 1
                    ? "这次拖入了 " + files.Length + " 项，支持工程文件夹、QC 和模型文件"
                    : "支持求生之路 2 工程文件夹、QC、GLB、SMD 与 DMX 模型";
            }
            catch { }
            e.Handled = true;
        }

        private void OnDragOverWindow(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { SetDropOverlayVisible(false); return; }
            bool canDrop = _vm.CanEdit && !Dialogs.IsOpen;
            e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
            SetDropOverlayVisible(canDrop);
            e.Handled = true;
        }

        private void OnDragLeaveWindow(object sender, DragEventArgs e)
        {
            SetDropOverlayVisible(false);
        }

        private async void OnDropWindow(object sender, DragEventArgs e)
        {
            SetDropOverlayVisible(false);
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;
            if (!_vm.CanEdit || Dialogs.IsOpen) return;
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null && paths.Length > 0) await _vm.AddPathsAsync(paths);
        }

        // ═══════════════════ 快捷键 ═══════════════════

        private async void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Dialogs.IsOpen)
            {
                if (!Dialogs.IsKeyboardFocusWithin)
                {
                    if (e.Key == Key.Escape) Dialogs.Dismiss();
                    else Dialogs.FocusDialog();
                    e.Handled = true;
                }
                return;
            }
            if (CmbPreset.IsDropDownOpen) return;

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (ctrl && e.Key >= Key.D1 && e.Key <= Key.D4)
            {
                SelectMainPage(e.Key - Key.D1);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F5) { e.Handled = true; await StartFromShellAsync(); return; }
            if (e.Key == Key.F1) { e.Handled = true; await _vm.ShowShortcutHelpAsync(); return; }
            if (e.Key == Key.Escape)
            {
                if (_openMenu != null && _openMenu.IsOpen) { CloseMainMenu(); e.Handled = true; return; }
                if (!_vm.CanEdit) { e.Handled = true; await _vm.StopAsync(); return; }
                if (Keyboard.FocusedElement is TextBox number && number.Tag is LodLevelVm && !_vm.LodPlan.IsValid)
                {
                    NormalizeLevelText(number);
                    e.Handled = true;
                    return;
                }
                if (_vm.IsPlanVisible) { OnClosePlanClick(null, null); e.Handled = true; return; }
                if (TxtSearch.IsKeyboardFocusWithin) { ClearSearch(); FocusQueue(); e.Handled = true; return; }
                if (_vm.Queue.IsFiltered) { OnResetFilterClick(null, null); e.Handled = true; return; }
                return;
            }
            if (ctrl && e.Key == Key.O)
            {
                if (shift) OnImportFolderClick(null, null);
                else OnImportFilesClick(null, null);
                e.Handled = true; return;
            }
            if (ctrl && e.Key == Key.F) { FocusSearch(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.L)
            {
                if (_currentMainPage != 0)
                {
                    SelectMainPage(0);
                    _vm.IsLogExpanded = true;
                }
                else _vm.IsLogExpanded = !_vm.IsLogExpanded;
                e.Handled = true;
                return;
            }
            if (ctrl && e.Key == Key.OemComma) { SelectMainPage(1, 0, true); e.Handled = true; return; }
            if (e.Key == Key.Enter && TxtSearch.IsKeyboardFocusWithin)
            {
                TxtSearch.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                if (QueueList.Items.Count > 0)
                {
                    if (QueueList.SelectedIndex < 0) QueueList.SelectedIndex = 0;
                    QueueList.ScrollIntoView(QueueList.SelectedItem);
                    QueueList.UpdateLayout();
                    var selected = QueueList.ItemContainerGenerator.ContainerFromItem(QueueList.SelectedItem) as ListBoxItem;
                    if (selected != null) selected.Focus();
                    else QueueList.Focus();
                }
                e.Handled = true;
            }
        }

        public void FocusSearch()
        {
            if (_currentMainPage != 0) SelectMainPage(0);
            if (_vm.IsPlanVisible) _vm.ClosePlan();
            if (!TxtSearch.IsVisible) { if (!_vm.IsPlanVisible) FocusQueue(); return; }
            TxtSearch.Focus();
            TxtSearch.SelectAll();
        }

        private void FocusQueue()
        {
            if (QueueList.IsVisible) QueueList.Focus();
            else if (BtnEmptyImportFolder.IsVisible) BtnEmptyImportFolder.Focus();
        }

        public void FocusLodLevel(int levelIndex)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var container = LevelList.ItemContainerGenerator.ContainerFromIndex(levelIndex) as DependencyObject;
                var tb = FindChild<TextBox>(container);
                if (tb?.IsVisible != true || !tb.IsEnabled || Dialogs.IsOpen) return;
                tb.BringIntoView();
                tb.Focus();
                tb.SelectAll();
            }));
        }

        // ═══════════════════ IShellInteraction ═══════════════════

        public Task<bool> ConfirmAsync(string title, string message, string okText, string cancelText, bool danger, string detail)
        {
            return Dialogs.ConfirmAsync(new DialogOptions
            {
                Title = title,
                Message = message,
                OkText = okText,
                CancelText = cancelText,
                Danger = danger,
                Detail = detail
            });
        }

        public Task ShowInfoAsync(string title, string message, string detail)
        {
            return Dialogs.ShowInfoAsync(new DialogOptions { Title = title, Message = message, Detail = detail });
        }

        public void ShowToast(string title, string subtitle, ToastTone tone)
        {
            Toasts.Show(title, subtitle, tone);
        }

        public Task<IReadOnlyList<string>> PickFilesAsync()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                CheckFileExists = true,
                Title = "选择要导入的 QC 或模型文件（可多选）",
                Filter = "支持的文件 (*.qc;*.glb;*.smd;*.dmx)|*.qc;*.glb;*.smd;*.dmx|QC 脚本 (*.qc)|*.qc|模型 (*.glb;*.smd;*.dmx)|*.glb;*.smd;*.dmx|所有文件 (*.*)|*.*"
            };
            var result = new List<string>();
            if (dlg.ShowDialog(this) == true && dlg.FileNames != null) result.AddRange(dlg.FileNames);
            return Task.FromResult<IReadOnlyList<string>>(result);
        }

        public Task<string> PickFolderAsync()
        {
            // 与「导入文件」同一类系统对话框（Vista+ 资源管理器风格，可粘贴地址）：
            // IFileOpenDialog + FOS_PICKFOLDERS；不再使用老式 FolderBrowserDialog，也不自绘窗口。
            string result = ModernFolderPicker.Pick(this, "选择工程文件夹", _lastFolderPickDir);
            if (!string.IsNullOrEmpty(result)) _lastFolderPickDir = result;
            return Task.FromResult(result);
        }

        public Task<string> PickFileAsync(string title, string filter, string initialDir)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
            if (!string.IsNullOrEmpty(initialDir)) { try { dlg.InitialDirectory = initialDir; } catch { } }
            return Task.FromResult(dlg.ShowDialog(this) == true ? dlg.FileName : null);
        }

        public void RevealInExplorer(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
                else if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(path) + "\"");
            }
            catch (Exception ex) { ShowToast("打开失败", ex.Message, ToastTone.Err); }
        }

        public void OpenFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { ShowToast("文件不存在", path, ToastTone.Err); return; }
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex) { ShowToast("打开失败", ex.Message, ToastTone.Err); }
        }

        public void OpenSettings(int page)
        {
            if (Dialogs.IsOpen) return;
            try
            {
                if (_settingsWindow == null)
                {
                    _settingsWindow = new SettingsWindow(_vm.Settings, _vm) { Owner = this };
                    _settingsWindow.Closed += delegate { _settingsWindow = null; };
                    _settingsWindow.Show();
                }
                else
                {
                    if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
                    _settingsWindow.Activate();
                }
                _settingsWindow.SelectPage(page);
            }
            catch (Exception ex)
            {
                ShowToast("打开设置失败", ex.Message, ToastTone.Err);
            }
        }

        public void FlashWindow()
        {
            try
            {
                if (IsActive) return;
                var fi = new FLASHWINFO
                {
                    hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle,
                    dwFlags = 0x00000002 | 0x00000004,   // FLASHW_ALL | FLASHW_TIMERNOFG
                    uCount = 3,
                    dwTimeout = 0
                };
                FlashWindowEx(ref fi);
            }
            catch { }
        }

        public bool AnimationsEnabled
        {
            get { try { return _settings != null && _settings.Animations && SystemParameters.ClientAreaAnimation; } catch { return true; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        // ═══════════════════ 视觉树工具 ═══════════════════

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var hit = child as T;
                if (hit != null) return hit;
                var deeper = FindChild<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }
    }
}
