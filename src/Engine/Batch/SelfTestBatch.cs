using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using LodStudio.Cli;

namespace LodStudio.Engine
{
    /// <summary>批量计划器 / 执行器（不需要 Blender 的部分）的自测。</summary>
    internal static class SelfTestBatch
    {
        // ─────────────────────── 造数据工具 ───────────────────────

        private static SmdModel MakeSmd(int triangleCount)
        {
            var model = new SmdModel { SourcePath = "synthetic.smd" };
            model.Bones.Add(new SmdBone { Index = 0, Name = "root", Parent = -1 });
            model.Bones.Add(new SmdBone { Index = 1, Name = "spine", Parent = 0 });
            for (int i = 0; i < triangleCount; i++)
            {
                var t = new SmdTriangle { Material = "body" };
                t.V0 = MakeVertex(0, 0, 0, i);
                t.V1 = MakeVertex(10, 0, 0, i);
                t.V2 = MakeVertex(0, 10, 0, i);
                model.Triangles.Add(t);
            }
            return model;
        }

        private static SmdVertex MakeVertex(float x, float y, float z, int seed)
        {
            var v = new SmdVertex { X = x, Y = y, Z = z, Nx = 0, Ny = 0, Nz = 1, U = (seed % 7) / 7f, V = (seed % 5) / 5f };
            v.Weights.Add(new SmdWeight { BoneIndex = 0, Weight = 0.6f });
            v.Weights.Add(new SmdWeight { BoneIndex = 1, Weight = 0.4f });
            return v;
        }

        private static string WriteSmdModel(string dir, string name, int triangles)
        {
            string path = Path.Combine(dir, name + ".smd");
            SmdCodec.Write(path, MakeSmd(triangles));
            return path;
        }

        private static SmdModel WriteGridGlb(string dir, int size)
        {
            var mesh = MakeGrid(size);
            File.WriteAllBytes(Path.Combine(dir, "mesh.glb"), SmdGlbCodec.SmdToGlb(mesh));
            return mesh;
        }

        private static SmdModel MakeGrid(int size)
        {
            SmdVertex Vertex(int x, int y) => new SmdVertex
            {
                X = x, Y = y, Nz = 1, U = x / (float)size, V = y / (float)size
            };
            var mesh = new SmdModel();
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var a = Vertex(x, y);
                    var b = Vertex(x + 1, y);
                    var c = Vertex(x + 1, y + 1);
                    var d = Vertex(x, y + 1);
                    mesh.Triangles.Add(new SmdTriangle { Material = "grid", V0 = a, V1 = b, V2 = c });
                    mesh.Triangles.Add(new SmdTriangle { Material = "grid", V0 = a, V1 = c, V2 = d });
                }
            return mesh;
        }

