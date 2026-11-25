namespace LodStudio.Engine
{
    /// <summary>一个减面任务：把 Source 减到 Ratio，输出 Destination。</summary>
    public sealed class ReductionJob
    {
        public string Source;
        public string Destination;
        public string DisplayDestination;
        /// <summary>计划中的 1-based 可见档位。独立阴影任务使用 0。</summary>
        public int Level;
        /// <summary>目标面数比例 0..1</summary>
        public double Ratio;
        public bool ShadowMode;
        /// <summary>LOD 切换距离，未指定时按目标比例使用原有预算。</summary>
        public double Distance;
        internal bool RequireFixedVertices;
        /// <summary>负载权重（源文件字节数），用于按大文件放宽超时</summary>
        public long Weight;

        public override string ToString() { return (Source ?? "") + " → " + (Destination ?? ""); }
    }

    public sealed class ReductionResult
    {
        public bool Ok;
        public int BeforeTriangles;
        public int AfterTriangles;
        /// <summary>配置比例对应的目标面数</summary>
        public int TargetTriangles;
        public int BudgetSourceTriangles;
        public int BudgetTargetTriangles;
        public int BalancedRegions;
        public int MaximumRegionalSpread;
        public int MaximumAdjacentRegionalSpread;
        public int RetriedComponents;
        public int RetainedComponents;
        public int ProtectedPrimitives;
        public int VertexUpdateTrials;
        public int VertexUpdateAccepted;
        public int VertexUpdateTrianglesSaved;
        public int WholePrimitiveTrianglesSaved;
        /// <summary>最大表面偏差，以包围盒范围的百万分之一计；未测量时为 0。</summary>
        public int MaxSurfaceDeviationPpm;
        public int MeanSurfaceDeviationPpm;
        public long DeviationSampleCount;
        public bool HasDeviationMetrics => DeviationSampleCount > 0;
        /// <summary>成功但有需要提示的情况（实际面数明显超目标等）</summary>
        public string Warning;
        public string Error;
    }

}
