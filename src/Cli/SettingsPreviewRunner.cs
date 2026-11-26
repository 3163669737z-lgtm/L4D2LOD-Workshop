using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LodStudio.Controls;
using LodStudio.Engine;
using LodStudio.Services;
using LodStudio.ViewModels;
using LodStudio.Views;

namespace LodStudio.Cli
{
    /// <summary>
    /// --settings-preview --out &lt;dir&gt;：把设置窗口的 5 个页面（浅/暗）、备份联动关闭态、诊断结果
    /// 与"跳过主模型"选择器离屏渲染成 PNG，供设计核对与迭代。
    ///
    /// 安全约定：
    ///  · 先把 L4D2LODSTUDIO_SETTINGS_DIR 指到 &lt;out&gt;\_settings，绝不读写用户真实设置；
    ///  · 窗口放在屏幕外（Left = -20000）+ RenderTargetBitmap 截图，不干扰桌面；
    ///  · 假的 IShellInteraction 只做空实现，不会弹任何真实对话框。
    /// </summary>
    public static class SettingsPreviewRunner
    {
        private static readonly List<string> Log = new List<string>();
        private static readonly List<string> Files = new List<string>();
        private static string _outDir;
        private static int _failures;

        public static int Run(CommandLine cli)
        {
            _outDir = cli.OutDir;
            if (string.IsNullOrEmpty(_outDir)) _outDir = Path.Combine(AppInfo.TempRoot, "settings-preview");
            try { Directory.CreateDirectory(_outDir); } catch { }

            // ── 1. 隔离设置目录（必须在 SettingsStore / AppInfo.SettingsDir 之前设置） ──
            string cfgDir = Path.Combine(_outDir, "_settings");
            try { Directory.CreateDirectory(cfgDir); } catch { }
            try { Environment.SetEnvironmentVariable(AppInfo.SettingsDirEnvVar, cfgDir); } catch { }

            // ── 2. 写一份预览用设置（覆盖式写入，防止读入用户真实配置 / 旧版导入） ──
            List<string> demoAll = CreateDemoCandidates();
            List<string> demoSelected = demoAll.Take(2).ToList();
            AppSettings seed = BuildSeedSettings(demoSelected);
            try { SettingsStore.Save(seed); } catch { }
            AppSettings settings = SettingsStore.Load();

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                // 反射宿主（例如用 powershell 加载 exe 调试）时，把包资源根指回本程序集
                try
                {
                    if (Application.ResourceAssembly != typeof(AppInfo).Assembly)
                        Application.ResourceAssembly = typeof(AppInfo).Assembly;
                }
                catch { }

                // 预览不走 app.Run()，要自己装 Dispatcher 同步上下文；
                // 否则 await Task.Run(...) 的续体会回到线程池，随后触碰控件会触发跨线程异常。
                try
                {
                    SynchronizationContext.SetSynchronizationContext(
                        new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                }
                catch { }

                ThemeService.Initialize(ThemeKind.Light);

                Log.Add(AppInfo.TitleText + "  —  settings-preview");
                Log.Add("out           : " + _outDir);
                Log.Add("设置目录      : " + AppInfo.SettingsDir);
                Log.Add("动画开关      : ClientAreaAnimation=" + SafeAnimationFlag());
                Log.Add("");

                var shell = new ShellViewModel(settings, new FakeShellInteraction());
                var win = new SettingsWindow(settings, shell, constrainMaximized: false)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false,
                    Width = 960,
                    Height = 660
                };
                win.Show();
                Pump(300);

                // ═══════════ 引擎页（浅 / 暗） ═══════════
                SetTheme(settings, 2, ThemeKind.Light);
                win.SelectPage(0);
                NoteLayout(win, "settings_engine_light");
                Capture(win, "settings_engine_light",
                    "引擎页 · 浅色，内置减面后端和编译器设置");
                SetTheme(settings, 1, ThemeKind.Dark);
                win.SelectPage(0);
                Pump(120);
                NoteLayout(win, "settings_engine_dark");
                Capture(win, "settings_engine_dark", "引擎页 · 暗色");

                // ═══════════ 常规页（浅 + 备份联动关闭态） ═══════════
                SetTheme(settings, 2, ThemeKind.Light);
                win.SelectPage(1);
                Pump(160);
                NoteLayout(win, "settings_general_light");
                Capture(win, "settings_general_light", "常规页 · 浅色：启动/面数门槛/报告位置（主题与动效在「外观」页）");

                bool backupWas = settings.BackupEnabled;
                int backupLocWas = settings.BackupLocation;
                string backupDirWas = settings.BackupCustomDir;
                settings.BackupEnabled = false;
                settings.BackupLocation = 2;     // 自定义：让"自定义行变灰不可点"也出现在截图里
                settings.BackupCustomDir = Path.Combine(_outDir, "_demo_工程", "LOD备份");
                win.SelectPage(1);
                Pump(140);
                win.ScrollPageToEnd();       // 备份联动在页面下半部分，滚到底再截
                Pump(80);
                NoteLayout(win, "settings_general_backupoff_light");
                Capture(win, "settings_general_backupoff_light",
                    "备份开关联动关闭态（已滚到页面底部）：位置下拉与自定义行变灰不可点、说明变 Warn 色「已关闭备份：改完无法一键还原」");
                settings.BackupEnabled = backupWas;
                settings.BackupLocation = backupLocWas;
                settings.BackupCustomDir = backupDirWas;
                win.SelectPage(1);
                win.ScrollPageTo(0);
                Pump(120);

                // ═══════════ LOD 高级页 ═══════════
                SetTheme(settings, 0, ThemeKind.Light);
                win.SelectPage(2);
                Pump(160);
                NoteLayout(win, "settings_advanced_light");
                Capture(win, "settings_advanced_light", "LOD 高级页 · 浅色：生成开关、保留清单和跳过清单");

                // ═══════════ 外观页（浅 / 暗） ═══════════
                SetTheme(settings, 0, ThemeKind.Light);
                win.SelectPage(3);
                Pump(160);
                NoteLayout(win, "settings_appearance_light");
                Capture(win, "settings_appearance_light", "外观页 · 浅色：主题三选 + 动效 + 令牌/控件状态示意（浅色令牌）");

                SetTheme(settings, 1, ThemeKind.Dark);
                win.SelectPage(3);
                Pump(160);
                NoteLayout(win, "settings_appearance_dark");
                Capture(win, "settings_appearance_dark", "外观页 · 暗色：同一页的令牌与示意全部随主题切换");

                // ═══════════ 诊断页 ═══════════
                SetTheme(settings, 0, ThemeKind.Light);
                win.SelectPage(4);
                Pump(120);
                Task diag = win.RunDiagnosticsAsync();
                WaitTask(diag, 6000);
                Pump(160);
                NoteLayout(win, "settings_diag_light");
                Capture(win, "settings_diag_light",
                    "诊断页 · 浅色：实际跑了一次环境诊断（EnvironmentDiagnostics 已接入），报告可选中复制");

                // ═══════════ 「已保存」脉冲（额外） ═══════════
                win.SelectPage(0);
                Pump(100);
                win.FlashSaved(false);
                Pump(120);
                Capture(win, "settings_saved_pill_light", "标题栏右侧「已保存」脉冲（900ms），固定宽度不挤动关闭按钮");

                // ═══════════ 跳过主模型选择器 ═══════════
                SetTheme(settings, 0, ThemeKind.Light);
                var picker = new SkipMainPickerWindow(demoAll, demoSelected)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false,
                    Width = 760,
                    Height = 560
                };
                picker.Show();
                Pump(320);
                Capture(picker, "skipmain_picker_light",
                    "跳过主模型选择器 · 浅色：搜索 + 全选/全不选 + 相对路径列表 + 已选计数（2 / 共 8）");
                picker.Close();
                Pump(80);

