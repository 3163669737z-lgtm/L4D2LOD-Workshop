using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LodStudio.Controls;
using LodStudio.Engine;

namespace LodStudio.ViewModels
{
    public sealed partial class ShellViewModel
    {
        private PlanRequest _previewRequest;

        private void InvalidatePlan()
        {
            if (IsPlanVisible && PlanPreview != null && !PlanPreview.IsStale && _previewRequest != null &&
                !_previewRequest.Matches(CapturePlanRequest()))
            {
                PlanPreview.MarkStale();
                if (CanEdit) ShowStalePlanStatus();
            }
            UpdateActions();
        }

        private void ShowStalePlanStatus()
        {
            FinishActivity("预览已过期", "任务或参数已改变，请重新扫描", "Warn");
        }

        public Task PreviewPlanAsync()
        {
            if (!CanPreview) return Task.CompletedTask;
            var request = CapturePlanRequest();
            return PerformOperationAsync("扫描预览失败", async token =>
            {
                SetStatus("正在扫描预览", "读取 QC、统计模型并计算档位", "Signal", true);
                var plan = await BuildPlanAsync(request, token);
                ShowPreview(request, plan);
            });
        }

        private async Task<BatchPlan> BuildPlanAsync(PlanRequest request, CancellationToken token)
        {
            var observer = new CollectingObserver();
            var plan = await Task.Run(() => BatchPlanner.Build(request.Tasks, request.Options.ProjectRoot,
                request.Options, observer, token), token);
            token.ThrowIfCancellationRequested();
            foreach (var line in observer.Lines)
            {
                if (line.StartsWith("[错误] ", StringComparison.Ordinal)) Log.Append(LogLevel.Error, line.Substring(5));
                else if (line.StartsWith("[提示] ", StringComparison.Ordinal)) Log.Append(LogLevel.Warn, line.Substring(5));
                else Log.Append(LogLevel.Detail, line);
            }
            if (plan.QcPlans.Count == 0 && plan.Tasks.Count == 0 && observer.Errors.Count > 0)
                throw new InvalidOperationException(observer.Errors[0]);
            return plan;
        }

        private void ShowPreview(PlanRequest request, BatchPlan plan)
        {
            _previewRequest = request;
            PlanPreview = new PlanPreviewViewModel(plan, request.Options);
            IsPlanVisible = true;
            InvalidatePlan();
            if (PlanPreview.IsStale)
                ShowStalePlanStatus();
            else if (PlanPreview.CanStartFromPlan)
                FinishActivity("预览就绪", "可查看各项详情，或按当前计划生成", "Ok");
            else
                FinishActivity("暂无可生成的作业", "可在预览中查看跳过或错误原因", PlanPreview.ErrorCount > 0 ? "Err" : "Warn");
            Log.Append(LogLevel.Stage, "作业计划：" + PlanPreview.Headline + " · " + PlanPreview.Subline);
        }

        public void ClosePlan()
        {
            if (!IsPlanVisible || !CanEdit) return;
            IsPlanVisible = false;
            PlanPreview = null;
            _previewRequest = null;
            UpdateQueueStatus();
        }

        public Task StartFromPlanAsync() => IsPlanVisible ? StartRunAsync() : Task.CompletedTask;

