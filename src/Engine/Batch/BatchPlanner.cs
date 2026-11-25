using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LodStudio.Engine
{
    /// <summary>
    /// 批量计划器：把用户任务展开为 QC 清单，解析 QC，全局去重，统计面数，生成减面任务。
    /// **只读**：不写任何文件、不启动减面进程、不修改 QC。
    /// </summary>
    public static class BatchPlanner
    {
        private static readonly Regex ShadowProductName = new Regex(@"_shadowlod(?:_[0-9a-f]{16})?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static BatchPlan Build(IReadOnlyList<TaskRef> tasks, string rootDir, BatchRunOptions options,
                                      IBatchObserver observer, CancellationToken cancel)
        {
            observer = observer ?? NullObserver.Instance;
            options = options ?? new BatchRunOptions();
            var plan = new BatchPlan
            {
                RootDir = FileUtil.Normalize(rootDir),
                BuiltAt = DateTime.Now,
                Plan = options.Plan ?? LodPlan.CreateDefault(),
                Automatic = options.Automatic,
                PresetName = options.Automatic ? PresetLibrary.AutomaticName : string.IsNullOrEmpty(options.PresetName) ? PresetLibrary.CustomName : options.PresetName
            };

            var logs = new List<string>();
            var wrap = new StringBuilder();
            Action<LogLevel, string> log = delegate (LogLevel lvl, string s)
            {
                if (s == null) return;
                lock (logs) logs.Add(s);
                observer.Log(lvl, s);
            };

            try
            {
                DoBuild(tasks, plan, options, observer, cancel, log);
            }
            catch (Exception ex)
            {
                log(LogLevel.Error, "生成计划时发生未预期错误：" + ex.GetType().Name + ": " + ex.Message);
                observer.Log(LogLevel.Detail, ex.StackTrace);
            }

            plan.HasWork = plan.QcPlans.Any(q => q.Action == BatchQcAction.Process && q.Models.Count > 0) && plan.JobCount > 0;
            return plan;
        }

        private static void DoBuild(IReadOnlyList<TaskRef> tasks, BatchPlan plan, BatchRunOptions options,
                                    IBatchObserver observer, CancellationToken cancel,
                                    Action<LogLevel, string> log)
        {
            var planLevels = plan.Plan;
            string perf = options.Automatic ? null : planLevels.Validate();
            if (perf != null) log(LogLevel.Error, "LOD 参数有误：" + perf);
            if (!options.Automatic && options.GenerateShadowLod && planLevels.Count > QcWriter.MaxVisibleLodsWithShadow)
            {
                log(LogLevel.Warn, "Source 引擎最多支持 6 级可见 LOD 加 $shadowlod，本次将生成 6 级可见 LOD + $shadowlod。");
                planLevels = new LodPlan { Levels = planLevels.Levels.Take(QcWriter.MaxVisibleLodsWithShadow)
                    .Select(level => level.Clone()).ToList() };
                plan.Plan = planLevels;
            }

            // ── 1. 展开任务 ──
            observer.Stage(BatchStage.Planning, "── 1/5 展开任务 ──");
            var qcOrder = new List<string>();
            var qcSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wrapperModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (tasks == null || tasks.Count == 0)
            {
                log(LogLevel.Error, "没有任务。");
                return;
            }

            foreach (var task in tasks)
            {
                if (cancel.IsCancellationRequested) return;
                if (task == null || string.IsNullOrEmpty(task.Path)) continue;
                var exp = new TaskExpansion { Task = task };
                plan.Tasks.Add(exp);
                string path = task.Path;
                try
                {
                    if (task.Kind == TaskKind.Folder)
                    {
                        if (!Directory.Exists(path)) { exp.Error = "文件夹不存在"; log(LogLevel.Error, "文件夹不存在：" + path); continue; }
                        var roots = ProjectScanner.FindRootQcs(path, s => log(LogLevel.Detail, s));
                        if (roots.Count == 0)
                        {
                            var loose = ProjectScanner.FindLooseModels(path, 3, s => log(LogLevel.Detail, s));
                            loose.RemoveAll(model => ShadowProductName.IsMatch(Path.GetFileNameWithoutExtension(model)));
                            if (loose.Count == 0)
                            {
                                exp.Error = "没有 QC，也没有 GLB、SMD 或 DMX 模型";
                                log(LogLevel.Warn, string.Format("[{0}] {1} → {2}", "工程", Path.GetFileName(path.TrimEnd('\\')), exp.Error));
                                continue;
                            }
                            log(LogLevel.Info, options.Automatic ? "没有 QC：仅生成模型 LOD" : "没有 QC：为 " + loose.Count + " 个模型各建一个包装 QC");
                            foreach (var m in loose)
                            {
                                if (options.Automatic) { AddQc(exp, qcOrder, qcSeen, m, log); continue; }
                                string qc = ProjectScanner.WrapperQcPath(m);
                                if (File.Exists(qc)) AddQc(exp, qcOrder, qcSeen, qc, log);
                                else
                                {
                                    if (!wrapperModels.ContainsKey(qc)) { wrapperModels[qc] = m; plan.PendingWrappers.Add(new KeyValuePair<string, string>(qc, m)); }
                                    AddQc(exp, qcOrder, qcSeen, qc, log);
                                    exp.WrapperModelPaths.Add(m);
                                }
                            }
                        }
                        else
                        {
                            foreach (var q in roots) AddQc(exp, qcOrder, qcSeen, q, log);
                        }
                    }
                    else if (task.Kind == TaskKind.Qc)
                    {
                        if (!File.Exists(path)) { exp.Error = "文件不存在"; log(LogLevel.Error, "QC 不存在：" + path); continue; }
                        AddQc(exp, qcOrder, qcSeen, path, log);
                    }
                    else
                    {
                        if (!File.Exists(path)) { exp.Error = "文件不存在"; log(LogLevel.Error, "模型不存在：" + path); continue; }
                        if (options.Automatic) { AddQc(exp, qcOrder, qcSeen, path, log); continue; }
                        string qc = ProjectScanner.WrapperQcPath(path);
                        if (!File.Exists(qc) && !wrapperModels.ContainsKey(qc))
                        {
                            wrapperModels[qc] = path;
                            plan.PendingWrappers.Add(new KeyValuePair<string, string>(qc, path));
                            exp.WrapperModelPaths.Add(path);
                        }
                        AddQc(exp, qcOrder, qcSeen, qc, log);
                    }
                }
                catch (Exception ex)
                {
                    exp.Error = "展开失败：" + ex.Message;
                    log(LogLevel.Error, "展开失败（" + Path.GetFileName(path) + "）：" + ex.Message);
                }
            }

            if (cancel.IsCancellationRequested) return;
            log(LogLevel.Info, "QC 清单：" + qcOrder.Count + " 个（已按路径去重）");

            // ── 2. 解析 QC ──
            observer.Stage(BatchStage.Planning, "── 2/5 解析 QC ──");
            var qcPlans = new List<BatchQcPlan>();
            var byPath = new Dictionary<string, BatchQcPlan>(StringComparer.OrdinalIgnoreCase);
            foreach (var qc in qcOrder)
            {
                if (cancel.IsCancellationRequested) return;
                var qp = new BatchQcPlan { QcPath = qc };
                bool synthetic = false;
                string modelForWrapper;
                if (options.Automatic && QcPathResolver.IsSupportedModelExtension(Path.GetExtension(qc)))
                {
                    qp.IsStandalone = true;
                    qp.Info = MakeWrapperInfo(qc, qc);
                    synthetic = true;
                }
                else if (wrapperModels.TryGetValue(qc, out modelForWrapper) && !File.Exists(qc))
                {
                    qp.Info = MakeWrapperInfo(qc, modelForWrapper);
                    qp.IsWrapperQc = true;
                    synthetic = true;
                }
                else
                {
                    try { qp.Info = QcParser.Parse(qc); }
                    catch (Exception ex)
                    {
                        qp.Info = new QcInfo { QcPath = qc, QcDir = SafeDir(qc), ReadFailed = true };
                        qp.Info.Warnings.Add("解析异常：" + ex.Message);
                    }
                }

                if (qp.Info == null) continue;
                var info = qp.Info;
                if (info.ReadFailed || (options.Automatic && info.Warnings.Any(w => !w.StartsWith("没有找到 $modelname", StringComparison.Ordinal))))
                {
                    qp.Action = BatchQcAction.Error;
                    qp.Reason = info.Warnings.FirstOrDefault() ?? "读取 QC 失败";
                    foreach (var w in info.Warnings) log(LogLevel.Warn, Path.GetFileName(qc) + "：" + w);
                }
                else if (info.ToolBlockBroken)
                {
                    qp.Action = BatchQcAction.Error;
                    qp.Reason = "工具标记块不完整（缺结束标记），请手动检查：" +
                                string.Join(", ", info.ToolBlockBrokenFiles.Select(Path.GetFileName).ToArray());
                }
                else if (info.HasForeignLod && !options.Force && options.OutputMode != OutputMode.CollectionOnly)
                {
                    qp.Action = BatchQcAction.Skip;
                    qp.Reason = "已有手写 $lod 或 $shadowlod（在 " + string.Join(", ", info.ForeignLodFiles.Select(Path.GetFileName).ToArray()) +
                                "）；要覆盖请在高级设置里勾选「覆盖手写 $lod」";
                }
                else
                {
                    qp.Action = BatchQcAction.Process;
                }
                if (info.FilesRead > 1 && qp.Action == BatchQcAction.Process)
                    log(LogLevel.Detail, "  " + Path.GetFileName(qc) + " → 展开 " + info.FilesRead + " 个文件，" + info.Studios.Count + " 个 studio 条目");
                qcPlans.Add(qp);
                byPath[FileUtil.Normalize(qc)] = qp;
                if (!synthetic && plan.PendingWrappers.Any(p => Path.GetFileName(p.Key).Equals(Path.GetFileName(qc), StringComparison.OrdinalIgnoreCase)))
                    qp.IsWrapperQc = false;
            }

            // 剔除被别的根 QC include 的 QC
            PruneIncludedRoots(qcPlans, byPath, log);

            // ── 3. 收集模型 + 全局去重 ──
            observer.Stage(BatchStage.Planning, "── 3/5 收集模型并去重 ──");
            var models = new Dictionary<string, PlannedModel>(StringComparer.OrdinalIgnoreCase);
            var modelOrder = new List<PlannedModel>();
            int skipMissing = 0, skipNonModel = 0, skipInnode = 0, skipFallback = 0, skipMain = 0;

            foreach (var qp in qcPlans)
            {
                if (qp.Action != BatchQcAction.Process || qp.Info == null) continue;
                qp.FacialModels = QcWriter.FacialStudioPaths(qp.Info);
                bool skipMainForThisQc = options.SkipMainModel &&
                                         (options.SkipMainQcPaths == null || options.SkipMainQcPaths.Any(x => PathsEqual(x, qp.QcPath)));
                foreach (var s in qp.Info.Studios)
                {
                    if (skipMainForThisQc && s.IsMainModel) { skipMain++; continue; }
                    string why = QcPathResolver.IneligibleReason(s);
                    bool retainedNode = options.Automatic && why == "innode";
                    if (retainedNode) skipInnode++;
                    if (why != null && !retainedNode)
                    {
                        if (options.Automatic && (why == "missing" || why == "fallback" || why == "nonglb"))
                        {
                            qp.Action = BatchQcAction.Error;
                            qp.Reason = "无法安全解析参考模型 " + s.RawFile + "（" + why + "）";
                        }
                        switch (why)
                        {
                            case "fallback": skipFallback++; break;
                            case "missing": skipMissing++; break;
                            case "nonglb": skipNonModel++; break;
                            default: skipInnode++; break;
                        }
                        continue;
                    }
                    PlannedModel pm;
                    if (!models.TryGetValue(s.ResolvedPath, out pm))
                    {
                        pm = new PlannedModel
                        {
                            Path = s.ResolvedPath,
                            BaseName = Path.GetFileNameWithoutExtension(s.ResolvedPath),
                            Dir = Path.GetDirectoryName(s.ResolvedPath),
                            Size = SafeSize(s.ResolvedPath)
                        };
                        models[s.ResolvedPath] = pm;
                        modelOrder.Add(pm);
                    }
                    pm.Entries.Add(s);
                    if (!pm.RootQcs.Contains(qp.QcPath, StringComparer.OrdinalIgnoreCase)) pm.RootQcs.Add(qp.QcPath);
                    if (!qp.Models.Contains(pm)) qp.Models.Add(pm);
                }
            }

            log(LogLevel.Info, "唯一模型文件：" + modelOrder.Count + " 个");
            if (skipMissing > 0) log(LogLevel.Warn, skipMissing + " 个 studio 指向的文件不存在（跳过）");
            if (skipFallback > 0) log(LogLevel.Warn, skipFallback + " 个 studio 只按文件名在别处找到同名文件，为安全起见未处理");
            if (skipNonModel > 0) log(LogLevel.Warn, skipNonModel + " 个 studio 格式不受支持（支持 GLB、SMD、DMX）");
            if (skipInnode > 0) log(LogLevel.Info, options.Automatic
                ? "含 innode 的条目沿用原模型并计入自动估算：" + skipInnode + " 个"
                : "含 innode 的条目跳过 " + skipInnode + " 个（需要拆分，批量不做）");
            if (skipMain > 0) log(LogLevel.Info, "已按设置跳过主模型条目 " + skipMain + " 个");

            // ── 4. 面数统计 + 生成任务 ──
            observer.Stage(BatchStage.Planning, "── 4/5 统计面数并生成任务 ──");
            var dstSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keepOriginalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in (options.KeepOriginalModels ?? "").Split('|'))
            {
                string path = entry.Trim();
                if (path.Length == 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
                keepOriginalPaths.Add(FileUtil.Normalize(Path.Combine(plan.RootDir ?? "", path)));
            }
            long totalSource = 0;
            int keptForFacial = 0;
            int jobCount = 0;
            foreach (var pm in modelOrder)
            {
                if (cancel.IsCancellationRequested) return;
                try
                {
                    if (options.Automatic) pm.SourceWriteTimeUtc = File.GetLastWriteTimeUtc(pm.Path);
                    if (IsSmd(pm.Path))
                    {
                        pm.IsSmd = true;
                        var smd = SmdCodec.Parse(pm.Path);
                        pm.Triangles = smd.TriangleCount;
                        pm.SmdSource = smd;
                        if (options.Automatic)
                        {
                            if (smd.Warnings.Count > 0) throw new InvalidDataException(smd.Warnings[0]);
                            pm.Extent = AutomaticLod.Extent(smd);
                        }
                    }
                    else if (string.Equals(Path.GetExtension(pm.Path), ".dmx", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            pm.DmxSource = DmxLodSource.Read(pm.Path);
                            pm.Triangles = pm.DmxSource.TriangleCount;
                            if (options.Automatic) pm.Extent = pm.DmxSource.Extent;
                        }
                        catch (NotSupportedException ex)
                        {
                            pm.SkipReason = ex.Message;
                            if (options.Automatic)
                            {
                                pm.Triangles = DmxLodSource.CountReferenceTriangles(pm.Path);
                                foreach (string q in pm.RootQcs)
                                    if (byPath[q].IsStandalone) { byPath[q].Action = BatchQcAction.Error; byPath[q].Reason = ex.Message; }
                            }
                            log(LogLevel.Info, pm.BaseName + "：" + pm.SkipReason + "，沿用源网格");
                            continue;
                        }
                    }
                    else
                    {
                        pm.Triangles = GlbCodec.CountTriangles(pm.Path);
                        if (options.Automatic)
                        {
                            var glb = GlbCodec.Read(pm.Path);
                            pm.Extent = AutomaticLod.GlbExtent(glb);
                            foreach (var entry in pm.Entries.Where(e => !string.IsNullOrEmpty(e.Innnode)))
                                entry.FixedTriangleCount = AutomaticLod.NodeTriangles(glb, entry.Innnode);
                        }
                    }
                }
                catch (Exception ex)
                {
                    pm.ReadError = ex.Message;
                    foreach (var q in pm.RootQcs)
                    {
                        BatchQcPlan qp;
                        if (byPath.TryGetValue(FileUtil.Normalize(q), out qp)) { qp.Action = BatchQcAction.Error; qp.Reason = "读取模型失败 " + pm.BaseName + "：" + ex.Message; }
                    }
                    log(LogLevel.Error, "读取模型失败 " + pm.BaseName + "：" + ex.Message);
                    continue;
                }

                if (options.Automatic && pm.Entries.Any(e => !string.IsNullOrEmpty(e.Innnode)))
                {
                    pm.SkipReason = "保留带 innode 子节点的模型";
                    continue;
                }
                if (keepOriginalPaths.Contains(pm.Path))
                {
                    pm.SkipReason = "保留指定的原网格";
                    continue;
                }
                if (!options.NoFacial && pm.RootQcs.Any(q => byPath[q].FacialModels.Contains(pm.Path)))
                {
                    pm.SkipReason = "保留 QC 表情模型的原始顶点";
                    keptForFacial++;
                    continue;
                }
                if (pm.Triangles < options.MinTriangles)
                {
                    pm.SkipReason = "面数 " + pm.Triangles + " 低于门槛 " + options.MinTriangles + "，不需要 LOD";
                    continue;
                }
                totalSource += pm.Triangles;
            }

            if (options.Automatic)
            {
                foreach (var q in qcPlans.Where(x => x.Action == BatchQcAction.Process))
                {
                    q.SourceTriangles = AutomaticLod.CountVisible(q, m => m.Triangles);
                    q.Levels = AutomaticLod.Plan(q.SourceTriangles, options.AutoTargetTriangles,
                        options.GenerateShadowLod ? QcWriter.MaxVisibleLodsWithShadow : LodPlan.MaxLevels);
                }
                planLevels = qcPlans.Where(q => q.Levels != null).OrderByDescending(q => q.Levels.Count)
                    .Select(q => q.Levels).FirstOrDefault() ?? new LodPlan();
                plan.Plan = planLevels;
                totalSource = modelOrder.Sum(m => (long)m.Triangles);
            }

            foreach (var pm in modelOrder.Where(m => m.SkipReason == null && m.ReadError == null))
            {
                if (cancel.IsCancellationRequested) return;
                var modelLevels = options.Automatic
                    ? pm.RootQcs.Select(q => byPath[q]).Where(q => q.Levels != null)
                        .OrderByDescending(q => q.SourceTriangles).Select(q => q.Levels).FirstOrDefault()
                    : planLevels;
                if (modelLevels == null) continue;
                string ext = Path.GetExtension(pm.Path).ToLowerInvariant();
                var roots = pm.RootQcs.Select(q => byPath[q]).ToList();
                for (int li = 0; li < modelLevels.Count; li++)
                {
                    int level = li + 1;
                    if (!roots.Any(q => (q.Levels ?? planLevels).Count >= level && q.CanReplaceAtLevel(pm, level, options)))
                        continue;
                    string finalPath = Path.Combine(pm.Dir, pm.BaseName + "_lod" + level + ext);
                    if (!dstSeen.Add(finalPath))
                    {
                        log(LogLevel.Warn, "输出路径重复，跳过：" + finalPath);
                        continue;
                    }
                    pm.Jobs.Add(new ReductionJob
                    {
                        Source = pm.Path,
                        Destination = finalPath,
                        Level = level,
                        Ratio = modelLevels.Levels[li].Ratio,
                        Distance = modelLevels.Levels[li].Distance,
                        Weight = pm.Size
                    });
                    jobCount++;
                }
                if (options.GenerateShadowLod && options.ShadowLodMode == ShadowLodMode.Dedicated && modelLevels.Count > 0)
                {
                    string finalPath = Path.Combine(pm.Dir, pm.BaseName + "_shadowlod" + ext);
                    if (!dstSeen.Add(finalPath))
                    {
                        log(LogLevel.Warn, "输出路径重复，跳过：" + finalPath);
                        continue;
                    }
                    pm.ShadowJob = new ReductionJob
                    {
                        Source = pm.Path,
                        Destination = finalPath,
                        Ratio = options.ShadowLodRatio,
                        Distance = 80,
                        ShadowMode = true,
                        Weight = pm.Size
                    };
                    jobCount++;
                }
            }
            if (options.Automatic)
                foreach (var q in qcPlans.Where(x => x.Levels != null && x.SourceTriangles > 0))
                    for (int i = 0; i < q.Levels.Count; i++)
                    {
                        long target = AutomaticLod.CountVisible(q, m =>
                        {
                            var job = q.CanReplaceAtLevel(m, i + 1, options) ? m.Jobs.Find(j => j.Level == i + 1) : null;
                            return job != null ? (int)Math.Ceiling(m.Triangles * job.Ratio) : m.Triangles;
                        });
                        q.Levels.Levels[i].Ratio = target / (double)q.SourceTriangles;
                    }
            plan.Models = modelOrder;
            plan.JobCount = jobCount;
            plan.TotalSourceTriangles = totalSource;
            log(LogLevel.Info, "要做 LOD 的模型：" + modelOrder.Count(x => x.Jobs.Count > 0 || x.ShadowJob != null) + " 个（原始共 " + totalSource + " 面）");
            int shadowJobs = modelOrder.Count(m => m.ShadowJob != null);
            string shadowTasks = !options.GenerateShadowLod ? "" : options.ShadowLodMode == ShadowLodMode.ReuseExistingLod
                ? "，阴影模式为复用（0 个独立阴影任务）"
                : "，" + shadowJobs + " 个独立阴影任务";
            log(LogLevel.Info, "减面任务：" + (jobCount - shadowJobs) + " 个可见 LOD 任务" + shadowTasks);
            int smallSkip = modelOrder.Count(x => x.SkipReason != null && x.Triangles < options.MinTriangles &&
                                              !keepOriginalPaths.Contains(x.Path) &&
                                              (options.NoFacial || !x.Entries.Any(e => e.HasFacial)));
            int keepCount = modelOrder.Count(x => x.SkipReason != null && keepOriginalPaths.Contains(x.Path));
            if (keepCount > 0) log(LogLevel.Info, "按保留清单沿用原网格：" + keepCount + " 个");
            if (keptForFacial > 0) log(LogLevel.Info, "为保留 QC 表情沿用原网格：" + keptForFacial + " 个");
            if (smallSkip > 0) log(LogLevel.Info, "面数 < " + options.MinTriangles + " 的小部件跳过 LOD：" + smallSkip + " 个");

            // ── 5. 汇总每个 QC 的动作 ──
            observer.Stage(BatchStage.Planning, "── 5/5 汇总计划 ──");
            foreach (var qp in qcPlans)
            {
                if (qp.Action != BatchQcAction.Process) continue;
                int maxLevel = (qp.Levels ?? planLevels).Count;
                bool hasJobs = qp.Models.Any(m => qp.VisibleJobs(m, maxLevel, options).Any() || qp.UsesShadowJob(m, options));
                if (qp.Models.Count == 0)
                {
                    qp.Action = BatchQcAction.Skip;
                    qp.Reason = qp.Info != null && qp.Info.Studios.Count == 0
                        ? "没有找到任何 studio 条目（无可处理模型）"
                        : "无可处理模型（文件缺失、格式不支持、带 innode 或只按文件名在别处找到）";
                }
                else if (!hasJobs)
                {
                    qp.Action = BatchQcAction.Skip;
                    qp.Reason = options.Automatic && qp.Levels.Count == 0
                        ? "可见组合不超过 " + Math.Max(1000, options.AutoTargetTriangles).ToString("N0", CultureInfo.InvariantCulture) + " 三角面，无需 LOD"
                        : qp.Models.All(m => keepOriginalPaths.Contains(m.Path))
                        ? "已选择保留原网格"
                        : "模型均已保留原网格或低于面数门槛 " + options.MinTriangles;
                }
            }

            foreach (var qp in qcPlans)
            {
                if (qp.Action == BatchQcAction.Skip) log(LogLevel.Info, "  · " + Path.GetFileName(qp.QcPath) + " → 跳过：" + qp.Reason);
                else if (qp.Action == BatchQcAction.Error) log(LogLevel.Error, "  ✘ " + Path.GetFileName(qp.QcPath) + "：" + qp.Reason);
            }

            plan.QcPlans = qcPlans;
            log(LogLevel.Info, "计划：" + plan.ProcessCount + " 个 QC 会处理 · " + plan.SkipCount + " 个跳过 · " + plan.ErrorCount + " 个错误");
        }

        private static void AddQc(TaskExpansion exp, List<string> order, HashSet<string> seen, string qcPath, Action<LogLevel, string> log)
        {
            string full;
            try { full = Path.GetFullPath(qcPath); } catch { return; }
            if (!seen.Add(full)) return;
            order.Add(full);
            exp.QcPaths.Add(full);
        }

        private static QcInfo MakeWrapperInfo(string wrapperQcPath, string modelPath)
        {
            string fileName = Path.GetFileName(modelPath);
            string baseName = Path.GetFileNameWithoutExtension(modelPath);
            var info = new QcInfo
            {
                QcPath = wrapperQcPath,
                QcDir = Path.GetDirectoryName(wrapperQcPath),
                ModelName = "props/" + baseName + ".mdl",
                TextEncoding = SystemAnsi.Encoding,
                HasBom = false,
                NewLine = "\r\n",
                FilesRead = 1
            };
            info.Studios.Add(new StudioRef
            {
                RawFile = fileName,
                Name = fileName,
                Bodygroup = baseName,
                BodygroupIndex = 0,
                IsMainModel = false,
                DeclaredInQc = wrapperQcPath,
                DeclaredLine = 6,
                ResolvedPath = Path.GetFullPath(modelPath),
                Exists = true
            });
            return info;
        }

        private static void PruneIncludedRoots(List<BatchQcPlan> qcPlans, Dictionary<string, BatchQcPlan> byPath, Action<LogLevel, string> log)
        {
            var remove = new List<BatchQcPlan>();
            foreach (var r in qcPlans)
            {
                if (r.Info == null) continue;
                foreach (var q in qcPlans)
                {
                    if (ReferenceEquals(q, r) || q.Info == null) continue;
                    if (!q.Info.IncludedFiles.Any(f => PathsEqual(f, r.QcPath))) continue;
                    bool mutual = r.Info.IncludedFiles.Any(f => PathsEqual(f, q.QcPath));
                    if (mutual && qcPlans.IndexOf(r) < qcPlans.IndexOf(q)) continue;
                    remove.Add(r);
                    log(LogLevel.Info, Path.GetFileName(r.QcPath) + " 被 " + Path.GetFileName(q.QcPath) +
                                        " $include，不是独立的根 QC，已从清单剔除（它随前者一起处理）");
                    break;
                }
            }
            foreach (var r in remove) qcPlans.Remove(r);
        }

        private static bool IsSmd(string path) { return string.Equals(Path.GetExtension(path), ".smd", StringComparison.OrdinalIgnoreCase); }
        private static bool PathsEqual(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }
        private static string SafeDir(string path) { try { return Path.GetDirectoryName(Path.GetFullPath(path)); } catch { return "."; } }
        private static long SafeSize(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }
    }
}
