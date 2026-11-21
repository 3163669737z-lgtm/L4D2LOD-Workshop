using System;
using LodStudio.Engine;

namespace LodStudio.ViewModels
{
    /// <summary>LOD 方案编辑（UI 层）回归：输入即时生效、拖动同步、格式整理。</summary>
    internal static class SelfTestPlanEditing
    {
        private static LodPlanViewModel NewVm()
        {
            var vm = new LodPlanViewModel();
            vm.LoadFrom(LodPlan.CreateDefault(), "角色·标准（5级）");
            return vm;
        }

        [SelfTest("LOD 方案：距离/比例边输入边生效（不需要回车）")]
        public static void ImmediateEdit(SelfTestContext ctx)
        {
            var vm = NewVm();
            ctx.AssertEqual(4, vm.Count, "默认级数");

            // 距离：输入 "30" 立刻写进方案；后面级别会被自动顶高（保持严格递增）
            vm.Levels[0].DistanceText = "30";
            ctx.AssertEqual(30.0, vm.Plan.Levels[0].Distance, 1e-9, "距离应立即生效");
            ctx.AssertEqual("30", vm.Levels[0].DistanceText, "输入文本不应被改写（避免抢焦点）");
            ctx.AssertEqual(31.0, vm.Plan.Levels[1].Distance, 1e-9, "第 2 级应自动顶到 31");
            ctx.AssertEqual(32.0, vm.Plan.Levels[2].Distance, 1e-9, "第 3 级应自动顶到 32");
            ctx.Assert(vm.IsValid, "自动顶高后方案应保持合法");
            vm.Levels[0].DistanceText = "10";
            ctx.Assert(vm.IsValid, "改回 10 后应重新合法");

            // 比例：输入 "50" 立刻生效（百分比界面 → 0.50），后面级别自动压低
            vm.Levels[0].RatioText = "50";
            ctx.AssertEqual(0.5, vm.Plan.Levels[0].Ratio, 1e-9, "比例应立即生效");
            ctx.AssertEqual("50", vm.Levels[0].RatioText, "输入文本不应被改写");
            ctx.AssertEqual(0.49, vm.Plan.Levels[1].Ratio, 1e-9, "第 2 级应自动压到 49%");
            ctx.Assert(vm.IsValid, "自动压低后方案应保持合法");
            vm.Levels[0].RatioText = "99.7";
            ctx.Assert(vm.IsValid, "改回 99.7 后应重新合法");

            // 百分比派生值跟着走
            ctx.AssertEqual(99.7, vm.Levels[0].RatioPercent, 0.05, "RatioPercent");
        }

        [SelfTest("LOD 方案：拖动比例条后百分比文本同步；失焦整理格式")]
        public static void DragSyncAndNormalize(SelfTestContext ctx)
        {
            var vm = NewVm();

            // 模拟拖动比例条：RatioBar 写回 VM.Ratio → SetRatio → 百分比文本同步刷新
            vm.Levels[0].Ratio = 0.6;
            ctx.AssertEqual(0.6, vm.Plan.Levels[0].Ratio, 1e-9, "拖动后的比例");
            ctx.AssertEqual("60", vm.Levels[0].RatioText, "百分比文本应同步为 60");

            // 距离输入 "010"：方案值 10，文本保留 "010"；失焦整理后变 "10"
            vm.Levels[0].DistanceText = "010";
            ctx.AssertEqual(10.0, vm.Plan.Levels[0].Distance, 1e-9, "010 应解析为 10");
            ctx.AssertEqual("010", vm.Levels[0].DistanceText, "输入过程中不改文本");
            vm.NormalizeLevelText(0);
            ctx.AssertEqual("10", vm.Levels[0].DistanceText, "失焦后应整理成规范格式");

            // 非数字输入不应崩溃、也不应改方案值
            double before = vm.Plan.Levels[1].Distance;
            vm.Levels[1].DistanceText = "abc";
            ctx.AssertEqual(before, vm.Plan.Levels[1].Distance, 1e-9, "非法输入不应改方案");
            vm.NormalizeLevelText(1);
            ctx.AssertEqual("15", vm.Levels[1].DistanceText, "整理回原值");

            // 范围夹取：比例输入 0.05% 会被夹到 0.1%
            vm.Levels[3].RatioText = "0.05";
            ctx.AssertEqual(0.001, vm.Plan.Levels[3].Ratio, 1e-9, "比例下限 0.1%");
        }