                win.Close();
                Pump(80);
            }
            catch (Exception ex)
            {
                _failures++;
                Log.Add("");
                Log.Add("FATAL: " + ex);
                try { Console.WriteLine("SETTINGS-PREVIEW FATAL: " + ex.Message); } catch { }
            }
            finally
            {
                try { app.Shutdown(); } catch { }
            }

            // ── 结果：必需文件齐不齐 ──
            string[] required =
            {
                "settings_engine_light", "settings_engine_dark",
                "settings_general_light", "settings_general_backupoff_light",
                "settings_advanced_light",
                "settings_appearance_light", "settings_appearance_dark",
                "settings_diag_light", "skipmain_picker_light"
            };
            foreach (var name in required)
            {
                string path = Path.Combine(_outDir, name + ".png");
                bool ok = false;
                try { var fi = new FileInfo(path); ok = fi.Exists && fi.Length > 1024; } catch { }
                if (!ok) { _failures++; Log.Add("  [missing] " + name + ".png"); }
            }

            Log.Add("");
            for (int i = 0; i < Files.Count; i++) Log.Add("  [ok] " + Files[i]);
            Log.Add("");
            Log.Add("SETTINGS-PREVIEW: " + Files.Count + " images, " + _failures + " failure(s)");
            try
            {
                File.WriteAllText(Path.Combine(_outDir, "settings-preview.txt"),
                    string.Join(Environment.NewLine, Log.ToArray()) + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
            for (int i = 0; i < Log.Count; i++) { try { Console.WriteLine(Log[i]); } catch { } }
            return _failures == 0 ? 0 : 2;
        }

        // ══════════════════════════ 预览数据 ══════════════════════════

        private static AppSettings BuildSeedSettings(List<string> demoQcs)
        {
            var s = new AppSettings
            {
                ThemeMode = 2,
                Animations = true,
                RestoreTasks = true,
                PopupOnSuccess = false,
                MinTriangles = 200,
                ReportLocation = 0,
                ReportCustomDir = "",
                BackupEnabled = true,
                BackupLocation = 0,
                BackupCustomDir = "",
                QualityFirst = true,
                ForceOverwriteLod = false,
                CompileAfter = false,
                NoFacial = true,
                SkipMainModel = true,
                Workers = 0
            };
            if (demoQcs != null && demoQcs.Count > 0) s.SkipMainQcs = new List<string>(demoQcs);
            return s;
        }

        /// <summary>造一组示例 QC 文件（在 out 目录下），让选择器里的相对路径看起来像真实工程。</summary>
        private static List<string> CreateDemoCandidates()
        {
            string root = Path.Combine(_outDir, "_demo_工程");
            string[] rels =
            {
                @"角色\survivor_zoey.qc",
                @"角色\survivor_teenangst.qc",
                @"武器\smg_uzi.qc",
                @"武器\pistol_m1911.qc",
                @"道具\crate_large.qc",
                @"道具\gascan.qc",
                @"感染者\common_male.qc",
                @"场景\safehouse_door.qc"
            };
            var list = new List<string>();
            foreach (var rel in rels)
            {
                string full = Path.Combine(root, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    if (!File.Exists(full))
                        File.WriteAllText(full, "// 预览用示例 QC（--settings-preview 生成）" + Environment.NewLine, new UTF8Encoding(false));
                }
                catch { }
                list.Add(full);
            }
            return list;
        }

        // ══════════════════════════ 基础设施 ══════════════════════════

        private static void SetTheme(AppSettings settings, int mode, ThemeKind kind)
        {
            try
            {
                settings.ThemeMode = mode;
                ThemeService.Apply(kind);
                UiCapture.DoEvents(2);
            }
            catch (Exception ex) { Log.Add("  [theme] " + ex.Message); }
        }


        private static void WaitTask(Task task, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (task != null && !task.IsCompleted && sw.ElapsedMilliseconds < timeoutMs)
            {
                Pump(30);
            }
            Pump(80);
        }

        /// <summary>记录当前页"内容高 / 可视高"，超出的页在截图里会看到滚动条（便于发现裁切/溢出）。</summary>
        private static void NoteLayout(SettingsWindow win, string name)
        {
            try
            {
                double content = win.PageContentHeight, viewport = win.PageViewportHeight;
                string flag = content > viewport + 1
                    ? "  ← 超出可视高度 " + Math.Round(content - viewport).ToString(CultureInfo.InvariantCulture) + "，需要滚动"
                    : "  ← 一屏放下";
                Log.Add("    layout " + name + ": content=" + Math.Round(content).ToString(CultureInfo.InvariantCulture) +
                        " / viewport=" + Math.Round(viewport).ToString(CultureInfo.InvariantCulture) + flag);
            }
            catch (Exception ex) { Log.Add("    layout " + name + ": " + ex.Message); }
        }

        private static void Capture(Window win, string name, string note)
        {
            string path = Path.Combine(_outDir, name + ".png");
            try
            {
                UiCapture.CaptureWindow(win, path);
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length <= 1024)
                {
                    _failures++;
                    Log.Add("  [FAIL] " + name + ".png（文件不存在或过小）");
                    return;
                }
                Files.Add(name + ".png  (" + ((int)Math.Round(win.ActualWidth)).ToString(CultureInfo.InvariantCulture) +
                          "×" + ((int)Math.Round(win.ActualHeight)).ToString(CultureInfo.InvariantCulture) +
                          ", " + fi.Length.ToString(CultureInfo.InvariantCulture) + " B)  " + note);
            }
            catch (Exception ex)
            {
                _failures++;
                Log.Add("  [FAIL] " + name + ": " + ex.Message);
            }
        }

