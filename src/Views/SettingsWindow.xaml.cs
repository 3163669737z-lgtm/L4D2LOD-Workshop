using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LodStudio.Controls;
using LodStudio.Engine;
using LodStudio.Services;
using LodStudio.ViewModels;
using LodStudio.Views.Settings;

namespace LodStudio.Views
{
    /// <summary>左导航的一项（图标 + 12.5 号文字，44 高）。</summary>
    public sealed class NavEntry
    {
        public IconKind Icon { get; private set; }
        public string Title { get; private set; }

        public NavEntry(IconKind icon, string title)
        {
            Icon = icon;
            Title = title;
        }
    }

    /// <summary>
    /// 设置窗口（非模态，由主窗口以 Owner 方式打开）：
    /// 自绘标题栏（48）+ 左导航 208 + 右侧滚动内容（页边 24、最大宽 620）+ 底栏 56。
    ///
    /// 数据流：任何控件改动 → 写 <see cref="Settings"/> 字段 → SettingsStore.Save + shell.OnSettingsChanged()
    /// → 标题栏右侧「已保存」脉冲（900ms，尊重动效开关）。窗口本身不持久化任何新字段，
    /// 上次页签只存在进程内的静态字段里。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        /// <summary>上次打开的页签：只在本次进程内有效（不写进 settings.ini）。</summary>
        private static int _lastPageIndex;

        private readonly AppSettings _settings;
        private readonly ShellViewModel _shell;
        private readonly List<SettingsPageBase> _pages = new List<SettingsPageBase>();
        private readonly DispatcherTimer _savedTimer;

        private SettingsPageEngine _pageEngine;
        private SettingsPageGeneral _pageGeneral;
        private SettingsPageAdvanced _pageAdvanced;
        private SettingsPageAppearance _pageAppearance;
        private SettingsPageDiagnostics _pageDiagnostics;

        private bool _engineProbeBusy;

