using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LodStudio.Engine;

namespace LodStudio.ViewModels
{
    public sealed class PlanQcRowVm
    {
        public string QcName { get; set; }
        public string QcPath { get; set; }
        public string ActionText { get; set; }
        public string ActionTone { get; set; }
        public string Summary { get; set; }
        public string Counts { get; set; }
        public string Tooltip { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>只读预览同时描述会执行的作业和可能修改的文件。</summary>
    public sealed class PlanPreviewViewModel : VmBase
    {
        private bool _isStale;

        public PlanPreviewViewModel(BatchPlan plan, BatchRunOptions options)
        {
            Plan = plan;
            var rows = new List<PlanQcRowVm>();
            foreach (var qc in plan.QcPlans.OrderBy(q => q.Action))
            {
                int maxLevel = (qc.Levels ?? plan.Plan).Count;
                var modelJobs = qc.Models.Select(m => (Visible: qc.VisibleJobs(m, maxLevel, options).Count(),
                    Shadow: qc.UsesShadowJob(m, options))).ToList();
                int visibleJobs = modelJobs.Sum(m => m.Visible);
                int shadowJobs = modelJobs.Count(m => m.Shadow);
                string jobSummary = DescribeJobs(visibleJobs, shadowJobs);
                bool process = qc.Action == BatchQcAction.Process;
                bool remove = RemovesOldBlock(plan, qc, options);
                var action = process ? "生成" : remove ? "清理" : qc.Action == BatchQcAction.Skip ? "跳过" : "错误";
                var tone = process ? "Ok" : remove ? "Warn" : qc.Action == BatchQcAction.Skip ? "Normal" : "Err";
                string summary = qc.Reason;
                var detail = new List<string>();
                if (!string.IsNullOrEmpty(qc.Reason)) detail.Add(qc.Reason);
                if (remove) detail.Add("将尝试移除已无有效档位的旧工具块。");
                if (process)
                {
                    summary = qc.IsStandalone ? "生成 LOD 网格" : qc.IsWrapperQc ? "新建包装 QC" : DescribeQcWrite(options.OutputMode);
                    if (plan.Automatic && qc.Levels?.Count > 0)
                    {
                        summary = qc.SourceTriangles.ToString("N0", CultureInfo.InvariantCulture) + " 面 · " + qc.Levels.Count + " 个候选档";
                        detail.Add(summary);
                        detail.Add("目标保留比例 " + string.Join(" / ", qc.Levels.Levels.Select(level => LodPlan.Pct(level.Ratio) + "%")));
                        detail.Add("有效档位、面数和切换距离以生成报告为准。");
                    }
                    if (qc.IsWrapperQc) detail.Add("将为源模型新建包装 QC。");
                    detail.Add(jobSummary);
                    if (options.GenerateShadowLod && !qc.IsStandalone)
                        detail.Add(DescribeShadow(options));
                }
                foreach (var model in qc.Models)
                {
                    string reason = model.ReadError ?? model.SkipReason;
                    detail.Add(model.BaseName + "  ·  " +
                        (reason ?? model.Triangles.ToString("N0", CultureInfo.InvariantCulture) + " 面") + "\n" + model.Path);
                }
                rows.Add(CreateRow(qc.QcPath, action, tone, summary,
                    process ? modelJobs.Count(m => m.Visible > 0 || m.Shadow) + " 个减面模型 · " + jobSummary : "", string.Join("\n", detail)));
            }
            var failedTasks = plan.Tasks.Where(t => !string.IsNullOrEmpty(t.Error)).ToList();
            foreach (var task in failedTasks)
                rows.Add(CreateRow(task.Task.Path, "错误", "Err", task.Error, "", task.Error));

            Rows = rows;
            ProcessCount = plan.ProcessCount;
            SkipCount = plan.SkipCount - plan.QcPlans.Count(q => RemovesOldBlock(plan, q, options));
            ErrorCount = plan.ErrorCount + failedTasks.Count;
            CleanupCount = plan.SkipCount - SkipCount;
            ModelCount = plan.Models.Count(m => m.Jobs.Count > 0 || m.ShadowJob != null);
            JobCount = plan.JobCount;
            HasWorkToStart = HasWork(plan, options);
            int dedicatedJobs = plan.Models.Count(m => m.ShadowJob != null);
            Headline = CleanupOnly ? "可清理 " + CleanupCount + " 个旧 $lod 工具块" : HasWorkToStart
                ? ProcessCount + " 项生成 · " + ModelCount + " 个模型参与减面 · " + DescribeJobs(JobCount - dedicatedJobs, dedicatedJobs)
                : "暂无可生成的作业";
            var breakdown = new List<string>();
            if (CleanupCount > 0) breakdown.Add(CleanupCount + " 项清理");
            if (SkipCount > 0) breakdown.Add(SkipCount + " 项跳过");
            if (ErrorCount > 0) breakdown.Add(ErrorCount + " 项错误");
            Subline = string.Join(" · ", breakdown);
            LevelsText = DescribeLevels(plan);
            WriteText = WriteSummary(plan, options);
        }

        public BatchPlan Plan { get; }
        public IReadOnlyList<PlanQcRowVm> Rows { get; }
        public int ProcessCount { get; }
        public int SkipCount { get; }
        public int ErrorCount { get; }
        public int CleanupCount { get; }
        public bool CleanupOnly => ProcessCount == 0 && CleanupCount > 0;
        public int ModelCount { get; }
        public int JobCount { get; }
        public string Headline { get; }
        public string Subline { get; }
        public bool HasBreakdown => Subline.Length > 0;
        public string LevelsText { get; }
        public string WriteText { get; }
        public bool IsEmpty => Rows.Count == 0;
        public bool IsStale => _isStale;
        public bool CanStartFromPlan => HasWorkToStart && !IsStale;
        private bool HasWorkToStart { get; }

        public void MarkStale()
        {
            if (Set(ref _isStale, true, nameof(IsStale))) Raise(nameof(CanStartFromPlan));
        }

        private static PlanQcRowVm CreateRow(string path, string action, string tone, string summary, string counts, string detail)
        {
            return new PlanQcRowVm
            {
                QcName = Path.GetFileName(path.TrimEnd('\\', '/')),
                QcPath = path,
                ActionText = action,
                ActionTone = tone,
                Summary = summary,
                Counts = counts,
                Tooltip = path + (string.IsNullOrEmpty(summary) ? "" : "\n" + summary),
                Detail = detail
            };
        }

        internal static bool RemovesOldBlock(BatchPlan plan, BatchQcPlan qc, BatchRunOptions options) =>
            !plan.Automatic && options.OutputMode != OutputMode.CollectionOnly && qc.Action == BatchQcAction.Skip &&
            qc.Info?.HasToolMarker == true;

        internal static bool ModifiesQc(BatchPlan plan, BatchRunOptions options) =>
            options.OutputMode != OutputMode.CollectionOnly &&
            plan.QcPlans.Any(q => !q.IsStandalone && (q.Action == BatchQcAction.Process || RemovesOldBlock(plan, q, options)));

        internal static bool HasWork(BatchPlan plan, BatchRunOptions options) =>
            plan.HasWork || plan.QcPlans.Any(q => RemovesOldBlock(plan, q, options));

        internal static string DescribeLevels(BatchPlan plan) => plan.Automatic
            ? "自动 · 预估候选档位和保留比例，切换距离以生成报告为准"
            : plan.PresetName + " · " + string.Join("  /  ",
                plan.Plan.Levels.Select(level => LodPlan.Fmt(level.Distance) + " HU → " + LodPlan.Pct(level.Ratio) + "%"));

        internal static string WriteSummary(BatchPlan plan, BatchRunOptions options)
        {
            if (!HasWork(plan, options)) return "本次没有写入操作";
            var parts = new List<string>();
            int qcCount = plan.QcPlans.Count(q => q.Action == BatchQcAction.Process && !q.IsStandalone);
            int standaloneCount = plan.QcPlans.Count(q => q.Action == BatchQcAction.Process && q.IsStandalone);
            if (qcCount > 0) parts.Add(qcCount + " 个 QC · " + DescribeQcWrite(options.OutputMode));
            if (standaloneCount > 0) parts.Add(standaloneCount + " 个独立模型 · 生成 LOD 网格");
            if (options.GenerateShadowLod && qcCount > 0) parts.Add(DescribeShadow(options));
            else if (plan.Models.Any(m => m.ShadowJob != null))
                parts.Add("独立阴影网格 " + LodPlan.Pct(options.ShadowLodRatio) + "%");
            if (qcCount > 0 && options.NoFacial)
                parts.Add("从第 " + options.NoFacialFromLevel + " 级起应用 nofacial");
            if (qcCount > 0 && !string.IsNullOrWhiteSpace(options.CollapseBones))
                parts.Add("从第 " + options.CollapseBonesFromLevel + " 级起应用 bonetreecollapse");
            int cleanupCount = plan.QcPlans.Count(q => RemovesOldBlock(plan, q, options));
            if (cleanupCount > 0) parts.Add(cleanupCount + " 个 QC · 清理旧工具块");
            return string.Join("，", parts);
        }

        private static string DescribeJobs(int visible, int dedicated) =>
            visible + " 个可见 LOD 任务" + (dedicated > 0 ? " · " + dedicated + " 个独立阴影任务" : "");

        private static string DescribeShadow(BatchRunOptions options)
        {
            if (options.ShadowLodMode == ShadowLodMode.Dedicated)
                return "含 $shadowlod（独立阴影网格 " + LodPlan.Pct(options.ShadowLodRatio) + "%）";
            return options.ShadowLodReuseLevel == 0 ? "含 $shadowlod（复用最远一级 LOD）"
                : "含 $shadowlod（复用第 " + options.ShadowLodReuseLevel + " 级 LOD，缺档时用最远一级）";
        }

        private static string DescribeQcWrite(OutputMode mode)
        {
            switch (mode)
            {
                case OutputMode.SeparateQc: return "生成 _LOD.qc，并在原 QC 中添加 $include";
                case OutputMode.CollectionOnly: return "输出 _LOD合集.qc";
                default: return "将有效 $lod 写入 QC 或声明模型的包含文件";
            }
        }
    }
}
