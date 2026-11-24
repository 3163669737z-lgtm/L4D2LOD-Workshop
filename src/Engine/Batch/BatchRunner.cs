using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LodStudio.Engine
{
    /// <summary>
    /// 批量执行器：按计划执行
    ///   建立包装 QC → 原生减面（并行/可取消/逐任务进度）→ SMD 回转 → 注入 $lod → 可选试编译 → 报告。
    /// 不抛异常：任何未预期异常都记录在 outcome.Errors 并让 Ok=false。
    /// </summary>
    public static class BatchRunner
    {
        public static BatchOutcome Run(BatchPlan plan, BatchRunOptions options, IBatchObserver observer,
                                      CancellationToken cancel)
        {
            observer = observer ?? NullObserver.Instance;
            options = options ?? new BatchRunOptions();
            var outcome = new BatchOutcome();
            var sw = Stopwatch.StartNew();
            var recorder = new RecordingObserver(observer);
            var temporaryModels = new List<string>();

            try
            {
                RunCore(plan, options, recorder, cancel, outcome, sw, temporaryModels);
            }
            catch (Exception ex)
            {
                recorder.Log(LogLevel.Error, "运行发生未预期异常：" + ex.GetType().Name + ": " + ex.Message);
                recorder.Log(LogLevel.Detail, ex.StackTrace);
                lock (outcome.Errors) outcome.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                foreach (string path in temporaryModels) FileUtil.TryDelete(path);
                sw.Stop();
                outcome.Elapsed = sw.Elapsed;
                outcome.FinishedAt = DateTime.Now;
                outcome.LogLines = recorder.AllLines;
                outcome.Ok = ComputeOk(plan, outcome);
                observer.Stage(BatchStage.Done, null);
            }
            return outcome;
        }

        private static bool ComputeOk(BatchPlan plan, BatchOutcome outcome)
        {
            if (outcome.Cancelled) return false;
            if (outcome.Errors.Count > 0) return false;
            foreach (var q in outcome.QcOutcomes) if (q.Errors.Count > 0) return false;
            if (outcome.Stats.JobCount > 0 && outcome.Stats.JobOk == 0) return false;
            if (outcome.Stats.InjectedQcCount == 0 && outcome.GeneratedModels == 0)
            {
                // 没有注入任何 QC：只有"全部合理跳过"才算成功
                int considered = plan.QcPlans.Count;
                if (considered == 0) return false;
                if (outcome.Stats.SkippedQcCount < considered && outcome.Stats.ErrorQcCount == 0) return false;
            }
            return true;
        }

        private static void RunCore(BatchPlan plan, BatchRunOptions options, RecordingObserver obs,
                                    CancellationToken cancel, BatchOutcome outcome, Stopwatch sw, List<string> temporaryModels)
        {
            var stats = outcome.Stats;
            foreach (var task in plan.Tasks.Where(t => t.Error != null))
                outcome.Errors.Add(task.Task.Path + "：" + task.Error);
            var qcOutcomes = new Dictionary<string, QcOutcome>(StringComparer.OrdinalIgnoreCase);
            foreach (var qp in plan.QcPlans)
                qcOutcomes[FileUtil.Normalize(qp.QcPath)] = new QcOutcome
                { QcPath = qp.QcPath, Standalone = qp.IsStandalone, SourceTriangles = qp.SourceTriangles };
            void QcErr(string qc, string msg)
            {
                QcOutcome qo;
                if (qcOutcomes.TryGetValue(FileUtil.Normalize(qc), out qo)) qo.Errors.Add(msg);
                else { lock (outcome.Errors) outcome.Errors.Add(Path.GetFileName(qc ?? "") + "：" + msg); }
            }

            // ── 0. 建包装 QC ──
            if (plan.PendingWrappers.Count > 0)
            {
                obs.Stage(BatchStage.Planning, "── 建立包装 QC ──");
                foreach (var kv in plan.PendingWrappers)
                {
                    if (cancel.IsCancellationRequested) { outcome.Cancelled = true; return; }
                    try
                    {
                        ProjectScanner.EnsureWrapperQc(kv.Value, (s, lvl) => obs.Log(lvl, s));
                    }
                    catch (Exception ex)
                    {
                        obs.Log(LogLevel.Error, "建立包装 QC 失败（" + Path.GetFileName(kv.Value) + "）：" + ex.Message);
                        var qc = plan.QcPlans.FirstOrDefault(q => string.Equals(FileUtil.Normalize(q.QcPath), FileUtil.Normalize(kv.Key), StringComparison.OrdinalIgnoreCase));
                        if (qc != null) { qc.Action = BatchQcAction.Error; qc.Reason = "建立包装 QC 失败：" + ex.Message; }
                    }
                }
            }

            var backup = new QcBackupRequest
            {
                Enabled = options.BackupEnabled,
                CentralBaseDir = options.BackupCentralBaseDir,
                ProjectRoot = options.ProjectRoot
            };

            // ── 1. 原生减面 ──
            var workModels = plan.Models.Where(m => m.Jobs.Count > 0 && m.ReadError == null).ToList();
            var jobOwners = new List<PlannedModel>();
            var jobLevels = new List<int>();
            var jobs = new List<ReductionJob>();
            string stageDir = AppInfo.TempRunDir;
            string runKey = "reduce_" + Guid.NewGuid().ToString("N");
            if (workModels.Count > 0) Directory.CreateDirectory(stageDir);

            // SMD 和 DMX 使用临时 GLB 参与减面。
            var proxySources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pm in workModels.ToList())
            {
                if (cancel.IsCancellationRequested)
                {
                    outcome.Cancelled = true;
                    FinishQcOutcomes(plan, outcome, qcOutcomes, 0, 0);
                    WriteReports(plan, options, outcome, obs, obs, sw.Elapsed);
                    return;
                }
                if (!pm.IsSmd && pm.DmxSource == null) continue;
                try
                {
                    string temp = Path.Combine(stageDir, runKey + "_" + proxySources.Count + "_source.glb");
                    temporaryModels.Add(temp);
                    byte[] glb = pm.DmxSource != null ? pm.DmxSource.CreateProxy() :
                        SmdGlbCodec.SmdToGlb(pm.SmdSource ?? SmdCodec.Parse(pm.Path));
                    string err = FileUtil.WriteBytesSafe(temp, glb);
                    if (err != null) throw new IOException(err);
                    proxySources[pm.Path] = temp;
                }
                catch (Exception ex)
                {
                    obs.Log(LogLevel.Error, "模型转换失败 " + pm.BaseName + "：" + ex.Message);
                    foreach (var q in pm.RootQcs) QcErr(q, "模型转换失败 " + pm.BaseName + "：" + ex.Message);
                    pm.ReadError = ex.Message;
                }
            }
            workModels = workModels.Where(m => m.ReadError == null).ToList();

            foreach (var pm in workModels)
            {
                for (int li = 0; li < pm.Jobs.Count; li++)
                {
                    var planned = pm.Jobs[li];
                    string stagePath = Path.Combine(stageDir, runKey + "_" + jobs.Count + "_candidate.glb");
                    temporaryModels.Add(stagePath);
                    jobs.Add(new ReductionJob
                    {
                        Source = proxySources.TryGetValue(pm.Path, out string proxy) ? proxy : planned.Source,
                        Destination = stagePath,
                        DisplayDestination = planned.Destination,
                        Ratio = planned.Ratio,
                        Distance = planned.Distance,
                        RequireFixedVertices = pm.DmxSource != null,
                        Weight = planned.Weight
                    });
                    jobOwners.Add(pm);
                    jobLevels.Add(li + 1);
                }
            }
            stats.JobCount = jobs.Count;
            stats.UniqueModelCount = plan.Models.Count;

            List<ReductionResult> results = new List<ReductionResult>();
            if (jobs.Count > 0)
            {
                obs.Stage(BatchStage.Reducing, "── 简化（内置 meshoptimizer）──");
                var reduceOptions = new GltfpackRunner.RunOptions
                {
                    Workers = options.Workers,
                    QualityFirst = options.QualityFirst,
                    AdaptiveTriangleBudget = options.AdaptiveTriangleBudget,
                    AllowVertexUpdates = options.AllowVertexUpdates,
                    OmitUnchangedOutputs = true,
                    Log = message => obs.Log(LogLevel.Info, message)
                };
                var reduceWatch = Stopwatch.StartNew();
                results = GltfpackRunner.RunAll(jobs, reduceOptions, cancel,
                    (done, total) => obs.Progress(done, total, reduceWatch.Elapsed));
                reduceWatch.Stop();
                if (cancel.IsCancellationRequested)
                {
                    outcome.Cancelled = true;
                    obs.Log(LogLevel.Warn, "已停止：减面进程已全部结束。");
                }
                obs.Log(LogLevel.Info, "简化完成：用时 " + reduceWatch.Elapsed.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture) + " 分钟");
            }

            // ── 2. 汇总减面结果（GLB 直接用；SMD 转回） ──
            obs.Stage(BatchStage.ConvertingSmd, "── 汇总减面结果 ──");
            var okDst = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var previousLods = new Dictionary<PlannedModel, (string Path, int Triangles)>();
            var candidates = new Dictionary<(string Source, int Level), (string Path, int Triangles)>();
            var usedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var triangleChangeByLevel = new long[plan.Plan.Count];
            int jobFail = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                var pm = jobOwners[i];
                int level = jobLevels[i];
                var r = (results != null && i < results.Count) ? results[i] : null;
                if (r == null || !r.Ok || (r.AfterTriangles < r.BeforeTriangles && !File.Exists(job.Destination)))
                {
                    jobFail++;
                    string err = r == null ? "未产出结果" : (!r.Ok ? (r.Error ?? "未知原因") : "输出文件不存在");
                    if (cancel.IsCancellationRequested || (err != null && err.IndexOf("已取消", StringComparison.Ordinal) >= 0))
                    {
                        // 用户取消：LOD 没生成是预期结果，不写进 QC 错误（界面/报告里已经有"已停止"状态）
                        continue;
                    }
                    foreach (var q in pm.RootQcs) QcErr(q, "模型 " + pm.BaseName + " 的 LOD" + level + " 简化失败：" + err);
                    obs.Log(LogLevel.Error, pm.BaseName + " LOD" + level + " 失败：" + err);
                    continue;
                }
                if (!string.IsNullOrEmpty(r.Warning))
                    obs.Log(LogLevel.Warn, Path.GetFileName(job.DisplayDestination) + "：" + r.Warning);

                string finalPath = job.DisplayDestination;
                try
                {
                    bool hasPrevious = previousLods.TryGetValue(pm, out var previous);
                    int inheritedTriangles = hasPrevious ? previous.Triangles : pm.Triangles;
                    int triangles = r.AfterTriangles;
                    SmdModel converted = null;
                    if (pm.IsSmd && triangles < inheritedTriangles)
                    {
                        var glb = GlbCodec.Read(job.Destination);
                        converted = SmdGlbCodec.GlbToSmd(glb, pm.SmdSource);
                        triangles = converted.TriangleCount;
                    }

                    stats.RetriedComponents += r.RetriedComponents;
                    stats.RetainedComponents += r.RetainedComponents;
                    stats.ProtectedPrimitives += r.ProtectedPrimitives;
                    stats.VertexUpdateTrials += r.VertexUpdateTrials;
                    if (triangles >= inheritedTriangles)
                    {
                        if (hasPrevious)
                        {
                            stats.ReusedLevels++;
                            obs.Log(LogLevel.Info, Path.GetFileName(finalPath) + "：QC 沿用 " +
                                                   Path.GetFileName(previous.Path) + "，不写重复网格");
                        }
                        else
                        {
                            stats.UnreducedLevels++;
                            obs.Log(LogLevel.Info, Path.GetFileName(finalPath) + "：沿用源模型，不生成相同网格");
                        }
                    }
                    else
                    {
                        string output = finalPath;
                        if (plan.Automatic && (pm.DmxSource != null || pm.IsSmd))
                        {
                            output = job.Destination + Path.GetExtension(finalPath);
                            temporaryModels.Add(output);
                        }
                        if (pm.DmxSource != null) pm.DmxSource.WriteLod(job.Destination, output);
                        else if (pm.IsSmd) SmdCodec.Write(output, converted);
                        if (plan.Automatic)
                        {
                            finalPath = PublishCandidate(pm.Dir, pm.BaseName, Path.GetExtension(finalPath), level,
                                pm.IsSmd || pm.DmxSource != null ? output : job.Destination, newCandidates);
                            candidates[(pm.Path, level)] = (finalPath, triangles);
                        }
                        else if (!pm.IsSmd && pm.DmxSource == null) FileUtil.CopyAtomic(job.Destination, finalPath);
                        previousLods[pm] = (finalPath, triangles);
                        triangleChangeByLevel[level - 1] += triangles - (long)inheritedTriangles;
                        okDst.Add(finalPath);
                        stats.PublishedLods++;
                        stats.VertexUpdateAccepted += r.VertexUpdateAccepted;
                        stats.VertexUpdateTrianglesSaved += r.VertexUpdateTrianglesSaved;
                        stats.BalancedRegions += r.BalancedRegions;
                        stats.MaximumRegionalSpread = Math.Max(stats.MaximumRegionalSpread, r.MaximumRegionalSpread);
                        stats.MaximumAdjacentRegionalSpread = Math.Max(stats.MaximumAdjacentRegionalSpread, r.MaximumAdjacentRegionalSpread);
                    }
                    stats.JobOk++;
                }
                catch (Exception ex)
                {
                    jobFail++;
                    foreach (var q in pm.RootQcs) QcErr(q, "模型 " + pm.BaseName + " 的 LOD" + level + " 写入失败：" + ex.Message);
                    obs.Log(LogLevel.Error, pm.BaseName + " LOD" + level + " 写入失败：" + ex.Message);
                }
            }
            stats.JobFail = jobFail;
            stats.ModelsWithoutReduction = workModels.Count(pm => !previousLods.ContainsKey(pm));
            if (jobs.Count > 0)
                obs.Log(LogLevel.Info, "简化结果：发布 " + stats.PublishedLods + " 个新网格，沿用已减网格 " +
                                       stats.ReusedLevels + " 档，沿用源网格 " + stats.UnreducedLevels + " 档");
            foreach (var pm in workModels.Where(m => !previousLods.ContainsKey(m)))
                obs.Log(LogLevel.Info, FileUtil.RelativeDisplay(plan.RootDir, pm.Path) + "：没有找到合格的减面档");

            stats.TotalSourceTriangles = plan.TotalSourceTriangles;
            stats.LodTriangles = new long[triangleChangeByLevel.Length];
            long trianglesAtLevel = stats.TotalSourceTriangles;
            for (int level = 0; level < triangleChangeByLevel.Length; level++)
            {
                trianglesAtLevel += triangleChangeByLevel[level];
                stats.LodTriangles[level] = trianglesAtLevel;
            }
            stats.TotalLastLodTriangles = trianglesAtLevel;

            if (outcome.Cancelled)
            {
                foreach (string path in newCandidates) FileUtil.TryDelete(path);
                obs.Log(LogLevel.Warn, "已停止：不注入任何 QC（已生成的 LOD 文件保留）。");
                FinishQcOutcomes(plan, outcome, qcOutcomes, 0, 0);
                WriteReports(plan, options, outcome, obs, obs, sw.Elapsed);
                return;
            }

            // ── 3. 注入 $lod ──
            obs.Stage(BatchStage.Injecting, "── 把 $lod 写回 QC ──");
            var collection = new StringBuilder();
            bool collectionMode = options.OutputMode == OutputMode.CollectionOnly;
            if (collectionMode)
            {
                collection.AppendLine("// 远景工坊 · L4D2 自动 LOD —— $lod 合集");
                collection.AppendLine("// 生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                collection.AppendLine("// 方案: " + plan.PresetName + "（" + plan.Plan.Summary() + "）");
                collection.AppendLine("//");
                collection.AppendLine("// ⚠ 这个文件不能直接编译，也不能被 $include —— 它把多个模型的 $lod 放在一起，");
                collection.AppendLine("//   而 replacemodel 只在对应模型自己的 QC 里才解析得到。");
                collection.AppendLine("//   用途：一眼看全部参数，或手工往某个 QC 里粘对应那一段。");
                collection.AppendLine();
            }

            int injected = 0, skipped = 0;
            var injectedPaths = new List<string>();
            var sharedHostBlocks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var qp in plan.QcPlans)
            {
                if (cancel.IsCancellationRequested) { outcome.Cancelled = true; break; }
                var qo = qcOutcomes[FileUtil.Normalize(qp.QcPath)];
                if (qp.Action == BatchQcAction.Error) { qo.Errors.Add(qp.Reason ?? "计划错误"); continue; }
                if (qp.Action == BatchQcAction.Skip)
                {
                    // 合理跳过：如果它带着上次的工具块（本次已无可写内容），把旧块移除（可能在根 QC，也可能在 include 文件里）
                    if (!plan.Automatic && qp.Info != null && qp.Info.HasToolMarker && !collectionMode &&
                        !qp.Info.ToolMarkerFiles.Any(f => sharedHostBlocks.ContainsKey(FileUtil.Normalize(f))))
                    {
                        try
                        {
                            var res = QcWriter.Apply(qp.Info, null, false, backup);
                            if (res.Success && !res.NoChange)
                            {
                                qo.LodBlockFile = res.HostFile;
                                obs.Log(LogLevel.Info, Path.GetFileName(qp.QcPath) + " → 本次没有可写的 $lod，已移除上次生成的工具块" +
                                        DescribeBlockFiles(res.WrittenFiles));
                            }
                        }
                        catch (Exception ex) { obs.Log(LogLevel.Warn, "移除旧工具块失败：" + ex.Message); }
                    }
                    qo.Skipped = true;
                    qo.SkipReason = qp.Reason;
                    skipped++;
                    outcome.SkippedQcPaths.Add(qp.QcPath);
                    continue;
                }

                if (qp.Info == null) { qo.Errors.Add("没有解析结果"); continue; }
                if (plan.Automatic && (qo.Errors.Count > 0 || outcome.Errors.Count > 0)) continue;
                if (plan.Automatic && qp.Models.Any(m => !File.Exists(m.Path) || File.GetLastWriteTimeUtc(m.Path) != m.SourceWriteTimeUtc))
                {
                    qo.Errors.Add("扫描后源模型已改变，请重新扫描");
                    continue;
                }
                if (plan.Automatic && new[] { qp.QcPath }.Concat(qp.Info.IncludedFiles).Any(path =>
                    !File.Exists(path) || File.GetLastWriteTimeUtc(path) > plan.BuiltAt.ToUniversalTime()))
                {
                    // A successful earlier root can have updated a shared include with the same tool block.
                    if (!new[] { qp.QcPath }.Concat(qp.Info.IncludedFiles).Where(path =>
                        !File.Exists(path) || File.GetLastWriteTimeUtc(path) > plan.BuiltAt.ToUniversalTime())
                        .All(path => sharedHostBlocks.ContainsKey(FileUtil.Normalize(path))))
                    {
                        qo.Errors.Add("计划生成后工程文件已改变，请重新扫描");
                        continue;
                    }
                }
                // 计划生成后 QC 又被编辑过：按最新内容重解析（$lod 块引用的是当前 studio 列表，不能按旧快照写）
                try
                {
                    DateTime lastWrite = File.GetLastWriteTimeUtc(qp.QcPath);
                    if (!plan.Automatic && lastWrite > plan.BuiltAt.ToUniversalTime().AddSeconds(1))
                    {
                        var fresh = QcParser.Parse(qp.QcPath);
                        if (fresh != null && !fresh.ReadFailed)
                        {
                            qp.Info = fresh;
                            obs.Log(LogLevel.Warn, Path.GetFileName(qp.QcPath) + " 在计划生成后被修改过，已按最新内容重新生成 $lod");
                        }
                    }
                }
                catch { }
                string block;
                var qcLevels = qp.Levels ?? plan.Plan;
                Dictionary<int, AutomaticLodResult> accepted = null;
                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool skipMain = options.SkipMainModel &&
                                (options.SkipMainQcPaths == null || options.SkipMainQcPaths.Any(x => PathsEqual(x, qp.QcPath)));
                try
                {
                    if (plan.Automatic)
                    {
                        accepted = SelectLevels(qp, qo, candidates);
                        foreach (int level in accepted.Keys)
                            foreach (var model in qp.Models)
                                for (int prior = level; prior > 0; --prior)
                                    if (candidates.TryGetValue((model.Path, prior), out var candidate))
                                    { referenced.Add(candidate.Path); break; }
                    }
                    block = QcWriter.BuildLodBlock(qp.Info, qcLevels, okDst, skipMain, !plan.Automatic && options.NoFacial,
                        reuseEarlierLods: true, publishedPath: !plan.Automatic ? null : (Func<string, int, string>)((source, level) =>
                            candidates.TryGetValue((source, level), out var candidate) ? candidate.Path : null), automaticLevels: accepted);
                }
                catch (Exception ex)
                {
                    qo.Errors.Add("生成 $lod 块失败：" + ex.Message);
                    continue;
                }
                if (string.IsNullOrEmpty(block))
                {
                    bool hadErrors = qo.Errors.Count > 0 || outcome.Errors.Count > 0;
                    qo.Skipped = true;
                    qo.SkipReason = hadErrors ? "模型简化失败，没有可写的 $lod（保留原 QC 不动）" : "没有合格的减面替换";
                    if (!plan.Automatic && !hadErrors && !collectionMode && qp.Info.HasToolMarker &&
                        !qp.Info.ToolMarkerFiles.Any(f => sharedHostBlocks.ContainsKey(FileUtil.Normalize(f))))
                    {
                        var cleanup = QcWriter.Apply(qp.Info, null, false, backup);
                        if (!cleanup.Success) qo.Errors.Add(cleanup.Error);
                        else if (!cleanup.NoChange)
                        {
                            qo.LodBlockFile = cleanup.HostFile;
                            obs.Log(LogLevel.Info, Path.GetFileName(qp.QcPath) + " → 移除已无有效替换的旧工具块");
                        }
                    }
                    skipped++;
                    outcome.SkippedQcPaths.Add(qp.QcPath);
                    continue;
                }

                try
                {
                    if (qp.IsStandalone)
                    {
                        usedCandidates.UnionWith(referenced);
                        qo.Generated = true;
                        qo.OutputFiles.AddRange(referenced.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
                        outcome.GeneratedModels++;
                        outcome.GeneratedModelPaths.Add(qp.QcPath);
                        obs.Log(LogLevel.Info, Path.GetFileName(qp.QcPath) + " → 生成 " + accepted.Count + " 级模型 LOD");
                        continue;
                    }
                    switch (options.OutputMode)
                    {
                        case OutputMode.CollectionOnly:
                            AppendCollection(collection, qp.Info, plan, block);
                            injected++;
                            injectedPaths.Add(qp.QcPath);
                            qo.Injected = true;
                            obs.Log(LogLevel.Info, "  · " + Path.GetFileName(qp.QcPath) + " → 写入合集");
                            break;

                        case OutputMode.SeparateQc:
                            {
                                string lodQc = QcWriter.SeparateQcPath(qp.QcPath);
                                bool lodExisted = File.Exists(lodQc);
                                if (backup.Enabled && lodExisted)
                                {
                                    string backupError;
                                    string bakPath = QcWriter.BackupFile(lodQc, backup, out backupError);
                                    if (backupError != null)
                                    {
                                        qo.Errors.Add("备份旧 " + Path.GetFileName(lodQc) + " 失败：" + backupError);
                                        break;
                                    }
                                }
                                string sepErr = QcWriter.WriteSeparateQc(lodQc, qp.Info, plan.PresetName, plan.Plan, block);
                                if (sepErr != null) { qo.Errors.Add(sepErr); break; }
                                string includeLine = "$include \"" + Path.GetFileName(lodQc) + "\"";
                                var body = QcWriter.WrapMarker(includeLine, plan.PresetName, plan.Plan, true);
                                // 独立 _LOD.qc 的 include 行属于根 QC，块必须写根 QC（不受"模型声明在 include"规则影响）
                                var res = QcWriter.Apply(qp.Info, body, options.Force, backup, qp.QcPath);
                                if (res.Success)
                                {
                                    injected++;
                                    injectedPaths.Add(qp.QcPath);
                                    qo.Injected = true;
                                    qo.LodBlockFile = qp.QcPath;
                                    obs.Log(LogLevel.Info, "  ✔ " + Path.GetFileName(qp.QcPath) + " → 生成 " + Path.GetFileName(lodQc) + "，原 QC 加一行 $include");
                                }
                                else
                                {
                                    qo.Errors.Add(res.Error ?? "写入失败");
                                    if (!lodExisted) TryDeleteFile(lodQc);   // 根 QC 没写成：回滚本次新建的 _LOD.qc
                                }
                                break;
                            }

                        default:
                            {
                                string hadSep = OldSeparateQcToDelete(qp.Info);
                                var body = QcWriter.WrapMarker(block, plan.PresetName, qcLevels, false);
                                // 模型声明在 include 文件里（如 main.qc）时，工具块写那个文件，根 QC 保持原样
                                string host = QcWriter.ResolveHostFile(qp.Info, skipMain);
                                if (plan.Automatic && (HasSharedConflict(plan, qp, host, qcOutcomes) ||
                                    sharedHostBlocks.TryGetValue(FileUtil.Normalize(host), out string existingBody) && existingBody != body))
                                {
                                    qo.Errors.Add("共享 QC 的 LOD 计划发生冲突：" + host);
                                    break;
                                }
                                var res = QcWriter.Apply(qp.Info, body, options.Force, backup, host);
                                if (res.Success)
                                {
                                    injected++;
                                    injectedPaths.Add(qp.QcPath);
                                    qo.Injected = true;
                                    qo.LodBlockFile = res.HostFile;
                                    string hostName = string.IsNullOrEmpty(res.HostFile) ? "" : Path.GetFileName(res.HostFile);
                                    bool sharedHost = !PathsEqual(res.HostFile, qp.QcPath);
                                    if (res.NoChange)
                                    {
                                        obs.Log(LogLevel.Info, "  ✔ " + Path.GetFileName(qp.QcPath) + " → $lod 已是最新（未改动）" +
                                                (sharedHost ? "，块在 " + hostName : ""));
                                    }
                                    else if (sharedHost)
                                    {
                                        obs.Log(LogLevel.Info, "  ✔ " + Path.GetFileName(qp.QcPath) + " → " + (accepted?.Count ?? qcLevels.Count) +
                                                " 级 $lod 写入 " + hostName + "（共享 include，根 QC 未改动）");
                                    }
                                    else
                                    {
                                        obs.Log(LogLevel.Info, "  ✔ " + Path.GetFileName(qp.QcPath) + " → 追加 " + (accepted?.Count ?? qcLevels.Count) + " 级 $lod");
                                    }
                                    // 多个 QC 共享同一 include 文件时，块内容必须一致；不一致只保留最后写入者，明确提示
                                    if (sharedHost && !string.IsNullOrEmpty(res.HostFile))
                                    {
                                        string hostKey = FileUtil.Normalize(res.HostFile);
                                        string prevBody;
                                        if (sharedHostBlocks.TryGetValue(hostKey, out prevBody))
                                        {
                                            if (!string.Equals(prevBody, body, StringComparison.Ordinal))
                                            {
                                                string msg = "多个 QC 共享 " + hostName + "，但生成的 $lod 内容不同；该文件只保留最后一次写入，" +
                                                             "请检查这些 QC 的模型列表是否一致（或改用独立 _LOD.qc 模式）";
                                                outcome.Warnings.Add(msg);
                                                obs.Log(LogLevel.Warn, msg);
                                            }
                                        }
                                        else sharedHostBlocks[hostKey] = body;
                                    }
                                    TryDeleteFile(hadSep);
                                }
                                else { qo.Errors.Add(res.Error ?? "写入失败"); }
                                break;
                            }
                    }
                    if (qo.Injected)
                    {
                        usedCandidates.UnionWith(referenced);
                        qo.OutputFiles.AddRange(referenced.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
                    }
                }
                catch (Exception ex)
                {
                    qo.Errors.Add("写 QC 失败：" + ex.Message);
                }
            }

            if (plan.Automatic)
            {
                foreach (string path in newCandidates.Except(usedCandidates, StringComparer.OrdinalIgnoreCase)) FileUtil.TryDelete(path);
                stats.PublishedLods = usedCandidates.Count;
            }

            if (collectionMode && !outcome.Cancelled)
            {
                try
                {
                    string colPath = Path.Combine(plan.RootDir, options.CollectionName);
                    QcTextFile.WriteAllText(colPath, collection.ToString(), new UTF8Encoding(false), true);
                    obs.Log(LogLevel.Info, "合集已写出：" + colPath);
                    obs.Log(LogLevel.Info, "（原 QC 一个都没有改动）");
                }
                catch (Exception ex)
                {
                    outcome.Errors.Add("写合集失败：" + ex.Message);
                    obs.Log(LogLevel.Error, "写合集失败：" + ex.Message);
                }
            }

            stats.InjectedQcCount = injected;
            stats.SkippedQcCount = skipped;
            outcome.InjectedQcPaths = injectedPaths;
            FinishQcOutcomes(plan, outcome, qcOutcomes, injected, skipped);

            // ── 4. 可选试编译 ──
            if (!plan.Automatic && !outcome.Cancelled && options.CompileAfter && !collectionMode &&
                !string.IsNullOrEmpty(options.NekomdlPath) && File.Exists(options.NekomdlPath))
            {
                obs.Stage(BatchStage.Compiling, "── 附加：逐个试编译（会写游戏目录，仅供校验）──");
                foreach (var qcPath in injectedPaths.ToList())
                {
                    if (cancel.IsCancellationRequested) { outcome.Cancelled = true; break; }
                    var qo = qcOutcomes[FileUtil.Normalize(qcPath)];
                    qo.CompileTried = true;
                    try
                    {
                        var cr = Nekomdl.Compile(options.NekomdlPath, qcPath);
                        if (cr.Success)
                        {
                            qo.CompileOk = true;
                            stats.CompileOk++;
                            if (cr.Vtx != null && cr.Vtx.Valid)
                            {
                                var tris = string.Join(" → ", cr.Vtx.Triangles.Select(t => t.ToString(CultureInfo.InvariantCulture)).ToArray());
                                qo.CompileSummary = "LOD" + cr.Vtx.NumLods + "  面数 " + tris;
                                obs.Log(LogLevel.Info, "  ✔ " + Path.GetFileName(qcPath) + "  " + qo.CompileSummary);
                            }
                            else
                            {
                                qo.CompileSummary = "编译成功（没读到 vtx）";
                                obs.Log(LogLevel.Info, "  ✔ " + Path.GetFileName(qcPath) + "（编译成功但没读到 vtx）");
                            }
                        }
                        else
                        {
                            stats.CompileFail++;
                            string first = cr.Errors.Count > 0 ? cr.Errors[0] : "未知错误";
                            qo.Errors.Add("编译失败：" + first);
                            obs.Log(LogLevel.Error, "  ✘ " + Path.GetFileName(qcPath) + " 编译失败：" + first);
                        }
                    }
                    catch (Exception ex)
                    {
                        stats.CompileFail++;
                        qo.Errors.Add("编译异常：" + ex.Message);
                        obs.Log(LogLevel.Error, "  ✘ " + Path.GetFileName(qcPath) + " 编译异常：" + ex.Message);
                    }
                }
            }
            else if (options.CompileAfter && !collectionMode &&
                     (string.IsNullOrEmpty(options.NekomdlPath) || !File.Exists(options.NekomdlPath)))
            {
                obs.Log(LogLevel.Warn, "开启了试编译但没有指定编译器，已跳过试编译。");
            }

            if (outcome.Cancelled)
            {
                obs.Log(LogLevel.Warn, "已停止：已完成注入的 QC 保留修改，剩余 QC 未处理。");
            }

            WriteReports(plan, options, outcome, obs, obs, sw.Elapsed);
        }

        private static string PublishCandidate(string directory, string model, string extension, int level, string source, HashSet<string> created)
        {
            using (var sha = SHA256.Create())
            {
                string Hash(string path)
                {
                    using (var input = File.OpenRead(path))
                        return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                }
                string hash = Hash(source);
                string destination = Path.Combine(directory, model + "_lod" + level + "_" + hash.Substring(0, 16) + extension);
                if (File.Exists(destination))
                {
                    if (Hash(destination) != hash) throw new IOException("现有 LOD 文件内容已改变：" + destination);
                }
                else
                {
                    FileUtil.CopyAtomic(source, destination);
                    created.Add(destination);
                }
                return destination;
            }
        }

        private static bool HasSharedConflict(BatchPlan plan, BatchQcPlan current, string host,
            Dictionary<string, QcOutcome> outcomes)
        {
            var changes = new HashSet<string>(current.Info.ToolMarkerFiles.Select(FileUtil.Normalize), StringComparer.OrdinalIgnoreCase);
            changes.Add(FileUtil.Normalize(host));
            var currentRefs = current.Info.Studios.Select(s => (s.RawFile, s.ResolvedPath, s.BodygroupIndex, s.Scale));
            foreach (var sibling in plan.QcPlans)
            {
                if (ReferenceEquals(current, sibling) || sibling.Info == null || sibling.IsStandalone ||
                    !sibling.Info.IncludedFiles.Any(f => changes.Contains(FileUtil.Normalize(f)))) continue;
                if (sibling.Action != BatchQcAction.Process || outcomes[FileUtil.Normalize(sibling.QcPath)].Errors.Count > 0 ||
                    sibling.SourceTriangles != current.SourceTriangles || !PathsEqual(QcWriter.ResolveHostFile(sibling.Info, false), host) ||
                    !currentRefs.SequenceEqual(sibling.Info.Studios.Select(s => (s.RawFile, s.ResolvedPath, s.BodygroupIndex, s.Scale))))
                    return true;
            }
            return false;
        }

        private static Dictionary<int, AutomaticLodResult> SelectLevels(BatchQcPlan qc, QcOutcome outcome,
            Dictionary<(string Source, int Level), (string Path, int Triangles)> candidates)
        {
            var accepted = new Dictionary<int, AutomaticLodResult>();
            var activeCounts = qc.Models.ToDictionary(m => m, m => m.Triangles);
            long previousTriangles = qc.SourceTriangles;
            double previousSwitch = 0;
            var modelExtents = qc.Models.ToDictionary(m => m.Path, m => m.Extent, StringComparer.OrdinalIgnoreCase);
            double extent = qc.Info.Studios.Where(s => s.ResolvedPath != null && modelExtents.ContainsKey(s.ResolvedPath))
                .Max(s => modelExtents[s.ResolvedPath] * s.Scale);
            if (qc.IsStandalone && Path.GetExtension(qc.QcPath).Equals(".glb", StringComparison.OrdinalIgnoreCase))
                extent /= 0.0254; // Standalone glTF units are metres. QC GLBs follow Source project units.
            for (int i = 0; i < qc.Levels.Count; ++i)
            {
                int level = i + 1;
                foreach (var model in qc.Models)
                    if (candidates.TryGetValue((model.Path, level), out var candidate)) activeCounts[model] = candidate.Triangles;
                var result = new AutomaticLodResult
                {
                    Candidate = level,
                    Triangles = AutomaticLod.CountVisible(qc, m => activeCounts[m]),
                    TargetRatio = qc.Levels.Levels[i].Ratio
                };
                outcome.Levels.Add(result);
                if (result.Triangles > previousTriangles * (1 - AutomaticLod.MinimumSavings))
                {
                    result.SkipReason = "相对上一有效档减面不足 10%";
                    continue;
                }
                AutomaticLod.Estimate(result, extent, qc.SourceTriangles, previousSwitch);
                previousTriangles = result.Triangles;
                previousSwitch = result.SwitchPoint;
                accepted.Add(level, result);
            }
            return accepted;
        }

        private static void FinishQcOutcomes(BatchPlan plan, BatchOutcome outcome, Dictionary<string, QcOutcome> qcOutcomes, int injected, int skipped)
        {
            outcome.QcOutcomes.Clear();
            int errors = 0;
            foreach (var qp in plan.QcPlans)
            {
                QcOutcome qo;
                if (!qcOutcomes.TryGetValue(FileUtil.Normalize(qp.QcPath), out qo)) continue;
                if (qp.Action == BatchQcAction.Error && qo.Errors.Count == 0) qo.Errors.Add(qp.Reason ?? "计划错误");
                if (qo.Errors.Count > 0) errors++;
                outcome.QcOutcomes.Add(qo);
            }
            outcome.Stats.ErrorQcCount = errors;
            outcome.Stats.InjectedQcCount = injected;
            outcome.Stats.SkippedQcCount = skipped;
            outcome.Stats.RootQcCount = plan.QcPlans.Count;
            outcome.Stats.UniqueModelCount = plan.Models.Count;
        }

        private static void AddQcError(BatchOutcome outcome, string qcPath, string message)
        {
            lock (outcome.Errors)
            {
                var qo = outcome.QcOutcomes.FirstOrDefault(q => string.Equals(FileUtil.Normalize(q.QcPath), FileUtil.Normalize(qcPath), StringComparison.OrdinalIgnoreCase));
                if (qo != null) qo.Errors.Add(message);
                else outcome.Errors.Add(Path.GetFileName(qcPath ?? "") + "：" + message);
            }
        }

        /// <summary>把写入文件列表拼成"（main.qc、root.qc）"形式的简短说明；空列表返回空串。</summary>
        private static string DescribeBlockFiles(List<string> files)
        {
            if (files == null || files.Count == 0) return "";
            var names = new List<string>(files.Count);
            foreach (string f in files)
            {
                try { names.Add(Path.GetFileName(f)); } catch { }
            }
            return names.Count == 0 ? "" : "（" + string.Join("、", names.ToArray()) + "）";
        }

        /// <summary>切换到"原 QC"模式时，如果上一次是独立 _LOD.qc 模式，返回要删除的旧文件（否则 null）。</summary>
        private static string OldSeparateQcToDelete(QcInfo info)
        {
            try
            {
                if (info == null || !info.RootHasToolMarker) return null;
                string sep = QcWriter.SeparateQcPath(info.QcPath);
                if (!File.Exists(sep)) return null;
                string rootText = QcParser.ReadTextSmart(info.QcPath);
                if (rootText == null || rootText.IndexOf("\"" + Path.GetFileName(sep) + "\"", StringComparison.OrdinalIgnoreCase) < 0) return null;
                string head = QcParser.ReadTextSmart(sep);
                if (head == null) return null;
                if (head.IndexOf("远景工坊", StringComparison.Ordinal) < 0 &&
                    head.IndexOf("自动 LOD 工具", StringComparison.Ordinal) < 0) return null;
                return sep;
            }
            catch { return null; }
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.Delete(path); } catch { }
        }
        private static void AppendCollection(StringBuilder sb, QcInfo info, BatchPlan plan, string block)
        {
            string rel;
            try { rel = FileUtil.RelativeDisplay(plan.RootDir, info.QcPath); } catch { rel = info.QcPath; }
            sb.AppendLine("// ═══════════════════════════════════════════════════════════");
            sb.AppendLine("// QC: " + rel);
            sb.AppendLine("// 模型: " + (string.IsNullOrEmpty(info.ModelName) ? "(未声明)" : info.ModelName));
            sb.AppendLine("// 类型: " + (info.IsStaticProp ? "静态道具（$staticprop）" : "角色/动态模型"));
            sb.AppendLine("// 级数: " + plan.Plan.Count + "  参数: " + plan.Plan.Summary());
            if (info.HasForeignLod)
                sb.AppendLine("// 注意：该 QC 已有手写 $lod（" +
                              string.Join(", ", info.ForeignLodFiles.Select(Path.GetFileName).ToArray()) +
                              "），粘贴前先删掉或注释原有的");
            sb.AppendLine("// —— 把下面这段（到下一个 ═ 为止）整段粘进上面这个 QC 的最末尾 ——");
            sb.AppendLine("// ═══════════════════════════════════════════════════════════");
            sb.Append(block);
            sb.AppendLine();
        }

        private static void WriteReports(BatchPlan plan, BatchRunOptions options, BatchOutcome outcome, RecordingObserver recorder, IBatchObserver obs, TimeSpan elapsed)
        {
            try
            {
                var ctx = new ReportContext
                {
                    RootDir = plan.RootDir,
                    PresetName = plan.PresetName,
                    Plan = plan.Plan,
                    Mode = options.OutputMode,
                    BackupEnabled = options.BackupEnabled,
                    BackupDescription = options.BackupEnabled
                        ? (string.IsNullOrEmpty(options.BackupCentralBaseDir) ? "同目录的 *.qc.lodbak" : options.BackupCentralBaseDir)
                        : "本次未生成备份",
                    StartedAt = DateTime.Now - elapsed,
                    Elapsed = elapsed,
                    Outcome = outcome,
                    LogLines = recorder != null ? recorder.AllLines : outcome.LogLines,
                    CompileAfter = options.CompileAfter,
                    Automatic = plan.Automatic
                };
                string dir = !string.IsNullOrEmpty(options.ReportDir) ? options.ReportDir : plan.RootDir;
                outcome.ReportPath = ReportWriter.WriteMarkdownReport(ctx, dir);
                outcome.LogPath = ReportWriter.WriteLogFile(ctx, dir, "运行日志");
                if (!string.IsNullOrEmpty(outcome.ReportPath))
                    obs.Log(LogLevel.Info, "总报告：" + outcome.ReportPath);
                if (!string.IsNullOrEmpty(outcome.LogPath))
                    obs.Log(LogLevel.Detail, "运行日志：" + outcome.LogPath);
            }
            catch (Exception ex)
            {
                obs.Log(LogLevel.Warn, "写报告/日志失败：" + ex.Message);
            }
        }

        private static bool PathsEqual(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>记录所有日志（报告/日志文件用），同时转发给上层观察者。</summary>
        private sealed class RecordingObserver : IBatchObserver
        {
            private readonly IBatchObserver _inner;
            private readonly List<string> _lines = new List<string>();
            private readonly object _gate = new object();
            public RecordingObserver(IBatchObserver inner) { _inner = inner; }

            public List<string> AllLines { get { lock (_gate) return _lines.ToList(); } }

            public void Log(LogLevel level, string text)
            {
                if (text == null) return;
                lock (_gate) _lines.Add(TextOf(level) + text);
                if (_inner != null) _inner.Log(level, text);
            }

            public void Progress(int done, int total, TimeSpan elapsed)
            {
                if (_inner != null) _inner.Progress(done, total, elapsed);
            }

            public void Stage(BatchStage stage, string detail)
            {
                if (detail != null) { lock (_gate) _lines.Add(detail); }
                if (_inner != null) _inner.Stage(stage, detail);
            }

            private static string TextOf(LogLevel lvl)
            {
                switch (lvl)
                {
                    case LogLevel.Error: return "[错误] ";
                    case LogLevel.Warn: return "[提示] ";
                    case LogLevel.Stage: return "";
                    default: return "";
                }
            }
        }
    }
}