        public SettingsWindow(AppSettings settings, ShellViewModel shell)
        {
            InitializeComponent();

            _settings = settings ?? (shell != null ? shell.Settings : new AppSettings());
            _shell = shell;

            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute)); }
            catch { }

            _savedTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(900)
            };
            _savedTimer.Tick += OnSavedTimerTick;

            BuildNav();
            BuildPages();

            NavList.SelectionChanged += OnNavSelectionChanged;
            KeyDown += OnWindowKeyDown;
            Loaded += OnWindowLoaded;
            Closed += OnWindowClosed;
            StateChanged += OnWindowStateChanged;
            ThemeService.ThemeChanged += OnThemeChanged;

            int page = Math.Max(0, Math.Min(_pages.Count - 1, _lastPageIndex));
            NavList.SelectedIndex = page;
            ShowPage(page);
        }

        // ─────────────────────────── 公开状态 / 协作 ───────────────────────────

        public AppSettings Settings { get { return _settings; } }
        public ShellViewModel Shell { get { return _shell; } }

        /// <summary>引擎页探测到的 Blender 版本（例如 "Blender 4.1.1"）；诊断页会用到。</summary>
        public string BlenderVersion { get; private set; }

        /// <summary>引擎页是否正在后台探测版本（预览脚本用它等待状态稳定）。</summary>
        public bool EngineProbeBusy { get { return _engineProbeBusy; } }

        /// <summary>切到指定页（0 引擎 / 1 常规 / 2 LOD 高级 / 3 外观 / 4 诊断）。</summary>
        public void SelectPage(int page)
        {
            if (_pages.Count == 0) return;
            int index = Math.Max(0, Math.Min(_pages.Count - 1, page));
            if (NavList.SelectedIndex != index) NavList.SelectedIndex = index;
            else ShowPage(index);
        }

        /// <summary>当前页内容的总高（DIP）——预览脚本用来核对"能不能一屏放下"。</summary>
        public double PageContentHeight
        {
            get { try { return PageHost.DesiredSize.Height; } catch { return 0; } }
        }

        /// <summary>内容区可视高度（DIP）。</summary>
        public double PageViewportHeight
        {
            get { try { return PageScroll.ViewportHeight; } catch { return 0; } }
        }

        /// <summary>把当前页滚到指定偏移（设置页较长时用）。</summary>
        public void ScrollPageTo(double offset)
        {
            try
            {
                PageScroll.ScrollToVerticalOffset(offset);
                PageScroll.UpdateLayout();
                UiCapture.DoEvents(1);
            }
            catch { }
        }

        /// <summary>把当前页滚到底。仅测试/预览脚本使用。</summary>
        public void ScrollPageToEnd()
        {
            try
            {
                PageScroll.ScrollToEnd();
                PageScroll.UpdateLayout();
                UiCapture.DoEvents(1);
            }
            catch { }
        }

        internal void SetEngineProbeBusy(bool busy)
        {
            _engineProbeBusy = busy;
        }

        internal void SetBlenderVersion(string version)
        {
            BlenderVersion = version ?? "";
        }

        /// <summary>让引擎页在需要时后台探测一次 Blender 版本（诊断页会调用，保证报告里有版本号）。</summary>
        public void EnsureEngineProbe()
        {
            if (_pageEngine != null) _pageEngine.EnsureProbe();
        }

        /// <summary>引擎页探测结束：把版本号同步给诊断页的环境行。</summary>
        internal void OnEngineProbeFinished()
        {
            if (_pageDiagnostics != null) _pageDiagnostics.SyncFromSettings();
        }

        /// <summary>运行诊断页的诊断（预览脚本用；主窗口自己的诊断走 shell.RunDiagnosticsAsync）。</summary>
        public Task RunDiagnosticsAsync()
        {
            if (_pageDiagnostics == null) return Task.Delay(0);
            return _pageDiagnostics.RunDiagnosticsAsync();
        }

        /// <summary>统一保存路径：写盘 + 通知主窗口 + 闪「已保存」。</summary>
        internal void Apply(bool warn)
        {
            if (_settings == null) return;
            try { SettingsStore.Save(_settings); } catch { }
            try { if (_shell != null) _shell.OnSettingsChanged(); } catch { }
            FlashSaved(warn);
        }

        // ─────────────────────────── 初始化 ───────────────────────────

        private void BuildNav()
        {
            NavList.ItemsSource = new List<NavEntry>
            {
                new NavEntry(IconKind.Gauge, "引擎"),
                new NavEntry(IconKind.Settings, "常规"),
                new NavEntry(IconKind.Layers, "LOD 高级"),
                new NavEntry(IconKind.Sun, "外观"),
                new NavEntry(IconKind.Shield, "诊断")
            };
        }

        private void BuildPages()
        {
            _pageEngine = new SettingsPageEngine();
            _pageGeneral = new SettingsPageGeneral();
            _pageAdvanced = new SettingsPageAdvanced();
            _pageAppearance = new SettingsPageAppearance();
            _pageDiagnostics = new SettingsPageDiagnostics();

            _pages.Add(_pageEngine);
            _pages.Add(_pageGeneral);
            _pages.Add(_pageAdvanced);
            _pages.Add(_pageAppearance);
            _pages.Add(_pageDiagnostics);

            foreach (var page in _pages)
            {
                page.Visibility = Visibility.Collapsed;
                PageHost.Children.Add(page);
                page.Attach(this);
            }
        }

        private void ShowPage(int index)
        {
            if (index < 0 || index >= _pages.Count) return;
            for (int i = 0; i < _pages.Count; i++)
                _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            try { PageScroll.ScrollToTop(); } catch { }
            _pages[index].OnPageShown();
            _lastPageIndex = index;
        }

        /// <summary>主题变化（含主窗口的菜单/快捷键切换）后，所有页同步一次控件状态与色块数值。</summary>
        private void OnThemeChanged(object sender, EventArgs e)
        {
            for (int i = 0; i < _pages.Count; i++)
            {
                try { _pages[i].SyncFromSettings(); } catch { }
            }
        }

        private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NavList.SelectedIndex < 0) return;
            ShowPage(NavList.SelectedIndex);
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            // 键盘优先：打开即可用 ↑↓ 切页
            try { NavList.Focus(); } catch { }
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            try { ThemeService.ThemeChanged -= OnThemeChanged; } catch { }
            try { _savedTimer.Stop(); } catch { }
        }

        private void OnWindowStateChanged(object sender, EventArgs e)
        {
            try
            {
                if (WindowState == WindowState.Maximized)
                {
                    RootBorder.BorderThickness = new Thickness(0);
                    RootBorder.Margin = new Thickness(7, 7, 7, 7);
                }
                else
                {
                    RootBorder.BorderThickness = new Thickness(1);
                    RootBorder.Margin = new Thickness(0);
                }
            }
            catch { }
        }

        // ─────────────────────────── 键盘 / 按钮 ───────────────────────────

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled || e.Key != Key.Escape) return;
            // 下拉框展开时 Esc 已被下拉框自己处理（不会走到这里）；这里只关窗口
            e.Handled = true;
            Close();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnOpenSettingsFolderClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_shell != null) _shell.OpenSettingsFolder();
                else Process.Start("explorer.exe", "\"" + AppInfo.SettingsDir + "\"");
            }
            catch (Exception ex)
            {
                if (_shell != null) _shell.Toast("打开设置文件夹失败", ex.Message, ToastTone.Err);
            }
        }

        // ─────────────────────────── 「已保存」脉冲 ───────────────────────────

        private bool AnimationsEnabled
        {
            get { try { return _settings != null && _settings.Animations && SystemParameters.ClientAreaAnimation; } catch { return true; } }
        }

        /// <summary>标题栏右侧闪一下「已保存」：900ms，warn=true 用 Warn 色（关备份/开覆盖等需要留意的改动）。</summary>
        internal void FlashSaved(bool warn)
        {
            _savedTimer.Stop();
            SavedText.Text = warn ? "已保存 · 注意" : "已保存";
            SavedPill.SetResourceReference(Border.BackgroundProperty, warn ? "Brush.WarnSoft" : "Brush.OkSoft");
            SavedText.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Brush.Warn" : "Brush.Ok");
            SavedIcon.Kind = warn ? IconKind.Warning : IconKind.Check;
            SavedIcon.SetResourceReference(LodStudio.Controls.Icon.IconBrushProperty, warn ? "Brush.Warn" : "Brush.Ok");

            SavedPill.BeginAnimation(OpacityProperty, null);
            if (AnimationsEnabled)
                SavedPill.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90)));
            else
                SavedPill.Opacity = 1;
            _savedTimer.Start();
        }

        private void OnSavedTimerTick(object sender, EventArgs e)
        {
            _savedTimer.Stop();
            if (!AnimationsEnabled) { SavedPill.Opacity = 0; return; }
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
            fade.Completed += delegate
            {
                SavedPill.BeginAnimation(OpacityProperty, null);
                SavedPill.Opacity = 0;
            };
            SavedPill.BeginAnimation(OpacityProperty, fade);
        }

        // ─────────────────────────── 数字输入框（各页共用） ───────────────────────────

        /// <summary>
        /// 给数字框挂上"只能输数字、上下键增减（Shift ×10）、失焦/回车提交并按范围夹紧"的行为。
        /// 非法输入在失焦时回退到当前值，并把边框闪成 Err 色。
        /// </summary>
        internal void AttachNumberBox(TextBox box, int min, int max, Func<int> get, Action<int> set, bool warn)
        {
            if (box == null || get == null || set == null) return;
            bool updating = false;
            Action commit = null;
            commit = delegate
            {
                if (updating) return;
                updating = true;
                try
                {
                    string raw = (box.Text ?? "").Trim();
                    int parsed;
                    bool ok = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
                    int value = ok ? parsed : get();
                    int clamped = Math.Max(min, Math.Min(max, value));
                    bool invalid = !ok || clamped != value;
                    box.Text = clamped.ToString(CultureInfo.InvariantCulture);
                    if (invalid) box.SetResourceReference(Control.BorderBrushProperty, "Brush.Err");
                    else box.ClearValue(Control.BorderBrushProperty);
                    if (clamped != get())
                    {
                        set(clamped);
                        Apply(warn);
                    }
                }
                finally { updating = false; }
            };

            box.PreviewTextInput += delegate(object sender, TextCompositionEventArgs e)
            {
                foreach (char c in e.Text)
                {
                    if (!char.IsDigit(c)) { e.Handled = true; return; }
                }
            };
            box.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Up || e.Key == Key.Down)
                {
                    int current;
                    if (!int.TryParse((box.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out current))
                        current = get();
                    int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                    int next = e.Key == Key.Up ? current + step : current - step;
                    next = Math.Max(min, Math.Min(max, next));
                    box.Text = next.ToString(CultureInfo.InvariantCulture);
                    commit();
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    commit();
                    e.Handled = true;
                }
            };
            box.LostFocus += delegate { commit(); };
            box.GotKeyboardFocus += delegate { box.SelectAll(); };
        }
    }
}
