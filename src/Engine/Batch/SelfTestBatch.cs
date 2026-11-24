using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

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
