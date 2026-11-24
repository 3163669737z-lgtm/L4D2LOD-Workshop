using System;
using System.Collections.Generic;
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

namespace LodStudio.Views
{
    /// <summary>
    /// 主窗口：自绘标题栏、三块区域（队列 / LOD 方案 / 日志）、HUD、覆盖层（拖放 / 对话框 / Toast）。
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

        private ContextMenu _openMenu;
        private string _lastFolderPickDir;
        private SettingsWindow _settingsWindow;
        private bool _syncingPreset;

        public ShellWindow() : this(true) { }

        internal ShellWindow(bool fitWorkArea)
        {
            Engine.PerfTrace.Mark("shell-ctor-start");
            InitializeComponent();
            Engine.PerfTrace.Mark("shell-xaml");
            try
            {
                Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
            }
            catch { }

            _settings = SettingsStore.Load();
            Engine.PerfTrace.Mark("shell-settings");
            _vm = new ShellViewModel(_settings, this);
            Engine.PerfTrace.Mark("shell-vm");
            DataContext = _vm;

            SetupSegments();
            SetupPresets();
            HookLodPlan();
            HookVm();
            ApplyWindowGeometry();
            UpdateThemeIcon();
            ApplyLogExpanded(_settings.LogExpanded, false);
            Engine.PerfTrace.Mark("shell-setup");

            Loaded += OnLoaded;
            if (fitWorkArea) WindowWorkArea.Track(this);
            RootBorder.SizeChanged += OnWorkspaceSizeChanged;
            Closing += OnClosing;
            StateChanged += OnStateChanged;
            PreviewKeyDown += OnWindowPreviewKeyDown;
            PreviewMouseDown += OnWindowPreviewMouseDownForSearch;

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

        private void HookVm()
        {
            _vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_vm.IsLogExpanded))
                    ApplyLogExpanded(_vm.IsLogExpanded, true);
            };
            _vm.Queue.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_vm.Queue.CountAll)) UpdateFilterItems();
                if (e.PropertyName == nameof(_vm.Queue.FilterIndex)) SegFilter.SelectedIndex = _vm.Queue.FilterIndex;
            };
            _vm.Log.Lines.CollectionChanged += (s, e) => FollowLog();
            _vm.Log.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_vm.Log.Filter)) SegLogFilter.SelectedIndex = _vm.Log.Filter;
            };
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
                double planWidth = Math.Max(318, Math.Min(400, width * 0.325));
                PlanColumn.Width = new GridLength(planWidth);
                Toasts.Width = Math.Min(360, planWidth - 8);
                double queueWidth = width - planWidth - 56;
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
                    RootBorder.Margin = new Thickness(7, 7, 7, 7);
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
            };
        }

        // ═══════════════════ 日志展开/滚动 ═══════════════════

        private void OnToggleLogClick(object sender, RoutedEventArgs e)
        {
            _vm.IsLogExpanded = !_vm.IsLogExpanded;
        }

        private void ApplyLogExpanded(bool expanded, bool animate)
        {
            double target = expanded ? Math.Min(240, Math.Max(104, (RootBorder.ActualHeight - 200) * 0.34)) : 44;
            double from = LogCard.ActualHeight > 0 ? LogCard.ActualHeight : 44;
            LogCard.BeginAnimation(HeightProperty, null);
            LogCard.Height = target;
            LogChevron.RenderTransformOrigin = new Point(0.5, 0.5);
            LogChevron.RenderTransform = new RotateTransform(expanded ? 180 : 0);
            if (!expanded && LogBody.IsKeyboardFocusWithin) BtnToggleLog.Focus();
            if (expanded) LogBody.Visibility = Visibility.Visible;

            if (!animate || !AnimationsEnabled || Math.Abs(from - target) < 1)
            {
                if (!expanded) LogBody.Visibility = Visibility.Collapsed;
                FollowLog();
                return;
            }

            var anim = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            anim.Completed += (s, e) => { if (!_vm.IsLogExpanded) LogBody.Visibility = Visibility.Collapsed; };
            LogCard.BeginAnimation(HeightProperty, anim);
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
            await _vm.PreviewPlanAsync();
            if (FocusWasOnOperation()) (BtnClosePlan.IsVisible ? BtnClosePlan : BtnPreview).Focus();
        }

        private async void OnStartClick(object sender, RoutedEventArgs e) => await StartFromShellAsync();

        private async Task StartFromShellAsync()
        {
            if (!_vm.CanStart) return;
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
        private void OnAdvancedClick(object sender, RoutedEventArgs e) { OpenSettings(2); }
        private void OnEnvClick(object sender, RoutedEventArgs e) { OpenSettings(4); }

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
            _vm.ToggleTheme();
            UpdateThemeIcon();
        }

        private void UpdateThemeIcon()
        {
            try
            {
                bool dark = ThemeService.Current == ThemeKind.Dark;
                (BtnTheme.Content as Icon).Kind = dark ? IconKind.Sun : IconKind.Moon;
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
            menu.Items.Add(NewMenuItem("打开设置…", IconKind.Settings, "Ctrl+,", (s, e) => OpenSettings(0)));
            menu.Items.Add(NewMenuItem("运行环境诊断…", IconKind.Gauge, null, (s, e) => _vm.Commands.Diagnostics.Execute(null)));
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

        private void OnDragEnterWindow(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (!_vm.CanEdit || Dialogs.IsOpen) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            DropOverlay.Visibility = Visibility.Visible;
            try
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                DropHint.Text = files != null && files.Length > 1
                    ? "松开加入 " + files.Length + " 项（工程文件夹 / QC / GLB / SMD / DMX）"
                    : "支持工程文件夹 / QC / GLB / SMD / DMX";
            }
            catch { }
            e.Handled = true;
        }

        private void OnDragOverWindow(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { DropOverlay.Visibility = Visibility.Collapsed; return; }
            bool canDrop = _vm.CanEdit && !Dialogs.IsOpen;
            e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
            DropOverlay.Visibility = canDrop ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        private void OnDragLeaveWindow(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        }

        private async void OnDropWindow(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
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
            if (ctrl && e.Key == Key.L) { _vm.IsLogExpanded = !_vm.IsLogExpanded; e.Handled = true; return; }
            if (ctrl && e.Key == Key.OemComma) { OpenSettings(0); e.Handled = true; return; }
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
                Title = "导入文件（QC / GLB / SMD / DMX，可多选）",
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
