using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace LodStudio.Engine
{
    /// <summary>
    /// 批量执行器：按计划执行
    ///   建包装 QC → Blender 减面（并行/可取消/逐任务进度）→ SMD 回转 → 注入 $lod → 可选试编译 → 报告。
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

            try
            {
                RunCore(plan, options, recorder, cancel, outcome, sw);
            }
            catch (Exception ex)
            {
                recorder.Log(LogLevel.Error, "运行发生未预期异常：" + ex.GetType().Name + ": " + ex.Message);
                recorder.Log(LogLevel.Detail, ex.StackTrace);
                lock (outcome.Errors) outcome.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
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
            if (outcome.Stats.InjectedQcCount == 0)
            {
                // 没有注入任何 QC：只有"全部合理跳过"才算成功
                int considered = plan.QcPlans.Count;
                if (considered == 0) return false;
                if (outcome.Stats.SkippedQcCount < considered && outcome.Stats.ErrorQcCount == 0) return false;
            }
            return true;
        }

        private static void RunCore(BatchPlan plan, BatchRunOptions options, RecordingObserver obs,
                                    CancellationToken cancel, BatchOutcome outcome, Stopwatch sw)
        {
            var stats = outcome.Stats;
            var qcOutcomes = new Dictionary<string, QcOutcome>(StringComparer.OrdinalIgnoreCase);
            foreach (var qp in plan.QcPlans)
                qcOutcomes[FileUtil.Normalize(qp.QcPath)] = new QcOutcome { QcPath = qp.QcPath };
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

            // ── 1. Blender 减面 ──
            var workModels = plan.Models.Where(m => m.Jobs.Count > 0 && m.ReadError == null).ToList();
            var jobOwners = new List<PlannedModel>();
            var jobLevels = new List<int>();
            var jobs = new List<BlenderJobSpec>();
            bool blenderNeeded = plan.JobCount > 0;

            // SMD：先把源 SMD 转成临时 GLB（Blender 只吃 glTF）
            var smdTempSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pm in workModels.ToList())
            {
                if (cancel.IsCancellationRequested)
                {
                    outcome.Cancelled = true;
                    FinishQcOutcomes(plan, outcome, qcOutcomes, 0, 0);
                    WriteReports(plan, options, outcome, obs, obs, sw.Elapsed);
                    return;
                }
                if (!pm.IsSmd) continue;
                try
                {
                    string temp = pm.Jobs[0].Source;
                    byte[] glb = SmdGlbCodec.SmdToGlb(pm.SmdSource ?? SmdCodec.Parse(pm.Path));
                    string err = FileUtil.WriteBytesSafe(temp, glb);
                    if (err != null) throw new IOException(err);
                    smdTempSources[pm.Path] = temp;
                }
                catch (Exception ex)
                {
                    obs.Log(LogLevel.Error, "SMD 转换失败 " + pm.BaseName + "：" + ex.Message);
                    foreach (var q in pm.RootQcs) QcErr(q, "SMD 转换失败 " + pm.BaseName + "：" + ex.Message);
                    pm.ReadError = ex.Message;
                }
            }
            workModels = workModels.Where(m => m.ReadError == null).ToList();

            foreach (var pm in workModels)
            {
                for (int li = 0; li < pm.Jobs.Count; li++)
                {
                    jobs.Add(pm.Jobs[li]);
                    jobOwners.Add(pm);
                    jobLevels.Add(li + 1);
                }
            }
            stats.JobCount = jobs.Count;
            stats.UniqueModelCount = plan.Models.Count;

            List<BlenderJobResult> results = new List<BlenderJobResult>();
            if (jobs.Count > 0)
            {
                string blenderPath = options.BlenderPath;
                if (string.IsNullOrEmpty(blenderPath) || !File.Exists(blenderPath))
                {
                    obs.Log(LogLevel.Error, "找不到 Blender（批量模式必须用 Blender 减面）。");
                    foreach (var q in plan.QcPlans.Where(x => x.Action == BatchQcAction.Process))
                        QcErr(q.QcPath, "没有可用的 Blender");
                    outcome.Errors.Add("批量模式必须用 Blender（内置简化器质量不够）。请到设置 → 引擎指定 blender.exe。");
                    WriteReports(plan, options, outcome, obs, obs, sw.Elapsed);
                    return;
                }
                obs.Stage(BatchStage.Blender, "── 简化（这一步最慢）──");
                int workers = options.Workers > 0 ? options.Workers : 0;
                obs.Log(LogLevel.Info, "Blender：" + blenderPath);
                obs.Log(LogLevel.Info, "并行进程：" + (workers > 0 ? workers + "（手动指定）" : "自动（按核数/空闲内存）"));
                obs.Log(LogLevel.Info, "质量模式：" + (options.QualityFirst ? "质量优先（按连通块保形）" : "速度优先（全局比例）"));

                var bopt = new BlenderRunOptions
                {
                    BlenderPath = blenderPath,
                    Workers = workers,
                    QualityFirst = options.QualityFirst,
                    Log = s => obs.Log(LogLevel.Info, s)
                };
                var blenderSw = Stopwatch.StartNew();
                results = BlenderScheduler.RunAll(jobs, bopt, cancel, (done, total) => obs.Progress(done, total, blenderSw.Elapsed));
                blenderSw.Stop();
                if (cancel.IsCancellationRequested)
                {
                    outcome.Cancelled = true;
                    obs.Log(LogLevel.Warn, "已停止：Blender 进程已全部结束。");
                }
                obs.Log(LogLevel.Info, "简化完成：用时 " + blenderSw.Elapsed.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture) + " 分钟");
            }

            // ── 2. 汇总减面结果（GLB 直接用；SMD 转回） ──
            obs.Stage(BatchStage.ConvertingSmd, "── 汇总减面结果 ──");
            var okDst = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var afterByDst = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int jobFail = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                var pm = jobOwners[i];
                int level = jobLevels[i];
                var r = (results != null && i < results.Count) ? results[i] : null;
                if (r == null || !r.Ok || !File.Exists(job.Destination))
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
                    obs.Log(LogLevel.Warn, Path.GetFileName(job.Destination) + "：" + r.Warning);
                if (pm.IsSmd)
                {
                    try
                    {
                        string finalPath = Path.Combine(pm.Dir, pm.BaseName + "_lod" + level + ".smd");
                        var glb = GlbCodec.Read(job.Destination);
                        var smd = SmdGlbCodec.GlbToSmd(glb, pm.SmdSource);
                        SmdCodec.Write(finalPath, smd);
                        okDst.Add(finalPath);
                        if (r.AfterTriangles > 0) afterByDst[finalPath] = r.AfterTriangles;
                        stats.JobOk++;
                    }
                    catch (Exception ex)
                    {
                        jobFail++;
                        foreach (var q in pm.RootQcs) QcErr(q, "模型 " + pm.BaseName + " 的 LOD" + level + " 转回 SMD 失败：" + ex.Message);
                        obs.Log(LogLevel.Error, pm.BaseName + " LOD" + level + " 转回 SMD 失败：" + ex.Message);
                    }
                }
                else
                {
                    okDst.Add(job.Destination);
                    if (r.AfterTriangles > 0) afterByDst[job.Destination] = r.AfterTriangles;
                    stats.JobOk++;
                }
            }
            stats.JobFail = jobFail;
            if (jobs.Count > 0)
                obs.Log(LogLevel.Info, "简化结果：" + stats.JobOk + " 成功 / " + jobs.Count + " 个任务");

            // 末级面数合计
            long lastLodTris = 0;
            foreach (var pm in plan.Models)
            {
                if (pm.Jobs.Count == 0) continue;
                string ext = pm.IsSmd ? ".smd" : ".glb";
                string last = Path.Combine(pm.Dir, pm.BaseName + "_lod" + pm.Jobs.Count + ext);
                int aft;
                if (afterByDst.TryGetValue(last, out aft)) lastLodTris += aft;
            }
            stats.TotalLastLodTriangles = lastLodTris;
            stats.TotalSourceTriangles = plan.TotalSourceTriangles;

            // 清理临时 SMD 文件
            foreach (var kv in smdTempSources)
            {
                FileUtil.TryDelete(kv.Value);
                foreach (var pm in plan.Models.Where(x => string.Equals(x.Path, kv.Key, StringComparison.OrdinalIgnoreCase)))
                    foreach (var j in pm.Jobs) FileUtil.TryDelete(j.Destination);
            }

            if (outcome.Cancelled)
            {
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
            foreach (var qp in plan.QcPlans)
            {
                if (cancel.IsCancellationRequested) { outcome.Cancelled = true; break; }
                var qo = qcOutcomes[FileUtil.Normalize(qp.QcPath)];
                if (qp.Action == BatchQcAction.Error) { qo.Errors.Add(qp.Reason ?? "计划错误"); continue; }
                if (qp.Action == BatchQcAction.Skip)
                {
                    // 合理跳过：如果它带着上次的工具块（本次已无可写内容），把旧块移除
                    if (qp.Info != null && qp.Info.RootHasToolMarker && !collectionMode)
                    {
                        try
                        {
                            var res = QcWriter.Apply(qp.Info, null, false, backup);
                            if (res.Success && !res.NoChange)
                                obs.Log(LogLevel.Info, Path.GetFileName(qp.QcPath) + " → 本次没有可写的 $lod，已移除上次生成的工具块");
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
                // 计划生成后 QC 又被编辑过：按最新内容重解析（$lod 块引用的是当前 studio 列表，不能按旧快照写）
                try
                {
                    DateTime lastWrite = File.GetLastWriteTimeUtc(qp.QcPath);
                    if (lastWrite > plan.BuiltAt.ToUniversalTime().AddSeconds(1))
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
                try
                {
                    bool skipMain = options.SkipMainModel && options.SkipMainQcPaths != null &&
                                    options.SkipMainQcPaths.Any(x => PathsEqual(x, qp.QcPath));
                    block = QcWriter.BuildLodBlock(qp.Info, plan.Plan, okDst, skipMain, options.NoFacial);
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
                    qo.SkipReason = hadErrors ? "模型简化失败，没有可写的 $lod（保留原 QC 不动）" : "没有可替换的条目";
                    skipped++;
                    outcome.SkippedQcPaths.Add(qp.QcPath);
                    continue;
                }

                try
                {
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
                                var res = QcWriter.Apply(qp.Info, body, options.Force, backup);
                                if (res.Success)
                                {
                                    injected++;
                                    injectedPaths.Add(qp.QcPath);
                                    qo.Injected = true;
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
                                var body = QcWriter.WrapMarker(block, plan.PresetName, plan.Plan, false);
                                var res = QcWriter.Apply(qp.Info, body, options.Force, backup);
                                if (res.Success)
                                {
                                    injected++;
                                    injectedPaths.Add(qp.QcPath);
                                    qo.Injected = true;
                                    obs.Log(LogLevel.Info, "  ✔ " + Path.GetFileName(qp.QcPath) + " → 追加 " + plan.Plan.Count + " 级 $lod");
                                    TryDeleteFile(hadSep);
                                }
                                else { qo.Errors.Add(res.Error ?? "写入失败"); }
                                break;
                            }
                    }
                }
                catch (Exception ex)
                {
                    qo.Errors.Add("写 QC 失败：" + ex.Message);
                }
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
            if (!outcome.Cancelled && options.CompileAfter && !collectionMode &&
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
                    CompileAfter = options.CompileAfter
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
