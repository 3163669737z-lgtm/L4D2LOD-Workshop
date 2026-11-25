using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LodStudio.Cli.UiAudit;
using LodStudio.Engine;
using LodStudio.Services;

namespace LodStudio.Cli
{
    /// <summary>Renders the studio pages offscreen and reports layout issues for each capture.</summary>
    public static class PagesPreviewRunner
    {
        private static readonly (string Name, ThemeKind Theme, int Page, int SubPage, bool Compact)[] Captures =
        {
            ("page0_workbench_dark",         ThemeKind.Dark,  0, 0, false),
            ("page0_workbench_light",        ThemeKind.Light, 0, 0, false),
            ("page0_workbench_compact_dark", ThemeKind.Dark,  0, 0, true),
            ("page1_strategy_sub0_dark",     ThemeKind.Dark,  1, 0, false),
            ("page1_strategy_sub0_light",    ThemeKind.Light, 1, 0, false),
            ("page1_strategy_sub1_dark",     ThemeKind.Dark,  1, 1, false),
            ("page1_strategy_sub2_dark",     ThemeKind.Dark,  1, 2, false),
            ("page2_toolkit_sub0_dark",      ThemeKind.Dark,  2, 0, false),
            ("page2_toolkit_sub1_dark",      ThemeKind.Dark,  2, 1, false),
            ("page3_prism_sub0_dark",        ThemeKind.Dark,  3, 0, false),
            ("page3_prism_sub0_light",       ThemeKind.Light, 3, 0, false),
            ("page3_prism_sub1_dark",        ThemeKind.Dark,  3, 1, false)
        };

        public static int Run(CommandLine cli)
        {
            const string legacySettingsEnvVar = "L4D2LOD_SETTINGS_DIR";
            string originalSettingsDir = Environment.GetEnvironmentVariable(AppInfo.SettingsDirEnvVar);
            string originalLegacyDir = Environment.GetEnvironmentVariable(legacySettingsEnvVar);
            bool originalAmbientSuppression = UiMotion.SuppressContinuousAmbient;
            var log = new List<string>();
            string outDir = null;
            Application app = null;
            int images = 0, layoutIssues = 0, failures = 0;

            try
            {
                outDir = Path.GetFullPath(string.IsNullOrEmpty(cli.OutDir)
                    ? Path.Combine(AppInfo.TempRoot, "pages-preview")
                    : cli.OutDir);
                string settingsDir = Path.Combine(outDir, "_settings");
                Directory.CreateDirectory(settingsDir);
                Environment.SetEnvironmentVariable(AppInfo.SettingsDirEnvVar, settingsDir);
                Environment.SetEnvironmentVariable(legacySettingsEnvVar, settingsDir);
                UiMotion.SuppressContinuousAmbient = true;

                var seed = new AppSettings
                {
                    NekomdlPath = Nekomdl.Find(null) ?? "",
                    ThemeMode = 1,
                    GenerateShadowLod = true,
                    NoFacial = true,
                    CollapseBones = "ValveBiped.Bip01_L_Hand|ValveBiped.Bip01_R_Hand",
                    SkipMainModel = true,
                    BackupEnabled = true,
                    BackupLocation = 2,
                    ReportLocation = 2,
                    AdaptiveTriangleBudget = true,
                    PrismAccent = 0,
                    MotionTier = 3
                };
                SettingsStore.Save(seed);
                if (!File.Exists(SettingsStore.FilePath))
                    throw new IOException("Could not save preview settings to " + SettingsStore.FilePath);

                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ThemeService.Initialize(ThemeKind.Dark);
                using (var session = ShellAuditSession.Create(outDir))
                {
                    if (File.Exists(seed.NekomdlPath))
                    {
                        var vm = session.Vm;
                        vm.TestSetEnvStrings(vm.ReductionText, vm.ReductionTone, "已设置", "Ok",
                            vm.PermissionText, vm.PermissionTone);
                    }
                    session.AddMixedTasks();
                    foreach (var capture in Captures)
                    {
                        try
                        {
                            double width = capture.Compact ? 960 : ShellAuditSession.DefaultWidth;
                            double height = capture.Compact ? 550 : ShellAuditSession.DefaultHeight;
                            session.SetSize(width, height);
                            session.Vm.Settings.ThemeMode = capture.Theme == ThemeKind.Dark ? 1 : 0;
                            if (ThemeService.Current != capture.Theme) session.SetTheme(capture.Theme);
                            session.Win.TestSetAuditRevealAllChildren(capture.Page == 1 && capture.SubPage == 0);
                            session.Win.TestSelectMainPage(capture.Page, capture.SubPage);
                            UiPump.Frames(2);

                            LayoutAuditResult audit = LayoutAuditor.Audit(session.Root, capture.Name);
                            layoutIssues += audit.Overlaps + audit.OutOfBounds;
                            log.Add($"{capture.Name}: {audit.Overlaps} overlap(s), {audit.OutOfBounds} out-of-bounds");
                            log.AddRange(audit.OverlapDetails);
                            log.AddRange(audit.OutOfBoundsDetails);

                            if (Math.Round(session.Win.ActualWidth) != width || Math.Round(session.Win.ActualHeight) != height)
                                throw new InvalidOperationException($"Expected {width}×{height}, got {session.Win.ActualWidth}×{session.Win.ActualHeight}");
                            string path = Path.Combine(outDir, capture.Name + ".png");
                            session.Capture(path);
                            if (new FileInfo(path).Length == 0)
                                throw new IOException("The PNG is empty.");
                            images++;
                            if (capture.Compact) VerifyCompactNav(session);
                        }
                        catch (Exception ex)
                        {
                            failures++;
                            log.Add($"[FAIL] {capture.Name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.Add("[FAIL] " + ex);
            }
            finally
            {
                try
                {
                    app?.Shutdown();
                }
                finally
                {
                    Environment.SetEnvironmentVariable(legacySettingsEnvVar, originalLegacyDir);
                    Environment.SetEnvironmentVariable(AppInfo.SettingsDirEnvVar, originalSettingsDir);
                    UiMotion.SuppressContinuousAmbient = originalAmbientSuppression;
                }
            }

            string Summary() => $"PAGES-PREVIEW: {images} images, {layoutIssues} layout issue(s), {failures} failure(s)";
            if (outDir != null)
            {
                try
                {
                    using (var report = File.CreateText(Path.Combine(outDir, "pages-preview.txt")))
                    {
                        foreach (string line in log) report.WriteLine(line);
                        report.WriteLine(Summary());
                    }
                }
                catch (Exception ex)
                {
                    failures++;
                    log.Add("[FAIL] Could not write pages-preview.txt: " + ex.Message);
                }
            }
            foreach (string line in log) Console.WriteLine(line);
            Console.WriteLine(Summary());
            return layoutIssues == 0 && failures == 0 ? 0 : 2;
        }

        private static void VerifyCompactNav(ShellAuditSession session)
        {
            var rail = (ColumnDefinition)session.Win.FindName("NavRailColumn");
            if (rail == null || Math.Abs(rail.ActualWidth - 56) > 0.5)
                throw new InvalidOperationException("Expected the compact navigation column to be 56 px wide.");
            for (int i = 0; i < 4; i++)
            {
                var label = (FrameworkElement)session.Win.FindName("NavLabel" + i);
                if (label == null || label.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("Expected all compact navigation labels to be collapsed.");
            }
        }
    }
}