        public Task StartRunAsync()
        {
            if (!CanPreview) return Task.CompletedTask;
            InvalidatePlan();
            if (!CanStart) return Task.CompletedTask;

            var request = IsPlanVisible ? _previewRequest : CapturePlanRequest();
            var previewPlan = IsPlanVisible ? PlanPreview.Plan : null;
            if (request == null) return Task.CompletedTask;
            return PerformOperationAsync("准备生成失败", async token =>
            {
                if (!GltfpackEmbeddedOk())
                {
                    FinishActivity("减面组件缺失", "请重新构建或重新下载完整的远景工坊", "Err");
                    return;
                }

                SetStatus("正在准备生成", previewPlan == null ? "读取工程并计算作业计划" : "检查计划中的输出位置", "Signal", true);
                var plan = previewPlan ?? await BuildPlanAsync(request, token);
                var options = request.Options;
                if (!PlanPreviewViewModel.HasWork(plan, options))
                {
                    ShowPreview(request, plan);
                    return;
                }

                if (options.CompileAfter && !await Task.Run(() => File.Exists(options.NekomdlPath), token))
                {
                    token.ThrowIfCancellationRequested();
                    FinishActivity("尚未指定试编译器", "在引擎设置中指定 nekomdl.exe，或关闭生成后试编译", "Warn");
                    bool go = await _ui.ConfirmAsync("需要设置试编译器",
                        StatusSub, "打开设置", "返回", false, null);
                    if (go) _ui.OpenSettings(0);
                    return;
                }

                var bad = await Task.Run(() => PrepareOutputLocations(request, plan, token), token);
                token.ThrowIfCancellationRequested();
                if (!RequestStillCurrent(request)) return;
                if (bad.Count > 0)
                {
                    FinishActivity("部分输出目录不可写", "检查目录权限后可重试", "Warn");
                    if (!await _ui.ConfirmAsync("部分输出目录不可写",
                        "这些目录中的作业可能失败。可以返回检查权限，也可以继续处理其他作业。",
                        "继续", "返回", true, string.Join("\n", bad)))
                        return;
                    token.ThrowIfCancellationRequested();
                    if (!RequestStillCurrent(request)) return;
                }

                bool modifiesQc = PlanPreviewViewModel.ModifiesQc(plan, options);
                if (modifiesQc || plan.PendingWrappers.Count > 0 || options.CompileAfter)
                {
                    FinishActivity("等待写入确认", PlanPreviewViewModel.WriteSummary(plan, options), "Signal");
                    var detail = new List<string> { PlanPreviewViewModel.DescribeLevels(plan) };
                    if (modifiesQc)
                    {
                        detail.Add(options.BackupEnabled
                            ? "修改前备份到" + (string.IsNullOrEmpty(options.BackupCentralBaseDir) ? "原文件旁（.lodbak）" : " " + options.BackupCentralBaseDir)
                            : "备份已关闭，本次修改无法通过 .lodbak 还原。");
                        if (options.Force) detail.Add("遇到已有的手写 $lod 时，会注释原有块。");
                    }
                    if (plan.PendingWrappers.Count > 0) detail.Add("将为 " + plan.PendingWrappers.Count + " 个模型新建包装 QC。");
                    if (options.CompileAfter) detail.Add("生成后将试编译，可能写入游戏目录。");
                    bool confirmed = await _ui.ConfirmAsync(modifiesQc ? "确认写入 QC" : "确认生成",
                        PlanPreviewViewModel.WriteSummary(plan, options), plan.JobCount > 0 ? "开始生成" : "清理旧块", "返回",
                        modifiesQc && !options.BackupEnabled, string.Join("\n", detail));
                    if (!confirmed)
                    {
                        FinishActivity(IsPlanVisible ? "预览就绪" : "已返回队列", "本次生成尚未开始", "Normal");
                        return;
                    }
                }

                token.ThrowIfCancellationRequested();
                if (!RequestStillCurrent(request)) return;
                await RunAsync(plan, options, token);
            });
        }

        private bool RequestStillCurrent(PlanRequest request)
        {
            if (LodPlan.IsValid && request.Matches(CapturePlanRequest())) return true;
            InvalidatePlan();
            FinishActivity("任务或参数已改变", IsPlanVisible ? "请重新扫描后再生成" : "请重新开始生成", "Warn");
            return false;
        }

        private PlanRequest CapturePlanRequest()
        {
            var tasks = Queue.Items.Select(t => t.ToRef()).ToList();
            string rootDir = tasks.FirstOrDefault(t => t.Kind == TaskKind.Folder)?.Path;
            if (rootDir == null && tasks.Count > 0)
            {
                try { rootDir = Path.GetDirectoryName(FileUtil.Normalize(tasks[0].Path)); }
                catch (ArgumentException) { }
            }

            var options = BuildRunOptions(rootDir ?? AppInfo.ExeDir);
            int backupLocation = options.BackupEnabled && Settings.BackupEnabled ? Settings.BackupLocation : 0;
            var locations = new AppSettings
            {
                BackupLocation = backupLocation,
                BackupCustomDir = backupLocation == 2 ? Settings.BackupCustomDir : null,
                ReportLocation = Settings.ReportLocation,
                ReportCustomDir = Settings.ReportLocation == 2 ? Settings.ReportCustomDir : null
            };
            return new PlanRequest(tasks, options, locations);
        }

