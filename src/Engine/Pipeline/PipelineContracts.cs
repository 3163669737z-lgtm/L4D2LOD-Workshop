using System;
using System.Collections.Generic;

namespace LodStudio.Engine
{
    // ══════════════════════════════════════════════════════════════════════════
    //  扫描 / 编译校验 / 报告 / 诊断的共享数据定义（实现见同目录其它文件）：
    //    ProjectScanner.cs · Nekomdl.cs · VtxInspector.cs · ReportWriter.cs
    //    EnvironmentDiagnostics.cs · SystemAnsi.cs · SelfTestPipeline.cs
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>.dx90.vtx 解析结果：级数 / 切换点 / 每级面数。</summary>
    public sealed class VtxInfo
    {
        public int NumLods;
        public List<double> SwitchPoints = new List<double>();
        public List<int> Triangles = new List<int>();
        public List<int> Vertices = new List<int>();
        public bool Valid;
        public string Error;
    }

    /// <summary>一次试编译的结果（含本次产物定位与 vtx 验收信息）。</summary>
    public sealed class CompileResult
    {
        public bool Success;
        public string MdlPath = "";
        public string VtxPath = "";
        public VtxInfo Vtx;
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public string StdOut = "";
    }

    /// <summary>报告输入（引擎把一次运行的完整事实交给报告层）。</summary>
    public sealed class ReportContext
    {
        public string ToolVersion = AppInfo.Version;
        public string RootDir;
        public string PresetName;
        public LodPlan Plan;
        public OutputMode Mode;
        public bool BackupEnabled;
        public string BackupDescription = "";
        public DateTime StartedAt;
        public TimeSpan Elapsed;
        public BatchOutcome Outcome;
        public List<string> LogLines = new List<string>();
        public string BatchDetailReportPath = "";
        public bool CompileAfter;
    }
}
