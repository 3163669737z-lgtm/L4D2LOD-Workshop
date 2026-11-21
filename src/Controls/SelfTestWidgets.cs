using System;
using LodStudio.Engine;

namespace LodStudio.Controls
{
    /// <summary>
    /// 自绘控件的纯逻辑自测（不实例化 WPF 元素，可 headless 跑）：
    /// 刻度步长 / 拖动取整 / 跨级严格约束 / 格式化 / 横轴域。
    /// 视觉与交互由 --widget-preview 截图人工核对，这里只覆盖能断言的数学契约。
    /// </summary>
    internal static class SelfTestWidgets
    {
        [SelfTest("Widget：横轴域 = max(距离)×1.25")]
        public static void Domain(SelfTestContext ctx)
        {
            ctx.AssertEqual(62.5, RangeMath.DomainFor(50, 0), 1e-9, "50 HU 最大距离 → 域 62.5");
            ctx.AssertEqual(125000.0, RangeMath.DomainFor(100000, 0), 1e-9, "100000 HU → 域 125000");
            ctx.AssertEqual(1.25, RangeMath.DomainFor(0, 0), 1e-9, "空方案兜底域 1.25");
            ctx.AssertEqual(62.5, RangeMath.DomainFor(40, 50), 1e-9, "拖动缩小距离时域不缩（取历史最大）");
            ctx.Assert(RangeMath.DomainFor(double.NaN, 0) > 0, "NaN 距离不能让域非法");
        }

        [SelfTest("Widget：刻度步长取 5/10/20/25/50/100/200 这类好看值")]
        public static void NiceStepValues(SelfTestContext ctx)
        {
            ctx.AssertEqual(10.0, RangeMath.NiceStep(62.5, 7), 1e-9, "62.5/7 → 10");
            ctx.AssertEqual(20.0, RangeMath.NiceStep(125, 7), 1e-9, "125/7 → 20");
            ctx.AssertEqual(20000.0, RangeMath.NiceStep(125000, 7), 1e-9, "125000/7 → 20000");
            ctx.AssertEqual(2.0, RangeMath.NiceStep(10, 7), 1e-9, "10/7 → 2");
            ctx.AssertEqual(1.0, RangeMath.NiceStep(0.5, 7), 1e-9, "极小域下限 1 HU（0.1 HU 无意义）");
            ctx.AssertEqual(1.0, RangeMath.NiceStep(double.NaN, 7), 1e-9, "NaN 域 → 1");
            ctx.AssertEqual(1.0, RangeMath.NiceStep(-5, 7), 1e-9, "负域 → 1");

            // 每个步长都必须是 1/2/2.5/5 × 10^n 形状，且每格至少 1 HU
            double[] domains = { 62.5, 125, 1250, 12500, 125000, 10, 25 };
            for (int i = 0; i < domains.Length; i++)
            {
                double step = RangeMath.NiceStep(domains[i], 7);
                ctx.Assert(step >= 1.0, "步长不得小于 1 HU：domain=" + domains[i] + " step=" + step);
                double mant = step / Math.Pow(10.0, Math.Floor(Math.Log10(step)));
                bool ok = Math.Abs(mant - 1.0) < 1e-9 || Math.Abs(mant - 2.0) < 1e-9 ||
                          Math.Abs(mant - 2.5) < 1e-9 || Math.Abs(mant - 5.0) < 1e-9 || Math.Abs(mant - 10.0) < 1e-9;
                ctx.Assert(ok, "步长尾数必须是 1/2/2.5/5：domain=" + domains[i] + " step=" + step);
            }
        }

        [SelfTest("Widget：刻度数量在可读范围（不挤成一坨）")]
        public static void TickCounts(SelfTestContext ctx)
        {
            double[] domains = { 10, 62.5, 125, 1250, 125000 };
            for (int i = 0; i < domains.Length; i++)
            {
                double step = RangeMath.NiceStep(domains[i], 7);
                int ticks = 0;
                while (ticks * step <= domains[i] + 1e-9 && ticks < 500) ticks++;
                ctx.Assert(ticks >= 2, "domain=" + domains[i] + " 刻度太少：" + ticks);
                ctx.Assert(ticks <= 20, "domain=" + domains[i] + " 刻度太密：" + ticks);
            }
        }

        [SelfTest("Widget：拖动取整（距离 1 HU / 比例 0.1%）并夹到合法范围")]
        public static void Snap(SelfTestContext ctx)
        {
            ctx.AssertEqual(15.0, RangeMath.SnapDistance(14.6), 1e-9, "距离取整到 15");
            ctx.AssertEqual(1.0, RangeMath.SnapDistance(0.2), 1e-9, "距离下限 1 HU");
            ctx.AssertEqual(100000.0, RangeMath.SnapDistance(1e9), 1e-9, "距离上限 100000 HU");
            ctx.AssertEqual(1.0, RangeMath.SnapDistance(double.NaN), 1e-9, "NaN 距离 → 1");
            ctx.AssertEqual(0.653, RangeMath.SnapRatio(0.6534), 1e-12, "比例取整到 0.1%");
            ctx.AssertEqual(0.01, RangeMath.SnapRatio(0.0004), 1e-12, "比例下限 1%");
            ctx.AssertEqual(1.0, RangeMath.SnapRatio(1.2), 1e-12, "比例上限 100%");
        }

