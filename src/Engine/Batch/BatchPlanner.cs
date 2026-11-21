using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace LodStudio.Engine
{
    /// <summary>
    /// 批量计划器：把用户任务展开为 QC 清单，解析 QC，全局去重，统计面数，生成减面任务。
    /// **只读**：不写任何文件、不启动 Blender、不修改 QC。
    /// </summary>
    public static class BatchPlanner
    {
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
                PresetName = string.IsNullOrEmpty(options.PresetName) ? "自定义" : options.PresetName
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
            string perf = planLevels.Validate();
            if (perf != null) log(LogLevel.Error, "LOD 参数有误：" + perf);

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
                            if (loose.Count == 0)
                            {
                                exp.Error = "没有 QC，也没有 .glb / .smd";
                                log(LogLevel.Warn, string.Format("[{0}] {1} → {2}", "工程", Path.GetFileName(path.TrimEnd('\\')), exp.Error));
                                continue;
                            }
                            log(LogLevel.Info, "没有 QC：为 " + loose.Count + " 个模型各建一个包装 QC");
                            foreach (var m in loose)
                            {
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
                if (wrapperModels.TryGetValue(qc, out modelForWrapper) && !File.Exists(qc))
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
                if (info.ReadFailed)
                {
                    qp.Action = BatchQcAction.Error;
                    qp.Reason = "读取 QC 失败";
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
                    qp.Reason = "已有手写 $lod（在 " + string.Join(", ", info.ForeignLodFiles.Select(Path.GetFileName).ToArray()) +
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
                bool skipMainForThisQc = options.SkipMainModel && options.SkipMainQcPaths != null &&
                                         options.SkipMainQcPaths.Any(x => PathsEqual(x, qp.QcPath));
                foreach (var s in qp.Info.Studios)
                {
                    if (skipMainForThisQc && s.IsMainModel) { skipMain++; continue; }
                    string why = Ineligible(s);
                    if (why != null)
                    {
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
            if (skipNonModel > 0) log(LogLevel.Warn, skipNonModel + " 个 studio 既不是 .glb 也不是 .smd（跳过）");
            if (skipInnode > 0) log(LogLevel.Info, "含 innode 的条目跳过 " + skipInnode + " 个（需要拆分，批量不做）");
            if (skipMain > 0) log(LogLevel.Info, "已按设置跳过主模型条目 " + skipMain + " 个");

            // ── 4. 面数统计 + 生成任务 ──
            observer.Stage(BatchStage.Planning, "── 4/5 统计面数并生成任务 ──");
            var dstSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalSource = 0;
            int jobCount = 0;
            foreach (var pm in modelOrder)
            {
                if (cancel.IsCancellationRequested) return;
                try
                {
                    if (IsSmd(pm.Path))
                    {
                        pm.IsSmd = true;
                        var smd = SmdCodec.Parse(pm.Path);
                        pm.Triangles = smd.TriangleCount;
                        pm.SmdSource = smd;
                    }
                    else
                    {
                        pm.Triangles = GlbCodec.CountTriangles(pm.Path);
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

                if (pm.Triangles < options.MinTriangles)
                {
                    pm.SkipReason = "面数 " + pm.Triangles + " 低于门槛 " + options.MinTriangles + "，不需要 LOD";
                    continue;
                }
                totalSource += pm.Triangles;
                for (int li = 0; li < planLevels.Count; li++)
                {
                    int level = li + 1;
                    string ext = pm.IsSmd ? ".smd" : ".glb";
                    string finalPath = Path.Combine(pm.Dir, pm.BaseName + "_lod" + level + ext);
                    string srcPath = pm.Path;
                    string dstPath = finalPath;
                    if (pm.IsSmd)
                    {
                        // 临时文件名必须带路径哈希：不同目录下同名 SMD 不能互相覆盖（否则第二个模型会被静默跳过）
                        string tempKey = pm.BaseName + "_" + FileUtil.ShortPathHash(pm.Path);
                        srcPath = Path.Combine(AppInfo.TempSmdDir, tempKey + "_src.glb");
                        dstPath = Path.Combine(AppInfo.TempSmdDir, tempKey + "_lod" + level + ".glb");
                        finalPath = Path.Combine(pm.Dir, pm.BaseName + "_lod" + level + ".smd");
                    }
                    if (!dstSeen.Add(dstPath))
                    {
                        log(LogLevel.Warn, "输出路径重复，跳过：" + dstPath);
                        continue;
                    }
                    try { FileUtil.EnsureDir(Path.GetDirectoryName(dstPath)); } catch { }
                    pm.Jobs.Add(new BlenderJobSpec
                    {
                        Source = srcPath,
                        Destination = dstPath,
                        Ratio = planLevels.Levels[li].Ratio,
                        ShrinkInner = 0.0009 * (1.0 + 0.15 * li),
                        MinFaces = 12,
                        GuardPercent = 35.0,
                        QualityOverride = planLevels.Levels[li].Ratio >= 0.99 ? (bool?)false : null,
                        Weight = pm.Size
                    });
                    jobCount++;
                }
            }
            plan.Models = modelOrder;
            plan.JobCount = jobCount;
            plan.TotalSourceTriangles = totalSource;
            log(LogLevel.Info, "要做 LOD 的模型：" + modelOrder.Count(x => x.Jobs.Count > 0) + " 个（原始共 " + totalSource + " 面）");
            log(LogLevel.Info, "减面任务：" + jobCount + " 个（含 " + modelOrder.Count(x => x.Jobs.Count > 0) + " 个模型 × " + planLevels.Count + " 级）");
            int smallSkip = modelOrder.Count(x => x.SkipReason != null);
            if (smallSkip > 0) log(LogLevel.Info, "面数 < " + options.MinTriangles + " 的小部件跳过 LOD：" + smallSkip + " 个");

            // ── 5. 汇总每个 QC 的动作 ──
            observer.Stage(BatchStage.Planning, "── 5/5 汇总计划 ──");
            foreach (var qp in qcPlans)
            {
                if (qp.Action != BatchQcAction.Process) continue;
                var withJobs = qp.Models.Where(m => m.Jobs.Count > 0).ToList();
                if (qp.Models.Count == 0)
                {
                    qp.Action = BatchQcAction.Skip;
                    qp.Reason = qp.Info != null && qp.Info.Studios.Count == 0
                        ? "没有找到任何 studio 条目（无可处理模型）"
                        : "无可处理模型（文件缺失 / 非 glb 或 smd / 带 innode / 只按文件名在别处找到）";
                }
                else if (withJobs.Count == 0)
                {
                    qp.Action = BatchQcAction.Skip;
                    qp.Reason = "模型面数都低于 " + options.MinTriangles + "，不需要 LOD";
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

        private static string Ineligible(StudioRef s)
        {
            if (s.Fallback) return "fallback";
            if (!s.Exists || string.IsNullOrEmpty(s.ResolvedPath)) return "missing";
            string ext = Path.GetExtension(s.ResolvedPath).ToLowerInvariant();
            if (ext != ".glb" && ext != ".smd") return "nonglb";
            if (!string.IsNullOrEmpty(s.Innnode)) return "innode";
            return null;
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
