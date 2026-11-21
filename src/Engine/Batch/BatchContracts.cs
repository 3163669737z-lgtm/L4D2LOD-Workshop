using System;
using System.Collections.Generic;
using System.IO;

namespace LodStudio.Engine
{
    // ══════════════════════════════════════════════════════════════════════════
    //  批量计划与执行契约。
    //  BatchPlanner：只读（不写任何文件）→ 产出可展示、可执行的计划。
    //  BatchRunner ：按计划执行（建包装 QC → Blender → SMD 回转 → 注入 → 试编译 → 报告）。
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>一个用户任务展开后的 QC 清单（顺序稳定）。</summary>
    public sealed class TaskExpansion
    {
        public TaskRef Task;
        public List<string> QcPaths = new List<string>();
        /// <summary>需要新建的包装 QC 对应的模型路径（计划阶段只记录，执行时才写文件）</summary>
        public List<string> WrapperModelPaths = new List<string>();
        public string Error;
    }

    public enum BatchQcAction
    {
        /// <summary>会处理（生成 LOD 并写 $lod）</summary>
        Process = 0,
        /// <summary>合理跳过（有原因，不算失败）</summary>
        Skip = 1,
        /// <summary>错误（解析失败 / 工具块损坏 / 只读等）</summary>
        Error = 2
    }

    /// <summary>计划里一个唯一模型（全局去重后）。</summary>
    public sealed class PlannedModel
    {
        public string Path;
        public string BaseName;
        public string Dir;
        public long Size;
        public int Triangles;
        public bool IsSmd;
        /// <summary>SMD 源解析结果（IsSmd 时非空，供后续转 GLB / 还原骨骼）</summary>
        public SmdModel SmdSource;
        public string ReadError;
        public List<StudioRef> Entries = new List<StudioRef>();
        public List<string> RootQcs = new List<string>();
        /// <summary>每级一个 Blender 任务（顺序 = LOD1..LODN）</summary>
        public List<BlenderJobSpec> Jobs = new List<BlenderJobSpec>();
        /// <summary>该模型面数低于门槛而被跳过时，这里放原因</summary>
        public string SkipReason;
    }

    /// <summary>计划里一个根 QC 的动作。</summary>
    public sealed class BatchQcPlan
    {
        public string QcPath;
        public QcInfo Info;
        public BatchQcAction Action;
        /// <summary>Skip/Error 的可读原因</summary>
        public string Reason;
        public List<PlannedModel> Models = new List<PlannedModel>();
        /// <summary>该 QC 是否由计划新建包装 QC</summary>
        public bool IsWrapperQc;
    }

    /// <summary>一次运行的完整计划（只读阶段的产物）。</summary>
    public sealed class BatchPlan
    {
        public string RootDir;
        public DateTime BuiltAt;
        public string PresetName;
        public LodPlan Plan;
        public List<TaskExpansion> Tasks = new List<TaskExpansion>();
        public List<BatchQcPlan> QcPlans = new List<BatchQcPlan>();
        public List<PlannedModel> Models = new List<PlannedModel>();
        public int JobCount;
        public long TotalSourceTriangles;
        public bool HasWork;
        /// <summary>计划阶段登记、执行时才创建的包装 QC（QC 路径 → 模型路径）</summary>
        public List<KeyValuePair<string, string>> PendingWrappers = new List<KeyValuePair<string, string>>();

        public int ProcessCount { get { int n = 0; foreach (var q in QcPlans) if (q.Action == BatchQcAction.Process) n++; return n; } }
        public int SkipCount { get { int n = 0; foreach (var q in QcPlans) if (q.Action == BatchQcAction.Skip) n++; return n; } }
        public int ErrorCount { get { int n = 0; foreach (var q in QcPlans) if (q.Action == BatchQcAction.Error) n++; return n; } }
    }

    /// <summary>执行参数（界面设置 / CLI 解析后传入）。</summary>
    public sealed class BatchRunOptions
    {
        public LodPlan Plan;
        public string PresetName = "";

        public string BlenderPath;
        public int Workers;
        public bool QualityFirst = true;

        /// <summary>覆盖已有手写 $lod（注释停用旧的）</summary>
        public bool Force;
        public bool CompileAfter;
        public string NekomdlPath;
        public bool NoFacial = true;
        public bool SkipMainModel;
        public HashSet<string> SkipMainQcPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public int MinTriangles = 200;

        public OutputMode OutputMode = OutputMode.InPlace;
        public string CollectionName = "_LOD合集.qc";

        public bool BackupEnabled = true;
        /// <summary>null = 备份在原 QC 旁</summary>
        public string BackupCentralBaseDir;
        /// <summary>工程根（集中备份镜像相对路径用）</summary>
        public string ProjectRoot;

        /// <summary>报告/日志目录；null = 用 RootDir</summary>
        public string ReportDir;
        public bool WriteReport = true;
    }

    public sealed class BatchStats
    {
        public int RootQcCount;
        public int UniqueModelCount;
        public int JobCount;
        public int JobOk;
        public int JobFail;
        public int InjectedQcCount;
        public int SkippedQcCount;
        public int ErrorQcCount;
        public int CompileOk;
        public int CompileFail;
        public long TotalSourceTriangles;
        public long TotalLastLodTriangles;
    }

    public sealed class QcOutcome
    {
        public string QcPath;
        public bool Injected;
        public bool Skipped;
        public string SkipReason;
        public List<string> Errors = new List<string>();
        public bool CompileTried;
        public bool CompileOk;
        public string CompileSummary;
    }

    public sealed class BatchOutcome
    {
        public bool Ok;
        public bool Cancelled;
        public BatchStats Stats = new BatchStats();
        public List<QcOutcome> QcOutcomes = new List<QcOutcome>();
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public List<string> LogLines = new List<string>();
        /// <summary>本次成功写入的 QC（供界面按任务分摊结果）</summary>
        public List<string> InjectedQcPaths = new List<string>();
        public List<string> SkippedQcPaths = new List<string>();
        public string ReportPath;
        public string LogPath;
        public TimeSpan Elapsed;
        public DateTime FinishedAt;
    }
}
