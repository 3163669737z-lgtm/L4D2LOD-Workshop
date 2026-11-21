using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
            sb.AppendLine("- LOD 方案：" + presetName + "（" + (plan == null ? "（未设置）" : plan.Summary()) + "）");
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
            sb.AppendLine("| 简化任务 | " + N(stats.JobCount) + " 个（成功 " + N(stats.JobOk) + " / 失败 " + N(stats.JobFail) + "） |");
            sb.AppendLine("| " + (mode == OutputMode.CollectionOnly ? "写入合集的 QC" : "已注入 $lod 的 QC") +
                          " | " + N(stats.InjectedQcCount) + " 个 |");
            sb.AppendLine("| 跳过 QC | " + N(stats.SkippedQcCount) + " 个 |");
            sb.AppendLine("| 出错 QC | " + N(errorQcTotal) + " 个 |");
            if (ctx != null && ctx.CompileAfter)
                sb.AppendLine("| 试编译 | 成功 " + N(stats.CompileOk) + " / 失败 " + N(stats.CompileFail) + " |");
            else
                sb.AppendLine("| 试编译 | 未开启 |");
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
                    sb.AppendLine("| QC · " + Cell(Relative(ctx, q.QcPath)) + " | " + Cell(StateText(q, mode)) +
                                  " | " + Cell(DescribeQc(ctx, q)) + " |");
                }
            }
            sb.AppendLine();

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
            if (q.Injected) return mode == OutputMode.CollectionOnly ? "已写入合集" : "已写入 $lod";
            if (q.CompileTried && q.CompileOk) return "成功";
            return "未处理";
        }

        private static string DescribeQc(ReportContext ctx, QcOutcome q)
        {
            var parts = new List<string>();
            if (q.Skipped && !string.IsNullOrEmpty(q.SkipReason)) parts.Add(q.SkipReason);
            if (q.Injected) parts.Add(ModeText(ctx == null ? OutputMode.InPlace : ctx.Mode) == "写入原 QC"
                ? "已在原 QC 末尾写入 $lod 块"
                : "已按当前写入方式写入");
            if (q.CompileTried)
            {
                if (q.CompileOk) parts.Add("试编译：" + (string.IsNullOrEmpty(q.CompileSummary) ? "成功" : q.CompileSummary));
                else parts.Add("试编译失败");
            }
            if (q.Errors != null && q.Errors.Count > 0) parts.Add("错误：" + OneLine(q.Errors[0]));
            if (parts.Count == 0) parts.Add("—");
            return string.Join("；", parts.ToArray());
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
