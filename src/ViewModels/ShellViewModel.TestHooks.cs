using System;
using System.Collections.Generic;
using System.Globalization;
using LodStudio.Controls;
using LodStudio.Engine;

namespace LodStudio.ViewModels
{
    /// <summary>
    /// 仅给 --ui-audit / 自动化测试用的状态注入钩子（同程序集 internal）。
    /// 生产代码不得调用；这些方法只改界面状态，不写用户文件、不启动引擎。
    /// </summary>
    public sealed partial class ShellViewModel
    {
        internal void TestAddTasks(List<(TaskKind kind, string path, TaskStatus status, int qc, int models, string note)> items)
        {
            Queue.Clear();
            foreach (var it in items)
            {
                bool dup;
                Queue.Add(new TaskRef(it.kind, it.path), out dup);
                var vm = Queue.Items[Queue.Items.Count - 1];
                vm.Scanned = true;
                vm.QcCount = it.qc;
                vm.ModelCount = it.models;
                vm.Note = it.note ?? "";
                vm.Status = it.status;
                if (it.status == TaskStatus.Failed) { vm.RunDetail = "已写入 0 / 2 个 QC"; vm.RunErrors = "模型 body_lod2.glb 简化失败：减面进程崩溃（退出码 -1073741819）"; }
                if (it.status == TaskStatus.Partial) { vm.RunDetail = "已写入 2 / 3 个 QC，跳过 1"; vm.RunErrors = "weapon.qc：模型 crate_lod1.glb 输出文件不存在"; }
                if (it.status == TaskStatus.Ok) vm.RunDetail = "已写入 3 / 3 个 QC";
                if (it.status == TaskStatus.Skipped) vm.RunDetail = "已有手写 $lod，已跳过";
            }
            Queue.Renumber();
            Queue.UpdateCounts();
            UpdateActions();
            IsPlanVisible = false;
        }

        internal void TestSetStatus(string title, string sub, string tone)
        {
            StatusTitle = title;
            StatusSub = sub;
            StatusTone = tone;
        }

        internal void TestSetReadout(string main, string sub)
        {
            ReadoutMainText = main;
            ReadoutSub = sub;
        }

        internal void TestSetProgress(bool visible, double value, bool indeterminate, bool failed)
        {
            ProgressVisible = visible;
            ProgressValue = value;
            ProgressIndeterminate = indeterminate;
            ProgressFailed = failed;
        }

        internal void TestSetRunning(bool running)
        {
            IsRunning = running;
            ProgressVisible = running;
            if (running) { ProgressIndeterminate = true; SetStatus("正在生成…", "准备减面任务", "Signal", true); }
        }

        internal void TestSetEnvStrings(string reductionText, string reductionTone, string compilerText, string compilerTone, string permText, string permTone)
        {
            ReductionText = reductionText; ReductionTone = reductionTone;
            CompilerText = compilerText; CompilerTone = compilerTone;
            PermissionText = permText; PermissionTone = permTone;
            RefreshEnvStatus();
        }

        internal void TestSetAdvancedSummary(string text) { AdvancedSummary = text; }
        internal void TestSetSearch(string text) { Queue.SearchText = text; }
        internal void TestSetFilterIndex(int index) { Queue.FilterIndex = index; }
        internal void TestSetLogExpanded(bool expanded) { IsLogExpanded = expanded; }

        internal void TestFillLogs(int count, bool withErrors)
        {
            Log.Clear();
            Log.Append(LogLevel.Info, AppInfo.TitleText + " 已启动");
            Log.Append(LogLevel.Detail, "设置文件：" + AppInfo.SettingsFile);
            Log.Append(LogLevel.Stage, "═══ 扫描预览（只读，不会修改任何文件）═══");
            string[] samples =
            {
                "[工程] 8人自动编译丨示例 → 3 个 QC · 12 个模型",
                "  └ 已按 .bat 读到 3 个 QC（批量编译.bat）",
                "  └ 2 个 QC 已有手写 $lod，将跳过（可在高级设置里覆盖）",
                "[QC] actor.qc → 1 个模型（角色）",
                "解析 actor_main.qc → 展开 2 个文件，6 个 studio 条目",
                "模型 body.glb 面数 12,964 ≥ 门槛 200，将生成 4 级 LOD",
                "面数低于门槛的模型跳过：hair_clip.glb（96 面）",
                "正在减面 3 / 24：body_lod1.glb",
                "✔ body_lod1.glb  12964 → 12923（目标 12927）",
                "✔ body_lod2.glb  12964 → 8466（目标 8466）",
                "[提示] hair_chain 的实际面数 214 超出目标 180 达 19%（3 个小块因差值<2 未简化）",
                "正在把 $lod 写回 QC（4 个）",
                "✔ actor.qc → 追加 4 级 $lod",
                "✔ weapon.qc → 追加 4 级 $lod",
                "═══ 开始生成：平衡 · 6 个 QC · 24 个任务 ═══",
                "简化完成：成功 24 / 24，用时 1.8 分钟",
                "总报告：_LOD运行报告_1115_184210.md",
                "════ 全部完成 ════"
            };
            for (int i = 0; i < count; i++)
            {
                string s = samples[i % samples.Length] + (i >= samples.Length ? "（" + (i / samples.Length + 1) + "）" : "");
                LogLevel lvl = LogLevel.Info;
                if (withErrors && i % 7 == 3) lvl = LogLevel.Error;
                else if (i % 5 == 2) lvl = LogLevel.Warn;
                Log.Append(lvl, s);
            }
        }

