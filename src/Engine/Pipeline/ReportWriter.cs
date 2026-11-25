using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>
    /// 运行报告（Markdown 总报告）与运行日志（纯文本）落盘。
    ///
    /// 报告文件名固定格式 <c>_LOD运行报告_MMdd_HHmmss.md</c>，日志 <c>_LOD运行日志_yyyyMMdd_HHmmss.txt</c>。
    /// 报告结构（docs/00 §4.5 + 旧版总报告）：标题 / 时间与用时 / 方案 / 写入方式 / 统计 / 逐项结果 /
    /// 跳过原因 / 错误（按 QC 分组）/ 提示 / 还原方法 / 下一步建议 / 工具版本。
    /// 目录不存在就创建；任何写入失败都返回 null（绝不因为报告让运行失败）。日志用 UTF-8 BOM。
    /// </summary>
    public static class ReportWriter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ───────────────────────────── 文件名 ─────────────────────────────

        /// <summary>生成报告文件名（含时间戳）：_LOD运行报告_MMdd_HHmmss.md</summary>
        public static string ReportFileName(DateTime at)
        {
            return "_LOD运行报告_" + at.ToString("MMdd_HHmmss", Inv) + ".md";
        }

        /// <summary>生成日志文件名：_LOD&lt;tag&gt;_yyyyMMdd_HHmmss.txt（tag 默认 "运行日志"，即 _LOD运行日志_*.txt）</summary>
        public static string LogFileName(DateTime at, string logNameTag)
        {
            string tag = string.IsNullOrEmpty(logNameTag) ? "运行日志" : logNameTag.Trim();
            if (tag.Length == 0) tag = "运行日志";
            return "_LOD" + tag + "_" + at.ToString("yyyyMMdd_HHmmss", Inv) + ".txt";
        }

        // ───────────────────────────── 总报告（Markdown）─────────────────────────────

        /// <summary>写 Markdown 总报告到 reportDir，返回路径；失败返回 null。</summary>
        public static string WriteMarkdownReport(ReportContext ctx, string reportDir)
        {
            string text;
            try { text = BuildMarkdown(ctx); }
            catch (Exception ex)
            {
                // 报告内容构建本身出问题也要落一个可读的短报告，方便用户反馈
                try
                {
                    text = "# 远景工坊 · L4D2 自动 LOD 运行报告\n\n" +
                           "生成报告时发生错误：" + ex.GetType().Name + ": " + ex.Message + "\n\n" +
                           "请把运行日志一起发给开发者。\n\n---\n远景工坊 · L4D2 自动 LOD 工具 v" + AppInfo.Version + "\n";
                }
                catch { return null; }
            }

            DateTime now = DateTime.Now;
            string fileName;
            try { fileName = ReportFileName(now); }
            catch { fileName = "_LOD运行报告.md"; }

            string root = null;
            try { if (ctx != null) root = ctx.RootDir; } catch { }
            return WriteWithFallback(reportDir, root, fileName, text);
        }

        /// <summary>写运行日志到 reportDir，返回路径；失败返回 null。</summary>
        public static string WriteLogFile(ReportContext ctx, string reportDir, string logNameTag)
        {
            string text;
            try { text = BuildLogText(ctx); }
            catch { return null; }

            DateTime now = DateTime.Now;
            string fileName;
            try { fileName = LogFileName(now, logNameTag); }
            catch { fileName = "_LOD运行日志.txt"; }

            string root = null;
            try { if (ctx != null) root = ctx.RootDir; } catch { }
            return WriteWithFallback(reportDir, root, fileName, text);
        }

        // ───────────────────────────── 报告正文 ─────────────────────────────

        private static string BuildMarkdown(ReportContext ctx)
        {
            var sb = new StringBuilder(8192);
            BatchOutcome outcome = SafeOutcome(ctx);
            BatchStats stats = SafeStats(outcome);
            LodPlan plan = SafePlan(ctx);
            OutputMode mode = ctx == null ? OutputMode.InPlace : ctx.Mode;
            DateTime now = DateTime.Now;
            TimeSpan elapsed = ctx == null ? TimeSpan.Zero : ctx.Elapsed;
            if (elapsed <= TimeSpan.Zero && outcome != null) elapsed = outcome.Elapsed;
            DateTime started = ctx == null ? now : ctx.StartedAt;
            if (started.Year < 2000) started = now - elapsed;

            int errorTotal = CountErrors(outcome);
            int warnTotal = outcome == null ? 0 : outcome.Warnings.Count;
            int errorQcTotal = Math.Max(stats.ErrorQcCount, CountErrorQcs(outcome));

            sb.AppendLine("# 远景工坊 · L4D2 自动 LOD 运行报告");
            sb.AppendLine();
            sb.AppendLine("- 工具版本：v" + (ctx == null || string.IsNullOrEmpty(ctx.ToolVersion) ? AppInfo.Version : ctx.ToolVersion));
            sb.AppendLine("- 开始时间：" + started.ToString("yyyy-MM-dd HH:mm:ss", Inv));
            sb.AppendLine("- 用时：" + FormatSpan(elapsed));
            if (ctx != null && !string.IsNullOrEmpty(ctx.RootDir))
                sb.AppendLine("- 工程目录：" + ctx.RootDir);
            string presetName = ctx == null || string.IsNullOrEmpty(ctx.PresetName) ? "自定义" : ctx.PresetName;
            sb.AppendLine("- LOD 方案：" + (ctx?.Automatic == true ? "按各工程可见组合面数规划" : presetName + "（" + (plan == null ? "未设置" : plan.Summary()) + "）"));
            sb.AppendLine("- 写入方式：" + ModeText(mode));
            if (outcome != null && outcome.Cancelled) sb.AppendLine("- 状态：**用户中途停止**");
            sb.AppendLine();

            // ── 统计 ──
            sb.AppendLine("## 统计");
            sb.AppendLine();
            sb.AppendLine("| 项目 | 数值 |");
            sb.AppendLine("| --- | --- |");
            sb.AppendLine("| 根 QC | " + N(stats.RootQcCount) + " 个 |");
            sb.AppendLine("| 唯一模型 | " + N(stats.UniqueModelCount) + " 个（全局去重后） |");
            sb.AppendLine("| 简化任务 | " + N(stats.JobCount) + " 个（检查完成 " + N(stats.JobOk) + " / 失败 " + N(stats.JobFail) + "） |");
            sb.AppendLine("| 实际新网格 | 发布 " + N(stats.PublishedLods) + " 个，直接沿用已减网格 " +
                          N(stats.ReusedLevels) + " 档，沿用源网格 " + N(stats.UnreducedLevels) + " 档 |");
            if (stats.ShadowLodsPublished > 0)
            {
                string fallback = stats.ShadowLodReusedQcs > 0
                    ? "，其中 " + N(stats.ShadowLodReusedQcs) + " 个 QC 含回退到可见 LOD 的条目" : "";
                sb.AppendLine("| 阴影 LOD | 发布 " + N(stats.ShadowLodsPublished) + " 个独立网格" + fallback + " |");
                sb.AppendLine("| 含 $shadowlod 的 QC | " + N(stats.ShadowLodQcsWritten) + " 个 |");
            }
            else if (stats.ShadowLodReusedQcs > 0)
                sb.AppendLine("| 阴影 LOD | 复用已有 LOD 写入 " + N(stats.ShadowLodReusedQcs) + " 个 QC（不额外生成阴影文件） |");
            if (stats.DeviationSampleCount > 0)
                sb.AppendLine("| 表面几何偏差 | 平均 " + DeviationPercent(stats.MeanSurfaceDeviationPpm) +
                              " / 峰值 " + DeviationPercent(stats.MaxSurfaceDeviationPpm) +
                              "（采样 " + N(stats.DeviationSampleCount) + " 顶点） |");
            if (stats.ModelsWithoutReduction > 0)
                sb.AppendLine("| 未找到合格减面 | " + N(stats.ModelsWithoutReduction) + " 个模型，详见运行日志 |");
            sb.AppendLine("| 外观保护 | 重试 " + N(stats.RetriedComponents) + " 个局部区域，保留 " +
                          N(stats.RetainedComponents) + " 个局部区域，保护 " + N(stats.ProtectedPrimitives) +
                          " 个图元，沿用上一档 " + N(stats.ReusedLevels) + " 次 |");
            if (stats.BalancedRegions > 0 || stats.MaximumRegionalSpread > 0)
                sb.AppendLine("| 区域均摊 | 调整 " + N(stats.BalancedRegions) + " 个区域，最大区域减面率差 " +
                              (stats.MaximumRegionalSpread / 100.0).ToString("0.##", CultureInfo.InvariantCulture) +
                              " 个百分点，相邻区域最大差 " +
                              (stats.MaximumAdjacentRegionalSpread / 100.0).ToString("0.##", CultureInfo.InvariantCulture) +
                              " 个百分点 |");
            if (stats.LodTriangles.Length > 0 && ctx?.Automatic != true)
                sb.AppendLine("| 各档实际面数 | " + string.Join(" · ", stats.LodTriangles
                    .Select((count, i) => "LOD" + (i + 1) + " " + N(count))) + " |");
            if (stats.VertexUpdateTrials > 0)
                sb.AppendLine("| 顶点微调 | 尝试 " + N(stats.VertexUpdateTrials) + " 个区域，采用 " +
                              N(stats.VertexUpdateAccepted) + " 个，较固定顶点额外减少 " +
                              N(stats.VertexUpdateTrianglesSaved) + " 面（各级合计） |");
            sb.AppendLine("| " + (mode == OutputMode.CollectionOnly ? "写入合集的 QC" : "已注入 $lod 的 QC") +
                          " | " + N(stats.InjectedQcCount) + " 个 |");
            sb.AppendLine("| 跳过 QC | " + N(stats.SkippedQcCount) + " 个 |");
            sb.AppendLine("| 出错 QC | " + N(errorQcTotal) + " 个 |");
            if (ctx != null && ctx.CompileAfter)
                sb.AppendLine("| 试编译 | 成功 " + N(stats.CompileOk) + " / 失败 " + N(stats.CompileFail) + " |");
            else
                sb.AppendLine("| 试编译 | 未开启 |");
            if (ctx?.Automatic != true)
                sb.AppendLine("| 原始面数 → 末级 LOD 面数 | " + N(stats.TotalSourceTriangles) + " → " + N(stats.TotalLastLodTriangles) + " |");
            sb.AppendLine("| 错误 / 提示 | " + N(errorTotal) + " / " + N(warnTotal) + " |");
            sb.AppendLine();

            // ── 逐项结果 ──
            sb.AppendLine("## 逐项结果");
            sb.AppendLine();
            sb.AppendLine("| 项目 | 结果 | 说明 |");
            sb.AppendLine("| --- | --- | --- |");
            if (outcome == null || outcome.QcOutcomes.Count == 0)
            {
                sb.AppendLine("| （没有逐项结果） | — | 本次运行没有产生 QC 结果（可能是参数错误或提前退出，见日志） |");
            }
            else
            {
                foreach (QcOutcome q in outcome.QcOutcomes)
                {
                    if (q == null) continue;
                    sb.AppendLine("| " + (q.Standalone ? "模型 · " : "QC · ") + Cell(Relative(ctx, q.QcPath)) + " | " + Cell(StateText(q, mode)) +
                                  " | " + Cell(DescribeQc(ctx, q)) + " |");
                }
            }
            sb.AppendLine();

            if (ctx?.Automatic == true && outcome != null)
            {
                sb.AppendLine("## 自动 LOD 结果");
                sb.AppendLine();
                sb.AppendLine("距离按模型尺寸、减面比例及参考视角估算。外观校验基于原始网格采样。");
                sb.AppendLine();
                foreach (var q in outcome.QcOutcomes.Where(q => q.SourceTriangles > 0))
                {
                    sb.AppendLine("### " + Relative(ctx, q.QcPath));
                    sb.AppendLine();
                    sb.AppendLine("原始可见组合上限 " + N(q.SourceTriangles) + " 三角面。");
                    sb.AppendLine();
                    if (q.Levels.Count == 0) continue;
                    bool showDeviation = q.Levels.Any(l => l.MaxSurfaceDeviationPpm > 0);
                    sb.AppendLine("| 候选档 | 目标保留 | 实际三角面 | 实际保留 | 估算距离 HU | Source 阈值 |" +
                                  (showDeviation ? " 几何峰值偏差 |" : "") + " 结果 |");
                    sb.AppendLine("| --- | --- | --- | --- | --- | --- |" + (showDeviation ? " --- |" : "") + " --- |");
                    foreach (var level in q.Levels)
                    {
                        bool kept = level.SkipReason == null && (q.Injected || q.Generated);
                        sb.Append($"| {level.Candidate} | {level.TargetRatio:P0} | {N(level.Triangles)} | " +
                            $"{level.Triangles / (double)q.SourceTriangles:P1} | " +
                            (kept ? level.EstimatedDistanceHu.ToString("0.0", Inv) : "—") + " | " +
                            (kept && !q.Standalone ? level.SwitchPoint.ToString("0.###", Inv) : "—") + " | ");
                        if (showDeviation)
                            sb.Append(level.MaxSurfaceDeviationPpm > 0 ? DeviationPercent(level.MaxSurfaceDeviationPpm) : "—").Append(" | ");
                        sb.AppendLine((kept ? "已输出" : Cell(level.SkipReason ?? "未发布")) + " |");
                    }
                    sb.AppendLine();
                }
            }

            // ── 跳过原因 ──
            sb.AppendLine("## 跳过的 QC（" + N(CountSkipped(outcome)) + "）");
            sb.AppendLine();
            var skips = CollectSkipped(outcome);
            if (skips.Count == 0) sb.AppendLine("（无）");
            else
            {
                sb.AppendLine("| 项目 | 跳过原因 |");
                sb.AppendLine("| --- | --- |");
                foreach (QcOutcome q in skips)
                    sb.AppendLine("| QC · " + Cell(Relative(ctx, q.QcPath)) + " | " + Cell(q.SkipReason ?? "（未写明原因）") + " |");
            }
            sb.AppendLine();

            // ── 错误（按 QC 分组）──
            sb.AppendLine("## 错误（" + N(errorTotal) + "）");
            sb.AppendLine();
            bool anyError = false;
            if (outcome != null)
            {
                foreach (QcOutcome q in outcome.QcOutcomes)
                {
                    if (q == null || q.Errors == null || q.Errors.Count == 0) continue;
                    anyError = true;
                    sb.AppendLine("### QC · " + Relative(ctx, q.QcPath));
                    sb.AppendLine();
                    foreach (string e in Cap(q.Errors, 50))
                        sb.AppendLine("- " + OneLine(e));
                    if (q.Errors.Count > 50)
                        sb.AppendLine("- …还有 " + N(q.Errors.Count - 50) + " 条（见运行日志）");
                    sb.AppendLine();
                }
                if (outcome.Errors.Count > 0)
                {
                    anyError = true;
                    sb.AppendLine("### 全局错误");
                    sb.AppendLine();
                    foreach (string e in Cap(outcome.Errors, 200))
                        sb.AppendLine("- " + OneLine(e));
                    if (outcome.Errors.Count > 200)
                        sb.AppendLine("- …还有 " + N(outcome.Errors.Count - 200) + " 条（见运行日志）");
                    sb.AppendLine();
                }
            }
            if (!anyError) sb.AppendLine("（无）");
            sb.AppendLine();

            // ── 提示 ──
            sb.AppendLine("## 提示（" + N(warnTotal) + "）");
            sb.AppendLine();
            if (outcome == null || outcome.Warnings.Count == 0) sb.AppendLine("（无）");
            else
            {
                foreach (string w in Cap(outcome.Warnings, 200))
                    sb.AppendLine("- " + OneLine(w));
                if (outcome.Warnings.Count > 200)
                    sb.AppendLine("- …还有 " + N(outcome.Warnings.Count - 200) + " 条（见运行日志）");
            }
            sb.AppendLine();

            // ── 还原方法 ──
            sb.AppendLine("## 还原方法");
            sb.AppendLine();
            sb.AppendLine(BackupText(ctx, mode));
            sb.AppendLine();

            // ── 下一步 ──
            sb.AppendLine("## 下一步建议");
            sb.AppendLine();
            foreach (string tip in NextSteps(ctx, outcome, stats, mode))
                sb.AppendLine("- " + tip);
            sb.AppendLine();

            if (ctx != null && !string.IsNullOrEmpty(ctx.BatchDetailReportPath))
            {
                sb.AppendLine("批量细节报告：" + ctx.BatchDetailReportPath);
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine("远景工坊 · L4D2 自动 LOD 工具 v" +
                          (ctx == null || string.IsNullOrEmpty(ctx.ToolVersion) ? AppInfo.Version : ctx.ToolVersion) +
                          " · 生成于 " + now.ToString("yyyy-MM-dd HH:mm:ss", Inv));
            return sb.ToString();
        }

        private static string BuildLogText(ReportContext ctx)
        {
            var sb = new StringBuilder(8192);
            var lines = new List<string>();
            if (ctx != null && ctx.LogLines != null) lines.AddRange(ctx.LogLines);
            else if (SafeOutcome(ctx) != null && SafeOutcome(ctx).LogLines != null) lines.AddRange(SafeOutcome(ctx).LogLines);

            foreach (string line in lines)
            {
                if (line == null) continue;
                sb.AppendLine(line);
            }
            BatchOutcome outcome = SafeOutcome(ctx);
            int errors = CountErrors(outcome);
            int warnings = outcome == null ? 0 : outcome.Warnings.Count;
            sb.AppendLine();
            sb.AppendLine("结果: " + errors.ToString(Inv) + " 错误 / " + warnings.ToString(Inv) + " 提示");
            return sb.ToString();
        }

        // ───────────────────────────── 辅助 ─────────────────────────────

        private static string WriteWithFallback(string reportDir, string rootDir, string fileName, string text)
        {
            var dirs = new List<string>();
            AddDir(dirs, reportDir);
            AddDir(dirs, rootDir);
            try { AddDir(dirs, Path.GetTempPath()); } catch { }

            foreach (string dir in dirs)
            {
                try
                {
                    if (!FileUtil.EnsureDir(dir)) continue;
                    string path = Path.Combine(dir, fileName);
                    QcTextFile.WriteAllText(path, text, new UTF8Encoding(false), true);
                    return path;
                }
                catch { }
            }
            return null;
        }

        private static void AddDir(List<string> dirs, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            foreach (string d in dirs)
                if (string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)) return;
            dirs.Add(dir);
        }

        private static BatchOutcome SafeOutcome(ReportContext ctx)
        {
            try { return ctx == null ? null : ctx.Outcome; } catch { return null; }
        }

        private static BatchStats SafeStats(BatchOutcome outcome)
        {
            if (outcome != null && outcome.Stats != null) return outcome.Stats;
            return new BatchStats();
        }

        private static LodPlan SafePlan(ReportContext ctx)
        {
            try { return ctx == null ? null : ctx.Plan; } catch { return null; }
        }

        private static string ModeText(OutputMode mode)
        {
            switch (mode)
            {
                case OutputMode.SeparateQc: return "独立 _LOD.qc（原 QC 只加一行 $include）";
                case OutputMode.CollectionOnly: return "仅出合集（未修改任何原 QC）";
                default: return "写入原 QC";
            }
        }

        private static string StateText(QcOutcome q, OutputMode mode)
        {
            bool hasError = q.Errors != null && q.Errors.Count > 0;
            if (hasError && q.Injected) return "部分完成";
            if (hasError) return "失败";
            if (q.Skipped) return "跳过";
            if (q.Generated) return "已生成模型 LOD";
            if (q.Injected) return mode == OutputMode.CollectionOnly ? "已写入合集" : "已写入 $lod";
            if (q.CompileTried && q.CompileOk) return "成功";
            return "未处理";
        }

        private static string DescribeQc(ReportContext ctx, QcOutcome q)
        {
            var parts = new List<string>();
            if (q.Skipped && !string.IsNullOrEmpty(q.SkipReason)) parts.Add(q.SkipReason);
            if (q.Generated) parts.Add(q.OutputFiles.Count + " 个 LOD 网格");
            if (q.Injected)
            {
                if (q.LodBlock.EmittedVisibleLevels > 0 || q.LodBlock.HasShadowLod)
                    parts.Add(q.LodBlock.Describe());
                OutputMode mode = ctx == null ? OutputMode.InPlace : ctx.Mode;
                bool sharedHost = !SamePath(q.LodBlockFile, q.QcPath);
                if (mode == OutputMode.CollectionOnly) parts.Add("已按当前写入方式写入");
                else if (sharedHost) parts.Add("已写入 " + Path.GetFileName(q.LodBlockFile) + " 末尾（共享 include，根 QC 未改动）");
                else parts.Add(mode == OutputMode.SeparateQc ? "已按当前写入方式写入" : "已在原 QC 末尾写入 $lod 块");
            }
            if (q.CompileTried)
            {
                if (q.CompileOk) parts.Add("试编译：" + (string.IsNullOrEmpty(q.CompileSummary) ? "成功" : q.CompileSummary));
                else parts.Add("试编译失败");
            }
            if (q.Errors != null && q.Errors.Count > 0) parts.Add("错误：" + OneLine(q.Errors[0]));
            if (parts.Count == 0) parts.Add("—");
            return string.Join("；", parts.ToArray());
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(FileUtil.Normalize(a), FileUtil.Normalize(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static List<QcOutcome> CollectSkipped(BatchOutcome outcome)
        {
            var list = new List<QcOutcome>();
            if (outcome == null || outcome.QcOutcomes == null) return list;
            foreach (QcOutcome q in outcome.QcOutcomes)
                if (q != null && q.Skipped) list.Add(q);
            return list;
        }

        private static int CountSkipped(BatchOutcome outcome)
        {
            int n = CollectSkipped(outcome).Count;
            if (n == 0 && outcome != null && outcome.Stats != null) n = outcome.Stats.SkippedQcCount;
            return n;
        }

        private static int CountErrors(BatchOutcome outcome)
        {
            if (outcome == null) return 0;
            int n = outcome.Errors == null ? 0 : outcome.Errors.Count;
            if (outcome.QcOutcomes != null)
                foreach (QcOutcome q in outcome.QcOutcomes)
                    if (q != null && q.Errors != null) n += q.Errors.Count;
            return n;
        }

        private static int CountErrorQcs(BatchOutcome outcome)
        {
            if (outcome == null || outcome.QcOutcomes == null) return 0;
            int n = 0;
            foreach (QcOutcome q in outcome.QcOutcomes)
                if (q != null && q.Errors != null && q.Errors.Count > 0) n++;
            return n;
        }

        private static string BackupText(ReportContext ctx, OutputMode mode)
        {
            if (mode == OutputMode.CollectionOnly)
                return "本模式没有修改任何原 QC，不需要还原。生成的 *_lodN.glb|smd 和 _LOD合集.qc 可以留着也可以删。";
            if (ctx == null || !ctx.BackupEnabled)
                return "本次已关闭备份，没有生成 .qc.lodbak 副本，无法一键还原（只能手工改回 QC）。" +
                       "\n\n建议下次运行时开启「备份 QC」选项。";
            string where = ctx == null || string.IsNullOrEmpty(ctx.BackupDescription)
                ? "原 QC 旁边"
                : ctx.BackupDescription;
            return "修改前的原文件已备份：" + where + "。\n\n" +
                   "- `.qc.lodbak` 是**第一次**修改前的原件，把它改回原来的 `.qc` 文件名即可还原；\n" +
                   "- 带时间戳的 `.lodbak_MMddHHmmss` 是之后每次修改前的版本，需要回退到某一次时用对应那份；\n" +
                   "- 本工具生成的 `*_lodN.glb|smd` 可以留着（QC 里不引用就不影响编译），也可以删。";
        }

        private static List<string> NextSteps(ReportContext ctx, BatchOutcome outcome, BatchStats stats, OutputMode mode)
        {
            var tips = new List<string>();
            if (outcome != null && outcome.Cancelled)
                tips.Add("本次运行被用户停止：已写入的 QC 保留修改，未处理的 QC 原样未动；需要的话重新运行一次即可（幂等）。");

            if (mode == OutputMode.CollectionOnly)
            {
                tips.Add("打开工程根目录的 _LOD合集.qc，把每段 $lod 手工粘进对应 QC 的末尾，再运行你的 批量编译.bat。");
                tips.Add("本模式没有修改任何原 QC；确认无误前不要删 .qc 原文件。");
            }
            else
            {
                tips.Add("直接双击你原来的 批量编译.bat 即可（$lod 已经写进 QC，不需要改批处理）。");
                tips.Add("如果编译报 $lod 相关错误：用报告「还原方法」里的 .qc.lodbak 还原，再检查该 QC 的手写 $lod。");
                // 多个 QC 共用 include 文件（工具块写在共享文件里）时说明清楚，避免用户以为根 QC 没被处理
                var sharedHosts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (outcome != null && outcome.QcOutcomes != null)
                {
                    foreach (QcOutcome q in outcome.QcOutcomes)
                    {
                        if (q == null || !q.Injected || string.IsNullOrEmpty(q.LodBlockFile)) continue;
                        if (SamePath(q.LodBlockFile, q.QcPath)) continue;
                        string key = FileUtil.Normalize(q.LodBlockFile);
                        int count;
                        sharedHosts.TryGetValue(key, out count);
                        sharedHosts[key] = count + 1;
                    }
                }
                foreach (var kv in sharedHosts)
                {
                    if (kv.Value < 2) continue;
                    tips.Add("有 " + N(kv.Value) + " 个 QC 的 $lod 写在共享文件 " + Path.GetFileName(kv.Key) +
                             " 里（这些 QC 只 include、根 QC 未改动）；它们的模型列表必须一致，否则该文件只保留最后一次写入。");
                }
            }

            if (stats.ErrorQcCount > 0 || CountErrorQcs(outcome) > 0 ||
                (outcome != null && outcome.Errors.Count > 0))
                tips.Add("有 " + N(Math.Max(Math.Max(stats.ErrorQcCount, CountErrorQcs(outcome)),
                                           outcome == null ? 0 : outcome.Errors.Count)) +
                         " 个错误项，先按上面「错误」一节处理，再重新运行。");
            if (stats.CompileFail > 0)
                tips.Add("有 " + N(stats.CompileFail) + " 个 QC 试编译失败：已写入的 $lod 不会自动回滚，请打开运行日志看具体 ERROR 行。");
            if (stats.JobFail > 0)
                tips.Add("有 " + N(stats.JobFail) + " 个减面任务失败：对应级别的 $lod 没有写入（引擎会沿用上一级），详见运行日志。");
            if (stats.InjectedQcCount == 0 && stats.SkippedQcCount > 0)
                tips.Add("本次没有写入任何 QC（全部被合理跳过），检查「跳过原因」是否符合预期。");
            if (tips.Count == 0)
                tips.Add("没有需要处理的问题。");
            return tips;
        }

        private static string Relative(ReportContext ctx, string path)
        {
            string root = null;
            try { if (ctx != null) root = ctx.RootDir; } catch { }
            return FileUtil.RelativeDisplay(root, path);
        }

        private static List<string> Cap(List<string> source, int max)
        {
            var list = new List<string>();
            if (source == null) return list;
            int n = Math.Min(max, source.Count);
            for (int i = 0; i < n; i++) list.Add(source[i]);
            return list;
        }

        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();
        }

        private static string Cell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "—";
            return OneLine(s).Replace("|", "\\|");
        }

        private static string N(long v) { return v.ToString(Inv); }

        private static string DeviationPercent(int ppm) { return (ppm / 10000.0).ToString("0.00", Inv) + "%"; }

        private static string FormatSpan(TimeSpan ts)
        {
            if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
            int totalSeconds = (int)Math.Round(ts.TotalSeconds, MidpointRounding.AwayFromZero);
            int h = totalSeconds / 3600;
            int m = (totalSeconds % 3600) / 60;
            int s = totalSeconds % 60;
            if (h > 0) return h.ToString(Inv) + " 小时 " + m.ToString(Inv) + " 分 " + s.ToString(Inv) + " 秒";
            if (m > 0) return m.ToString(Inv) + " 分 " + s.ToString(Inv) + " 秒";
            return s.ToString(Inv) + " 秒";
        }
    }
}