        [SelfTest("LOD 方案：距离自动保持递增（比上一级小 → 强制上一级 +1）")]
        public static void DistanceAutoCorrect(SelfTestContext ctx)
        {
            var vm = NewVm();                                  // 10 / 15 / 25 / 50
            vm.Levels[1].DistanceText = "5";                   // 上一级 10 → 必须 11
            ctx.AssertEqual(11.0, vm.Plan.Levels[1].Distance, 1e-9, "第 2 级距离应强制为 11");
            ctx.AssertEqual("5", vm.Levels[1].DistanceText, "输入框文本在失焦前保留原样");
            vm.NormalizeLevelText(1);
            ctx.AssertEqual("11", vm.Levels[1].DistanceText, "失焦后显示强制值");

            // 提高第 1 级：后面所有级别都要被顶着递增
            vm.Levels[0].DistanceText = "30";
            vm.NormalizeLevelText(0);
            ctx.AssertEqual(30.0, vm.Plan.Levels[0].Distance, 1e-9, "第 1 级=30");
            ctx.AssertEqual(31.0, vm.Plan.Levels[1].Distance, 1e-9, "第 2 级=31");
            ctx.AssertEqual(32.0, vm.Plan.Levels[2].Distance, 1e-9, "第 3 级=32");
            ctx.AssertEqual(50.0, vm.Plan.Levels[3].Distance, 1e-9, "第 4 级保持 50");
            ctx.Assert(vm.IsValid, "级联后应合法");
        }

        [SelfTest("LOD 方案：比例自动保持递减（比上一级大 → 强制上一级 -1）")]
        public static void RatioAutoCorrect(SelfTestContext ctx)
        {
            var vm = NewVm();                                  // 99.7 / 65.3 / 34 / 16.1
            vm.Levels[1].RatioText = "99";                     // 上一级 99.7 → 上限 98.7
            ctx.AssertEqual(0.987, vm.Plan.Levels[1].Ratio, 1e-9, "第 2 级比例应强制为 98.7%");
            vm.NormalizeLevelText(1);
            ctx.AssertEqual("98.7", vm.Levels[1].RatioText, "失焦后显示强制值");

            // 降低第 1 级：后面所有级别都要被压着递减
            vm.Levels[0].RatioText = "10";
            vm.NormalizeLevelText(0);
            ctx.AssertEqual(0.1, vm.Plan.Levels[0].Ratio, 1e-9, "第 1 级=10%");
            ctx.AssertEqual(0.09, vm.Plan.Levels[1].Ratio, 1e-9, "第 2 级=9%");
            ctx.AssertEqual(0.08, vm.Plan.Levels[2].Ratio, 1e-9, "第 3 级=8%");
            ctx.AssertEqual(0.07, vm.Plan.Levels[3].Ratio, 1e-9, "第 4 级=7%");
            ctx.Assert(vm.IsValid, "级联后应合法");
        }

        [SelfTest("LOD 方案：比例条上限不能超过上一级（MaxRatio）")]
        public static void RatioBarCap(SelfTestContext ctx)
        {
            var vm = NewVm();
            ctx.AssertEqual(0.987, vm.Levels[1].MaxRatio, 1e-9, "第 2 级比例条上限=98.7%");
            vm.Levels[1].Ratio = 0.995;                        // 模拟比例条拖到超过上限
            ctx.Assert(vm.Plan.Levels[1].Ratio <= 0.987 + 1e-9, "比例不应超过上限");
            ctx.AssertEqual(0.987, vm.Plan.Levels[1].Ratio, 1e-9, "被夹到 98.7%");
        }
    }
}