        internal void TestSetPlanPreview(int qcCount, int modelCount, int jobCount)
        {
            var options = BuildRunOptions(null);
            var plan = new BatchPlan
            {
                RootDir = @"E:\L4D2\求生之路2 MOD制作\工程文件\8人自动编译",
                BuiltAt = DateTime.Now,
                Automatic = options.Automatic,
                PresetName = options.PresetName,
                Plan = LodPlan.ClonePlan(),
                JobCount = jobCount,
                TotalSourceTriangles = 428800,
                HasWork = true
            };
            for (int i = 0; i < qcCount; i++)
            {
                var qp = new BatchQcPlan
                {
                    QcPath = @"E:\L4D2\求生之路2 MOD制作\工程文件\8人自动编译\qc\" + (i == 1 ? "handwritten.qc" : "actor" + i + ".qc"),
                    Action = i == 1 ? BatchQcAction.Skip : BatchQcAction.Process,
                    Reason = i == 1 ? "已有手写 $lod（在 actor_main.qc）；要覆盖请在高级设置里勾选「覆盖手写 $lod」" : ""
                };
                if (plan.Automatic)
                {
                    qp.SourceTriangles = 169498;
                    qp.Levels = AutomaticLod.Plan(qp.SourceTriangles);
                }
                for (int m = 0; m < Math.Max(1, modelCount / Math.Max(1, qcCount)); m++)
                {
                    var pm = new PlannedModel
                    {
                        Path = @"E:\L4D2\...\models\SMD\body" + i + "_" + m + ".glb",
                        BaseName = "body" + i + "_" + m,
                        Dir = @"E:\L4D2\...\models\SMD",
                        Size = 1024 * 1024,
                        Triangles = 12964,
                        Jobs = new List<ReductionJob>()
                    };
                    for (int j = 0; j < LodPlan.Plan.Count; j++) pm.Jobs.Add(new ReductionJob());
                    qp.Models.Add(pm);
                }
                foreach (var pm0 in qp.Models) plan.Models.Add(pm0);   // 摘要按 plan.Models.Count 显示
                plan.QcPlans.Add(qp);
            }
            ShowPreview(CapturePlanRequest(), plan);
        }

        internal void TestSetLastRun(bool ok, string reportPath, string outputDir)
        {
            _lastRunAt = DateTime.Now.AddMinutes(-3);
            _lastOk = ok ? 6 : 4;
            _lastPartial = 0;
            _lastFailed = ok ? 0 : 1;
            _lastReportPath = reportPath;
            _lastOutputDir = outputDir;
            Raise("HasLastRun"); Raise("LastRunText"); Raise("CanOpenLastReport"); Raise("CanOpenOutputFolder");
        }

        internal void TestResetState()
        {
            Queue.Clear();
            Queue.SearchText = "";
            Queue.FilterIndex = 0;
            Queue.UpdateCounts();
            Log.Clear();
            Log.Filter = 0;
            PlanPreview = null;
            _previewRequest = null;
            IsPlanVisible = false;
            IsRunning = false;
            ProgressVisible = false;
            ProgressValue = 0;
            ProgressIndeterminate = false;
            ProgressFailed = false;
            _lastRunAt = DateTime.MinValue;
            _lastReportPath = null;
            _lastOutputDir = null;
            Raise("HasLastRun"); Raise("LastRunText"); Raise("CanOpenLastReport"); Raise("CanOpenOutputFolder");
            UpdateQueueStatus();

        }
    }
}
