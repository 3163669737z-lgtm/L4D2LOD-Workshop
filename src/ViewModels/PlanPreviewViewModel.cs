using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using LodStudio.Engine;

namespace LodStudio.ViewModels
{
    /// <summary>计划视图的一行（一个根 QC 的动作）。</summary>
    public sealed class PlanQcRowVm
    {
        // 必须是属性：WPF 的 {Binding X} 不能绑字段（绑字段会静默显示空白）
        public string QcName { get; set; }
        public string QcPath { get; set; }
        public string ActionText { get; set; }
        public string ActionTone { get; set; }       // Ok/Skip/Err
        public string Reason { get; set; }
        public int ModelCount { get; set; }
        public int JobCount { get; set; }
        public string ModelsText { get; set; }
        public string Tooltip { get; set; }
        public bool Actionable { get; set; }
    }

    /// <summary>
    /// 扫描预览 / 作业计划视图：只读阶段的产物。用户在这里看"会做什么、哪些跳过、为什么"，
    /// 也可以直接按这个计划开始生成。
    /// </summary>
    public sealed class PlanPreviewViewModel : VmBase
    {
        private readonly BatchPlan _plan;

        public PlanPreviewViewModel(BatchPlan plan)
        {
            _plan = plan;
            Rows = new ObservableCollection<PlanQcRowVm>();
            foreach (var q in plan.QcPlans.OrderBy(x => x.Action == BatchQcAction.Skip ? 1 : x.Action == BatchQcAction.Error ? 2 : 0))
            {
                string action = q.Action == BatchQcAction.Process ? "生成" : q.Action == BatchQcAction.Skip ? "跳过" : "错误";
                string tone = q.Action == BatchQcAction.Process ? "Ok" : q.Action == BatchQcAction.Skip ? "Faint" : "Err";
                int jobs = q.Models.Sum(m => m.Jobs.Count);
                var modelsText = q.Models.Count == 0 ? "—"
                    : string.Join("  ", q.Models.Take(3).Select(m => m.BaseName + " (" + m.Triangles.ToString("N0", CultureInfo.InvariantCulture) + ")")) +
                      (q.Models.Count > 3 ? "  …+" + (q.Models.Count - 3) : "");
                string tip = q.QcPath;
                if (q.Info != null)
                {
                    tip += "\n模型名: " + (string.IsNullOrEmpty(q.Info.ModelName) ? "(未声明)" : q.Info.ModelName);
                    tip += "\nstudio 条目: " + q.Info.Studios.Count;
                }
                if (!string.IsNullOrEmpty(q.Reason)) tip += "\n原因: " + q.Reason;
                Rows.Add(new PlanQcRowVm
                {
                    QcName = System.IO.Path.GetFileName(q.QcPath),
                    QcPath = q.QcPath,
                    ActionText = action,
                    ActionTone = tone,
                    Reason = q.Reason ?? "",
                    ModelCount = q.Models.Count,
                    JobCount = jobs,
                    ModelsText = modelsText,
                    Tooltip = tip,
                    Actionable = q.Action == BatchQcAction.Process
                });
            }
            CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
            StartCommand = new RelayCommand(() => StartRequested?.Invoke(this, EventArgs.Empty));
        }

        public BatchPlan Plan { get { return _plan; } }
        public ObservableCollection<PlanQcRowVm> Rows { get; private set; }
        public event EventHandler CloseRequested;
        public event EventHandler StartRequested;
        public RelayCommand CloseCommand { get; private set; }
        public RelayCommand StartCommand { get; private set; }

        public int ProcessCount { get { return _plan.ProcessCount; } }
        public int SkipCount { get { return _plan.SkipCount; } }
        public int ErrorCount { get { return _plan.ErrorCount; } }
        public int ModelCount { get { return _plan.Models.Count; } }
        public int JobCount { get { return _plan.JobCount; } }

        public string Headline
        {
            get
            {
                if (_plan.QcPlans.Count == 0) return "没有找到可处理的 QC";
                return "将处理 " + ProcessCount + " 个 QC · " + ModelCount + " 个模型 · " + JobCount + " 个减面任务";
            }
        }

        public string Subline
        {
            get
            {
                var parts = new List<string>();
                if (SkipCount > 0) parts.Add("跳过 " + SkipCount);
                if (ErrorCount > 0) parts.Add("错误 " + ErrorCount);
                if (parts.Count == 0) parts.Add("没有跳过项");
                parts.Add("计划生成于 " + _plan.BuiltAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                return string.Join(" · ", parts.ToArray());
            }
        }

        public bool HasErrors { get { return ErrorCount > 0; } }
        public bool CanStartFromPlan { get { return ProcessCount > 0; } }
        public bool IsEmpty { get { return _plan.QcPlans.Count == 0; } }
    }
}
