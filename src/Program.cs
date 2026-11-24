using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LodStudio.Cli;
using LodStudio.Services;

namespace LodStudio
{
    /// <summary>
    /// 程序入口。GUI 是主形态；CLI 模式服务于自动化测试、回归与高级用户。
    /// 注意：输出到 stdout 的模式在 WinExe 下需要调用方重定向管道（测试脚本都这样做），
    /// 同时把结果写进 --out 指定的文件，便于人工核对。
    /// </summary>
    internal static class Program
    {
        private const string MutexName = @"Local\L4D2LOD远景工坊.v7";

        [STAThread]
        private static int Main(string[] args)
        {
            Engine.PerfTrace.Mark("main-entry");
            try { Engine.TempCleaner.CleanStale(); } catch { }
            Engine.PerfTrace.Mark("temp-clean");
            var cli = CommandLine.Parse(args);
#if CONSOLE_BUILD
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            if (args.Length == 0) cli.Mode = CliMode.Help;
#endif
            try
            {
                switch (cli.Mode)
                {
                    case CliMode.Version:
                        Console.WriteLine(AppInfo.VersionTextForCli);
                        return 0;
                    case CliMode.Help:
                        Console.WriteLine(cli.Usage);
                        return 0;
                    case CliMode.SelfTest:
                        return SelfTestRunner.Run(cli);
                    case CliMode.UiAudit:
                        return UiAuditRunner.Run(cli);
                    case CliMode.IconSheet:
                        return IconSheetRunner.Run(cli);
                    case CliMode.WidgetPreview:
                        return WidgetPreviewRunner.Run(cli);
                    case CliMode.SettingsPreview:
                        return SettingsPreviewRunner.Run(cli);
                    case CliMode.Plan:
                    case CliMode.Batch:
                        return RunCliWithInstanceMutex(cli);
                    default:
                        return RunGui(cli);
                }
            }
            catch (Exception ex)
            {
                TryWriteCrashLog(ex, cli.Mode.ToString());
                TryConsole("FATAL: " + ex);
                return 3;
            }
        }

        /// <summary>
        /// 命令行批处理也持有单实例互斥：界面正在运行（或在跑另一个批量）时拒绝启动，
        /// 避免两个进程同时改同一批 QC。
        /// </summary>
        private static int RunCliWithInstanceMutex(CommandLine cli)
        {
            using (var mutex = new Mutex(false, MutexName))
            {
                bool owned = false;
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    Console.WriteLine("已有「远景工坊」实例在运行（界面或另一个批量任务）。");
                    Console.WriteLine("为避免两个进程同时修改同一批 QC，命令行模式需要先关闭它再运行。");
                    return 2;
                }
                try
                {
                    return cli.Mode == CliMode.Plan ? BatchCommand.RunPlan(cli) : BatchCommand.RunBatch(cli);
                }
                finally
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        private static int RunGui(CommandLine cli)
        {
            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    // 已有实例：把新实例的路径交给它（写一个投递文件，已有实例会轮询），然后退出。
                    SingleInstance.DeliverToRunningInstance(cli.Paths);
                    SingleInstance.ActivateExistingWindow();
                    return 0;
                }

                var app = new Application
                {
                    ShutdownMode = ShutdownMode.OnMainWindowClose
                };
                app.DispatcherUnhandledException += OnDispatcherUnhandledException;

                var settings = Engine.SettingsStore.Load();
                Engine.PerfTrace.Mark("settings-loaded");
                ThemeService.Initialize(ResolveTheme(settings));
                Engine.PerfTrace.Mark("theme-init");
                var window = new Views.ShellWindow();
                Engine.PerfTrace.Mark("window-ctor");
                app.MainWindow = window;
                window.ApplyCliPaths(cli.Paths);
                app.Run(window);
                Engine.PerfTrace.Flush();
                return 0;
            }
        }

        private static ThemeKind ResolveTheme(Engine.AppSettings settings)
        {
            if (settings == null) return ThemeKind.Light;
            if (settings.ThemeMode == 1) return ThemeKind.Dark;
            if (settings.ThemeMode == 2 && ThemeService.SystemPrefersDark()) return ThemeKind.Dark;
            return settings.ThemeMode == 2 ? ThemeKind.Light : ThemeKind.Light;
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            TryWriteCrashLog(e.Exception, "UI");
            try
            {
                MessageBar.ShowUnhandled(e.Exception);
                e.Handled = true;
            }
            catch { }
        }

        internal static void TryWriteCrashLog(Exception ex, string where)
        {
            try
            {
                Directory.CreateDirectory(AppInfo.TempRoot);
                string path = Path.Combine(AppInfo.TempRoot,
                    "crash_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                File.WriteAllText(path,
                    AppInfo.TitleText + Environment.NewLine +
                    "time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                    "where: " + where + Environment.NewLine +
                    "os: " + Environment.OSVersion.VersionString + "  clr: " + Environment.Version +
                    "  " + AppInfo.Bitness + Environment.NewLine +
                    "---- exception ----" + Environment.NewLine + ex);
            }
            catch { }
        }

        internal static void TryConsole(string s)
        {
            try { Console.WriteLine(s); } catch { }
        }
    }
}