        private static string WriteQc(string dir, string name, string modelRef, string extra = null, string toolBlockBroken = null)
        {
            string path = Path.Combine(dir, name + ".qc");
            var sb = new StringBuilder();
            sb.AppendLine("$modelname \"models/" + name + ".mdl\"");
            sb.AppendLine("$bodygroup \"mesh\"");
            sb.AppendLine("{");
            sb.AppendLine("    studio \"" + modelRef + "\"");
            sb.AppendLine("}");
            if (extra != null) sb.AppendLine(extra);
            if (toolBlockBroken != null) sb.AppendLine(toolBlockBroken);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        private static BatchRunOptions Options(int levels = 2, int minTriangles = 1)
        {
            var plan = new LodPlan();
            for (int i = 0; i < levels; i++) plan.Levels.Add(new LodLevel(10 + i * 10, 0.6 - i * 0.2));
            return new BatchRunOptions
            {
                Plan = plan,
                PresetName = "测试方案",
                MinTriangles = minTriangles,
                OutputMode = OutputMode.InPlace,
                BackupEnabled = true,
                ProjectRoot = null
            };
        }

        private static BatchPlan Plan(List<TaskRef> tasks, string rootDir, BatchRunOptions options, CollectingObserver obs = null)
        {
            return BatchPlanner.Build(tasks, rootDir, options, obs ?? new CollectingObserver(), CancellationToken.None);
        }

        // ─────────────────────── 用例 ───────────────────────

        [SelfTest("自动 LOD：按面数估算级数和比例，多份 QC 共用网格任务")]
        public static void AutomaticBudgets(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_auto");
            string shared = WriteSmdModel(dir, "shared", 12000);
            WriteSmdModel(dir, "extra", 12000);
            WriteSmdModel(dir, "tiny", 4000);
            string small = WriteQc(dir, "small", "shared.smd");
            string large = WriteQc(dir, "large", "shared.smd", "$body \"extra\" \"extra.smd\"");
            string tiny = WriteQc(dir, "tiny", "tiny.smd");

            var options = Options();
            options.Automatic = true;
            var plan = Plan(new List<TaskRef>
            {
                new TaskRef(TaskKind.Qc, small), new TaskRef(TaskKind.Qc, large), new TaskRef(TaskKind.Qc, tiny)
            }, dir, options);
            var smallPlan = plan.QcPlans.Single(q => q.QcPath == small);
            var largePlan = plan.QcPlans.Single(q => q.QcPath == large);
            ctx.AssertEqual(1, smallPlan.Levels.Count, "12,000 面生成一档候选");
            ctx.AssertEqual(2, largePlan.Levels.Count, "24,000 面生成两档候选");
            ctx.AssertEqual(BatchQcAction.Skip.ToString(), plan.QcPlans.Single(q => q.QcPath == tiny).Action.ToString(),
                "低面数方案可跳过");
            var sharedModel = plan.Models.Single(m => m.Path == shared);
            ctx.AssertEqual(2, sharedModel.Jobs.Count, "共享网格按更详细的引用计划减面一次");
            ctx.AssertEqual(4, plan.JobCount, "两份 QC 应复用两个共享模型档位");
            ctx.AssertEqual(sharedModel.Jobs[0].Ratio, smallPlan.Levels.Levels[0].Ratio, 0.0001, "预览显示实际分配给共享网格的目标");
            ctx.Assert(AutomaticLod.Plan(12000).Ratios[0] > AutomaticLod.Plan(18000).Ratios[0],
                "同样只有一档时，高面数模型使用更低的目标保留比例");
        }

        [SelfTest("自动 LOD：自定义目标与最小减面比例影响候选和发布")]
        public static void ConfigurableAutomaticBudgets(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_auto_custom");
            const int size = 25;
            var mesh = WriteGridGlb(dir, size);
            string qc = WriteQc(dir, "actor", "mesh.glb");
            var tasks = new List<TaskRef> { new TaskRef(TaskKind.Qc, qc) };
            var options = Options();
            options.Automatic = true;
            options.WriteReport = false;
            options.Workers = 1;
            options.AutoTargetTriangles = 1500;
            ctx.Assert(Plan(tasks, dir, options).QcPlans.Single().Action == BatchQcAction.Skip, "源面数低于自定义目标时跳过");

            options.AutoTargetTriangles = 1000;
            options.AutoMinSavingsPercent = 50;
            var plan = Plan(tasks, dir, options);
            ctx.AssertEqual(1, plan.JobCount, "降低目标后生成一个候选");
            ctx.AssertEqual(0.8, plan.QcPlans.Single().Levels.Levels.Single().Ratio, 1e-9, "候选按自定义目标计算保留比例");
            var rejected = BatchRunner.Run(plan, options, null, CancellationToken.None);
            ctx.Assert(rejected.Ok, "候选减面运行应成功：" + string.Join("; ", rejected.Errors));
            var rejectedLevel = rejected.QcOutcomes.Single().Levels.Single();
            ctx.Assert(rejectedLevel.Triangles < mesh.TriangleCount, "候选确实减少了面数");
            ctx.AssertContains(rejectedLevel.SkipReason, "50%", "严格的减面门槛应拒绝候选");
            ctx.AssertEqual(0, rejected.Stats.PublishedLods, "拒绝的候选未发布");

            string output = Path.Combine(dir, "out");
            string settingsDir = Directory.CreateDirectory(Path.Combine(dir, "settings")).FullName;
            File.WriteAllText(Path.Combine(settingsDir, "settings.ini"), "");
            string previousSettingsDir = Environment.GetEnvironmentVariable(AppInfo.SettingsDirEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(AppInfo.SettingsDirEnvVar, settingsDir);
                var cli = CommandLine.Parse(new[] { "--batch", qc, "--out", output, "--profile", "1", "--workers", "1",
                    "--shadowlod", "--shadow-ratio=0.3", "--auto-target=1000", "--auto-min-savings=1",
                    "--collapse-bones", "detail|toe", "--collapse-bones-from=1" });
                ctx.AssertEqual(0, BatchCommand.RunBatch(cli), "CLI 使用宽松的减面门槛执行批量");
            }
            finally
            {
                Environment.SetEnvironmentVariable(AppInfo.SettingsDirEnvVar, previousSettingsDir);
            }

            var planJson = Json.ParseObjectSafe(File.ReadAllText(Path.Combine(output, "plan.json")));
            ctx.Assert(Json.GetBool(planJson, "automatic"), "计划 JSON 标记自动模式");
            ctx.AssertEqual(1, Json.GetInt(planJson, "reductionProfile", -1), "计划 JSON 保真档位");
            ctx.Assert(Json.GetBool(planJson, "generateShadowLod"), "计划 JSON 标记生成阴影网格");
            ctx.AssertEqual(0.3, Json.GetDouble(planJson, "shadowLodRatio"), 1e-9, "计划 JSON 阴影保留比例");
            ctx.AssertEqual(0, Json.GetInt(planJson, "shadowLodMode", -1), "默认保持独立阴影模式");
            ctx.AssertEqual(0, Json.GetInt(planJson, "shadowLodReuseLevel", -1), "默认复用档位为最远档");
            ctx.AssertEqual(1000, Json.GetInt(planJson, "autoTargetTriangles"), "计划 JSON 目标面数");
            ctx.AssertEqual(1, Json.GetInt(planJson, "autoMinSavingsPercent"), "计划 JSON 最小减面比例");

            var batchJson = Json.ParseObjectSafe(File.ReadAllText(Path.Combine(output, "batch.json")));
            var stats = (Dictionary<string, object>)batchJson["stats"];
            ctx.AssertEqual(1, Json.GetInt(stats, "publishedLods"), "宽松的减面门槛应发布距离网格");
            ctx.AssertEqual(1, Json.GetInt(stats, "shadowLodsPublished"), "结果 JSON 包含已发布阴影网格");
            ctx.AssertEqual(1, Json.GetInt(stats, "shadowLodQcsWritten"), "独立阴影写入一个 QC");
            ctx.AssertEqual(0, Json.GetInt(stats, "shadowLodReusedQcs", -1), "独立阴影未回退");
            ctx.AssertContains(File.ReadAllText(qc), "$lod ", "自动模式的一档可见 LOD 仍然写入");
            ctx.Assert(Json.GetDouble(stats, "deviationSampleCount") > 0, "结果 JSON 包含偏差采样数");
            ctx.Assert(Json.GetInt(stats, "maxSurfaceDeviationPpm", -1) >= Json.GetInt(stats, "meanSurfaceDeviationPpm", -1) &&
                Json.GetInt(stats, "meanSurfaceDeviationPpm", -1) >= 0, "结果 JSON 包含有效的峰值和平均偏差");
            var qcJson = (Dictionary<string, object>)((List<object>)batchJson["qc"]).Single();
            var levelJson = (Dictionary<string, object>)((List<object>)qcJson["levels"]).Single();
            ctx.AssertEqual((int)rejectedLevel.Triangles, Json.GetInt(levelJson, "triangles"), "修改门槛时同一候选面数不变");
            ctx.AssertEqual(rejectedLevel.MaxSurfaceDeviationPpm, Json.GetInt(levelJson, "maxSurfaceDeviationPpm", -1),
                "自动档结果 JSON 包含候选峰值偏差");
            ctx.Assert(Json.GetBool(qcJson, "injected"), "宽松的减面门槛应发布 QC");
            string finalQc = File.ReadAllText(qc);
            ctx.AssertContains(finalQc, "bonetreecollapse \"detail\"", "CLI 应传入第一个骨骼");
            ctx.AssertContains(finalQc, "bonetreecollapse \"toe\"", "CLI 应传入第二个骨骼");
        }

        [SelfTest("CLI：保真档位优先级、参数范围及自动模式限制")]
        public static void CliLodOptions(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_cli_options");
            var settings = new AppSettings { ReductionProfile = 0, ShadowLodRatio = 0.32 };
            var observer = new CollectingObserver();
            BatchRunOptions Build(params string[] args) => BatchCommand.BuildOptions(CommandLine.Parse(args), dir, settings, observer);

            var cli = CommandLine.Parse(new[] { "--plan", "--SHADOWLOD", dir, "--profile=-8", "--fast", "--shadow-ratio", "0.125",
                "--nofacial-from=8", "--collapse-bones", "detail|toe", "--collapse-bones-from=-1",
                "--auto-target", "5", "--auto-min-savings", "90", "--mode=0" });
            ctx.AssertEqual(dir, cli.Paths.Single(), "阴影开关后仍可传入项目路径");
            var culture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                ctx.AssertEqual(0.125, cli.GetDoubleOption("shadow-ratio", -1), 1e-9, "浮点参数使用固定小数点");
                ctx.AssertEqual(0.32, CommandLine.Parse(new[] { "--shadow-ratio=0,25" }).GetDoubleOption("shadow-ratio", 0.32),
                    1e-9, "无效浮点参数使用回退值");
            }
            finally { Thread.CurrentThread.CurrentCulture = culture; }

            var automatic = BatchCommand.BuildOptions(cli, dir, settings, observer);
            ctx.Assert(automatic.Automatic && automatic.GenerateShadowLod, "自动模式允许启用阴影 LOD");
            ctx.AssertEqual(0, automatic.ReductionProfile, "显式档位优先于 --fast，并限制下界");
            ctx.Assert(automatic.QualityFirst, "严格档同步质量模式");
            ctx.AssertEqual(0.125, automatic.ShadowLodRatio, 1e-9, "阴影比例");
            ctx.AssertEqual(7, automatic.NoFacialFromLevel, "表情开始档位上界");
            ctx.AssertEqual("detail|toe", automatic.CollapseBones, "骨骼列表");
            ctx.AssertEqual(1, automatic.CollapseBonesFromLevel, "骨骼折叠开始档位下界");
            ctx.AssertEqual(1000, automatic.AutoTargetTriangles, "自动目标下界");
            ctx.AssertEqual(50, automatic.AutoMinSavingsPercent, "最小减面比例上界");

            settings.GenerateShadowLod = true;
            var manual = Build("--preset=1", "--nofacial", "--nofacial-from=0", "--no-shadowlod", "--shadow-ratio=0.01",
                "--collapse-bones-from=9", "--profile=9", "--quality", "--auto-target=999999", "--auto-min-savings=0");
            ctx.Assert(!manual.Automatic && manual.NoFacial && !manual.GenerateShadowLod, "手动表情开关与关闭阴影");
            ctx.AssertEqual(1, manual.NoFacialFromLevel, "表情开始档位下界");
            ctx.AssertEqual(7, manual.CollapseBonesFromLevel, "骨骼折叠开始档位上界");
            ctx.AssertEqual(0.05, manual.ShadowLodRatio, 1e-9, "阴影比例下界");
            ctx.AssertEqual(0.60, Build("--shadow-ratio=1").ShadowLodRatio, 1e-9, "阴影比例上界");
            ctx.AssertEqual(0.20, Build("--shadow-ratio=NaN").ShadowLodRatio, 1e-9, "NaN 阴影比例恢复默认");
            ctx.AssertEqual(200000, manual.AutoTargetTriangles, "自动目标上界");
            ctx.AssertEqual(1, manual.AutoMinSavingsPercent, "最小减面比例下界");
            ctx.AssertEqual(2, manual.ReductionProfile, "显式档位优先于 --quality，并限制上界");
            ctx.Assert(!manual.QualityFirst, "高压缩档同步快速模式");

            ctx.AssertEqual(0, Build("--quality").ReductionProfile, "--quality 保留设置中的严格档");
            ctx.AssertEqual(2, Build("--fast").ReductionProfile, "--fast 选择高压缩档");
            settings.ReductionProfile = 1;
            settings.QualityFirst = false;
            ctx.AssertEqual(2, Build().ReductionProfile, "无参数沿用设置中的有效档位");
            ctx.AssertEqual(1, Build("--quality").ReductionProfile, "--quality 将高压缩有效档切到平衡档");
            ctx.AssertEqual(1, Build("--profile=1", "--fast").ReductionProfile, "显式平衡档优先于 --fast");
            ctx.Assert(Build("--profile=1", "--fast").QualityFirst, "显式平衡档同步质量模式");

            var toolOptions = Build("--auto", "--nofacial", "--nofacial-from=2", "--compile", "--force");
            ctx.Assert(toolOptions != null && toolOptions.NoFacial && toolOptions.CompileAfter && toolOptions.Force,
                "自动模式应接受表情、试编译和覆盖开关");
            ctx.AssertEqual(2, toolOptions.NoFacialFromLevel, "自动模式使用显式表情起始档");
            ctx.AssertEqual(Nekomdl.Find(null), toolOptions.NekomdlPath, "自动模式试编译会探测编译器");
            settings.NoFacial = true;
            ctx.Assert(Build("--auto").NoFacial, "自动模式沿用设置中的 nofacial");
            ctx.Assert(!Build("--auto", "--facial", "--nofacial").NoFacial, "--facial 可关闭 nofacial");

            foreach (string flag in new[] { "--no-backup", "--mode=1", "--mode=-1", "--levels=2" })
                ctx.Assert(Build("--auto", flag) == null, "显式自动模式应拒绝 " + flag);
            foreach (string flag in new[] { "--no-backup", "--mode=1", "--mode=-1" })
                ctx.Assert(Build(flag) == null, "自动模式应拒绝 " + flag);
        }

