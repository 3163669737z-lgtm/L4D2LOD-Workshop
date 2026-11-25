using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LodStudio.Engine;

namespace LodStudio.Cli
{
    /// <summary>
    /// --plan / --batch：无界面调用引擎（与 GUI 完全同一套计划器/执行器），给自动化回归与高级用户用。
    /// 输出：stdout（需调用方重定向）+ &lt;out&gt;\plan.json / batch.json / 报告 / 日志。
    /// 退出码：0 成功；1 运行失败（有错误项）；2 参数/环境错误。
    /// </summary>
    public static class BatchCommand
    {
        public static int RunPlan(CommandLine cli)
        {
            if (!TryInputs(cli, out var tasks, out string root))
            {
                Console.WriteLine("PLAN: 请指定现有的工程目录、QC、GLB、SMD 或 DMX。");
                return 2;
            }
            string outDir = EnsureOut(cli, "plan");
            var obs = new CollectingObserver(s => Console.WriteLine(s));
            var options = BuildOptions(cli, root, SettingsStore.Load(), obs);
            if (options == null) return 2;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var plan = BatchPlanner.Build(tasks, root, options, obs, CancellationToken.None);
            sw.Stop();

            string json = PlanToJson(plan, options);
            File.WriteAllText(Path.Combine(outDir, "plan.json"), json, new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine("PLAN: " + plan.QcPlans.Count + " QC · " + plan.Models.Count + " 模型 · " + plan.JobCount + " 任务 · " +
                              plan.ProcessCount + " 处理 / " + plan.SkipCount + " 跳过 / " + plan.ErrorCount + " 错误 · " +
                              sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");
            Console.WriteLine("PLAN JSON -> " + Path.Combine(outDir, "plan.json"));
            return plan.ErrorCount > 0 || plan.Tasks.Any(t => t.Error != null) ? 1 : 0;
        }

        public static int RunBatch(CommandLine cli)
        {
            if (!TryInputs(cli, out var tasks, out string root))
            {
                Console.WriteLine("BATCH: 请指定现有的工程目录、QC、GLB、SMD 或 DMX。");
                return 2;
            }
            string outDir = EnsureOut(cli, "batch");
            var obs = new CollectingObserver(s => Console.WriteLine(s));
            var options = BuildOptions(cli, root, SettingsStore.Load(), obs);
            if (options == null) return 2;
            options.ReportDir = !string.IsNullOrEmpty(cli.GetOption("report-dir")) ? cli.GetOption("report-dir") : outDir;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var plan = BatchPlanner.Build(tasks, root, options, obs, CancellationToken.None);
            File.WriteAllText(Path.Combine(outDir, "plan.json"), PlanToJson(plan, options), new UTF8Encoding(false));

            BatchOutcome outcome = null;
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += delegate (object s, ConsoleCancelEventArgs e) { e.Cancel = true; try { cts.Cancel(); } catch { } try { ReductionScheduler.KillAll(); } catch { } };
                outcome = BatchRunner.Run(plan, options, obs, cts.Token);
            }
            sw.Stop();

            File.WriteAllText(Path.Combine(outDir, "batch.json"), OutcomeToJson(outcome, plan), new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine("BATCH: " + (outcome.Ok ? "OK" : "FAILED") + " · " +
                              outcome.Stats.InjectedQcCount + " QC 写入 · " + outcome.GeneratedModels + " 个独立模型 · " + outcome.Stats.PublishedLods + " 个 LOD 网格 · " +
                              "用时 " + sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");
            if (outcome.Stats.ShadowLodQcsWritten > 0 || outcome.Stats.ShadowLodsPublished > 0)
                Console.WriteLine("$shadowlod：写入 " + outcome.Stats.ShadowLodQcsWritten + " 个 QC（" +
                    outcome.Stats.ShadowLodReusedQcs + " 个 QC 含复用），发布 " + outcome.Stats.ShadowLodsPublished + " 个独立阴影网格");
            if (!string.IsNullOrEmpty(outcome.ReportPath)) Console.WriteLine("报告: " + outcome.ReportPath);
            if (!string.IsNullOrEmpty(outcome.LogPath)) Console.WriteLine("日志: " + outcome.LogPath);
            foreach (var e in outcome.Errors.Take(20)) Console.WriteLine("  ERR " + e);
            return outcome.Ok ? 0 : 1;
        }

        // ─────────────────────── 选项 ───────────────────────

        internal static BatchRunOptions BuildOptions(CommandLine cli, string rootDir, AppSettings settings, CollectingObserver obs)
        {
            int settingsProfile = settings.EffectiveReductionProfile();
            int profile = settingsProfile;
            if (cli.HasFlag("profile"))
                profile = Math.Max(0, Math.Min(2, cli.GetIntOption("profile", settingsProfile)));
            else if (cli.HasFlag("fast"))
                profile = 2;
            else if (cli.HasFlag("quality"))
                profile = Math.Min(1, settingsProfile);

            double shadowRatio = cli.GetDoubleOption("shadow-ratio", settings.ShadowLodRatio);
            var shadowMode = cli.GetOption("shadow-mode")?.Trim().ToLowerInvariant() switch
            {
                "dedicated" or "independent" or "0" => ShadowLodMode.Dedicated,
                "reuse" or "existing" or "1" => ShadowLodMode.ReuseExistingLod,
                _ => (ShadowLodMode)Math.Max(0, Math.Min(1, settings.ShadowLodMode))
            };
            int reuseLevel = string.Equals(cli.GetOption("shadow-reuse-level")?.Trim(), "last", StringComparison.OrdinalIgnoreCase)
                ? 0 : cli.GetIntOption("shadow-reuse-level", settings.ShadowLodReuseLevel);
            int mode = cli.GetIntOption("mode", settings.OutputMode);
            var opts = new BatchRunOptions
            {
                NekomdlPath = cli.GetOption("nekomdl", settings.NekomdlPath),
                Workers = cli.GetIntOption("workers", settings.Workers),
                QualityFirst = profile != 2,
                ReductionProfile = profile,
                GenerateShadowLod = !cli.HasFlag("no-shadowlod") && (cli.HasFlag("shadowlod") || settings.GenerateShadowLod),
                ShadowLodRatio = double.IsNaN(shadowRatio) ? 0.20 : Math.Max(0.05, Math.Min(0.60, shadowRatio)),
                ShadowLodMode = shadowMode,
                ShadowLodReuseLevel = Math.Max(0, Math.Min(QcWriter.MaxVisibleLodsWithShadow, reuseLevel)),
                AutoTargetTriangles = Math.Max(1000, Math.Min(200000, cli.GetIntOption("auto-target", settings.AutoTargetTriangles))),
                AutoMinSavingsPercent = Math.Max(1, Math.Min(50, cli.GetIntOption("auto-min-savings", settings.AutoMinSavingsPercent))),
                AdaptiveTriangleBudget = !cli.HasFlag("legacy-budget") &&
                                         (cli.HasFlag("density-budget") || settings.AdaptiveTriangleBudget),
                AllowVertexUpdates = !cli.HasFlag("fixed-vertices") &&
                                     (cli.HasFlag("vertex-update") || settings.AllowVertexUpdates),
                KeepOriginalModels = cli.GetOption("keep-models", settings.KeepOriginalModels),
                Force = cli.HasFlag("force"),
                CompileAfter = cli.HasFlag("compile"),
                NoFacial = !cli.HasFlag("facial") && (cli.HasFlag("nofacial") || settings.NoFacial),
                NoFacialFromLevel = Math.Max(1, Math.Min(LodPlan.MaxLevels, cli.GetIntOption("nofacial-from", settings.NoFacialFromLevel))),
                CollapseBones = cli.GetOption("collapse-bones", settings.CollapseBones),
                CollapseBonesFromLevel = Math.Max(1, Math.Min(LodPlan.MaxLevels, cli.GetIntOption("collapse-bones-from", settings.CollapseBonesFromLevel))),
                SkipMainModel = cli.HasFlag("skip-main"),
                OutputMode = (OutputMode)Math.Max(0, Math.Min(2, mode)),
                BackupEnabled = !cli.HasFlag("no-backup"),
                ProjectRoot = rootDir,
                ReportDir = null,
                WriteReport = true
            };
            opts.MinTriangles = cli.GetIntOption("min-triangles", opts.AdaptiveTriangleBudget ? 0 : settings.MinTriangles);
            if (cli.HasFlag("shrink-body"))
                obs.Log(LogLevel.Warn, "Body 内缩选项已停用。可用 --keep-models 为指定文件沿用原网格。");
            string backend = cli.GetOption("backend", "gltfpack").Trim();
            if (string.Equals(backend, "blender", StringComparison.OrdinalIgnoreCase))
                obs.Log(LogLevel.Info, "旧 Blender 选项已迁移到内置 meshoptimizer。");
            else if (backend.Length != 0 && !string.Equals(backend, "gltfpack", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("BATCH: --backend 支持 gltfpack（内置 meshoptimizer）。");
                return null;
            }
            int presetIndex = cli.GetIntOption("preset", PresetLibrary.DefaultIndex);
            bool autoPreset = cli.HasFlag("preset") && presetIndex == PresetLibrary.AutomaticIndex;
            bool manual = cli.HasFlag("preset") && !autoPreset || cli.HasFlag("dist") || cli.HasFlag("ratio") || cli.HasFlag("levels");
            opts.Automatic = cli.HasFlag("auto") || autoPreset || !manual;
            if (opts.Automatic)
            {
                if (manual || opts.CompileAfter || opts.Force || cli.HasFlag("nofacial") || cli.HasFlag("no-backup") ||
                    (cli.HasFlag("mode") && mode != (int)OutputMode.InPlace))
                {
                    Console.WriteLine("自动模式使用原 QC 写入、备份和表情保护。已有手工参数的脚本可显式指定 --preset 或 --dist 与 --ratio。");
                    return null;
                }
                opts.OutputMode = OutputMode.InPlace;
                opts.NoFacial = false;
                opts.AdaptiveTriangleBudget = !cli.HasFlag("legacy-budget");
                opts.AllowVertexUpdates = cli.HasFlag("vertex-update") && !cli.HasFlag("fixed-vertices");
                opts.KeepOriginalModels = cli.GetOption("keep-models", "");
                opts.MinTriangles = cli.GetIntOption("min-triangles", 0);
                if (opts.SkipMainModel) opts.SkipMainQcPaths = null;
                return opts;
            }
            if (opts.CompileAfter && string.IsNullOrEmpty(opts.NekomdlPath)) opts.NekomdlPath = Nekomdl.Find(null);
            if (opts.SkipMainModel)
                opts.SkipMainQcPaths = null;

            // 方案
            var all = PresetLibrary.Builtins();
            LodPlan plan;
            string dist = cli.GetOption("dist");
            string ratio = cli.GetOption("ratio");
            if (!string.IsNullOrEmpty(dist) && !string.IsNullOrEmpty(ratio))
            {
                var d = ParseDoubles(dist);
                var r = ParseDoubles(ratio);
                if (d.Count != r.Count || d.Count == 0)
                {
                    Console.WriteLine("BATCH: --dist 与 --ratio 数量不一致。");
                    return null;
                }
                plan = new LodPlan();
                for (int i = 0; i < d.Count; i++) plan.Levels.Add(new LodLevel(d[i], r[i] / 100.0));
                opts.PresetName = PresetLibrary.CustomName;
            }
            else
            {
                if (presetIndex < 0 || presetIndex > PresetLibrary.CustomIndex)
                {
                    Console.WriteLine("BATCH: --preset 支持 0 保守、1 平衡、2 激进、3 自定义、4 自动。");
                    return null;
                }
                if (presetIndex == PresetLibrary.CustomIndex)
                {
                    plan = settings.GetCustomPlan();
                    opts.PresetName = PresetLibrary.CustomName;
                }
                else
                {
                    plan = LodPlan.FromPreset(all[presetIndex]);
                    opts.PresetName = all[presetIndex].Name;
                }
            }
            int levels = cli.GetIntOption("levels", 0);
            if (levels > 0 && levels < plan.Count)
            {
                plan.Levels = plan.Levels.Take(levels).ToList();
                opts.PresetName = PresetLibrary.CustomName;
            }
            string err = plan.Validate();
            if (err != null)
            {
                Console.WriteLine("BATCH: LOD 参数有误：" + err);
                return null;
            }
            opts.Plan = plan;
            return opts;
        }

        private static List<double> ParseDoubles(string s)
        {
            var list = new List<double>();
            foreach (var part in (s ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double v;
                if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) list.Add(v);
            }
            return list;
        }

        private static bool TryInputs(CommandLine cli, out List<TaskRef> tasks, out string root)
        {
            tasks = new List<TaskRef>();
            root = null;
            foreach (var p in cli.Paths)
            {
                var task = TaskRef.FromPath(p);
                if (task == null) return false;
                tasks.Add(task);
                if (root == null) root = task.Kind == TaskKind.Folder ? task.Path : Path.GetDirectoryName(task.Path);
            }
            return tasks.Count > 0;
        }

        private static string EnsureOut(CommandLine cli, string tag)
        {
            string outDir = cli.OutDir;
            if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(AppInfo.TempRoot, tag + "_" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(outDir);
            return outDir;
        }

        // ─────────────────────── JSON 输出 ───────────────────────

        private static string PlanToJson(BatchPlan plan, BatchRunOptions opt)
        {
            var w = new Json.Writer();
            w.BeginObject();
            w.Prop("rootDir", plan.RootDir);
            w.Prop("preset", plan.PresetName);
            w.Prop("plan", plan.Automatic ? "按各项目面数自动规划" : plan.Plan.Summary());
            w.Prop("automatic", plan.Automatic);
            w.Prop("mode", (int)opt.OutputMode);
            w.Prop("qualityFirst", opt.QualityFirst);
            w.Prop("reductionProfile", opt.ReductionProfile);
            w.Prop("generateShadowLod", opt.GenerateShadowLod);
            w.Prop("shadowLodRatio", opt.ShadowLodRatio);
            w.Prop("shadowLodMode", (int)opt.ShadowLodMode);
            w.Prop("shadowLodReuseLevel", opt.ShadowLodReuseLevel);
            int shadowJobs = plan.Models.Count(model => model.ShadowJob != null);
            w.Prop("autoTargetTriangles", opt.AutoTargetTriangles);
            w.Prop("autoMinSavingsPercent", opt.AutoMinSavingsPercent);
            w.Prop("adaptiveTriangleBudget", opt.AdaptiveTriangleBudget);
            w.Prop("allowVertexUpdates", opt.AllowVertexUpdates);
            w.Prop("force", opt.Force);
            w.Name("counts").BeginObject()
                .Prop("qc", plan.QcPlans.Count).Prop("process", plan.ProcessCount).Prop("skip", plan.SkipCount)
                .Prop("error", plan.ErrorCount).Prop("models", plan.Models.Count).Prop("jobs", plan.JobCount)
                .Prop("visibleJobs", plan.JobCount - shadowJobs).Prop("shadowJobs", shadowJobs)
                .Prop("sourceTriangles", plan.TotalSourceTriangles)
                .EndObject();
            w.Name("qc").BeginArray();
            foreach (var q in plan.QcPlans)
            {
                w.BeginObject();
                w.Prop("path", q.QcPath);
                w.Prop("action", q.Action.ToString());
                w.Prop("reason", q.Reason ?? "");
                w.Prop("wrapper", q.IsWrapperQc);
                w.Prop("standalone", q.IsStandalone);
                w.Prop("sourceTriangles", q.SourceTriangles);
                w.Name("candidates").BeginArray();
                foreach (var level in q.Levels?.Levels ?? new List<LodLevel>()) w.Value(level.Ratio);
                w.EndArray();
                w.Name("models").BeginArray();
                foreach (var m in q.Models)
                {
                    w.BeginObject();
                    w.Prop("path", m.Path);
                    w.Prop("triangles", m.Triangles);
                    w.Prop("smd", m.IsSmd);
                    w.Prop("jobs", m.Jobs.Count);
                    w.Prop("shadowJobs", m.ShadowJob == null ? 0 : 1);
                    w.Prop("skipReason", m.SkipReason ?? "");
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
            w.Name("models").BeginArray();
            foreach (var m in plan.Models)
            {
                w.BeginObject();
                w.Prop("path", m.Path);
                w.Prop("triangles", m.Triangles);
                w.Prop("smd", m.IsSmd);
                w.Prop("jobs", m.Jobs.Count);
                w.Prop("shadowJobs", m.ShadowJob == null ? 0 : 1);
                w.Prop("skipReason", m.SkipReason ?? "");
                w.Name("outputs").BeginArray();
                if (!plan.Automatic) foreach (var j in m.Jobs) w.Value(j.Destination);
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
            return w.ToString();
        }

        private static string OutcomeToJson(BatchOutcome outcome, BatchPlan plan)
        {
            var w = new Json.Writer();
            w.BeginObject();
            w.Prop("ok", outcome != null && outcome.Ok);
            w.Prop("cancelled", outcome != null && outcome.Cancelled);
            if (outcome != null)
            {
                var s = outcome.Stats;
                w.Name("stats").BeginObject()
                    .Prop("rootQc", s.RootQcCount).Prop("uniqueModels", s.UniqueModelCount)
                    .Prop("jobs", s.JobCount).Prop("jobOk", s.JobOk).Prop("jobFail", s.JobFail)
                    .Prop("injectedQc", s.InjectedQcCount).Prop("skippedQc", s.SkippedQcCount).Prop("errorQc", s.ErrorQcCount)
                    .Prop("compileOk", s.CompileOk).Prop("compileFail", s.CompileFail)
                    .Prop("sourceTriangles", s.TotalSourceTriangles).Prop("lastLodTriangles", s.TotalLastLodTriangles)
                    .Prop("retriedComponents", s.RetriedComponents).Prop("retainedComponents", s.RetainedComponents)
                    .Prop("protectedPrimitives", s.ProtectedPrimitives).Prop("reusedLevels", s.ReusedLevels)
                    .Prop("unreducedLevels", s.UnreducedLevels).Prop("publishedLods", s.PublishedLods)
                    .Prop("shadowLodsPublished", s.ShadowLodsPublished)
                    .Prop("shadowLodQcsWritten", s.ShadowLodQcsWritten).Prop("shadowLodReusedQcs", s.ShadowLodReusedQcs)
                    .Prop("maxSurfaceDeviationPpm", s.MaxSurfaceDeviationPpm).Prop("meanSurfaceDeviationPpm", s.MeanSurfaceDeviationPpm)
                    .Prop("deviationSampleCount", s.DeviationSampleCount)
                    .Prop("modelsWithoutReduction", s.ModelsWithoutReduction)
                    .Prop("vertexUpdateTrials", s.VertexUpdateTrials).Prop("vertexUpdateAccepted", s.VertexUpdateAccepted)
                    .Prop("vertexUpdateTrianglesSaved", s.VertexUpdateTrianglesSaved)
                    .Prop("balancedRegions", s.BalancedRegions).Prop("maximumRegionalSpread", s.MaximumRegionalSpread / 100.0)
                    .Prop("maximumAdjacentRegionalSpread", s.MaximumAdjacentRegionalSpread / 100.0)
                    .EndObject();
                w.Name("lodTriangles").BeginArray();
                foreach (long triangles in s.LodTriangles) w.Value(triangles);
                w.EndArray();
                w.Prop("reportPath", outcome.ReportPath ?? "");
                w.Prop("logPath", outcome.LogPath ?? "");
                w.Prop("elapsedSeconds", outcome.Elapsed.TotalSeconds);
                w.Prop("generatedModels", outcome.GeneratedModels);
                w.Name("qc").BeginArray();
                foreach (var q in outcome.QcOutcomes)
                {
                    w.BeginObject();
                    w.Prop("path", q.QcPath);
                    w.Prop("injected", q.Injected);
                    w.Prop("emittedVisibleLevels", q.LodBlock.EmittedVisibleLevels);
                    w.Prop("hasShadowLod", q.LodBlock.HasShadowLod);
                    w.Prop("shadowReusedLevel", q.LodBlock.ShadowReusedLevel);
                    w.Prop("skipped", q.Skipped);
                    w.Prop("skipReason", q.SkipReason ?? "");
                    w.Prop("compileOk", q.CompileOk);
                    w.Prop("lodBlockFile", q.LodBlockFile ?? "");
                    w.Prop("sourceTriangles", q.SourceTriangles);
                    w.Prop("generated", q.Generated);
                    w.Name("outputs").BeginArray();
                    foreach (var file in q.OutputFiles) w.Value(file);
                    w.EndArray();
                    w.Name("levels").BeginArray();
                    foreach (var level in q.Levels)
                        w.BeginObject().Prop("candidate", level.Candidate).Prop("triangles", level.Triangles)
                            .Prop("targetRatio", level.TargetRatio).Prop("estimatedDistanceHu", level.EstimatedDistanceHu)
                            .Prop("maxSurfaceDeviationPpm", level.MaxSurfaceDeviationPpm)
                            .Prop("switchPoint", level.SwitchPoint).Prop("skipReason", level.SkipReason ?? "").EndObject();
                    w.EndArray();
                    w.Name("errors").BeginArray();
                    foreach (var e in q.Errors) w.Value(e);
                    w.EndArray();
                    w.EndObject();
                }
                w.EndArray();
                w.Name("errors").BeginArray();
                foreach (var e in outcome.Errors) w.Value(e);
                w.EndArray();
            }
            else
            {
                w.Prop("reason", "no work");
            }
            w.EndObject();
            return w.ToString();
        }
    }
}
