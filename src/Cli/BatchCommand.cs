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
            string root = FirstPath(cli);
            if (root == null || !Directory.Exists(root))
            {
                Console.WriteLine("PLAN: 需要一个存在的工程目录。用法：--plan <工程目录> [--out 目录]");
                return 2;
            }
            string outDir = EnsureOut(cli, "plan");
            var obs = new CollectingObserver(s => Console.WriteLine(s));
            var options = BuildOptions(cli, root, out obs);
            if (options == null) return 2;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var plan = BatchPlanner.Build(new List<TaskRef> { new TaskRef(TaskKind.Folder, root) }, root, options, obs, CancellationToken.None);
            sw.Stop();

            string json = PlanToJson(plan, options);
            File.WriteAllText(Path.Combine(outDir, "plan.json"), json, new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine("PLAN: " + plan.QcPlans.Count + " QC · " + plan.Models.Count + " 模型 · " + plan.JobCount + " 任务 · " +
                              plan.ProcessCount + " 处理 / " + plan.SkipCount + " 跳过 / " + plan.ErrorCount + " 错误 · " +
                              sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");
            Console.WriteLine("PLAN JSON -> " + Path.Combine(outDir, "plan.json"));
            return plan.ErrorCount > 0 ? 1 : 0;
        }

        public static int RunBatch(CommandLine cli)
        {
            string root = FirstPath(cli);
            if (root == null || !Directory.Exists(root))
            {
                Console.WriteLine("BATCH: 需要一个存在的工程目录。用法：--batch <工程目录> [--out 目录] [选项]");
                return 2;
            }
            string outDir = EnsureOut(cli, "batch");
            var obs = new CollectingObserver(s => Console.WriteLine(s));
            var options = BuildOptions(cli, root, out obs);
            if (options == null) return 2;
            options.ReportDir = !string.IsNullOrEmpty(cli.GetOption("report-dir")) ? cli.GetOption("report-dir") : outDir;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var plan = BatchPlanner.Build(new List<TaskRef> { new TaskRef(TaskKind.Folder, root) }, root, options, obs, CancellationToken.None);
            File.WriteAllText(Path.Combine(outDir, "plan.json"), PlanToJson(plan, options), new UTF8Encoding(false));
            if (plan.QcPlans.Count == 0 || plan.ErrorCount > 0 && plan.ProcessCount == 0)
            {
                Console.WriteLine("BATCH: 没有可处理的内容（见上面的日志）。");
                File.WriteAllText(Path.Combine(outDir, "batch.json"), OutcomeToJson(null, plan), new UTF8Encoding(false));
                return 1;
            }

            BatchOutcome outcome = null;
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += delegate (object s, ConsoleCancelEventArgs e) { e.Cancel = true; try { cts.Cancel(); } catch { } try { BlenderScheduler.KillAll(); } catch { } };
                outcome = BatchRunner.Run(plan, options, obs, cts.Token);
            }
            sw.Stop();

            File.WriteAllText(Path.Combine(outDir, "batch.json"), OutcomeToJson(outcome, plan), new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine("BATCH: " + (outcome.Ok ? "OK" : "FAILED") + " · " +
                              outcome.Stats.InjectedQcCount + " QC 写入 · " + outcome.Stats.JobOk + "/" + outcome.Stats.JobCount + " 减面成功 · " +
                              "用时 " + sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");
            if (!string.IsNullOrEmpty(outcome.ReportPath)) Console.WriteLine("报告: " + outcome.ReportPath);
            if (!string.IsNullOrEmpty(outcome.LogPath)) Console.WriteLine("日志: " + outcome.LogPath);
            foreach (var e in outcome.Errors.Take(20)) Console.WriteLine("  ERR " + e);
            return outcome.Ok ? 0 : 1;
        }

        // ─────────────────────── 选项 ───────────────────────

        private static BatchRunOptions BuildOptions(CommandLine cli, string rootDir, out CollectingObserver obs)
        {
            obs = new CollectingObserver(s => Console.WriteLine(s));
            var settings = SettingsStore.Load();
            var opts = new BatchRunOptions
            {
                BlenderPath = cli.GetOption("blender", settings.BlenderPath),
                NekomdlPath = cli.GetOption("nekomdl", settings.NekomdlPath),
                Workers = cli.GetIntOption("workers", settings.Workers),
                MinTriangles = cli.GetIntOption("min-triangles", settings.MinTriangles),
                QualityFirst = !cli.HasFlag("fast"),
                Force = cli.HasFlag("force"),
                CompileAfter = cli.HasFlag("compile"),
                NoFacial = !cli.HasFlag("facial"),
                SkipMainModel = cli.HasFlag("skip-main"),
                OutputMode = (OutputMode)Math.Max(0, Math.Min(2, cli.GetIntOption("mode", settings.OutputMode))),
                BackupEnabled = !cli.HasFlag("no-backup"),
                ProjectRoot = rootDir,
                ReportDir = null,
                WriteReport = true
            };
            if (string.IsNullOrEmpty(opts.BlenderPath)) opts.BlenderPath = BlenderLocator.Find(null);
            if (string.IsNullOrEmpty(opts.NekomdlPath)) opts.NekomdlPath = Nekomdl.Find(null);
            if (opts.SkipMainModel)
                opts.SkipMainQcPaths = new HashSet<string>(settings.SkipMainQcs, StringComparer.OrdinalIgnoreCase);

            // 方案
            var all = PresetLibrary.Builtins();
            int presetIndex = cli.GetIntOption("preset", 0);
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
                opts.PresetName = "自定义";
            }
            else
            {
                if (presetIndex < 0 || presetIndex >= all.Length) presetIndex = 0;
                plan = LodPlan.FromPreset(all[presetIndex]);
                opts.PresetName = all[presetIndex].Name;
            }
            int levels = cli.GetIntOption("levels", 0);
            if (levels > 0 && levels < plan.Count)
            {
                plan.Levels = plan.Levels.Take(levels).ToList();
                opts.PresetName += "（前 " + levels + " 级）";
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

        private static string FirstPath(CommandLine cli)
        {
            foreach (var p in cli.Paths)
            {
                try { if (Directory.Exists(p)) return Path.GetFullPath(p); } catch { }
            }
            return cli.Paths.FirstOrDefault();
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
            w.Prop("plan", plan.Plan.Summary());
            w.Prop("mode", (int)opt.OutputMode);
            w.Prop("qualityFirst", opt.QualityFirst);
            w.Prop("force", opt.Force);
            w.Name("counts").BeginObject()
                .Prop("qc", plan.QcPlans.Count).Prop("process", plan.ProcessCount).Prop("skip", plan.SkipCount)
                .Prop("error", plan.ErrorCount).Prop("models", plan.Models.Count).Prop("jobs", plan.JobCount)
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
                w.Name("models").BeginArray();
                foreach (var m in q.Models)
                {
                    w.BeginObject();
                    w.Prop("path", m.Path);
                    w.Prop("triangles", m.Triangles);
                    w.Prop("smd", m.IsSmd);
                    w.Prop("jobs", m.Jobs.Count);
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
                w.Prop("skipReason", m.SkipReason ?? "");
                w.Name("outputs").BeginArray();
                foreach (var j in m.Jobs) w.Value(j.Destination);
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
                    .EndObject();
                w.Prop("reportPath", outcome.ReportPath ?? "");
                w.Prop("logPath", outcome.LogPath ?? "");
                w.Prop("elapsedSeconds", outcome.Elapsed.TotalSeconds);
                w.Name("qc").BeginArray();
                foreach (var q in outcome.QcOutcomes)
                {
                    w.BeginObject();
                    w.Prop("path", q.QcPath);
                    w.Prop("injected", q.Injected);
                    w.Prop("skipped", q.Skipped);
                    w.Prop("skipReason", q.SkipReason ?? "");
                    w.Prop("compileOk", q.CompileOk);
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