        [SelfTest("批量：表情模型从计划第 2 档起减面（手动、自动、混合与纯表情）")]
        public static void FacialTieredBatch(SelfTestContext ctx)
        {
            foreach (bool automatic in new[] { false, true })
            foreach (bool mixed in new[] { false, true })
            {
                string dir = ctx.NewDir("batch_facial_" + automatic + "_" + mixed);
                var mesh = WriteGridGlb(dir, 45);
                File.Move(Path.Combine(dir, "mesh.glb"), Path.Combine(dir, "face.glb"));
                if (mixed) File.Copy(Path.Combine(dir, "face.glb"), Path.Combine(dir, "body.glb"));
                string qc = Path.Combine(dir, "actor.qc");
                File.WriteAllText(qc, "$modelname \"actor.mdl\"\n" +
                    (mixed ? "$body \"body\" \"body.glb\"\n" : "") +
                    "$model \"face\" \"face.glb\" {\n\tflexfile \"face.vta\"\n}\n");
                var options = Options(3);
                options.Automatic = automatic;
                options.NoFacial = true;
                options.NoFacialFromLevel = 2;
                options.AutoTargetTriangles = 1000;
                options.AutoMinSavingsPercent = 1;
                options.Workers = 1;
                options.WriteReport = false;
                var plan = Plan(new List<TaskRef> { new TaskRef(TaskKind.Qc, qc) }, dir, options);
                var qcPlan = plan.QcPlans.Single();
                var face = plan.Models.Single(m => m.BaseName == "face");
                ctx.AssertEqual(2, face.Jobs.First().Level, "表情减面从计划第 2 档开始");
                ctx.Assert(face.Jobs.All(j => j.Level >= 2), "表情模型没有较近档位任务");
                if (automatic)
                {
                    var bodyJob = mixed ? plan.Models.Single(m => m.BaseName == "body").Jobs.First() : null;
                    double expected = mixed
                        ? (mesh.TriangleCount + Math.Ceiling(mesh.TriangleCount * bodyJob.Ratio)) / qcPlan.SourceTriangles
                        : 1;
                    ctx.AssertEqual(expected, qcPlan.Levels.Levels[0].Ratio, 1e-9, "近档计划按原表情网格计算面数");
                }

                var outcome = BatchRunner.Run(plan, options, null, CancellationToken.None);
                var result = outcome.QcOutcomes.Single();
                ctx.Assert(outcome.Ok && result.Injected, "应写入分档 QC：" + string.Join("; ", result.Errors.Concat(outcome.Errors)));
                var blocks = VisibleBlocks(File.ReadAllText(qc));
                ctx.Assert(blocks.Count > (mixed ? 1 : 0), "应有远档表情替换");
                if (mixed)
                {
                    var near = blocks[0].Value;
                    ctx.AssertContains(near, "replacemodel \"body.glb\"", "近档减面身体");
                    ctx.AssertNotContains(near, "replacemodel \"face.glb\"", "近档保留表情网格");
                    ctx.AssertNotContains(near, "\tnofacial\n", "近档保留表情");
                    if (automatic)
                    {
                        string bodyPath = Regex.Match(near, "replacemodel \"body\\.glb\" \"([^\"]+)\"").Groups[1].Value;
                        long expected = mesh.TriangleCount + GlbCodec.CountTriangles(Path.Combine(dir, bodyPath));
                        ctx.Assert(result.Levels[0].Triangles == expected, "近档实际面数计入原始表情网格");
                    }
                }
                else if (automatic)
                {
                    ctx.Assert(qcPlan.SourceTriangles == result.Levels[0].Triangles, "纯表情 QC 近档面数保持原样");
                    ctx.Assert(!string.IsNullOrEmpty(result.Levels[0].SkipReason), "近档没有减面，自动模式跳过");
                }
                var far = blocks[mixed ? 1 : 0].Value;
                ctx.AssertContains(far, "replacemodel \"face.glb\" \"face_lod2", "首个表情替换使用计划第 2 档网格");
                for (int i = mixed ? 1 : 0; i < blocks.Count; i++)
                    ctx.AssertContains(blocks[i].Value, "\tnofacial\n", "每个较远档显式关闭表情");
            }
        }

