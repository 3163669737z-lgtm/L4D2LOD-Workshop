using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LodStudio.Controls;
using LodStudio.Engine;

namespace LodStudio.Views.Settings
{
    /// <summary>
    /// 常规页：外观、启动与完成、模型过滤、日志/报告位置、备份联动。
    /// 下拉/开关/主题一旦变化立即保存；自定义路径在失焦或回车时保存。
    /// </summary>
    public partial class SettingsPageGeneral : SettingsPageBase
    {
        public SettingsPageGeneral()
        {
            InitializeComponent();
        }

        protected override void OnAttached()
        {
            ChkRestore.Checked += OnRestoreChanged;
            ChkRestore.Unchecked += OnRestoreChanged;
            ChkPopup.Checked += OnPopupChanged;
            ChkPopup.Unchecked += OnPopupChanged;

            Win.AttachNumberBox(TxtMinTriangles, 0, 2000000,
                delegate { return Settings.MinTriangles; },
                delegate (int v) { Settings.MinTriangles = v; },
                false);

            CmbReport.SelectionChanged += OnReportLocationChanged;
            TxtReportDir.LostFocus += delegate { CommitReportDir(); };
            TxtReportDir.KeyDown += OnDirKeyDown;
            BtnBrowseReportDir.Click += OnBrowseReportDir;

            CmbBackup.SelectionChanged += OnBackupLocationChanged;
            ChkBackup.Checked += OnBackupEnabledChanged;
            ChkBackup.Unchecked += OnBackupEnabledChanged;
            TxtBackupDir.LostFocus += delegate { CommitBackupDir(); };
            TxtBackupDir.KeyDown += OnDirKeyDown;
            BtnBrowseBackupDir.Click += OnBrowseBackupDir;
        }

        public override void SyncFromSettings()
        {
            if (Settings == null) return;
            Syncing = true;
            try
            {
                ChkRestore.IsChecked = Settings.RestoreTasks;
                ChkPopup.IsChecked = Settings.PopupOnSuccess;
                TxtMinTriangles.Text = Settings.MinTriangles.ToString(System.Globalization.CultureInfo.InvariantCulture);

                CmbReport.SelectedIndex = Math.Max(0, Math.Min(2, Settings.ReportLocation));
                TxtReportDir.Text = Settings.ReportCustomDir ?? "";
                PanelReportCustom.Visibility = Settings.ReportLocation == 2 ? Visibility.Visible : Visibility.Collapsed;

                ChkBackup.IsChecked = Settings.BackupEnabled;
                CmbBackup.SelectedIndex = Math.Max(0, Math.Min(2, Settings.BackupLocation));
                TxtBackupDir.Text = Settings.BackupCustomDir ?? "";
                UpdateBackupUi();
            }
            finally { Syncing = false; }
        }

        // ─────────────────────────── 启动与完成 ───────────────────────────

        private void OnRestoreChanged(object sender, RoutedEventArgs e)
        {
            if (Syncing || Settings == null) return;
            Settings.RestoreTasks = ChkRestore.IsChecked == true;
            Apply(false);
        }

        private void OnPopupChanged(object sender, RoutedEventArgs e)
        {
            if (Syncing || Settings == null) return;
            Settings.PopupOnSuccess = ChkPopup.IsChecked == true;
            Apply(false);
        }

        // ─────────────────────────── 日志与报告位置 ───────────────────────────

        private void OnReportLocationChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Syncing || Settings == null) return;
            Settings.ReportLocation = Math.Max(0, Math.Min(2, CmbReport.SelectedIndex));
            PanelReportCustom.Visibility = Settings.ReportLocation == 2 ? Visibility.Visible : Visibility.Collapsed;
            Apply(false);
        }

        private void CommitReportDir()
        {
            if (Syncing || Settings == null) return;
            string v = (TxtReportDir.Text ?? "").Trim().Trim('"');
            if (string.Equals(v, Settings.ReportCustomDir ?? "", StringComparison.Ordinal)) return;
            Settings.ReportCustomDir = v;
            Apply(false);
        }

        private async void OnBrowseReportDir(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = await Win.Shell.PickFolder();
                if (string.IsNullOrEmpty(dir)) return;
                TxtReportDir.Text = dir;
                CommitReportDir();
            }
            catch (Exception ex) { Win.Shell.Toast("选择目录失败", ex.Message, ToastTone.Err); }
        }

        // ─────────────────────────── 备份（开关联动） ───────────────────────────

        private void OnBackupEnabledChanged(object sender, RoutedEventArgs e)
        {
            if (Syncing || Settings == null) return;
            Settings.BackupEnabled = ChkBackup.IsChecked == true;
            UpdateBackupUi();
            Apply(!Settings.BackupEnabled);
        }

        private void OnBackupLocationChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Syncing || Settings == null) return;
            Settings.BackupLocation = Math.Max(0, Math.Min(2, CmbBackup.SelectedIndex));
            UpdateBackupUi();
            Apply(false);
        }

        private void CommitBackupDir()
        {
            if (Syncing || Settings == null) return;
            string v = (TxtBackupDir.Text ?? "").Trim().Trim('"');
            if (string.Equals(v, Settings.BackupCustomDir ?? "", StringComparison.Ordinal)) return;
            Settings.BackupCustomDir = v;
            Apply(false);
        }

        private async void OnBrowseBackupDir(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = await Win.Shell.PickFolder();
                if (string.IsNullOrEmpty(dir)) return;
                TxtBackupDir.Text = dir;
                CommitBackupDir();
            }
            catch (Exception ex) { Win.Shell.Toast("选择目录失败", ex.Message, ToastTone.Err); }
        }

        /// <summary>备份开关 / 位置 / 自定义行的联动状态（关闭时下拉与自定义行变灰、说明改成警告）。</summary>
        private void UpdateBackupUi()
        {
            bool on = ChkBackup.IsChecked == true;
            CmbBackup.IsEnabled = on;
            FrameBackupCombo.Opacity = on ? 1.0 : 0.42;
            PanelBackupCustom.IsEnabled = on;
            PanelBackupCustom.Opacity = on ? 1.0 : 0.55;
            PanelBackupCustom.Visibility = Settings.BackupLocation == 2 ? Visibility.Visible : Visibility.Collapsed;

            if (!on)
            {
                TxtBackupNote.Text = "已关闭备份：改完无法一键还原。";
                TxtBackupNote.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Warn");
            }
            else
            {
                TxtBackupNote.Text = "当前：备份到 " + DescribeBackupLocation() + "。";
                TxtBackupNote.SetResourceReference(TextBlock.ForegroundProperty, "Brush.InkFaint");
            }
        }

        private string DescribeBackupLocation()
        {
            switch (Settings.BackupLocation)
            {
                case 1: return "exe 旁的「" + AppInfo.BackupFolderName + "」";
                case 2: return string.IsNullOrEmpty(Settings.BackupCustomDir) ? "自定义目录（尚未指定，会回退到 QC 旁边）" : Settings.BackupCustomDir;
                default: return "QC 旁边";
            }
        }

        // ─────────────────────────── 小工具 ───────────────────────────

        private void OnDirKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var box = sender as TextBox;
            if (box == null) return;
            if (ReferenceEquals(box, TxtReportDir)) CommitReportDir();
            else if (ReferenceEquals(box, TxtBackupDir)) CommitBackupDir();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }
}