        internal BatchRunOptions BuildRunOptions(string rootDir)
        {
            bool auto = LodPlan.IsAutomatic;
            var mode = auto ? OutputMode.InPlace : (OutputMode)LodPlan.WriteMode;
            bool writesQc = mode != OutputMode.CollectionOnly;
            bool compile = writesQc && Settings.CompileAfter;
            int profile = Settings.EffectiveReductionProfile();
            return new BatchRunOptions
            {
                Plan = auto ? null : LodPlan.ClonePlan(),
                PresetName = LodPlan.PresetDisplay,
                Automatic = auto,
                Workers = Settings.Workers,
                ReductionProfile = profile,
                QualityFirst = profile != 2,
                GenerateShadowLod = Settings.GenerateShadowLod,
                ShadowLodRatio = Settings.ShadowLodRatio,
                ShadowLodMode = (ShadowLodMode)Settings.ShadowLodMode,
                ShadowLodReuseLevel = Settings.ShadowLodReuseLevel,
                AutoTargetTriangles = Settings.AutoTargetTriangles,
                AutoMinSavingsPercent = Settings.AutoMinSavingsPercent,
                AdaptiveTriangleBudget = Settings.AdaptiveTriangleBudget,
                AllowVertexUpdates = Settings.AllowVertexUpdates,
                KeepOriginalModels = Settings.KeepOriginalModels,
                Force = writesQc && Settings.ForceOverwriteLod,
                CompileAfter = compile,
                NekomdlPath = compile ? Settings.NekomdlPath : null,
                NoFacial = Settings.NoFacial,
                NoFacialFromLevel = Settings.NoFacialFromLevel,
                CollapseBones = Settings.CollapseBones,
                CollapseBonesFromLevel = Settings.CollapseBonesFromLevel,
                SkipMainModel = Settings.SkipMainModel,
                SkipMainQcPaths = Settings.SkipMainModel
                    ? new HashSet<string>(Settings.SkipMainQcs, StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                MinTriangles = Settings.AdaptiveTriangleBudget ? 0 : Settings.MinTriangles,
                OutputMode = mode,
                BackupEnabled = writesQc && (auto || Settings.BackupEnabled),
                ProjectRoot = rootDir
            };
        }

        private static List<string> PrepareOutputLocations(PlanRequest request, BatchPlan plan, CancellationToken token)
        {
            var options = request.Options;
            options.ReportDir = OutputLocations.ResolveReportDir(request.Locations, FirstQcDir(plan));
            options.BackupCentralBaseDir = PlanPreviewViewModel.ModifiesQc(plan, options) && options.BackupEnabled
                ? OutputLocations.ResolveBackupBaseDir(request.Locations) : null;

            var files = plan.Models.Where(m => m.Jobs.Count > 0 || m.ShadowJob != null).Select(m => m.Path)
                .Concat(plan.PendingWrappers.Select(pair => pair.Key));
            if (options.OutputMode != OutputMode.CollectionOnly)
            {
                files = files.Concat(plan.QcPlans
                    .Where(q => !q.IsStandalone && (q.Action == BatchQcAction.Process || PlanPreviewViewModel.RemovesOldBlock(plan, q, options)))
                    .Select(q => options.OutputMode == OutputMode.InPlace && q.Info != null
                        ? QcWriter.ResolveHostFile(q.Info, options.SkipMainModel && options.SkipMainQcPaths.Contains(q.QcPath))
                        : q.QcPath));
            }

            var dirs = files.Where(p => !string.IsNullOrEmpty(p)).Select(Path.GetDirectoryName);
            if (options.OutputMode == OutputMode.CollectionOnly) dirs = dirs.Concat(new[] { plan.RootDir });
            var bad = new List<string>();
            foreach (string dir in dirs.Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (FileUtil.TestWritable(dir) != null) bad.Add(dir);
            }
            return bad;
        }

        private sealed class PlanRequest
        {
            internal readonly List<TaskRef> Tasks;
            internal readonly BatchRunOptions Options;
            internal readonly AppSettings Locations;

            internal PlanRequest(List<TaskRef> tasks, BatchRunOptions options, AppSettings locations)
            {
                Tasks = tasks;
                Options = options;
                Locations = locations;
            }

            internal bool Matches(PlanRequest other)
            {
                var a = Options;
                var b = other.Options;
                return a.PresetName == b.PresetName && a.Automatic == b.Automatic &&
                       a.OutputMode == b.OutputMode && a.Force == b.Force && a.CompileAfter == b.CompileAfter &&
                       a.NekomdlPath == b.NekomdlPath && a.Workers == b.Workers && a.QualityFirst == b.QualityFirst &&
                       a.ReductionProfile == b.ReductionProfile &&
                       a.GenerateShadowLod == b.GenerateShadowLod && a.ShadowLodRatio == b.ShadowLodRatio &&
                       a.ShadowLodMode == b.ShadowLodMode && a.ShadowLodReuseLevel == b.ShadowLodReuseLevel &&
                       a.AutoTargetTriangles == b.AutoTargetTriangles && a.AutoMinSavingsPercent == b.AutoMinSavingsPercent &&
                       a.AdaptiveTriangleBudget == b.AdaptiveTriangleBudget && a.AllowVertexUpdates == b.AllowVertexUpdates &&
                       a.MinTriangles == b.MinTriangles && a.KeepOriginalModels == b.KeepOriginalModels &&
                       a.NoFacial == b.NoFacial && a.NoFacialFromLevel == b.NoFacialFromLevel &&
                       a.CollapseBones == b.CollapseBones && a.CollapseBonesFromLevel == b.CollapseBonesFromLevel &&
                       a.SkipMainModel == b.SkipMainModel && a.SkipMainQcPaths.SetEquals(b.SkipMainQcPaths) &&
                       a.BackupEnabled == b.BackupEnabled && Locations.BackupLocation == other.Locations.BackupLocation &&
                       Locations.BackupCustomDir == other.Locations.BackupCustomDir &&
                       Locations.ReportLocation == other.Locations.ReportLocation && Locations.ReportCustomDir == other.Locations.ReportCustomDir &&
                       (a.Automatic || a.Plan.Levels.Select(x => (x.Distance, x.Ratio)).SequenceEqual(b.Plan.Levels.Select(x => (x.Distance, x.Ratio)))) &&
                       Tasks.Select(t => t.Serialize()).SequenceEqual(other.Tasks.Select(t => t.Serialize()), StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
