using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LodStudio.Engine;
using LodStudio.ViewModels;

namespace LodStudio.Views
{
    /// <summary>选择器里的一项（勾选状态变化要通知界面，才能点整行切换）。</summary>
    public sealed class SkipPickItem : INotifyPropertyChanged
    {
        private bool _isChecked;

        public string FullPath { get; private set; }
        /// <summary>相对根目录的显示路径（同一工程时更短、更好扫）。</summary>
        public string Display { get; private set; }

        public bool IsChecked
        {
            get { return _isChecked; }
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                var h = PropertyChanged;
                if (h != null) h(this, new PropertyChangedEventArgs("IsChecked"));
            }
        }

        public SkipPickItem(string fullPath, string display, bool isChecked)
        {
            FullPath = fullPath;
            Display = display;
            _isChecked = isChecked;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// 跳过主模型选择器（模态对话框，Owner = 设置窗口）：
    /// 顶部搜索 + 全选/全不选 + 计数；列表勾选写回 settings.SkipMainQcs（由设置页去重保存）。
    /// 候选来自 shell.CollectQcCandidatesAsync()（队列里能找到的根 QC + 已保存清单）。
    /// </summary>
    public partial class SkipMainPickerWindow : Window
    {
        private readonly SettingsWindow _owner;
        private readonly ShellViewModel _shell;
        private readonly List<string> _preloaded;
        private readonly List<string> _preselected;
        private readonly List<SkipPickItem> _all = new List<SkipPickItem>();
        private readonly ObservableCollection<SkipPickItem> _view = new ObservableCollection<SkipPickItem>();

        private bool _dialogMode;
        private bool _loading;
        private string _loadError;

        /// <summary>确定后有效的选中路径（绝对路径，未去重；由调用方去重排序）。</summary>
        public List<string> SelectedPaths { get; private set; }

        /// <summary>从设置窗口打开：候选与已选清单都从 shell / settings 取（异步加载）。</summary>
        public SkipMainPickerWindow(SettingsWindow owner) : this()
        {
            _owner = owner;
            _preselected = owner != null && owner.Settings.SkipMainQcs != null
                ? new List<string>(owner.Settings.SkipMainQcs)
                : new List<string>();
            if (owner != null)
            {
                Owner = owner;
                Services.WindowWorkArea.Track(this);
            }
        }

        public SkipMainPickerWindow(Window owner, ShellViewModel shell, AppSettings settings) : this()
        {
            Owner = owner;
            _shell = shell;
            _preselected = settings?.SkipMainQcs != null ? new List<string>(settings.SkipMainQcs) : new List<string>();
            if (owner != null || shell != null) Services.WindowWorkArea.Track(this);
        }

        /// <summary>直接给数据（预览/测试用；不依赖 shell）。</summary>
        public SkipMainPickerWindow(IEnumerable<string> candidates, IEnumerable<string> selected) : this()
        {
            _preloaded = candidates == null
                ? new List<string>()
                : candidates.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            _preselected = selected == null
                ? new List<string>()
                : selected.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        }

        private SkipMainPickerWindow()
        {
            InitializeComponent();
            Services.WindowGlass.Attach(this);
            PickList.ItemsSource = _view;

            TxtSearch.TextChanged += delegate { RebuildView(); };
            BtnSelectAll.Click += OnSelectAll;
            BtnSelectNone.Click += OnSelectNone;
            BtnCancel.Click += OnCancelClick;
            BtnOk.Click += OnOkClick;
            PickList.PreviewMouseLeftButtonDown += OnListMouseDown;
            PickList.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnPickStateChanged));
            PickList.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnPickStateChanged));
            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += OnLoaded;
            StateChanged += OnWindowStateChanged;
        }

        /// <summary>区分"对话框"与"普通 Show"（预览脚本会普通 Show）；决定确定时是 DialogResult 还是 Close。</summary>
        public new bool? ShowDialog()
        {
            _dialogMode = true;
            return base.ShowDialog();
        }

        // ─────────────────────────── 加载 ───────────────────────────

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_preloaded != null)
            {
                Populate(_preloaded);
                return;
            }
            _loading = true;
            RebuildView();
            try
            {
                List<string> candidates = null;
                var shell = _shell ?? _owner?.Shell;
                if (shell != null) candidates = await shell.CollectQcCandidatesAsync();
                if (!IsVisible) return;
                Populate(candidates ?? new List<string>());
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
                _loading = false;
                RebuildView();
            }
        }

        private void Populate(IEnumerable<string> candidates)
        {
            _loading = false;
            _loadError = null;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();
            foreach (var raw in candidates)
            {
                string full;
                try { full = FileUtil.Normalize(raw); } catch { full = raw; }
                if (string.IsNullOrEmpty(full)) continue;
                if (seen.Add(full)) paths.Add(full);
            }
            paths.Sort(StringComparer.OrdinalIgnoreCase);

            string root = CommonRoot(paths);
            _all.Clear();
            foreach (var path in paths)
            {
                string display = path;
                if (!string.IsNullOrEmpty(root) && path.Length > root.Length &&
                    path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    display = path.Substring(root.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                bool isChecked = _preselected.Exists(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
                _all.Add(new SkipPickItem(path, display, isChecked));
            }

            TxtRoot.Text = string.IsNullOrEmpty(root) ? "显示完整路径" : "相对路径基于：" + root;
            TxtRoot.ToolTip = TxtRoot.Text;
            RebuildView();
        }

        private void RebuildView()
        {
            string query = (TxtSearch.Text ?? "").Trim();
            _view.Clear();
            foreach (var item in _all)
            {
                if (query.Length == 0 ||
                    item.Display.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.FullPath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _view.Add(item);
                }
            }
            UpdateCount();
            UpdateEmptyState();
            BtnSelectAll.IsEnabled = _view.Count > 0;
            BtnSelectNone.IsEnabled = _all.Any(x => x.IsChecked);
            BtnOk.IsEnabled = _all.Count > 0;
        }

        private void UpdateCount()
        {
            int checkedCount = _all.Count(x => x.IsChecked);
            string text = "已选 " + checkedCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                          " / 共 " + _all.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_view.Count != _all.Count)
                text += " · 当前显示 " + _view.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TxtCount.Text = text + " · 只影响 $model 主模型";
        }

        private void UpdateEmptyState()
        {
            string message = null;
            if (_loading) message = "正在收集队列里的 QC…";
            else if (!string.IsNullOrEmpty(_loadError)) message = "无法读取队列里的 QC：" + _loadError;
            else if (_all.Count == 0) message = "队列里还没有可以跳过的 QC。\n先在主窗口导入工程文件夹或 QC，再回到这里选择。";
            else if (_view.Count == 0) message = "没有匹配「" + (TxtSearch.Text ?? "").Trim() + "」的 QC。";

            TxtEmpty.Text = message ?? "";
            TxtEmpty.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // ─────────────────────────── 交互 ───────────────────────────

        private void OnPickStateChanged(object sender, RoutedEventArgs e)
        {
            UpdateCount();
            BtnSelectNone.IsEnabled = _all.Any(x => x.IsChecked);
        }

        private void OnSelectAll(object sender, RoutedEventArgs e)
        {
            foreach (var item in _view) item.IsChecked = true;
            UpdateCount();
            BtnSelectNone.IsEnabled = _all.Any(x => x.IsChecked);
        }

        private void OnSelectNone(object sender, RoutedEventArgs e)
        {
            foreach (var item in _view) item.IsChecked = false;
            UpdateCount();
            BtnSelectNone.IsEnabled = false;
        }

        /// <summary>点整行也能勾选（复选框本身由 CheckBox 处理，这里跳过）。</summary>
        private void OnListMouseDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (source == null) return;
            if (FindAncestor<CheckBox>(source) != null) return;
            var container = FindAncestor<ListBoxItem>(source);
            var item = container == null ? null : container.DataContext as SkipPickItem;
            if (item == null) return;
            item.IsChecked = !item.IsChecked;
            e.Handled = true;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (!string.IsNullOrEmpty(TxtSearch.Text)) { TxtSearch.Text = ""; }
                else { Cancel(); }
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && BtnOk.IsEnabled)
            {
                e.Handled = true;
                Confirm();
            }
        }

        private void OnOkClick(object sender, RoutedEventArgs e) { Confirm(); }
        private void OnCancelClick(object sender, RoutedEventArgs e) { Cancel(); }

        private void Confirm()
        {
            SelectedPaths = _all.Where(x => x.IsChecked).Select(x => x.FullPath).ToList();
            if (_dialogMode)
            {
                try { DialogResult = true; return; } catch { }
            }
            Close();
        }

        private void Cancel()
        {
            if (_dialogMode)
            {
                try { DialogResult = false; return; } catch { }
            }
            Close();
        }

        private void OnWindowStateChanged(object sender, EventArgs e)
        {
            try
            {
                if (WindowState == WindowState.Maximized)
                {
                    RootBorder.BorderThickness = new Thickness(0);
                    RootBorder.Margin = new Thickness(0);
                }
                else
                {
                    RootBorder.BorderThickness = new Thickness(1);
                    RootBorder.Margin = new Thickness(0);
                }
            }
            catch { }
        }

        // ─────────────────────────── 小工具 ───────────────────────────

        private static string CommonRoot(List<string> paths)
        {
            if (paths.Count == 0) return "";
            try
            {
                string common = Path.GetDirectoryName(paths[0]) ?? "";
                if (string.IsNullOrEmpty(common)) return "";
                for (int i = 1; i < paths.Count; i++)
                {
                    string dir = Path.GetDirectoryName(paths[i]) ?? "";
                    while (common.Length > 0 && !IsUnder(dir, common))
                    {
                        string parent = Path.GetDirectoryName(common);
                        if (string.IsNullOrEmpty(parent)) { common = ""; break; }
                        common = parent;
                    }
                    if (common.Length == 0) return "";
                }
                return common;
            }
            catch { return ""; }
        }

        private static bool IsUnder(string dir, string root)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(dir)) return false;
            if (dir.Length < root.Length) return false;
            if (!dir.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            if (dir.Length == root.Length) return true;
            char c = dir[root.Length];
            return c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
        }

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                var hit = d as T;
                if (hit != null) return hit;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }
    }
}
