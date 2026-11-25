using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LodStudio.Controls;
using LodStudio.Engine;
using LodStudio.Services;

namespace LodStudio.Views.Pages
{
    public partial class ToolkitPage : StudioPageBase
    {
        public ToolkitPage()
        {
            InitializeComponent();
            InitializeStudio(SubTabButtons, SubTabTrack, SubTabIndicator, SubPageScroll, MaintenancePanel, EnvironmentPanel);
            MountPrismCards(RestoreCard, CleanCard, BackupCard, ReportCard,
                ReductionStatusCard, CompilerStatusCard, PermissionStatusCard, CompilerCard, DiagnosticsCard);
            UiMotion.AttachIconMicroMotion(RestoreCard, RestoreIcon, "tilt", () => PrismEffectsEnabled);
            UiMotion.AttachIconMicroMotion(CleanCard, CleanIcon, "lift", () => PrismEffectsEnabled);
            BindTextBox(BackupPath, SaveBackupPath);
            BindTextBox(ReportPath, SaveReportPath);
        }

        protected override void ReadSettings()
        {
            BackupToggle.IsChecked = Settings.BackupEnabled;
            BackupLocation.SelectedIndex = Math.Max(0, Math.Min(2, Settings.BackupLocation));
            BackupPath.Text = Settings.BackupCustomDir ?? "";
            ReportLocation.SelectedIndex = Math.Max(0, Math.Min(2, Settings.ReportLocation));
            ReportPath.Text = Settings.ReportCustomDir ?? "";
            ReadCompilerPath();
        }

        protected override void RefreshChildren(bool animate)
        {
            bool enabled = BackupToggle.IsChecked == true;
            RevealChild(BackupDetails, enabled, animate);
            RevealChild(BackupCustom, enabled && BackupLocation.SelectedIndex == 2, animate);
            RevealChild(ReportCustom, ReportLocation.SelectedIndex == 2, animate);
            BackupWarning.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void OnRestoreClick(object sender, RoutedEventArgs e)
        {
            if (_vm != null) await _vm.RestoreQcBackupsAsync();
        }

        private async void OnCleanClick(object sender, RoutedEventArgs e)
        {
            if (_vm != null) await _vm.CleanOrphanLodFilesAsync();
        }

        private void OnBackupClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            bool enabled = Settings.BackupEnabled = BackupToggle.IsChecked == true;
            RevealChild(BackupDetails, enabled, true);
            RevealChild(BackupCustom, enabled && Settings.BackupLocation == 2, true);
            BackupWarning.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            Save(!enabled);
        }