        [SelfTest("自动 LOD：CLI nofacial、覆盖手写档并触发真实试编译")]
        public static void AutomaticForceAndCompile(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_auto_force_compile");
            var mesh = MakeGrid(25);
            mesh.Bones.Add(new SmdBone { Index = 0, Name = "root", Parent = -1 });
            mesh.SkeletonPose.Add("0 0 0 0 0 0 0");
            SmdCodec.Write(Path.Combine(dir, "body.smd"), mesh);
            string qc = WriteQc(dir, "actor", "body.smd", "$sequence \"idle\" \"body.smd\" fps 30\n$lod 99\n{\n}\n");
            var options = Options();
            options.Automatic = true;
            options.AutoTargetTriangles = 1000;
            var plan = Plan(new List<TaskRef> { new TaskRef(TaskKind.Qc, qc) }, dir, options);
            ctx.AssertContains(plan.QcPlans.Single().Reason, "手写 $lod", "自动模式默认跳过手写档");

            string output = Path.Combine(dir, "out");
            string settingsDir = Directory.CreateDirectory(Path.Combine(dir, "settings")).FullName;
            File.WriteAllText(Path.Combine(settingsDir, "settings.ini"), "");
            string previousSettingsDir = Environment.GetEnvironmentVariable(AppInfo.SettingsDirEnvVar);
            string compiler = Nekomdl.Find(null);
            try
            {
                Environment.SetEnvironmentVariable(AppInfo.SettingsDirEnvVar, settingsDir);
                var cli = CommandLine.Parse(new[] { "--batch", qc, "--out", output, "--auto", "--auto-target=1000",
                    "--auto-min-savings=1", "--nofacial", "--force", "--compile", "--workers=1" });
                ctx.AssertEqual(0, BatchCommand.RunBatch(cli), "自动模式 CLI 应成功写入");
            }
            finally
            {
                Environment.SetEnvironmentVariable(AppInfo.SettingsDirEnvVar, previousSettingsDir);
            }

            var planJson = Json.ParseObjectSafe(File.ReadAllText(Path.Combine(output, "plan.json")));
            ctx.Assert(Json.GetBool(planJson, "automatic") && Json.GetBool(planJson, "force") &&
                Json.GetBool(planJson, "noFacial") && Json.GetBool(planJson, "compileAfter"), "计划 JSON 记录生效的自动工具开关");
            ctx.AssertEqual(1, Json.GetInt(planJson, "noFacialFromLevel"), "默认从第 1 档应用");
            ctx.AssertContains(VisibleBlocks(File.ReadAllText(qc)).Cast<Match>().Single().Value,
                "\tnofacial\n", "--batch --nofacial 写入第 1 档");
            var qcInfo = QcParser.Parse(qc);
            ctx.Assert(!qcInfo.HasForeignLod && qcInfo.HasToolMarker, "自动覆盖停用手写档并写入工具块");
            ctx.AssertContains(File.ReadAllText(qc + ".lodbak"), "$lod 99", "备份保留原手写档");
            var batchJson = Json.ParseObjectSafe(File.ReadAllText(Path.Combine(output, "batch.json")));
            var stats = (Dictionary<string, object>)batchJson["stats"];
            if (compiler == null)
                ctx.Log("SKIP: 本机没有 nekomdl.exe，跳过自动模式真实试编译断言");
            else
            {
                ctx.AssertEqual(1, Json.GetInt(stats, "compileOk"), "自动模式实际试编译 1 个 QC");
                var qcJson = (Dictionary<string, object>)((List<object>)batchJson["qc"]).Single();
                ctx.Assert(Json.GetBool(qcJson, "compileOk"), "结果记录成功试编译");
                ctx.Log("自动模式真实试编译成功：" + compiler);
            }
        }

        private static MatchCollection VisibleBlocks(string text) =>
            Regex.Matches(text.Replace("\r\n", "\n"), @"(?m)^\$lod [^\n]+\n\{\n[^}]*\}");

