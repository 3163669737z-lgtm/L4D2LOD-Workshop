using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using LodStudio.Controls;
using LodStudio.Engine;

namespace LodStudio.Views.Settings
{
    public partial class SettingsPageEngine : SettingsPageBase
    {
        private const string CompilerFilter =
            "nekomdl (nekomdl.exe)|nekomdl.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";

        public SettingsPageEngine()
        {
            InitializeComponent();
        }

        protected override void OnAttached()
        {
            BtnBrowseCompiler.Click += OnBrowseCompiler;
            BtnFindCompiler.Click += OnFindCompiler;
        }

        public override void SyncFromSettings()
        {
            if (Settings == null) return;
            Syncing = true;
            try
            {
                string compiler = Settings.NekomdlPath ?? "";
                TxtCompilerPath.Text = compiler;
                TxtCompilerPath.ToolTip = string.IsNullOrEmpty(compiler) ? "还没有指定 nekomdl.exe（可选）" : compiler;

                RefreshCompilerStatus();
            }
            finally { Syncing = false; }
        }

        // ─────────────────────────── 编译器 ───────────────────────────

        private async void OnBrowseCompiler(object sender, RoutedEventArgs e)
        {
            try
            {
                string picked = await Win.Shell.PickFile("选择 nekomdl.exe", CompilerFilter, InitialDir(Settings.NekomdlPath));
                if (string.IsNullOrEmpty(picked)) return;
                Settings.NekomdlPath = FileUtil.Normalize(picked);
                Apply(false);
                SyncFromSettings();
            }
            catch (Exception ex)
            {
                Win.Shell.Toast("选择编译器失败", ex.Message, ToastTone.Err);
            }
        }

        private async void OnFindCompiler(object sender, RoutedEventArgs e)
        {
            BtnFindCompiler.IsEnabled = false;
            SetCompilerState("正在查找…", "Signal");
            try
            {
                string found = await Task.Run(() => Nekomdl.Find(null));
                if (string.IsNullOrEmpty(found) || !File.Exists(found))
                {
                    RefreshCompilerStatus();
                    Win.Shell.Toast("没有自动找到 nekomdl.exe", "它通常在 studiomdl 旁边；可点「浏览…」手动指定", ToastTone.Warn);
                    return;
                }
                Settings.NekomdlPath = found;
                Apply(false);
                SyncFromSettings();
                Win.Shell.Toast("已找到编译器", found, ToastTone.Ok);
            }
            catch (NotImplementedException)
            {
                RefreshCompilerStatus();
                Win.Shell.Toast("编译器查找尚未接入", "该模块正在并行开发；可先点「浏览…」手动指定 nekomdl.exe", ToastTone.Info);
            }
            catch (Exception ex)
            {
                RefreshCompilerStatus();
                Win.Shell.Toast("自动查找编译器失败", ex.Message, ToastTone.Err);
            }
            finally
            {
                BtnFindCompiler.IsEnabled = true;
            }
        }

        private void RefreshCompilerStatus()
        {
            string path = Settings == null ? "" : (Settings.NekomdlPath ?? "");
            if (string.IsNullOrWhiteSpace(path)) { SetCompilerState("未设置", "Faint"); return; }
            if (!File.Exists(path)) { SetCompilerState("文件不存在：请重新选择", "Err"); return; }
            SetCompilerState("已设置", "Ok");
        }

        private void SetCompilerState(string text, string tone)
        {
            ApplyTone(TxtCompilerStatus, DotCompiler, text, tone);
        }

        // ─────────────────────────── 小工具 ───────────────────────────

        private static void ApplyTone(TextBlock text, Ellipse dot, string content, string tone)
        {
            text.Text = content;
            string key = tone == "Ok" ? "Brush.Ok"
                       : tone == "Err" ? "Brush.Err"
                       : tone == "Warn" ? "Brush.Warn"
                       : tone == "Signal" ? "Brush.Signal"
                       : "Brush.InkFaint";
            text.SetResourceReference(TextBlock.ForegroundProperty, key);
            if (dot != null) dot.SetResourceReference(Shape.FillProperty, key);
        }

        private static string InitialDir(string file)
        {
            try
            {
                if (!string.IsNullOrEmpty(file) && File.Exists(file)) return System.IO.Path.GetDirectoryName(file);
            }
            catch { }
            return null;
        }
    }
}