        private void OnBackupLocationChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!CanSave || BackupLocation.SelectedIndex < 0) return;
            Settings.BackupLocation = BackupLocation.SelectedIndex;
            RevealChild(BackupCustom, Settings.BackupEnabled && Settings.BackupLocation == 2, true);
            Save();
        }

        private void OnReportLocationChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!CanSave || ReportLocation.SelectedIndex < 0) return;
            Settings.ReportLocation = ReportLocation.SelectedIndex;
            RevealChild(ReportCustom, Settings.ReportLocation == 2, true);
            Save();
        }

        private async void OnBrowseBackupClick(object sender, RoutedEventArgs e) => await PickDirectoryAsync(SaveBackupPath);
        private async void OnBrowseReportClick(object sender, RoutedEventArgs e) => await PickDirectoryAsync(SaveReportPath);

        private async Task PickDirectoryAsync(Action<string> savePath)
        {
            if (!CanSave) return;
            try
            {
                string path = await _vm.PickFolder();
                if (!string.IsNullOrWhiteSpace(path)) savePath(path);
            }
            catch (Exception ex)
            {
                _vm.Toast("无法选择目录", ex.Message, ToastTone.Err);
            }
        }

        private void SaveBackupPath(string path)
        {
            BackupPath.Text = path;
            if (path == (Settings.BackupCustomDir ?? "")) return;
            Settings.BackupCustomDir = path;
            Save();
        }

        private void SaveReportPath(string path)
        {
            ReportPath.Text = path;
            if (path == (Settings.ReportCustomDir ?? "")) return;
            Settings.ReportCustomDir = path;
            Save();
        }

        private async void OnBrowseCompilerClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            SetCompilerButtonsEnabled(false);
            try
            {
                const string filter = "模型编译器 (nekomdl.exe)|nekomdl.exe|可执行文件 (*.exe)|*.exe";
                string initialDir = File.Exists(Settings.NekomdlPath) ? Path.GetDirectoryName(Settings.NekomdlPath) : null;
                string path = await _vm.PickFile("选择模型编译器 nekomdl.exe", filter, initialDir);
                if (!string.IsNullOrWhiteSpace(path)) SaveCompilerPath(FileUtil.Normalize(path));
            }
            catch (Exception ex)
            {
                _vm.Toast("无法选择编译器", ex.Message, ToastTone.Err);
            }
            finally
            {
                SetCompilerButtonsEnabled(true);
                ReadCompilerPath();
            }
        }

        private async void OnFindCompilerClick(object sender, RoutedEventArgs e)
        {
            if (!CanSave) return;
            SetCompilerButtonsEnabled(false);
            SetCompilerStatus("正在查找编译器…", "Signal");
            try
            {
                string found = await Task.Run(() => Nekomdl.Find(null));
                if (File.Exists(found))
                {
                    SaveCompilerPath(found);
                    _vm.Toast("已找到编译器", found, ToastTone.Ok);
                }
                else
                {
                    _vm.Toast("没有自动找到 nekomdl.exe", "可以点「浏览…」手动选择编译器。", ToastTone.Warn);
                }
            }
            catch (Exception ex)
            {
                _vm.Toast("自动查找编译器失败", ex.Message, ToastTone.Err);
            }
            finally
            {
                SetCompilerButtonsEnabled(true);
                ReadCompilerPath();
            }
        }

        private void SaveCompilerPath(string path)
        {
            if (string.Equals(path, Settings.NekomdlPath, StringComparison.OrdinalIgnoreCase)) return;
            Settings.NekomdlPath = path;
            Save();
        }

        private void SetCompilerButtonsEnabled(bool enabled)
        {
            FindCompilerButton.IsEnabled = enabled;
            BrowseCompilerButton.IsEnabled = enabled;
        }

        private void ReadCompilerPath()
        {
            string path = Settings.NekomdlPath ?? "";
            CompilerPath.Text = path;
            CompilerPath.ToolTip = string.IsNullOrWhiteSpace(path) ? "还没有指定 nekomdl.exe" : path;
            if (string.IsNullOrWhiteSpace(path)) SetCompilerStatus("还没设置路径，需要试编译时再选择。", "Faint");
            else if (File.Exists(path)) SetCompilerStatus("已找到指定的编译器，可以用来试编译。", "Ok");
            else SetCompilerStatus("找不到指定的文件，请重新选择。", "Err");
        }

        private void SetCompilerStatus(string text, string tone)
        {
            CompilerPathStatus.Text = text;
            CompilerPathDot.Tag = tone;
        }

        private async void OnRunDiagnosticsClick(object sender, RoutedEventArgs e) => await RunDiagnosticsAsync();

        public async Task RunDiagnosticsAsync()
        {
            if (Settings == null || !RunDiagnosticsButton.IsEnabled) return;
            RunDiagnosticsButton.IsEnabled = false;
            CopyReportButton.IsEnabled = false;
            DiagnosticsStatus.Text = "正在检查本地环境…";
            DiagnosticsDot.Tag = "Signal";

            var settings = Settings;
            var dirs = new List<string>();
            try
            {
                foreach (var task in _vm.Queue.Items)
                {
                    string dir;
                    try { dir = task.Kind == TaskKind.Folder ? task.Path : Path.GetDirectoryName(FileUtil.Normalize(task.Path)); }
                    catch { continue; }
                    if (string.IsNullOrWhiteSpace(dir) ||
                        dirs.Exists(existing => string.Equals(existing, dir, StringComparison.OrdinalIgnoreCase))) continue;
                    dirs.Add(dir);
                    if (dirs.Count == 8) break;
                }

                string report = await Task.Run(() => EnvironmentDiagnostics.Build(settings, dirs,
                    RuntimeContext.IsAdmin, RuntimeContext.IsLowIntegrity()));
                if (string.IsNullOrWhiteSpace(report))
                    throw new InvalidOperationException("体检没有返回结果。");

                DiagnosticsReport.Text = report;
                DiagnosticsStatus.Text = $"体检完成 · {DateTime.Now:HH:mm:ss}";
                DiagnosticsDot.Tag = "Ok";
            }
            catch (Exception ex)
            {
                var fallback = new StringBuilder();
                fallback.AppendLine("完整体检暂时无法完成。这里是当前能读取到的信息。");
                fallback.AppendLine(ex.Message);
                fallback.AppendLine($"程序版本  {AppInfo.DisplayVersion}（{AppInfo.Bitness}）");
                fallback.AppendLine($"系统版本  {Environment.OSVersion.VersionString}");
                fallback.AppendLine($"目录权限  {_vm.PermissionText}");
                fallback.AppendLine($"减面引擎  {_vm.ReductionText}");
                fallback.AppendLine($"编译器    {_vm.CompilerText}");
                fallback.AppendLine($"配置文件  {AppInfo.SettingsFile}");
                fallback.AppendLine($"临时目录  {AppInfo.TempRoot}");
                foreach (string dir in dirs) fallback.AppendLine($"工程目录  {dir}");
                DiagnosticsReport.Text = fallback.ToString();
                DiagnosticsStatus.Text = "已显示基本环境信息";
                DiagnosticsDot.Tag = "Warn";
            }
            finally
            {
                DiagnosticsReport.ScrollToHome();
                RunDiagnosticsButton.IsEnabled = true;
                CopyReportButton.IsEnabled = !string.IsNullOrWhiteSpace(DiagnosticsReport.Text);
            }
        }

        private void OnCopyReportClick(object sender, RoutedEventArgs e)
        {
            if (_vm == null || string.IsNullOrWhiteSpace(DiagnosticsReport.Text)) return;
            try
            {
                Clipboard.SetText(DiagnosticsReport.Text);
                _vm.Toast("已复制体检报告", "可以直接粘贴到问题反馈里。", ToastTone.Ok);
            }
            catch (Exception ex)
            {
                _vm.Toast("无法复制体检报告", ex.Message, ToastTone.Err);
            }
        }

        private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsFolder();
    }
}