        [SelfTest("自动 LOD：报告按采样显示阴影统计与几何峰值偏差")]
        public static void ReportShadowAndDeviation(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_report_deviation");
            var outcome = new BatchOutcome();
            var measured = new QcOutcome { QcPath = Path.Combine(dir, "measured.qc"), SourceTriangles = 2000, Injected = true };
            measured.Levels.Add(new AutomaticLodResult { Candidate = 1, Triangles = 1600, TargetRatio = 0.8 });
            measured.Levels.Add(new AutomaticLodResult { Candidate = 2, Triangles = 1200, TargetRatio = 0.6 });
            var unmeasured = new QcOutcome { QcPath = Path.Combine(dir, "unmeasured.qc"), SourceTriangles = 2000, Injected = true };
            unmeasured.Levels.Add(new AutomaticLodResult { Candidate = 1, Triangles = 1600, TargetRatio = 0.8 });
            outcome.QcOutcomes.AddRange(new[] { measured, unmeasured });
            var report = new ReportContext { RootDir = dir, Automatic = true, Outcome = outcome };
            string ReadReport(string name)
            {
                string path = ReportWriter.WriteMarkdownReport(report, Path.Combine(dir, name));
                ctx.Assert(File.Exists(path), "应写入 Markdown 报告");
                return File.ReadAllText(path);
            }

            const string header = "| 候选档 | 目标保留 | 实际三角面 | 实际保留 | 估算距离 HU | Source 阈值 |";
            string defaultTable = header + " 结果 |" + Environment.NewLine + "| --- | --- | --- | --- | --- | --- | --- |";
            string empty = ReadReport("empty");
            ctx.AssertContains(empty, defaultTable, "原始表头与分隔线");
            ctx.AssertNotContains(empty, "阴影 LOD", "未发布时省略阴影统计");
            ctx.AssertNotContains(empty, "表面几何偏差", "无采样时省略偏差统计");
            ctx.AssertNotContains(empty, "几何峰值偏差", "无偏差时省略偏差列");

            outcome.Stats.ShadowLodsPublished = 2;
            outcome.Stats.ShadowLodQcsWritten = 2;
            outcome.Stats.MeanSurfaceDeviationPpm = 12345;
            outcome.Stats.MaxSurfaceDeviationPpm = 25000;
            outcome.Stats.DeviationSampleCount = 42;
            measured.Levels[0].MaxSurfaceDeviationPpm = 25000;
            var culture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                string text = ReadReport("measured");
                ctx.AssertContains(text, "| 阴影 LOD | 发布 2 个独立网格 |", "阴影网格统计");
                ctx.AssertContains(text, "| 含 $shadowlod 的 QC | 2 个 |", "阴影 QC 统计");
                ctx.AssertContains(text, "| 表面几何偏差 | 平均 1.23% / 峰值 2.50%（采样 42 顶点） |", "偏差换算与固定小数点");
                ctx.AssertContains(text, header + " 几何峰值偏差 | 结果 |" + Environment.NewLine +
                    "| --- | --- | --- | --- | --- | --- | --- | --- |", "偏差表头及分隔线");
                ctx.AssertContains(text, "| 2.50% | 已输出 |", "档位峰值偏差");
                ctx.AssertContains(text, "| — | 已输出 |", "零偏差档位用破折号");
                ctx.AssertContains(text, defaultTable, "同一报告中零偏差项目保留原表格");
            }
            finally { Thread.CurrentThread.CurrentCulture = culture; }
        }

        [SelfTest("批量：手动阴影档只在第七级占用可见档的槽位")]
        public static void DedicatedShadowSlotPlanning(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_shadow_slots");
            WriteSmdModel(dir, "mesh", 60);
            string qc = WriteQc(dir, "actor", "mesh.smd");
            var tasks = new List<TaskRef> { new TaskRef(TaskKind.Qc, qc) };
            foreach (int requested in new[] { 2, 6, 7 })
            {
                var options = Options(0);
                for (int level = 1; level <= requested; level++)
                    options.Plan.Levels.Add(new LodLevel(level * 10, 1 - level * 0.1));
                options.GenerateShadowLod = true;
                var observer = new CollectingObserver();
                var plan = Plan(tasks, dir, options, observer);
                var model = plan.Models.Single();
                int visibleCount = Math.Min(requested, 6);
                ctx.AssertEqual(requested, options.Plan.Count, "只截断执行计划");
                ctx.AssertEqual(visibleCount, plan.Plan.Count, "计划显示实际可见档数");
                ctx.AssertEqual(visibleCount, model.Jobs.Count, "可见任务数");
                ctx.Assert(model.Jobs.All(j => !j.Destination.Contains("_lod7")), "不计划 _lod7");
                ctx.Assert(model.ShadowJob?.ShadowMode == true, "计划独立阴影任务");
                ctx.AssertEqual(visibleCount + 1, plan.JobCount, "总任务数含独立阴影");
                var blockOptions = new QcLodBlockOptions
                {
                    GenerateShadowLod = true, ShadowPublishedPath = _ => model.ShadowJob.Destination
                };
                string block = QcWriter.BuildLodBlock(plan.QcPlans.Single().Info, plan.Plan,
                    new HashSet<string>(model.Jobs.Select(j => j.Destination)), false, false, blockOptions,
                    false, null, null, null, out var emitted);
                ctx.AssertEqual(visibleCount, emitted.EmittedVisibleLevels, "QC 输出实际可见档数");
                ctx.Assert(emitted.HasShadowLod && emitted.UsesDedicatedShadow, "QC 输出独立阴影档");
                ctx.AssertContains(block, "// Shadow LOD  条目 1\n$shadowlod\n{\n", "保留独立阴影格式");
            }
        }

        [SelfTest("批量：复用最远或指定阴影档，档位不足时回退")]
        public static void ReuseShadowLod(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_reuse_shadow");
            foreach (int requested in new[] { 0, 2, 5 })
            {
                string project = Directory.CreateDirectory(Path.Combine(dir, requested.ToString())).FullName;
                WriteGridGlb(project, 25);
                string qc = WriteQc(project, "actor", "mesh.glb");
                var tasks = new List<TaskRef> { new TaskRef(TaskKind.Qc, qc) };
                var options = Options();
                options.GenerateShadowLod = true;
                options.ShadowLodMode = ShadowLodMode.ReuseExistingLod;
                options.ShadowLodReuseLevel = requested;
                options.OutputMode = requested == 2 ? OutputMode.CollectionOnly : OutputMode.InPlace;
                options.Workers = 1;
                options.WriteReport = true;
                var plan = Plan(tasks, project, options);
                ctx.Assert(plan.Models.All(m => m.ShadowJob == null), "复用不计划独立阴影任务");
                ctx.AssertEqual(2, plan.JobCount, "只计划两个可见档");
                var outcome = BatchRunner.Run(plan, options, null, CancellationToken.None);
                ctx.Assert(outcome.Ok, "复用阴影运行成功：" + string.Join(" / ", outcome.Errors));
                ctx.AssertEqual(0, Directory.GetFiles(project, "*_shadowlod*").Length, "复用不生成独立阴影文件");
                ctx.AssertEqual(0, outcome.Stats.ShadowLodsPublished, "复用不发布独立阴影文件");
                ctx.AssertEqual(1, outcome.Stats.ShadowLodQcsWritten, "写入一个阴影 QC");
                ctx.AssertEqual(1, outcome.Stats.ShadowLodReusedQcs, "一个 QC 复用阴影");
                var block = outcome.QcOutcomes.Single().LodBlock;
                ctx.AssertEqual(2, block.EmittedVisibleLevels, "两个可见档都写入");
                ctx.AssertEqual(2, block.ShadowReusedLevel, "选中第二个可见档");
                string qcText = File.ReadAllText(options.OutputMode == OutputMode.CollectionOnly
                    ? Path.Combine(project, options.CollectionName) : qc).Replace("\r\n", "\n");
                ctx.AssertContains(qcText, "// Shadow LOD（复用 LOD2）  条目 1\n$shadowlod\n{\n\treplacemodel \"mesh.glb\" \"mesh_lod2.glb\"",
                    "阴影引用实际写入的第二档");
                if (requested == 5)
                {
                    ctx.AssertContains(qcText, "第 5 档未写入", "QC 说明回退原因");
                    ctx.Assert(outcome.LogLines.Any(line => line.Contains("回退到最远档 LOD2")), "日志说明回退原因");
                }
                ctx.AssertContains(File.ReadAllText(outcome.ReportPath),
                    "| 阴影 LOD | 复用已有 LOD 写入 1 个 QC（不额外生成阴影文件） |", "复用模式的报告统计");
            }

            string standalone = Directory.CreateDirectory(Path.Combine(dir, "standalone")).FullName;
            WriteGridGlb(standalone, 25);
            var standaloneTasks = new List<TaskRef> { new TaskRef(TaskKind.Glb, Path.Combine(standalone, "mesh.glb")) };
            var automatic = Options();
            automatic.Automatic = true;
            automatic.GenerateShadowLod = true;
            automatic.ShadowLodMode = ShadowLodMode.ReuseExistingLod;
            automatic.AutoTargetTriangles = 1000;
            automatic.AutoMinSavingsPercent = 1;
            automatic.Workers = 1;
            automatic.WriteReport = false;
            var standalonePlan = Plan(standaloneTasks, standalone, automatic);
            ctx.Assert(standalonePlan.QcPlans.Single().IsStandalone, "自动模式下独立模型无需 QC");
            ctx.Assert(standalonePlan.Models.All(m => m.ShadowJob == null), "独立模型无阴影任务");
            var standaloneOutcome = BatchRunner.Run(standalonePlan, automatic, null, CancellationToken.None);
            ctx.Assert(standaloneOutcome.Ok && standaloneOutcome.GeneratedModels == 1,
                "独立模型生成成功：" + string.Join(" / ", standaloneOutcome.LogLines));
            ctx.AssertEqual(0, standaloneOutcome.Stats.ShadowLodQcsWritten, "独立模型无阴影 QC");
            ctx.AssertEqual(0, Directory.GetFiles(standalone, "*_shadowlod*").Length, "独立模型无额外阴影网格");
        }

        [SelfTest("阴影设置：INI 默认值、往返保存、范围和 CLI 覆盖")]
        public static void ShadowSettingsAndCli(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_shadow_ini");
            string settingsDir = Directory.CreateDirectory(Path.Combine(dir, "settings")).FullName;
            string oldDir = Environment.GetEnvironmentVariable(AppInfo.SettingsDirEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(AppInfo.SettingsDirEnvVar, settingsDir);
                File.WriteAllText(SettingsStore.FilePath, "[Advanced]\nGenerateShadowLod=1\nShadowLodRatio=0.3\n");
                var settings = SettingsStore.Load();
                ctx.AssertEqual(0, settings.ShadowLodMode, "旧版设置默认独立阴影");
                ctx.AssertEqual(0, settings.ShadowLodReuseLevel, "旧版设置末档默认");
                settings.ShadowLodMode = 1;
                settings.ShadowLodReuseLevel = 3;
                SettingsStore.Save(settings);
                settings = SettingsStore.Load();
                ctx.AssertEqual(1, settings.ShadowLodMode, "保存复用模式");
                ctx.AssertEqual(3, settings.ShadowLodReuseLevel, "保存指定档位");

                var observer = new CollectingObserver();
                BatchRunOptions Build(params string[] args) => BatchCommand.BuildOptions(CommandLine.Parse(args), dir, settings, observer);
                ctx.Assert(Build().ShadowLodMode == ShadowLodMode.ReuseExistingLod, "CLI 默认采用设置中的复用模式");
                ctx.AssertEqual(3, Build().ShadowLodReuseLevel, "CLI 默认采用设置中的指定档");
                foreach (string alias in new[] { "dedicated", "INDEPENDENT", "0" })
                    ctx.Assert(Build("--shadow-mode=" + alias).ShadowLodMode == ShadowLodMode.Dedicated, "独立阴影别名 " + alias);
                settings.ShadowLodMode = 0;
                foreach (string alias in new[] { "reuse", "EXISTING", "1" })
                    ctx.Assert(Build("--shadow-mode=" + alias).ShadowLodMode == ShadowLodMode.ReuseExistingLod, "复用阴影别名 " + alias);
                ctx.AssertEqual(0, Build("--shadow-reuse-level=LAST").ShadowLodReuseLevel, "last 对应末档");
                ctx.AssertEqual(0, Build("--shadow-reuse-level=-1").ShadowLodReuseLevel, "档位下限");
                ctx.AssertEqual(6, Build("--shadow-reuse-level=7").ShadowLodReuseLevel, "档位上限");
                ctx.Assert(!Build("--shadow-mode=reuse", "--shadowlod", "--no-shadowlod").GenerateShadowLod, "关闭阴影优先");
                settings.GenerateShadowLod = false;
                ctx.Assert(!Build("--shadow-mode=reuse", "--shadow-reuse-level=2").GenerateShadowLod, "选择模式或档位不会打开开关");

                settings.ShadowLodMode = 8;
                settings.ShadowLodReuseLevel = 9;
                settings.Normalize();
                ctx.AssertEqual(1, settings.ShadowLodMode, "设置模式上界");
                ctx.AssertEqual(6, settings.ShadowLodReuseLevel, "设置档位上界");

                WriteGridGlb(dir, 25);
                string qc = WriteQc(dir, "actor", "mesh.glb");
                string output = Path.Combine(dir, "out");
                var cli = CommandLine.Parse(new[] { "--plan", qc, "--out", output, "--shadowlod",
                    "--shadow-mode=reuse", "--shadow-reuse-level=2", "--auto-target=1000" });
                ctx.AssertEqual(0, BatchCommand.RunPlan(cli), "CLI 计划成功");
                var json = Json.ParseObjectSafe(File.ReadAllText(Path.Combine(output, "plan.json")));
                ctx.Assert(Json.GetBool(json, "generateShadowLod"), "CLI 计划标记阴影已启用");
                ctx.AssertEqual(1, Json.GetInt(json, "shadowLodMode", -1), "CLI 计划标记复用模式");
                ctx.AssertEqual(2, Json.GetInt(json, "shadowLodReuseLevel", -1), "CLI 计划标记指定档");
                var counts = (Dictionary<string, object>)json["counts"];
                ctx.AssertEqual(1, Json.GetInt(counts, "visibleJobs", -1), "CLI 计划只安排一档候选");
                ctx.AssertEqual(0, Json.GetInt(counts, "shadowJobs", -1), "CLI 计划不安排阴影任务");
            }
            finally
            {
                Environment.SetEnvironmentVariable(AppInfo.SettingsDirEnvVar, oldDir);
            }
        }

        [SelfTest("批量：基本计划（QC→模型→每级任务）")]
        public static void BasicPlan(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_basic");
            WriteSmdModel(dir, "mesh", 60);
            string qc = WriteQc(dir, "a", "mesh.smd");
            var plan = Plan(new List<TaskRef> { new TaskRef(TaskKind.Qc, qc) }, dir, Options());
            ctx.AssertEqual(1, plan.QcPlans.Count, "QC 数");
            ctx.AssertEqual(1, plan.ProcessCount, "可处理 QC");
            ctx.AssertEqual(1, plan.Models.Count, "唯一模型");
            ctx.AssertEqual(60, plan.Models[0].Triangles, "模型面数");
            ctx.AssertEqual(2, plan.Models[0].Jobs.Count, "每级一个任务");
            ctx.AssertEqual(2, plan.JobCount, "任务总数");
            ctx.AssertEqual(60, (int)plan.TotalSourceTriangles, "原始面数合计");
            ctx.Assert(plan.HasWork, "HasWork");
            ctx.AssertEqual(0.6, plan.Models[0].Jobs[0].Ratio, 1e-9, "第一级比例");
            ctx.Assert(plan.Models[0].Jobs[0].Ratio > plan.Models[0].Jobs[1].Ratio, "比例应逐级降低");
        }

        [SelfTest("批量：精确匹配保留原网格清单")]
        public static void KeepOriginalModels(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_keep_original");
            string parts = Directory.CreateDirectory(Path.Combine(dir, "SMD")).FullName;
            string selected = WriteSmdModel(parts, "body", 60);
            string unselected = WriteSmdModel(dir, "body", 60);
            var tasks = new List<TaskRef> { new TaskRef(TaskKind.Smd, selected), new TaskRef(TaskKind.Smd, unselected) };
            var options = Options();
            options.KeepOriginalModels = "SMD/body.smd";

            var plan = Plan(tasks, dir, options);
            ctx.AssertEqual(2, plan.JobCount, "仅未选中的同名模型生成两级");
            ctx.AssertEqual(0, plan.Models.Single(m => m.Path == selected).Jobs.Count, "选中模型沿用原网格");
            ctx.AssertEqual(2, plan.Models.Single(m => m.Path == unselected).Jobs.Count, "同名文件不受牵连");
        }

        [SelfTest("批量：QC 表情模型默认原样保留")]
        public static void KeepFacialMesh(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_keep_facial");
            WriteSmdModel(dir, "face", 60);
            WriteSmdModel(dir, "body", 100);
            string qc = Path.Combine(dir, "face.qc");
            File.WriteAllText(qc,
                "$modelname \"models/face.mdl\"\n$model \"face\" \"face.smd\"\n{\n" +
                "  flexfile \"face.vta\" { }\n}\n$body \"body\" \"body.smd\"\n");
            var tasks = new List<TaskRef> { new TaskRef(TaskKind.Qc, qc) };
            var options = Options();
            var plan = Plan(tasks, dir, options);
            ctx.AssertEqual(0, plan.Models.Single(m => m.BaseName == "face").Jobs.Count, "表情面默认沿用原网格");
            ctx.AssertEqual(2, plan.Models.Single(m => m.BaseName == "body").Jobs.Count, "其余部件照常减面");
            options.NoFacial = true;
            ctx.AssertEqual(4, Plan(tasks, dir, options).JobCount, "显式禁用表情时可一起减面");
        }

        [SelfTest("批量：100% SMD 沿用源模型，不写重复替换")]
        public static void KeepSmdBytes(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_keep_smd_bytes");
            string model = WriteSmdModel(dir, "body", 60);
            string qc = WriteQc(dir, "actor", "body.smd");
            var tasks = new List<TaskRef> { new TaskRef(TaskKind.Qc, qc) };
            var options = Options(levels: 1);
            options.Plan.Levels[0].Ratio = 1;
            options.WriteReport = false;
            byte[] source = File.ReadAllBytes(model);
            var outcome = BatchRunner.Run(Plan(tasks, dir, options), options, null, CancellationToken.None);
            ctx.Assert(outcome.Ok, "批量应完成");
            ctx.AssertEqual(1, outcome.Stats.UnreducedLevels, "应记录沿用源网格");
            ctx.AssertEqual(0, outcome.Stats.PublishedLods, "不能把相同源网格算为新的 LOD");
            ctx.Assert(File.ReadAllBytes(model).SequenceEqual(source), "源模型应保持原样");
            ctx.Assert(!File.Exists(Path.Combine(dir, "body_lod1.smd")), "不应写内容相同的文件");
            ctx.Assert(!File.ReadAllText(qc).Contains("replacemodel"), "不应写重复替换");
        }

        [SelfTest("批量：面数低于门槛 → 跳过该 QC")]
        public static void MinTrianglesSkip(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_min");
            WriteSmdModel(dir, "mesh", 40);
            string qc = WriteQc(dir, "a", "mesh.smd");
            var plan = Plan(new List<TaskRef> { new TaskRef(TaskKind.Qc, qc) }, dir, Options(minTriangles: 1000));
            ctx.AssertEqual(0, plan.JobCount, "任务数应为 0");
            ctx.AssertEqual(1, plan.SkipCount, "应跳过 1 个 QC");
            ctx.AssertContains(plan.QcPlans[0].Reason, "低于", "跳过原因");
        }

        [SelfTest("批量：已有手写 $lod → 默认跳过，Force 时处理")]
        public static void ForeignLod(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_foreign");
            WriteSmdModel(dir, "mesh", 60);
            string extra = "$lod 40\r\n{\r\n    replacemodel \"mesh.smd\" \"mesh_lod1.smd\"\r\n}";
            string qc = WriteQc(dir, "a", "mesh.smd", extra);
            var optSkip = Options();
            var plan1 = Plan(new List<TaskRef> { new TaskRef(TaskKind.Qc, qc) }, dir, optSkip);
            ctx.AssertEqual(1, plan1.SkipCount, "默认应跳过");
            ctx.AssertContains(plan1.QcPlans[0].Reason, "手写 $lod", "跳过原因");
            var optForce = Options();
            optForce.Force = true;
            var plan2 = Plan(new List<TaskRef> { new TaskRef(TaskKind.Qc, qc) }, dir, optForce);
            ctx.AssertEqual(1, plan2.ProcessCount, "Force 时应处理");
            ctx.AssertEqual(2, plan2.JobCount, "Force 时应生成任务");
            // 仅合集模式也不跳过（因为不改 QC）
            var optCollection = Options();
            optCollection.OutputMode = OutputMode.CollectionOnly;
            var plan3 = Plan(new List<TaskRef> { new TaskRef(TaskKind.Qc, qc) }, dir, optCollection);
            ctx.AssertEqual(1, plan3.ProcessCount, "合集模式应处理");
        }

        [SelfTest("批量：工具块不完整 → 错误、不处理")]
        public static void BrokenBlock(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_broken");
            WriteSmdModel(dir, "mesh", 60);
            string qc = WriteQc(dir, "a", "mesh.smd", null, QcLexer.MarkerBegin);
            var plan = Plan(new List<TaskRef> { new TaskRef(TaskKind.Qc, qc) }, dir, Options());
            ctx.AssertEqual(1, plan.ErrorCount, "应报 1 个错误");
            ctx.AssertContains(plan.QcPlans[0].Reason, "工具标记块不完整", "错误原因");
            ctx.AssertEqual(0, plan.JobCount, "不应生成任务");
        }

        [SelfTest("批量：散落模型 → 计划里登记包装 QC（不写盘）")]
        public static void WrapperPlan(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_wrapper");
            string model = WriteSmdModel(dir, "loose", 60);
            string wrapper = ProjectScanner.WrapperQcPath(model);
            var plan = Plan(new List<TaskRef> { new TaskRef(TaskKind.Smd, model) }, dir, Options());
            ctx.AssertEqual(1, plan.QcPlans.Count, "QC 数");
            ctx.Assert(plan.QcPlans[0].IsWrapperQc, "应标记为包装 QC");
            ctx.AssertEqual(FileUtil.Normalize(wrapper), FileUtil.Normalize(plan.QcPlans[0].QcPath), "包装 QC 路径");
            ctx.AssertEqual(1, plan.PendingWrappers.Count, "执行时才创建的包装 QC 应登记 1 个");
            ctx.Assert(!File.Exists(wrapper), "计划阶段不应创建包装 QC 文件");
            ctx.AssertEqual(1, plan.Models.Count, "模型数");
            ctx.AssertEqual(2, plan.JobCount, "任务数");
        }

        [SelfTest("批量：多 QC 引用同一模型只简化一次")]
        public static void GlobalDedupe(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_dedupe");
            WriteSmdModel(dir, "shared", 60);
            string qc1 = WriteQc(dir, "a", "shared.smd");
            string qc2 = WriteQc(dir, "b", "shared.smd");
            var plan = Plan(new List<TaskRef> { new TaskRef(TaskKind.Qc, qc1), new TaskRef(TaskKind.Qc, qc2) }, dir, Options());
            ctx.AssertEqual(2, plan.ProcessCount, "两个 QC 都应处理");
            ctx.AssertEqual(1, plan.Models.Count, "模型应去重为 1 个");
            ctx.AssertEqual(2, plan.JobCount, "任务数不应翻倍");
            ctx.AssertEqual(2, plan.Models[0].RootQcs.Count, "模型应归属两个根 QC");
        }

        [SelfTest("批量：被别的 QC $include 的 QC 从根清单剔除")]
        public static void PruneIncluded(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_prune");
            WriteSmdModel(dir, "mesh", 60);
            WriteQc(dir, "child", "mesh.smd");
            string root = Path.Combine(dir, "root.qc");
            File.WriteAllText(root,
                "$modelname \"models/root.mdl\"\r\n$include \"child.qc\"\r\n", new UTF8Encoding(false));
            var plan = Plan(new List<TaskRef> { new TaskRef(TaskKind.Qc, root), new TaskRef(TaskKind.Qc, Path.Combine(dir, "child.qc")) }, dir, Options());
            ctx.AssertEqual(1, plan.QcPlans.Count, "被 include 的子 QC 应被剔除");
            ctx.AssertEqual(FileUtil.Normalize(root), FileUtil.Normalize(plan.QcPlans[0].QcPath), "应保留根 QC");
            ctx.AssertEqual(1, plan.Models.Count, "模型应随根 QC 处理");
        }

        [SelfTest("批量：模型文件缺失 → 跳过并给出原因")]
        public static void MissingModel(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_missing");
            string qc = WriteQc(dir, "a", "not_here.smd");
            var plan = Plan(new List<TaskRef> { new TaskRef(TaskKind.Qc, qc) }, dir, Options());
            ctx.AssertEqual(0, plan.JobCount, "不应有任务");
            ctx.AssertEqual(1, plan.SkipCount, "应跳过");
            ctx.AssertContains(plan.QcPlans[0].Reason, "无可处理模型", "跳过原因");
        }

        [SelfTest("批量：SMD 解析失败 → 该 QC 记为错误")]
        public static void BadModel(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_badmodel");
            File.WriteAllText(Path.Combine(dir, "bad.smd"), "这不是 SMD 文件", new UTF8Encoding(false));
            string qc = WriteQc(dir, "a", "bad.smd");
            var plan = Plan(new List<TaskRef> { new TaskRef(TaskKind.Qc, qc) }, dir, Options());
            ctx.AssertEqual(1, plan.ErrorCount, "应报错误");
            ctx.AssertContains(plan.QcPlans[0].Reason, "读取模型失败", "错误原因");
        }

        [SelfTest("批量：不同目录的同名 SMD 各自生成任务（临时文件不冲突）")]
        public static void SmdSameBasenameDifferentDirs(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_smd_same_name");
            string d1 = Path.Combine(dir, "a");
            string d2 = Path.Combine(dir, "b");
            Directory.CreateDirectory(d1);
            Directory.CreateDirectory(d2);
            WriteSmdModel(d1, "mesh", 60);
            WriteSmdModel(d2, "mesh", 60);
            string qc1 = WriteQc(d1, "qa", "mesh.smd");
            string qc2 = WriteQc(d2, "qb", "mesh.smd");
            var plan = Plan(new List<TaskRef> { new TaskRef(TaskKind.Qc, qc1), new TaskRef(TaskKind.Qc, qc2) }, dir, Options());
            ctx.AssertEqual(2, plan.ProcessCount, "两个 QC 都应处理");
            ctx.AssertEqual(2, plan.Models.Count, "应识别出两个同名但不同路径的模型");
            ctx.AssertEqual(4, plan.JobCount, "每个模型 2 级 → 4 个任务");
            foreach (var m in plan.Models)
                ctx.AssertEqual(2, m.Jobs.Count, "模型 " + m.BaseName + " 的任务数");
            var dsts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in plan.Models) foreach (var j in m.Jobs) ctx.Assert(dsts.Add(j.Destination), "临时输出路径重复：" + j.Destination);
        }

        [SelfTest("批量：计划阶段完全只读（不创建/修改任何文件）")]
        public static void PlanIsReadOnly(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("batch_readonly");
            WriteSmdModel(dir, "mesh", 60);
            WriteQc(dir, "a", "mesh.smd");
            string model2 = WriteSmdModel(dir, "loose", 60);
            var before = Snapshot(dir);
            var plan = Plan(new List<TaskRef>
            {
                new TaskRef(TaskKind.Qc, Path.Combine(dir, "a.qc")),
                new TaskRef(TaskKind.Smd, model2)
            }, dir, Options());
            var after = Snapshot(dir);
            ctx.AssertEqual(before, after, "计划阶段目录快照（文件内容哈希）");
            ctx.Assert(plan.JobCount > 0, "应生成任务");
        }

        // ─────────────────────── 工具 ───────────────────────

        private static string Snapshot(string dir)
        {
            var sb = new StringBuilder();
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            {
                sb.Append(f.Substring(dir.Length)).Append('|');
                try
                {
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    {
                        byte[] h = sha.ComputeHash(File.ReadAllBytes(f));
                        sb.Append(BitConverter.ToString(h));
                    }
                }
                catch { sb.Append("ERR"); }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