        private static void Pump(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                UiCapture.DoEvents(1);
                Thread.Sleep(8);
            }
            UiCapture.DoEvents(2);
        }

        private static string SafeAnimationFlag()
        {
            try { return SystemParameters.ClientAreaAnimation ? "on" : "off"; }
            catch { return "unknown"; }
        }

        // ══════════════════════════ 假的主窗口交互 ══════════════════════════

        /// <summary>预览用：所有对话框/文件选择/Toast 都空实现，保证离屏渲染期间不阻塞、不弹窗。</summary>
        private sealed class FakeShellInteraction : IShellInteraction
        {
            public Task<bool> ConfirmAsync(string title, string message, string okText, string cancelText, bool danger, string detail)
            {
                return Task.FromResult(false);
            }

            public Task ShowInfoAsync(string title, string message, string detail)
            {
                return Task.FromResult(0);
            }

            public void ShowToast(string title, string subtitle, ToastTone tone) { }

            public Task<IReadOnlyList<string>> PickFilesAsync()
            {
                return Task.FromResult<IReadOnlyList<string>>(new List<string>());
            }

            public Task<string> PickFolderAsync() { return Task.FromResult<string>(null); }

            public Task<string> PickFileAsync(string title, string filter, string initialDir) { return Task.FromResult<string>(null); }

            public void RevealInExplorer(string path) { }

            public void OpenFile(string path) { }

            public void OpenSettings(int page) { }

            public void FocusLodLevel(int levelIndex) { }

            public void FocusSearch() { }

            public void FlashWindow() { }

            public bool AnimationsEnabled { get { return true; } }
        }
    }
}
