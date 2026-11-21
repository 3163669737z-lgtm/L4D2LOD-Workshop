using System;
using System.Collections.Generic;

namespace LodStudio.Engine
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Blender 后端契约（数据 + 公开签名）。实现负责人：Blender 模块
    //  本文件只放跨模块共享的数据类型；实现分别在同目录的
    //  BlenderLocator.cs / BackendScript.cs / BlenderScheduler.cs，
    //  后端脚本源码在 src/Assets/blender_backend.py（嵌入资源）。
    //  公开签名（BlenderJobSpec / BlenderJobResult / BlenderRunOptions /
    //  BlenderScheduler / BlenderLocator / BackendScript）已冻结，不要改。
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>一个减面任务：把 Source 减到 Ratio，输出 Destination。</summary>
    public sealed class BlenderJobSpec
    {
        public string Source;
        public string Destination;
        /// <summary>目标面数比例 0..1</summary>
        public double Ratio;
        /// <summary>内层部件（名字含 body）沿法线内缩量：包围盒对角线的比例（0=不内缩）</summary>
        public double ShrinkInner;
        /// <summary>每个连通块的保底面数（0 合法 = 不保底）</summary>
        public int MinFaces = 12;
        /// <summary>保底面数最多占用总预算的百分比（0 合法）</summary>
        public double GuardPercent = 35.0;
        /// <summary>null = 跟随整批设置；true/false 强制该任务的质量模式</summary>
        public bool? QualityOverride;
        /// <summary>负载权重（源文件字节数），用于分桶摊平与超时放宽</summary>
        public long Weight;

        public override string ToString() { return (Source ?? "") + " → " + (Destination ?? ""); }
    }

    public sealed class BlenderJobResult
    {
        public bool Ok;
        public int BeforeTriangles;
        public int AfterTriangles;
        /// <summary>按比例 + 连通块保底算出的期望面数</summary>
        public int TargetTriangles;
        public int Blocks;
        public int AppliedBlocks;
        /// <summary>成功但有需要提示的情况（实际面数明显超目标等）</summary>
        public string Warning;
        public string Error;
    }

    public sealed class BlenderRunOptions
    {
        public string BlenderPath;
        /// <summary>0 = 自动（min(cpu/2, 空闲GB/1.2, 8, 任务数)）</summary>
        public int Workers;
        public bool QualityFirst = true;
        /// <summary>单任务看门狗基础时长（分钟），大文件自动放宽</summary>
        public int TaskTimeoutMinutes = 20;
        /// <summary>引擎级日志（会转发到 IBatchObserver）</summary>
        public Action<string> Log;
        /// <summary>每完成一个任务回调（索引, 结果）；可能来自任意线程</summary>
        public Action<int, BlenderJobResult> OnJobCompleted;
    }
}
