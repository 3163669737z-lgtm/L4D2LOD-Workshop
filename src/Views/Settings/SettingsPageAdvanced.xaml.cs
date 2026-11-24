using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LodStudio.Controls;
using LodStudio.Engine;
using LodStudio.Views;

namespace LodStudio.Views.Settings
{
    /// <summary>
    /// LOD 高级页：生成开关、保留清单、跳过主模型和并行进程数。
    /// 跳过清单来自当前队列里能找到的根 QC（<see cref="LodStudio.ViewModels.ShellViewModel.CollectQcCandidatesAsync"/>）。
    /// </summary>
    public partial class SettingsPageAdvanced : SettingsPageBase
    {
        public SettingsPageAdvanced()
        {
            InitializeComponent();
        }

        protected override void OnAttached()
        {
            ChkQuality.Checked += OnQualityChanged;
            ChkQuality.Unchecked += OnQualityChanged;
            ChkAdaptiveBudget.Checked += OnAdaptiveBudgetChanged;
            ChkAdaptiveBudget.Unchecked += OnAdaptiveBudgetChanged;
            ChkVertexUpdates.Checked += OnVertexUpdatesChanged;
            ChkVertexUpdates.Unchecked += OnVertexUpdatesChanged;
            TxtKeepOriginalModels.LostKeyboardFocus += delegate { SaveKeepOriginalModels(); };
            TxtKeepOriginalModels.KeyDown += OnKeepOriginalModelsKeyDown;
            ChkForce.Checked += OnForceChanged;
            ChkForce.Unchecked += OnForceChanged;
            ChkCompile.Checked += OnCompileChanged;
            ChkCompile.Unchecked += OnCompileChanged;
            ChkNofacial.Checked += OnNofacialChanged;
            ChkNofacial.Unchecked += OnNofacialChanged;
            ChkSkipMain.Checked += OnSkipMainChanged;
            ChkSkipMain.Unchecked += OnSkipMainChanged;
            BtnPickSkip.Click += OnPickSkip;

            Win.AttachNumberBox(TxtWorkers, 0, 16,
                delegate { return Settings.Workers; },
                delegate (int v) { Settings.Workers = v; },
                false);
        }

        public override void SyncFromSettings()
        {
            if (Settings == null) return;
            Syncing = true;
            try
            {
                ChkQuality.IsChecked = Settings.QualityFirst;
                ChkAdaptiveBudget.IsChecked = Settings.AdaptiveTriangleBudget;
                ChkVertexUpdates.IsChecked = Settings.AllowVertexUpdates;
                TxtKeepOriginalModels.Text = Settings.KeepOriginalModels;
                ChkForce.IsChecked = Settings.ForceOverwriteLod;
                ChkCompile.IsChecked = Settings.CompileAfter;
                ChkNofacial.IsChecked = Settings.NoFacial;
                ChkSkipMain.IsChecked = Settings.SkipMainModel;
                TxtWorkers.Text = Settings.Workers.ToString(CultureInfo.InvariantCulture);
                UpdateSkipUi();
            }
            finally { Syncing = false; }
        }

        // ─────────────────────────── 开关 ───────────────────────────

        private void OnQualityChanged(object sender, RoutedEventArgs e)
        {
            if (Syncing || Settings == null) return;
            Settings.QualityFirst = ChkQuality.IsChecked == true;
            Apply(false);
        }

        private void OnAdaptiveBudgetChanged(object sender, RoutedEventArgs e)
        {
            if (Syncing || Settings == null) return;
            Settings.AdaptiveTriangleBudget = ChkAdaptiveBudget.IsChecked == true;
            Apply(false);
        }

        private void OnVertexUpdatesChanged(object sender, RoutedEventArgs e)
        {
            if (Syncing || Settings == null) return;
            Settings.AllowVertexUpdates = ChkVertexUpdates.IsChecked == true;
            Apply(false);
        }

        private void OnKeepOriginalModelsKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SaveKeepOriginalModels();
        }

        private void SaveKeepOriginalModels()
        {
            if (Syncing || Settings == null) return;
            string paths = TxtKeepOriginalModels.Text.Trim();
            if (paths == Settings.KeepOriginalModels) return;
            Settings.KeepOriginalModels = paths;
            Apply(false);
        }

