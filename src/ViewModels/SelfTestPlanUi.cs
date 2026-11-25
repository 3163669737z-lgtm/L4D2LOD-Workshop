using System;
using System.Collections.Generic;
using LodStudio.Engine;

namespace LodStudio.ViewModels
{
    /// <summary>计划视图（UI 层）的自测：与引擎计划的契约一致性。</summary>
    internal static class SelfTestPlanUi
    {
        [SelfTest("计划视图：行/摘要/按钮可用性与引擎计划一致")]
        public static void PlanPreviewMapping(SelfTestContext ctx)
        {
            var plan = new BatchPlan
            {
                RootDir = @"C:\synthetic",
                BuiltAt = new DateTime(2026, 11, 15, 12, 0, 0),
                PresetName = "测试方案",
                Plan = LodPlan.CreateDefault(),
                JobCount = 7,
                TotalSourceTriangles = 12345,
                HasWork = true
            };
            var process = new BatchQcPlan { QcPath = @"C:\synthetic\a.qc", Action = BatchQcAction.Process };
            var plannedModel = new PlannedModel { Path = @"C:\synthetic\a.smd", BaseName = "a", Triangles = 1000, Jobs = new List<ReductionJob> { new ReductionJob { Level = 1 }, new ReductionJob { Level = 2 } } };
            process.Models.Add(plannedModel);
            plan.Models.Add(plannedModel);
            var skip = new BatchQcPlan { QcPath = @"C:\synthetic\b.qc", Action = BatchQcAction.Skip, Reason = "已有手写 $lod" };
            var error = new BatchQcPlan { QcPath = @"C:\synthetic\c.qc", Action = BatchQcAction.Error, Reason = "工具标记块不完整（缺结束标记），请手动检查" };
            plan.QcPlans.Add(process);
            plan.QcPlans.Add(skip);
            plan.QcPlans.Add(error);

            var vm = new PlanPreviewViewModel(plan, new BatchRunOptions { OutputMode = OutputMode.SeparateQc });
            ctx.AssertEqual(3, vm.Rows.Count, "行数");
            ctx.AssertEqual(1, vm.ProcessCount, "可处理数");
            ctx.AssertEqual(1, vm.SkipCount, "跳过数");
            ctx.AssertEqual(1, vm.ErrorCount, "错误数");
            ctx.AssertEqual(1, vm.ModelCount, "模型数");
            ctx.AssertEqual(7, vm.JobCount, "任务数（来自 plan.JobCount）");
            ctx.Assert(vm.CanStartFromPlan, "有可处理项时按钮应可用");
            ctx.AssertContains(vm.Headline, "1 项生成", "Headline");
            ctx.AssertContains(vm.Subline, "1 项跳过", "Subline");
            ctx.AssertContains(vm.WriteText, "$include", "预览应显示独立 QC 写入方式");
            // 错误项排最后
            ctx.AssertEqual("c.qc", vm.Rows[vm.Rows.Count - 1].QcName, "错误行应排在最后");
            ctx.AssertEqual("生成", vm.Rows[0].ActionText, "动作文案");
            ctx.AssertEqual("跳过", vm.Rows[1].ActionText, "跳过动作文案");
            ctx.AssertEqual("错误", vm.Rows[2].ActionText, "错误动作文案");
            vm.MarkStale();
            ctx.Assert(vm.IsStale && !vm.CanStartFromPlan, "过期计划不能开始生成");
        }

        [SelfTest("计划视图：自动模式沿用表情、覆盖和试编译开关")]
        public static void AutomaticToolSettings(SelfTestContext ctx)
        {
            var settings = new AppSettings
            {
                PresetName = PresetLibrary.AutomaticName,
                OutputMode = (int)OutputMode.CollectionOnly,
                NoFacial = true,
                NoFacialFromLevel = 2,
                ForceOverwriteLod = true,
                CompileAfter = true,
                NekomdlPath = @"C:\synthetic\nekomdl.exe"
            };
            var shell = new ShellViewModel(settings, null);
            var options = shell.BuildRunOptions(ctx.Root);
            ctx.Assert(options.Automatic && options.OutputMode == OutputMode.InPlace && options.BackupEnabled,
                "自动模式写回原 QC 并备份");
            ctx.Assert(options.NoFacial && options.Force && options.CompileAfter, "自动模式应用工具开关");
            ctx.AssertEqual(2, options.NoFacialFromLevel, "沿用 nofacial 起始档");
            ctx.AssertEqual(settings.NekomdlPath, options.NekomdlPath, "试编译传入编译器路径");
            shell.RefreshAdvancedSummary();
            foreach (string fragment in new[] { "覆盖手写", "试编译", "nofacial(≥2)" })
                ctx.AssertContains(shell.AdvancedSummary, fragment, "自动模式设置摘要");

            var levels = new LodPlan();
            levels.Levels.Add(new LodLevel(10, 0.7));
            levels.Levels.Add(new LodLevel(20, 0.4));
            var limited = new LodPlan();
            limited.Levels.Add(levels.Levels[0]);
            var plan = new BatchPlan { Automatic = true, Plan = levels, HasWork = true, JobCount = 2 };
            var qc = new BatchQcPlan { QcPath = "mixed.qc", Levels = limited };
            var body = new PlannedModel { Path = "body.smd", Jobs = { new ReductionJob { Level = 1 } } };
            var face = new PlannedModel { Path = "face.smd", Jobs = { new ReductionJob { Level = 2 } } };
            qc.Models.AddRange(new[] { body, face });
            plan.QcPlans.Add(qc);
            plan.Models.AddRange(qc.Models);
            var preview = new PlanPreviewViewModel(plan, options);
            ctx.AssertContains(preview.WriteText, "从第 2 级起应用 nofacial", "自动模式的写入摘要");
            ctx.AssertContains(preview.Rows[0].Counts, "1 个减面模型 · 1 个可见 LOD 任务", "预览只计入 QC 可用的任务档位");
        }
    }
}
