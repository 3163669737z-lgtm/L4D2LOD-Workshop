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
    /// <summary>
    /// 引擎页：Blender / nekomdl.exe 路径与可用性。
    /// 探测都在后台线程（ProbeVersion 15s 超时），不阻塞界面；探测中状态行显示「检测中…」。
    /// </summary>
    public partial class SettingsPageEngine : SettingsPageBase
    {
        private const string BlenderFilter =
            "Blender (blender.exe)|blender.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";

        private const string CompilerFilter =
            "nekomdl (nekomdl.exe)|nekomdl.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";

        private bool _probeInFlight;
        private string _probeAttemptedPath;
        private DateTime _probeAttemptedAt = DateTime.MinValue;
        private string _probeText = "未检测";
        private string _probeTone = "Faint";

        public SettingsPageEngine()
        {
            InitializeComponent();
        }

        protected override void OnAttached()
        {
            BtnBrowseBlender.Click += OnBrowseBlender;
            BtnFindBlender.Click += OnFindBlender;
            BtnBrowseCompiler.Click += OnBrowseCompiler;
            BtnFindCompiler.Click += OnFindCompiler;
        }

        public override void SyncFromSettings()
        {
            if (Settings == null) return;
            Syncing = true;
            try
            {
                string blender = Settings.BlenderPath ?? "";
                TxtBlenderPath.Text = blender;
                TxtBlenderPath.ToolTip = string.IsNullOrEmpty(blender)
                    ? "还没有指定 blender.exe（启动时会自动查找一次）"
                    : blender;

                string compiler = Settings.NekomdlPath ?? "";
                TxtCompilerPath.Text = compiler;
                TxtCompilerPath.ToolTip = string.IsNullOrEmpty(compiler) ? "还没有指定 nekomdl.exe（可选）" : compiler;

                RefreshBlenderStatus();
                RefreshCompilerStatus();
            }
            finally { Syncing = false; }
        }

        public override void OnPageShown()
        {
            base.OnPageShown();
            EnsureProbe();
        }

        /// <summary>路径存在且还没探测（或超过 30 秒）时，后台探测一次版本。其它页也可以调用（例如诊断页需要版本号）。</summary>
        public void EnsureProbe()
        {
            if (Settings == null || _probeInFlight) return;
            string path = Settings.BlenderPath ?? "";
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            bool stale = !string.Equals(path, _probeAttemptedPath, StringComparison.OrdinalIgnoreCase) ||
                         (DateTime.UtcNow - _probeAttemptedAt).TotalSeconds > 30;
            if (stale) _ = ProbeAsync(false);
        }

        // ─────────────────────────── Blender ───────────────────────────

        private async void OnBrowseBlender(object sender, RoutedEventArgs e)
        {
            try
            {
                string picked = await Win.Shell.PickFile("选择 blender.exe", BlenderFilter, InitialDir(Settings.BlenderPath));
                if (string.IsNullOrEmpty(picked)) return;
                Settings.BlenderPath = FileUtil.Normalize(picked);
                Apply(false);
                SyncFromSettings();
                await ProbeAsync(true);
            }
            catch (Exception ex)
            {
                Win.Shell.Toast("选择 Blender 失败", ex.Message, ToastTone.Err);
            }
        }

        private async void OnFindBlender(object sender, RoutedEventArgs e)
        {
            BtnFindBlender.IsEnabled = false;
            SetBlenderState("正在查找…", "Signal");
            try
            {
                string found = await Task.Run(() => BlenderLocator.Find(null));
                if (string.IsNullOrEmpty(found) || !File.Exists(found))
                {
                    SetBlenderState("未找到：请点「浏览…」指定 blender.exe", "Err");
                    Win.Shell.Toast("没有自动找到 Blender", "请点「浏览…」指定 blender.exe 的完整路径", ToastTone.Warn);
                    return;
                }
                Settings.BlenderPath = found;
                Apply(false);
                SyncFromSettings();
                await ProbeAsync(true);
                Win.Shell.Toast("已找到 Blender", found, ToastTone.Ok);
            }
            catch (Exception ex)
            {
                SetBlenderState("自动查找失败：" + ex.Message, "Err");
                Win.Shell.Toast("自动查找失败", ex.Message, ToastTone.Err);
            }
            finally
            {
                BtnFindBlender.IsEnabled = true;
            }
        }

        /// <summary>后台探测 Blender 版本。announce=true 时探测失败会额外弹 Toast。</summary>
        private async Task ProbeAsync(bool announce)
        {
            string path = Settings == null ? "" : (Settings.BlenderPath ?? "");
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                RefreshBlenderStatus();
                if (announce) Win.Shell.Toast("路径不可用", "指定的 blender.exe 不存在，请重新选择", ToastTone.Warn);
                return;
            }
            if (_probeInFlight) return;

            _probeInFlight = true;
            Win.SetEngineProbeBusy(true);
            SetBlenderState("检测中…", "Faint");
            if (TxtBlenderPath.Text != path) { TxtBlenderPath.Text = path; TxtBlenderPath.ToolTip = path; }
            try
            {
                string version = await Task.Run(() => BlenderLocator.ProbeVersion(path, 15000));
                _probeAttemptedPath = path;
                _probeAttemptedAt = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(version))
                {
                    _probeText = version;
                    _probeTone = "Ok";
                    Win.SetBlenderVersion(version);
                    SetBlenderState(version, "Ok");
                }
                else
                {
                    _probeText = "无法运行：文件存在但启动失败（可能不是 blender.exe，或被安全软件拦截）";
                    _probeTone = "Err";
                    Win.SetBlenderVersion("");
                    SetBlenderState(_probeText, "Err");
                    if (announce) Win.Shell.Toast("Blender 无法运行", "文件存在但启动失败；请确认路径正确、未被拦截", ToastTone.Err);
                }
            }
            catch (Exception ex)
            {
                _probeText = "探测失败：" + ex.Message;
                _probeTone = "Err";
                SetBlenderState(_probeText, "Err");
            }
            finally
            {
                _probeInFlight = false;
                Win.SetEngineProbeBusy(false);
                Win.OnEngineProbeFinished();
            }
        }

        private void RefreshBlenderStatus()
        {
            if (_probeInFlight) { SetBlenderState("检测中…", "Faint"); return; }
            string path = Settings == null ? "" : (Settings.BlenderPath ?? "");
            if (string.IsNullOrWhiteSpace(path)) { SetBlenderState("未找到：点「自动查找」或「浏览…」指定", "Err"); return; }
            if (!File.Exists(path)) { SetBlenderState("文件不存在：请重新选择或点「自动查找」", "Err"); return; }
            if (!string.Equals(path, _probeAttemptedPath, StringComparison.OrdinalIgnoreCase))
            {
                SetBlenderState("未检测", "Faint");
                return;
            }
            SetBlenderState(_probeText, _probeTone);
        }

        private void SetBlenderState(string text, string tone)
        {
            ApplyTone(TxtBlenderStatus, DotBlender, text, tone);
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