        private void OnForceChanged(object sender, RoutedEventArgs e)
        {
            if (Syncing || Settings == null) return;
            Settings.ForceOverwriteLod = ChkForce.IsChecked == true;
            Apply(Settings.ForceOverwriteLod);
        }

        private void OnCompileChanged(object sender, RoutedEventArgs e)
        {
            if (Syncing || Settings == null) return;
            Settings.CompileAfter = ChkCompile.IsChecked == true;
            if (Settings.CompileAfter && string.IsNullOrEmpty(Settings.NekomdlPath))
                Win.Shell.Toast("已开启试编译", "还需要在「引擎」页指定 nekomdl.exe，否则运行时会被拦下", ToastTone.Warn);
            Apply(false);
        }

        private void OnNofacialChanged(object sender, RoutedEventArgs e)
        {
            if (Syncing || Settings == null) return;
            Settings.NoFacial = ChkNofacial.IsChecked == true;
            Apply(false);
        }

        private void OnSkipMainChanged(object sender, RoutedEventArgs e)
        {
            if (Syncing || Settings == null) return;
            Settings.SkipMainModel = ChkSkipMain.IsChecked == true;
            UpdateSkipUi();
            Apply(Settings.SkipMainModel);
        }

        // ─────────────────────────── 跳过清单 ───────────────────────────

        private void UpdateSkipUi()
        {
            if (Settings == null) return;
            bool on = ChkSkipMain.IsChecked == true;
            int count = Settings.SkipMainQcs == null ? 0 : Settings.SkipMainQcs.Count;

            BtnPickSkip.IsEnabled = on;
            PanelSkip.IsEnabled = on;
            if (!on)
            {
                TxtSkipCount.Text = "先开启「跳过主模型」才能选择";
                TxtSkipCount.SetResourceReference(TextBlock.ForegroundProperty, "Brush.InkFaint");
                TxtSkipHint.Text = "关闭状态下清单会保留，但不会生效。";
                TxtSkipHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.InkFaint");
                return;
            }
            if (count == 0)
            {
                TxtSkipCount.Text = "还没有选择：只跳过清单里的 QC，其它照常生成";
                TxtSkipCount.SetResourceReference(TextBlock.ForegroundProperty, "Brush.InkFaint");
            }
            else
            {
                TxtSkipCount.Text = "已选择 " + count.ToString(CultureInfo.InvariantCulture) + " 个 QC";
                TxtSkipCount.SetResourceReference(TextBlock.ForegroundProperty, "Brush.InkSoft");
            }
            TxtSkipHint.Text = "只跳过这些 QC 里 $model 的主模型；$body / $bodygroup 的 LOD 仍会生成。";
            TxtSkipHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.InkFaint");
        }

        private void OnPickSkip(object sender, RoutedEventArgs e)
        {
            BtnPickSkip.IsEnabled = false;
            try
            {
                var dlg = new SkipMainPickerWindow(Win);
                if (dlg.ShowDialog() == true)
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var list = new List<string>();
                    foreach (var raw in dlg.SelectedPaths)
                    {
                        string full;
                        try { full = FileUtil.Normalize(raw); } catch { full = raw; }
                        if (string.IsNullOrEmpty(full)) continue;
                        if (seen.Add(full)) list.Add(full);
                    }
                    list.Sort(StringComparer.OrdinalIgnoreCase);
                    Settings.SkipMainQcs = list;
                    Apply(false);
                    SyncFromSettings();
                    Win.Shell.Toast("已保存跳过清单",
                        list.Count == 0 ? "清单已清空，所有 QC 都会生成主模型 LOD" : "共 " + list.Count.ToString(CultureInfo.InvariantCulture) + " 个 QC",
                        ToastTone.Ok);
                }
            }
            catch (Exception ex)
            {
                Win.Shell.Toast("无法打开跳过清单", ex.Message, ToastTone.Err);
            }
            finally
            {
                BtnPickSkip.IsEnabled = ChkSkipMain.IsChecked == true;
            }
        }
    }
}
