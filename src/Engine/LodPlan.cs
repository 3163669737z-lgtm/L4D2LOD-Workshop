using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>LOD 方案里的一级（LOD1 起；LOD0 是原始模型，不在这里）。</summary>
    public sealed class LodLevel
    {
        /// <summary>切换距离（HU，游戏单位；1 HU ≈ 1.9 厘米）。</summary>
        public double Distance;
        /// <summary>保留面数比例 0..1（不是百分比）。</summary>
        public double Ratio;

        public LodLevel() { }
        public LodLevel(double distance, double ratio) { Distance = distance; Ratio = ratio; }
        public LodLevel Clone() { return new LodLevel(Distance, Ratio); }
    }

    /// <summary>
    /// LOD 方案：一组 (距离, 比例)。约束：1~8 级、距离逐级变远、比例逐级变低。
    /// 所有引擎与界面共用这一份校验，避免两处规则不一致。
    /// </summary>
    public sealed class LodPlan
    {
        public const int MinLevels = 1;
        public const int MaxLevels = 7;
        public const double MinDistance = 0.1;
        public const double MaxDistance = 100000;
        public const double MinRatio = 0.01;
        public const double MaxRatio = 1.0;

        public List<LodLevel> Levels = new List<LodLevel>();

        public int Count { get { return Levels.Count; } }

        public static LodPlan CreateDefault()
        {
            return FromPreset(PresetLibrary.Builtins()[PresetLibrary.DefaultIndex]);
        }

        public static LodPlan FromPreset(LodPreset p)
        {
            var plan = new LodPlan();
            if (p == null) return plan;
            for (int i = 0; i < p.Distances.Length; i++)
                plan.Levels.Add(new LodLevel(p.Distances[i], p.Ratios[i]));
            return plan;
        }

        public LodPlan Clone()
        {
            var c = new LodPlan();
            foreach (var l in Levels) c.Levels.Add(l.Clone());
            return c;
        }

        public double[] Distances { get { return Levels.Select(x => x.Distance).ToArray(); } }
        public double[] Ratios { get { return Levels.Select(x => x.Ratio).ToArray(); } }

        /// <summary>签名：用于判断当前参数是否与某个预设一致/被改过。</summary>
        public string Signature()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Levels.Count; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(Levels[i].Distance.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append(Levels[i].Ratio.ToString("0.#####", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>校验；通过返回 null，否则返回可直接展示给用户的中文原因。</summary>
        public string Validate()
        {
            if (Levels == null) return "参数为空。";
            if (Levels.Count < MinLevels) return "至少要 " + MinLevels + " 级 LOD。";
            if (Levels.Count > MaxLevels) return "最多 " + MaxLevels + " 级 LOD。";
            for (int i = 0; i < Levels.Count; i++)
            {
                var l = Levels[i];
                if (double.IsNaN(l.Distance) || double.IsInfinity(l.Distance) || l.Distance < MinDistance)
                    return "第 " + (i + 1) + " 级的距离必须是大于 " + Fmt(MinDistance) + " 的正数。";
                if (double.IsNaN(l.Ratio) || double.IsInfinity(l.Ratio) || l.Ratio <= 0 || l.Ratio > MaxRatio)
                    return "第 " + (i + 1) + " 级的保留比例必须在 0~100% 之间。";
            }
            for (int i = 1; i < Levels.Count; i++)
            {
                if (Levels[i].Distance <= Levels[i - 1].Distance)
                    return "距离必须一级比一级远（第 " + (i + 1) + " 级 " + Fmt(Levels[i].Distance) +
                           " HU 不大于第 " + i + " 级 " + Fmt(Levels[i - 1].Distance) + " HU）。";
                if (Levels[i].Ratio >= Levels[i - 1].Ratio)
                    return "保留比例必须一级比一级低（第 " + (i + 1) + " 级 " + Pct(Levels[i].Ratio) +
                           "% 不小于第 " + i + " 级 " + Pct(Levels[i - 1].Ratio) + "%）。";
            }
            return null;
        }

        /// <summary>把数值夹到合法范围（界面拖动/输入容错用；不做跨级约束修正）。</summary>
        public void Clamp()
        {
            if (Levels == null) { Levels = new List<LodLevel>(); return; }
            if (Levels.Count > MaxLevels) Levels.RemoveRange(MaxLevels, Levels.Count - MaxLevels);
            for (int i = 0; i < Levels.Count; i++)
            {
                if (double.IsNaN(Levels[i].Distance) || Levels[i].Distance < MinDistance) Levels[i].Distance = MinDistance;
                if (Levels[i].Distance > MaxDistance) Levels[i].Distance = MaxDistance;
                if (double.IsNaN(Levels[i].Ratio) || Levels[i].Ratio < MinRatio) Levels[i].Ratio = MinRatio;
                if (Levels[i].Ratio > MaxRatio) Levels[i].Ratio = MaxRatio;
            }
        }

        /// <summary>在末尾加一级：距离 = 上一级 ×2（至少 +1），比例 = 上一级 ×0.5（至少 5%）。</summary>
        public bool TryAddLevel()
        {
            if (Levels.Count >= MaxLevels) return false;
            LodLevel last = Levels.Count > 0 ? Levels[Levels.Count - 1] : null;
            double d = last == null ? 30 : Math.Min(MaxDistance, Math.Max(last.Distance + 1, last.Distance * 2));
            double r = last == null ? 0.5 : Math.Max(0.05, last.Ratio * 0.5);
            if (Levels.Count > 0 && r >= last.Ratio) r = Math.Max(MinRatio, last.Ratio * 0.9);
            Levels.Add(new LodLevel(d, r));
            return true;
        }

        public string Summary()
        {
            if (Levels.Count == 0) return "（未设置）";
            var parts = new List<string>();
            foreach (var l in Levels)
                parts.Add(Fmt(l.Distance) + " HU→" + Pct(l.Ratio) + "%");
            return string.Join("  ", parts.ToArray());
        }

        public string SummaryShort()
        {
            if (Levels.Count == 0) return "未设置";
            return Levels.Count + " 级 · " + string.Join("/", Levels.Select(x => Fmt(x.Distance)).ToArray()) + " HU";
        }

        internal static string Fmt(double v)
        {
            if (Math.Abs(v - Math.Round(v)) < 1e-9) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        internal static string Pct(double ratio)
        {
            return (ratio * 100.0).ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>内置预设的名称、距离和保留比例（0..1）。</summary>
    public sealed class LodPreset
    {
        public string Name;
        public double[] Distances;
        public double[] Ratios;

        public bool SameAs(LodPlan plan)
        {
            if (plan == null || Distances == null) return false;
            if (plan.Count != Distances.Length) return false;
            for (int i = 0; i < Distances.Length; i++)
            {
                if (Math.Abs(plan.Levels[i].Distance - Distances[i]) > 1e-6) return false;
                if (Math.Abs(plan.Levels[i].Ratio - Ratios[i]) > 1e-6) return false;
            }
            return true;
        }
    }

    public static class PresetLibrary
    {
        public const string DefaultName = "平衡";
        public const string CustomName = "自定义";
        public const string AutomaticName = "自动";
        public const int DefaultIndex = 1;
        public const int CustomIndex = 3;
        public const int AutomaticIndex = 4;

        private static readonly LodPreset[] Presets =
        {
            new LodPreset
            {
                Name = "保守",
                Distances = new[] { 10.0, 15.0, 25.0, 50.0 },
                Ratios = new[] { 0.95, 0.80, 0.65, 0.50 }
            },
            new LodPreset
            {
                Name = DefaultName,
                Distances = new[] { 10.0, 15.0, 25.0, 50.0 },
                Ratios = new[] { 0.85, 0.65, 0.45, 0.30 }
            },
            new LodPreset
            {
                Name = "激进",
                Distances = new[] { 10.0, 15.0, 25.0, 50.0 },
                Ratios = new[] { 0.70, 0.50, 0.30, 0.15 }
            }
        };

        public static LodPreset[] Builtins() => Presets;

        internal static int SelectionFor(string name, LodPlan plan)
        {
            if (string.Equals(name, AutomaticName, StringComparison.Ordinal)) return AutomaticIndex;
            for (int i = 0; i < Presets.Length; i++)
                if (string.Equals(Presets[i].Name, name, StringComparison.Ordinal) && Presets[i].SameAs(plan))
                    return i;
            return CustomIndex;
        }
    }
}