        [SelfTest("Widget：比例把手严格小于上一级、严格大于下一级")]
        public static void ConstrainRatio(SelfTestContext ctx)
        {
            LodPlan plan = LodPlan.FromPreset(PresetLibrary.Builtins()[0]);   // 0.997/0.653/0.34/0.161
            double v;

            ctx.Assert(RangeMath.TryConstrainRatio(plan, 0, 1.5, out v), "第 1 级可约束");
            ctx.AssertEqual(1.0, v, 1e-12, "向上拖过头 → 100%");
            ctx.Assert(RangeMath.TryConstrainRatio(plan, 0, 0.2, out v), "第 1 级可约束");
            ctx.AssertEqual(0.654, v, 1e-12, "第 1 级不得压到第 2 级（0.653+0.001）");

            ctx.Assert(RangeMath.TryConstrainRatio(plan, 3, 0.9, out v), "最后一级可约束");
            ctx.AssertEqual(0.339, v, 1e-12, "最后一级不得超过上一级（0.34-0.001）");
            ctx.Assert(RangeMath.TryConstrainRatio(plan, 3, 0.0001, out v), "最后一级可约束");
            ctx.AssertEqual(0.01, v, 1e-12, "最后一级下限 1%");

            ctx.Assert(RangeMath.TryConstrainRatio(plan, 1, 0.50049, out v), "第 2 级可约束");
            ctx.AssertEqual(0.5, v, 1e-12, "0.1% 步进取整");

            // 邻级挤得太近（0.1% 步长下中间级没有合法值）→ 返回 false，调用方保持原值
            var dense = new LodPlan();
            dense.Levels.Add(new LodLevel(10, 0.6524));
            dense.Levels.Add(new LodLevel(20, 0.6523));
            dense.Levels.Add(new LodLevel(30, 0.6522));
            ctx.Assert(dense.Validate() == null, "测试方案本身合法");
            ctx.Assert(!RangeMath.TryConstrainRatio(dense, 1, 0.5, out v), "无合法区间时返回 false");
        }

        [SelfTest("Widget：距离把手严格大于上一级、严格小于下一级（1 HU 步进）")]
        public static void ConstrainDistance(SelfTestContext ctx)
        {
            LodPlan plan = LodPlan.FromPreset(PresetLibrary.Builtins()[0]);   // 10/15/25/50
            double v;

            ctx.Assert(RangeMath.TryConstrainDistance(plan, 0, 100, out v), "第 1 级可约束");
            ctx.AssertEqual(14.0, v, 1e-12, "第 1 级不得压到第 2 级（15-1）");
            ctx.Assert(RangeMath.TryConstrainDistance(plan, 3, 1, out v), "最后一级可约束");
            ctx.AssertEqual(26.0, v, 1e-12, "最后一级不得小于上一级（25+1）");
            ctx.Assert(RangeMath.TryConstrainDistance(plan, 1, 12.6, out v), "第 2 级可约束");
            ctx.AssertEqual(13.0, v, 1e-12, "距离 1 HU 步进取整");

            var max = new LodPlan();
            max.Levels.Add(new LodLevel(100000, 0.5));
            ctx.Assert(RangeMath.TryConstrainDistance(max, 0, 999999, out v), "单级可约束");
            ctx.AssertEqual(100000.0, v, 1e-9, "距离上限 100000 HU");

            // 两级只差 0.1 HU（合法校验通过，但第 1 级没有 1 HU 空位）→ 拒绝移动，保持原值
            var tight = new LodPlan();
            tight.Levels.Add(new LodLevel(0.2, 0.5));
            tight.Levels.Add(new LodLevel(0.3, 0.4));
            ctx.Assert(tight.Validate() == null, "测试方案本身合法");
            ctx.Assert(!RangeMath.TryConstrainDistance(tight, 0, 5, out v), "无 1 HU 空位时返回 false");
        }

        [SelfTest("Widget：阶梯读数格式（百分比 1 位小数 / 距离整数优先）")]
        public static void Formatting(SelfTestContext ctx)
        {
            ctx.AssertEqual("65.3%", RangeMath.FormatPct(0.653), "百分比一位小数");
            ctx.AssertEqual("34.0%", RangeMath.FormatPct(0.34), "整数百分比也保留一位小数（量具读数一致）");
            ctx.AssertEqual("100.0%", RangeMath.FormatPct(1.0), "100%");
            ctx.AssertEqual("0.0%", RangeMath.FormatPct(-1), "越界比例夹到 0");
            ctx.AssertEqual("15", RangeMath.FormatDistance(15), "整数距离不带小数点");
            ctx.AssertEqual("10.5", RangeMath.FormatDistance(10.5), "非整数距离保留小数");
            ctx.AssertEqual("20", RangeMath.FormatTick(20, 20), "刻度步长≥1 取整");
            ctx.AssertEqual("0.2", RangeMath.FormatTick(0.2, 0.2), "刻度步长<1 保留小数");
        }

        [SelfTest("Widget：拖动提交后方案仍然是严格合法的 LOD 方案")]
        public static void DragResultStaysValid(SelfTestContext ctx)
        {
            LodPlan plan = LodPlan.FromPreset(PresetLibrary.Builtins()[6]);   // 8 级
            var work = plan.Clone();
            double v;
            // 把第 5 级的比例往上顶到极限、距离往下顶到极限，结果都必须通过引擎校验
            if (RangeMath.TryConstrainRatio(work, 4, 10, out v)) work.Levels[4].Ratio = v;
            if (RangeMath.TryConstrainDistance(work, 4, -100, out v)) work.Levels[4].Distance = v;
            ctx.AssertEqual(null, work.Validate(), "极限拖动后方案仍然合法");

            // 每一级都往上/往下顶一遍
            for (int i = 0; i < work.Count; i++)
            {
                if (RangeMath.TryConstrainRatio(work, i, 5, out v)) work.Levels[i].Ratio = v;
                if (RangeMath.TryConstrainDistance(work, i, 1e9, out v)) work.Levels[i].Distance = v;
                ctx.AssertEqual(null, work.Validate(), "第 " + (i + 1) + " 级顶到极限后方案仍然合法");
            }
        }
    }
}
