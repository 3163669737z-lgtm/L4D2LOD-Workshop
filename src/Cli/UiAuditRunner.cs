using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using LodStudio.Cli.UiAudit;
using LodStudio.Controls;
using LodStudio.Engine;
using LodStudio.Services;

namespace LodStudio.Cli
{
    /// <summary>
    /// --ui-audit：界面审计（截图状态矩阵 + 布局审计 + 对比度审计 + 主题往返 + 三档缩放 + 附加断言）。
    ///
    /// 输出：&lt;out&gt;\*.png（全部截图）、&lt;out&gt;\audit.txt（全部指标与 PASS/FAIL 明细）；
    /// 关键截图另复制到 docs\screenshots\main\（可用 --shots-out 指定别处）。
    /// 退出码：0 = 全部检查通过，1 = 有失败项（失败明细写在 audit.txt 末尾）。
    ///
    /// 约定：
    ///  - 窗口放在屏幕外（Left = -20000），截图走 RenderTargetBitmap，不占用户屏幕；
    ///  - 设置目录重定向到 &lt;out&gt;\settings，全程不读写用户 %APPDATA% 的真实配置；
    ///  - 所有等待都在 UI 线程上"泵消息"，不阻塞（见 UiPump）。
    /// </summary>
    public static class UiAuditRunner
    {
        public static int Run(CommandLine cli)
        {
            UiMotion.SuppressContinuousAmbient = true;
            try { return RunCore(cli); }
            finally { UiMotion.SuppressContinuousAmbient = false; }
        }

