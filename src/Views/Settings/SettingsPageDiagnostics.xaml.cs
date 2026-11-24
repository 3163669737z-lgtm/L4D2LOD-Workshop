using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LodStudio.Controls;
using LodStudio.Engine;

namespace LodStudio.Views.Settings
{
    /// <summary>
    /// 诊断页：调用 EnvironmentDiagnostics.Build 生成可复制的报告。
    /// 该模块可能还没接入（NotImplementedException）——此时不崩，改为显示本页能直接读到的基础信息。
    /// </summary>
    public partial class SettingsPageDiagnostics : SettingsPageBase
    {
        private bool _hasReport;

        public SettingsPageDiagnostics()
        {
            InitializeComponent();
        }

        protected override void OnAttached()
        {
            BtnRun.Click += async delegate { await RunDiagnosticsAsync(); };
            BtnCopy.Click += OnCopy;
            BtnOpenSettingsDir.Click += delegate
            {
                try { Win.Shell.OpenSettingsFolder(); }
                catch (Exception ex) { Win.Shell.Toast("打开设置文件夹失败", ex.Message, ToastTone.Err); }
            };
        }

        public override void SyncFromSettings()
        {
            if (Settings == null) return;
            Syncing = true;
            try
            {
                TxtSettingsFile.Text = AppInfo.SettingsFile;
                TxtSettingsFile.ToolTip = AppInfo.SettingsFile;
                TxtTempDir.Text = AppInfo.TempRoot;
                TxtTempDir.ToolTip = AppInfo.TempRoot;
                TxtPermission.Text = DescribePermission();
                TxtReductionEngine.Text = "内置 meshoptimizer";
            }
            finally { Syncing = false; }
        }

        public override void OnPageShown()
        {
            base.OnPageShown();
            if (!_hasReport) SetStatus("尚未运行", "Faint");
        }

        // ─────────────────────────── 运行诊断 ───────────────────────────

        /// <summary>运行诊断并把结果写进下面的只读报告框（按钮与预览脚本共用）。</summary>
        public async Task RunDiagnosticsAsync()
        {
            if (Settings == null) return;
            BtnRun.IsEnabled = false;
            BtnCopy.IsEnabled = false;
            SetStatus("正在收集环境信息…", "Signal");
            var probeDirs = CollectProbeDirs();
            string text;
            string status;
            string tone;
            try
            {
                text = await Task.Run(() => EnvironmentDiagnostics.Build(Settings, probeDirs,
                    RuntimeContext.IsAdmin, RuntimeContext.IsLowIntegrity()));
                status = "完成 · " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                tone = "Ok";
            }
            catch (NotImplementedException)
            {
                text = BuildFallbackReport(probeDirs,
                    "诊断模块尚未接入（EnvironmentDiagnostics 仍在并行开发中）。",
                    "下面是本页可以直接读取到的基础信息：");
                status = "诊断模块尚未接入 · 已显示基础信息";
                tone = "Warn";
            }
            catch (Exception ex)
            {
                text = BuildFallbackReport(probeDirs, "诊断失败：" + ex.Message, "下面是本页可以直接读取到的基础信息：");
                status = "诊断失败：" + ex.Message;
                tone = "Err";
            }

            TxtReport.Text = string.IsNullOrEmpty(text) ? "（诊断没有返回内容）" : text;
            _hasReport = true;
            BtnRun.IsEnabled = true;
            BtnCopy.IsEnabled = true;
            SetStatus(status, tone);
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(TxtReport.Text)) return;
                Clipboard.SetText(TxtReport.Text);
                Win.Shell.Toast("已复制诊断报告", "可直接粘贴给开发者", ToastTone.Ok);
            }
            catch (Exception ex)
            {
                Win.Shell.Toast("复制失败", "剪贴板被其它程序占用，请稍后重试（" + ex.Message + "）", ToastTone.Err);
            }
        }

        // ─────────────────────────── 内容 ───────────────────────────

        /// <summary>探针目录：队列里每个文件夹 + QC/模型文件所在目录，去重后取前 8 个（与主窗口诊断一致）。</summary>
        private List<string> CollectProbeDirs()
        {
            var dirs = new List<string>();
            try
            {
                foreach (var t in Win.Shell.Queue.Items)
                {
                    string dir = null;
                    try
                    {
                        dir = t.Kind == TaskKind.Folder ? t.Path : Path.GetDirectoryName(FileUtil.Normalize(t.Path));
                    }
                    catch { }
                    if (string.IsNullOrEmpty(dir)) continue;
                    if (dirs.Exists(x => string.Equals(x, dir, StringComparison.OrdinalIgnoreCase))) continue;
                    dirs.Add(dir);
                    if (dirs.Count >= 8) break;
                }
            }
            catch { }
            return dirs;
        }

        private string BuildFallbackReport(List<string> probeDirs, string headline, string subtitle)
        {
            var sb = new StringBuilder();
            sb.AppendLine(headline);
            if (!string.IsNullOrEmpty(subtitle)) { sb.AppendLine(subtitle); }
            sb.AppendLine();
            sb.AppendLine("时间        : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("程序        : " + AppInfo.TitleText + "（文件版本 " + AppInfo.FileVersionText + "，" + AppInfo.Bitness + "）");
            sb.AppendLine("系统        : " + Environment.OSVersion.VersionString);
            sb.AppendLine("运行权限    : " + DescribePermission());
            sb.AppendLine("设置文件    : " + AppInfo.SettingsFile);
            sb.AppendLine("设置目录存在: " + (SafeExists(AppInfo.SettingsDir) ? "是" : "否"));
            sb.AppendLine("临时目录    : " + AppInfo.TempRoot);
            sb.AppendLine("线程        : " + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture) + " 个逻辑处理器");
            sb.AppendLine("主题模式    : " + DescribeTheme(Settings.ThemeMode));
            sb.AppendLine("运行设置    : 并行 " + (Settings.Workers <= 0 ? "自动" : Settings.Workers.ToString(CultureInfo.InvariantCulture)) +
                          " · 备份 " + (Settings.BackupEnabled ? "开" : "关") +
                          " · 报告位置 " + OutputLocations.DescribeReport(Settings));
            sb.AppendLine();
            sb.AppendLine("待检查目录（前 8 个）:");
            if (probeDirs.Count == 0)
            {
                sb.AppendLine("  （队列里还没有任务）");
            }
            else
            {
                foreach (var d in probeDirs) sb.AppendLine("  " + d);
            }
            sb.AppendLine();
            int queueCount = 0;
            try { queueCount = Win.Shell.Queue.Items.Count; } catch { }
            sb.AppendLine("队列任务数  : " + queueCount.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static bool SafeExists(string path)
        {
            try { return Directory.Exists(path); } catch { return false; }
        }

        private static string DescribePermission()
        {
            if (RuntimeContext.IsLowIntegrity()) return "低完整性（写不了普通目录，请换位置运行）";
            if (RuntimeContext.IsAdmin) return "管理员";
            return "普通权限";
        }

        private static string DescribeTheme(int mode)
        {
            switch (mode)
            {
                case 0: return "浅色";
                case 1: return "暗色";
                default: return "跟随系统";
            }
        }

        private void SetStatus(string text, string tone)
        {
            TxtStatus.Text = text;
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty,
                tone == "Ok" ? "Brush.Ok"
                : tone == "Warn" ? "Brush.Warn"
                : tone == "Err" ? "Brush.Err"
                : tone == "Signal" ? "Brush.Signal"
                : "Brush.InkFaint");
        }
    }
}
