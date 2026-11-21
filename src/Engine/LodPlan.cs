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
        public const int MaxLevels = 8;
        public const double MinDistance = 0.1;
        public const double MaxDistance = 100000;
        public const double MinRatio = 0.01;
        public const double MaxRatio = 1.0;

        public List<LodLevel> Levels = new List<LodLevel>();

        public int Count { get { return Levels.Count; } }

        public static LodPlan CreateDefault()
        {
            return FromPreset(PresetLibrary.Builtins()[0]);
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

    /// <summary>内置/用户预设：名称 + 描述 + 距离数组 + 比例数组（0..1）。</summary>
    public sealed class LodPreset
    {
        public string Name;
        public string Description;
        public double[] Distances;
        public double[] Ratios;
        public bool IsUser;

        /// <summary>"除 LOD0 外的级数"——与旧版命名保持一致（"5级" = LOD0..LOD4）。</summary>
        public int LevelCount { get { return Distances == null ? 0 : Distances.Length; } }

        public LodPreset Clone()
        {
            return new LodPreset
            {
                Name = Name,
                Description = Description,
                Distances = Distances == null ? new double[0] : (double[])Distances.Clone(),
                Ratios = Ratios == null ? new double[0] : (double[])Ratios.Clone(),
                IsUser = IsUser
            };
        }

        public string Summary()
        {
            if (Distances == null || Ratios == null || Distances.Length == 0) return "（未设置）";
            var p = new StringBuilder();
            for (int i = 0; i < Distances.Length; i++)
            {
                if (i > 0) p.Append("  ");
                p.Append(LodPlan.Fmt(Distances[i])).Append(" HU→").Append(LodPlan.Pct(Ratios[i])).Append('%');
            }
            return p.ToString();
        }

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

    /// <summary>
    /// 内置预设。基准来自官方 zoey 实测（survivor_teenangst.dx90.vtx，LOD0..4）：
    /// 距离 10/15/25/50、面数 99.7%/65.3%/34.0%/16.1%。数值与旧版完全一致（用户认识它们）。
    /// </summary>
    public static class PresetLibrary
    {
        private static LodPreset[] _builtins;

        public static LodPreset[] Builtins()
        {
            if (_builtins != null) return _builtins;
            _builtins = new[]
            {
                new LodPreset
                {
                    Name = "角色·标准（5级）",
                    Description = "官方 zoey 实测数据：距离 10/15/25/50，面数 99.7%/65.3%/34.0%/16.1%。与原版角色表现一致。",
                    Distances = new[] { 10.0, 15.0, 25.0, 50.0 },
                    Ratios = new[] { 0.997, 0.653, 0.340, 0.161 }
                },
                new LodPreset
                {
                    Name = "角色·温和（5级）",
                    Description = "沿用官方距离 10/15/25/50，但降面更保守：85%/65%/45%/30%。适合减面容易破面的模型。",
                    Distances = new[] { 10.0, 15.0, 25.0, 50.0 },
                    Ratios = new[] { 0.85, 0.65, 0.45, 0.30 }
                },
                new LodPreset
                {
                    Name = "角色·精简（3级）",
                    Description = "3 级：距离 15/30/60，降面 70%/45%/25%。文件更小、切换次数更少。",
                    Distances = new[] { 15.0, 30.0, 60.0 },
                    Ratios = new[] { 0.70, 0.45, 0.25 }
                },
                new LodPreset
                {
                    Name = "道具·标准（2级）",
                    Description = "静态道具：距离 30/70，降面 55%/30%。适合箱子、家具、载具。",
                    Distances = new[] { 30.0, 70.0 },
                    Ratios = new[] { 0.55, 0.30 }
                },
                new LodPreset
                {
                    Name = "极保守（2级）",
                    Description = "只降到 85%/65%，距离 40/80。几乎不会破面，适合先用它验证流程。",
                    Distances = new[] { 40.0, 80.0 },
                    Ratios = new[] { 0.85, 0.65 }
                },
                new LodPreset
                {
                    Name = "角色·性能（6级）",
                    Description = "6 级：10/15/25/50/100/200，降面 99.7%/85%/65%/45%/25%/12%。远距离降得更狠。",
                    Distances = new[] { 10.0, 15.0, 25.0, 50.0, 100.0, 200.0 },
                    Ratios = new[] { 0.997, 0.85, 0.65, 0.45, 0.25, 0.12 }
                },
                new LodPreset
                {
                    Name = "角色·极致（8级）",
                    Description = "8 级满上限：10/15/25/40/60/90/130/180，降面 99.7%/92%/78%/62%/46%/32%/18%/10%。过渡最平滑。",
                    Distances = new[] { 10.0, 15.0, 25.0, 40.0, 60.0, 90.0, 130.0, 180.0 },
                    Ratios = new[] { 0.997, 0.92, 0.78, 0.62, 0.46, 0.32, 0.18, 0.10 }
                },
                new LodPreset
                {
                    Name = "载具·大件（3级）",
                    Description = "3 级：距离 30/70/150，降面 70%/45%/20%。适合载具、大型静态物。",
                    Distances = new[] { 30.0, 70.0, 150.0 },
                    Ratios = new[] { 0.70, 0.45, 0.20 }
                }
            };
            return _builtins;
        }

        public static LodPreset FindBuiltin(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var p in Builtins())
                if (string.Equals(p.Name, name, StringComparison.Ordinal)) return p;
            return null;
        }

        /// <summary>用户预设编码：名称|距离列表|百分比列表（与旧版格式兼容，便于导入）。</summary>
        public static string EncodeUserPreset(string name, LodPlan plan)
        {
            var safe = (name ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim();
            return safe + "|" + string.Join(",", plan.Distances.Select(d => d.ToString("0.###", CultureInfo.InvariantCulture)).ToArray())
                        + "|" + string.Join(",", plan.Ratios.Select(r => (r * 100.0).ToString("0.####", CultureInfo.InvariantCulture)).ToArray());
        }

        /// <summary>解析用户预设；失败返回 null（坏数据绝不让程序起不来）。</summary>
        public static LodPreset DecodeUserPreset(string line)
        {
            try
            {
                if (string.IsNullOrEmpty(line)) return null;
                var parts = line.Split('|');
                if (parts.Length != 3) return null;
                var d = ParseDoubles(parts[1]);
                var r = ParseDoubles(parts[2]);
                if (d.Count == 0 || d.Count != r.Count || d.Count > LodPlan.MaxLevels) return null;
                var plan = new LodPlan();
                for (int i = 0; i < d.Count; i++) plan.Levels.Add(new LodLevel(d[i], r[i] / 100.0));
                if (plan.Validate() != null) return null;
                return new LodPreset
                {
                    Name = parts[0].Trim(),
                    Description = "我的预设",
                    Distances = plan.Distances,
                    Ratios = plan.Ratios,
                    IsUser = true
                };
            }
            catch { return null; }
        }

        private static List<double> ParseDoubles(string s)
        {
            var list = new List<double>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (var part in s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double v;
                if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) list.Add(v);
            }
            return list;
        }
    }
}