        private static int RunCore(CommandLine cli)
        {
            string outDir = cli.OutDir;
            if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(AppInfo.TempRoot, "ui-audit");
            try { Directory.CreateDirectory(outDir); } catch { }

            // 设置/旧版设置目录都指向临时目录：审计绝不碰用户真实配置（窗口关闭时会 SaveSettings）
            try { Environment.SetEnvironmentVariable(AppInfo.SettingsDirEnvVar, Path.Combine(outDir, "settings")); } catch { }
            try { Environment.SetEnvironmentVariable("L4D2LOD_SETTINGS_DIR", Path.Combine(outDir, "legacy-empty")); } catch { }

            var report = new AuditReport();
            var swAll = Stopwatch.StartNew();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ShellAuditSession session = null;
            int exitCode = 1;

            // 兜底诊断：任何未处理异常都写进 progress.txt（否则 WinExe 直接消失，看不到原因）
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Progress(outDir, "UNHANDLED: " + e.ExceptionObject);
            };
            app.DispatcherUnhandledException += delegate (object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            {
                Progress(outDir, "DISPATCHER-UNHANDLED: " + e.Exception);
                e.Handled = true;
            };

            try
            {
                ThemeService.Initialize(ThemeKind.Light);
                Progress(outDir, "phase: 创建离屏窗口");
                session = ShellAuditSession.Create(outDir);

                // 诊断模式：--ui-audit --out DIR --probe pump
                // 只验证消息泵让 DispatcherTimer/Toast 动画按真实时间推进（不产出正式审计结果）。
                if (string.Equals(cli.GetOption("probe"), "pump", StringComparison.OrdinalIgnoreCase))
                {
                    ProbePump(session, report, outDir);
                    return 0;
                }
                // 诊断：--ui-audit --out DIR --probe plan   （计划视图内部布局/文本可见性）
                if (string.Equals(cli.GetOption("probe"), "plan", StringComparison.OrdinalIgnoreCase))
                {
                    ProbePlan(session, report, outDir);
                    return 0;
                }
                // 诊断：--ui-audit --out DIR --probe dialog
                if (string.Equals(cli.GetOption("probe"), "dialog", StringComparison.OrdinalIgnoreCase))
                {
                    ProbeDialog(session, report, outDir);
                    return 0;
                }
                if (string.Equals(cli.GetOption("probe"), "automatic", StringComparison.OrdinalIgnoreCase))
                {
                    ProbeAutomaticPreset(session, cli, outDir);
                    return 0;
                }

                WriteHeader(report, session, outDir);
                report.Info("窗口：" + session.Win.ActualWidth.ToString("0") + "×" +
                            session.Win.ActualHeight.ToString("0") + "（离屏 Left=-20000,Top=-20000,ShowInTaskbar=false）");
                report.Info("环境读数：已用 TestSetEnvStrings 固定为内置 meshoptimizer / 编译器未设置 / 普通权限（保证截图像素可复现）");
                report.Info("");

                // 先做主题往返（此时还没有任何 Toast/动画残留），再做状态矩阵
                RunPhase(report, outDir, "2. 主题往返", delegate { ScreenshotMatrix.RunThemeRoundTrip(session, report, outDir); });
                RunPhase(report, outDir, "1. 状态矩阵", delegate { ScreenshotMatrix.RunMatrix(session, report, outDir); });
                RunPhase(report, outDir, "3. 布局审计", delegate { LayoutAuditPhase.Run(session, report, outDir); });
                RunPhase(report, outDir, "4. 对比度审计", delegate { ContrastAuditor.Run(session, report); });
                RunPhase(report, outDir, "5. 附加断言", delegate { ExtraAssertions.Run(session, report); });
                RunPhase(report, outDir, "6. 三档缩放", delegate { ScreenshotMatrix.RunScaling(session, report, outDir); });
                RunPhase(report, outDir, "7. 导入、计划与写入交互", delegate { WorkflowAssertions.Run(session, report); });

                if (session.UsedForceClose)
                    report.Note("关闭对话框时用了 ForceCloseForTest 兜底（说明 DialogHost.ForceCloseForTest 未走 Finish/TCS 完成）。");
                if (session.ToastForceCleared)
                    report.Note("截图清理时用过强制清空 Toast 兜底（说明 ToastHost 计时器没让条目自然退场）。");
            }
            catch (Exception ex)
            {
                report.Fail("审计中断（FATAL）", ex.ToString());
                Progress(outDir, "FATAL: " + ex);
            }
            finally
            {
                Progress(outDir, "phase: 结束，关闭窗口 / 写报告");
                if (session != null)
                {
                    try { session.Dispose(); } catch { }
                }
                try { app.Shutdown(); } catch { }
            }

            swAll.Stop();

            // ── 截图清单 + 复制到 docs/screenshots/main ──
            List<string> shots = ListPngs(outDir);
            CopyKeyScreenshots(report, outDir, cli);

            // ── 汇总 ──
            report.Section("汇总");
            report.Info("状态数：" + ScreenshotMatrix.MatrixStates.Length + "（浅/暗各一套）");
            report.Info("截图数：" + shots.Count + " 张 PNG（含主题往返与三档离屏缩放）");
            report.Info("对比度：23 组组合 × 2 主题 = 46 项（其中 2 组为允许标注的辅助网格）");
            report.Info("耗时：" + swAll.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒");
            report.Info("输出目录：" + outDir);
            report.Info("复现命令：\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --ui-audit --out \"" + outDir + "\"");
            report.Info("");
            report.Info("检查汇总：PASS " + report.PassCount + " / FAIL " + report.FailCount + " / NOTE " + report.NoteCount);

            if (report.FailCount > 0)
            {
                report.Line("");
                report.Line("── 失败明细 " + new string('─', 56));
                for (int i = 0; i < report.Failures.Count; i++)
                    report.Line("  " + (i + 1).ToString("00", CultureInfo.InvariantCulture) + ". " + report.Failures[i]);
            }

            report.Line("");
            report.Line("UI-AUDIT: " + (report.FailCount == 0 ? "PASS" : report.FailCount + " failure(s)"));

            string text = report.ToText();
            try { File.WriteAllText(Path.Combine(outDir, "audit.txt"), text, new UTF8Encoding(false)); } catch { }
            try { Console.WriteLine(text); } catch { }

            exitCode = report.FailCount == 0 ? 0 : 1;
            return exitCode;
        }

        // ══════════════════════ 泵/Toast 诊断 ══════════════════════

        /// <summary>
        /// 诊断：显示一条 Toast，然后每 400ms 采样一次 Toast 条数，验证它是否在 ~4.3s 内自然退场。
        /// 用 --ui-audit --out DIR --probe pump 触发（不产出正式审计结果）。
        /// </summary>
        private static void ProbePump(ShellAuditSession session, AuditReport report, string outDir)
        {
            Progress(outDir, "probe: toast 生命周期");
            session.ResetToRestState();
            session.Win.TestShowToast("探针 · Toast", "验证 DispatcherTimer 是否按真实时间推进", ToastTone.Ok);
            var sw = Stopwatch.StartNew();
            var samples = new List<string>();
            for (int i = 0; i < 22; i++)
            {
                UiPump.For(400);
                samples.Add(sw.ElapsedMilliseconds + "ms=" + session.ToastCount);
                if (session.ToastCount <= 0) break;
            }
            string line = "Toast 条数采样：" + string.Join(" ", samples.ToArray());
            report.Line(line);
            Progress(outDir, line);
            UiPump.For(200);
            session.WaitToastGone(3000);
            string after = "强制清理后：" + session.ToastCount + "（ForceCleared=" + session.ToastForceCleared + "）";
            report.Line(after);
            Progress(outDir, after);
        }

        /// <summary>诊断：计划视图内部（PlanOverlay 被正式审计排除，这里单独看它内部有没有重叠/文本缺失）。</summary>
        private static void ProbePlan(ShellAuditSession session, AuditReport report, string outDir)
        {
            Progress(outDir, "probe: 计划视图");
            session.ResetToRestState();
            session.AddMixedTasks();
            session.Vm.TestSetPlanPreview(6, 12, 24);
            UiPump.For(420);

            var overlay = session.Named("PlanOverlay") as FrameworkElement;
            if (overlay == null) { report.Line("找不到 PlanOverlay"); return; }
            report.Line("PlanOverlay 可见=" + overlay.IsVisible + "  尺寸=" + overlay.ActualWidth.ToString("0.#") + "×" + overlay.ActualHeight.ToString("0.#") + "  Rows=" + session.Vm.PlanPreview.Rows.Count);

            var res = LayoutAuditor.Audit(overlay, "plan-overlay");
            report.Line("内部布局：" + res.Summary);
            foreach (string d in res.OverlapDetails) report.Line("  " + d);
            foreach (string d in res.OutOfBoundsDetails) report.Line("  " + d);

            var texts = new List<System.Windows.Controls.TextBlock>();
            UiTree.CollectAll<System.Windows.Controls.TextBlock>(overlay, texts);
            report.Line("TextBlock 共 " + texts.Count + " 个：");
            for (int i = 0; i < texts.Count && i < 40; i++)
            {
                var t = texts[i];
                report.Line("  '" + (t.Text ?? "").Replace("\n", "\\n") + "'  visible=" + t.IsVisible +
                            "  " + t.ActualWidth.ToString("0.#") + "×" + t.ActualHeight.ToString("0.#"));
            }
            foreach (string line in report.ToText().Split('\n')) Progress(outDir, line);
        }

        /// <summary>诊断：三种对话框内部元素矩形（确认按钮是否在这些可视范围内）。</summary>
        private static void ProbeDialog(ShellAuditSession session, AuditReport report, string outDir)
        {
            Progress(outDir, "probe: 对话框");
            session.ResetToRestState();
            session.AddMixedTasks();
            session.OpenInfo("运行环境诊断",
                "减面后端：内置 meshoptimizer\n编译器 nekomdl：未设置（可选）\n权限：普通权限",
                "配置文件：%APPDATA%\\L4D2LOD远景工坊\\settings.ini\n临时目录：%TEMP%\\L4D2LOD远景工坊\n日志：12 条 · 0 错误");
            // 打开后按 150/400/900ms 三个时刻检查主按钮是否可见（排除"截图太早"的可能）
            foreach (int delay in new[] { 150, 250, 500 })
            {
                UiPump.For(delay);
                var host = session.Dialogs;
                var panel = ReadPrivateField(host, "_panel") as FrameworkElement;
                report.Line("[info +" + delay + "ms] hostH=" + host.ActualHeight.ToString("0.#") +
                            " panel=" + (panel == null ? "?" : panel.ActualWidth.ToString("0.#") + "×" + panel.ActualHeight.ToString("0.#") +
                                         " maxH=" + panel.MaxHeight.ToString("0.#")));
                foreach (FrameworkElement fe in AllButtons(host))
                {
                    var b = fe as System.Windows.Controls.Button;
                    string content = b != null ? (b.Content as string ?? "?") : fe.GetType().Name;
                    Rect r = session.RectOf(fe);
                    bool insidePanel = panel != null && !panel.Equals(fe) && UiTree.IntersectArea(r, session.RectOf(panel)) > 0.5;
                    report.Line("    button '" + content + "' visible=" + fe.IsVisible + " rect=" + UiTree.RectText(r) +
                                " inPanel=" + insidePanel);
                }
                foreach (string line in report.ToText().Split('\n')) Progress(outDir, line);
            }
            DumpDialog(session, report, outDir, "info");
            session.CloseDialogs();
            session.OpenConfirmDanger("移除选中的 3 项任务？", "只会从列表里移除，不会删除任何文件；再次导入即可恢复。", "8人自动编译\n武器包\n道具包");
            UiPump.For(260);
            DumpDialog(session, report, outDir, "confirm");
            session.CloseDialogs();
            session.OpenPrompt("输入名称", "名称", "测试名称");
            UiPump.For(260);
            DumpDialog(session, report, outDir, "prompt");
            session.CloseDialogs();
        }

        private static void ProbeAutomaticPreset(ShellAuditSession session, CommandLine cli, string outDir)
        {
            var picker = session.Named("CmbPreset") as System.Windows.Controls.ComboBox;
            if (picker == null || !picker.Items.Cast<string>().SequenceEqual(new[] { "保守", "平衡", "激进", "自定义", "自动" }))
                throw new InvalidOperationException("主界面预设列表不正确");
            picker.SelectedIndex = PresetLibrary.AutomaticIndex;
            UiPump.Frames(3);
            var options = session.Vm.BuildRunOptions(outDir);
            if (!options.Automatic || options.Plan != null || session.Named("LodTableHost").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("自动预设未切换到按模型估算");
            session.Capture(Path.Combine(outDir, "automatic-preset.png"));

            if (cli.Paths.Count == 0) return;
            var import = session.Win.Dispatcher.InvokeAsync(new Func<Task>(() => session.Vm.AddPathsAsync(cli.Paths))).Task.Unwrap();
            if (!UiPump.WaitTask(import, 15000)) throw new TimeoutException("导入超时");
            import.GetAwaiter().GetResult();
            var preview = session.Win.Dispatcher.InvokeAsync(new Func<Task>(() => session.Vm.PreviewPlanAsync())).Task.Unwrap();
            if (!UiPump.WaitTask(preview, 15000)) throw new TimeoutException("扫描预览超时");
            preview.GetAwaiter().GetResult();
            var plan = session.Vm.PlanPreview?.Plan;
            if (plan == null || !plan.Automatic || plan.ErrorCount != 0)
                throw new InvalidOperationException("原主界面的扫描预览未生成自动计划");
            foreach (var qc in plan.QcPlans)
                Console.WriteLine(Path.GetFileName(qc.QcPath) + " · " + qc.SourceTriangles + " 面 · " +
                    (qc.Levels?.Count ?? 0) + " 个候选档");
            session.Capture(Path.Combine(outDir, "automatic-plan.png"));
        }

        private static List<FrameworkElement> AllButtons(DependencyObject root)
        {
            var all = new List<FrameworkElement>();
            var buttons = new List<System.Windows.Controls.Button>();
            UiTree.CollectAll<System.Windows.Controls.Button>(root, buttons);
            foreach (var b in buttons) all.Add(b);
            return all;
        }

        private static object ReadPrivateField(object target, string name)
        {
            try
            {
                if (target == null) return null;
                var f = target.GetType().GetField(name,
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return f != null ? f.GetValue(target) : null;
            }
            catch { return null; }
        }

        private static void DumpDialog(ShellAuditSession session, AuditReport report, string outDir, string tag)
        {
            var host = session.Dialogs;
            report.Line("[" + tag + "] DialogHost " + host.ActualWidth.ToString("0.#") + "×" + host.ActualHeight.ToString("0.#") +
                        " IsOpen=" + host.IsOpen);
            var hostRect = session.RectOf(host);
            report.Line("[" + tag + "] host rect " + UiTree.RectText(hostRect));
            var all = new List<FrameworkElement>();
            UiTree.CollectAll<FrameworkElement>(host, all);
            int shown = 0;
            foreach (var fe in all)
            {
                if (fe == null || !fe.IsVisible) continue;
                if (!(fe is System.Windows.Controls.Button) && !(fe is System.Windows.Controls.Border) &&
                    !(fe is System.Windows.Controls.TextBlock) && !(fe is System.Windows.Controls.ScrollViewer)) continue;
                Rect r = session.RectOf(fe);
                if (r.IsEmpty) continue;
                string text = "";
                var b = fe as System.Windows.Controls.Button;
                if (b != null) text = " content='" + (b.Content as string ?? "?") + "'";
                var tb = fe as System.Windows.Controls.TextBlock;
                if (tb != null) text = " text='" + (tb.Text ?? "").Replace("\n", "\\n") + "'";
                report.Line("  " + UiTree.Describe(fe) + text + "  " + UiTree.RectText(r));
                if (++shown > 26) break;
            }
            foreach (string line in report.ToText().Split('\n')) Progress(outDir, line);
        }

        // ══════════════════════ 阶段 / 进度 ══════════════════════

        /// <summary>跑一个阶段：异常只记该阶段失败，不影响其它阶段（WinExe 崩溃时 progress.txt 能定位到阶段）。</summary>
        private static void RunPhase(AuditReport report, string outDir, string name, Action action)
        {
            Progress(outDir, "phase start: " + name);
            try
            {
                action();
                Progress(outDir, "phase done : " + name);
            }
            catch (Exception ex)
            {
                report.Fail("阶段「" + name + "」异常", ex.ToString());
                Progress(outDir, "PHASE-FAIL " + name + ": " + ex);
            }
        }

        private static void Progress(string outDir, string message)
        {
            try
            {
                File.AppendAllText(Path.Combine(outDir, "progress.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
            try { Console.WriteLine("[progress] " + message); } catch { }
        }

        // ══════════════════════ 头部 ══════════════════════

        private static void WriteHeader(AuditReport report, ShellAuditSession session, string outDir)
        {
            report.Line(AppInfo.TitleText + "  —  ui-audit（状态矩阵 / 布局 / 对比度 / 主题往返 / 缩放）");
            report.Line("time : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            report.Line("out  : " + outDir);
            report.Line("exe  : " + Path.Combine(AppInfo.ExeDir, "L4D2LOD远景工坊.exe"));
            report.Line("设置 : " + AppInfo.SettingsDir + "（已重定向到输出目录下的临时设置，不碰用户配置）");
            report.Line("动画 : ClientAreaAnimation=" + SafeAnimationFlag());
            report.Line("");
        }

        private static string SafeAnimationFlag()
        {
            try { return SystemParameters.ClientAreaAnimation ? "on" : "off"; }
            catch { return "unknown"; }
        }

        // ══════════════════════ 截图清单 / 复制 ══════════════════════

        private static List<string> ListPngs(string dir)
        {
            var list = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.png"))
                    list.Add(Path.GetFileName(f));
            }
            catch { }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        private static readonly string[] KeyStates =
        {
            "empty", "tasks", "running", "done", "failed", "plan", "log",
            "dialog_confirm", "toast", "eight", "min", "wide", "auto_plan", "stale_plan", "cancelled"
        };

        /// <summary>把关键截图复制到 docs\screenshots\main\（没有 docs 目录时只记录 NOTE）。</summary>
        private static void CopyKeyScreenshots(AuditReport report, string outDir, CommandLine cli)
        {
            string target = cli.GetOption("shots-out");
            if (string.IsNullOrEmpty(target))
            {
                string projectRoot = null;
                try { projectRoot = Path.GetFullPath(Path.Combine(AppInfo.ExeDir, "..")); } catch { }
                if (!string.IsNullOrEmpty(projectRoot) && Directory.Exists(Path.Combine(projectRoot, "docs")))
                    target = Path.Combine(projectRoot, "docs", "screenshots", "main");
            }

            report.Section("截图归档");
            if (string.IsNullOrEmpty(target))
            {
                report.Note("没有找到 docs 目录，关键截图未复制；可加 --shots-out <目录> 指定位置。" +
                            "（当前输出目录已包含全部 PNG：" + outDir + "）");
                return;
            }

            try { Directory.CreateDirectory(target); }
            catch (Exception ex)
            {
                report.Note("无法创建截图目录 " + target + " : " + ex.Message);
                return;
            }

            int copied = 0;
            var missing = new List<string>();
            foreach (string state in KeyStates)
            {
                foreach (string theme in new[] { "light", "dark" })
                {
                    string file = state + "_" + theme + ".png";
                    string src = Path.Combine(outDir, file);
                    if (!File.Exists(src)) { missing.Add(file); continue; }
                    try
                    {
                        File.Copy(src, Path.Combine(target, file), true);
                        copied++;
                    }
                    catch (Exception ex)
                    {
                        missing.Add(file + "（" + ex.Message + "）");
                    }
                }
            }

            report.Info("归档目录：" + target);
            report.Info("已复制：" + copied + " 张（" + string.Join(", ", KeyStates) + " × 浅/暗）");
            report.Check("关键截图已归档", missing.Count == 0,
                copied + " 张，缺失 " + missing.Count + (missing.Count == 0 ? "" : "：" + string.Join(", ", missing.ToArray())));
        }
    }
}
