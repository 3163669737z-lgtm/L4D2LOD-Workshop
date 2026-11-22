using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>一次 QC 写入的结果。</summary>
    public sealed class QcWriteResult
    {
        public bool Success;
        public string Error;
        public List<string> WrittenFiles = new List<string>();
        public int DisabledForeignLodBlocks;
        /// <summary>内容没有变化（幂等重跑时为 true，不算失败）</summary>
        public bool NoChange;
        /// <summary>本次 $lod 工具块写入（或已存在）的文件；通常等于根 QC，模型声明在 include 文件时是那个 include 文件。</summary>
        public string HostFile;
    }

    /// <summary>
    /// QC 写入：幂等注入/移除工具块、Force 停用旧 $lod、备份、原子写与失败回滚。
    /// 实现负责人：QC 模块。契约不可变。
    ///
    /// **工具块位置（宿主文件）**：真实工程常把 $model/$bodygroup 写在被 $include 的子文件（如 main.qc）里，
    /// 根 QC 只做 include。此时 $lod 工具块必须写在**声明这些模型的文件**里，根 QC 保持原样，
    /// 否则编译展开后会出现两套 $lod（studiomdl 会把 LOD 级数叠加，例如 4 级变 7 级）。
    /// 宿主判定见 <see cref="ResolveHostFile"/>：优先模型声明文件唯一 → 已有工具块文件唯一 → 根 QC。
    ///
    /// 安全硬约束（顺序不可变，任何一步失败都不改文件）：
    ///   1) force 时注释 include 文件与根 QC 里的手写 $lod/$shadowlod；
    ///   2) 擦掉各文件里的旧工具块（缺结束标记 → 拒绝修改）；
    ///   3) 在宿主文件末尾追加新工具块（与前文之间空一行）；
    ///   4) 内容没变 → 不写盘、不备份（幂等）；
    ///   5) 先把所有待写文件备份成功，再动第一个文件；
    ///   6) 逐个原子写；任何一个失败 → 把已写的恢复成读入时内容；
    ///   7) 汇总停用块数与写入列表。
    /// </summary>
    public static class QcWriter
    {
        /// <summary>工具标记块缺结束标记时的可读错误（与旧版文案一致，便于用户/日志检索）。</summary>
        public const string BrokenBlockMessage = "工具标记块不完整（缺结束标记），请手动检查";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private sealed class PendingWrite
        {
            public QcTextFile File;
            public string NewText;
            public int Disabled;
            public bool Written;
        }

        /// <summary>
        /// 修改一个根 QC（force 时也含带手写 $lod 的 include 文件）：
        ///  - 工具块写入「宿主文件」：模型声明文件唯一时写那个 include 文件（根 QC 不动），否则写根 QC；
        ///  - 其它文件（含根 QC）里残留的旧工具块会被清理，保证展开后只有一套 $lod；
        ///  - force=true：工具块之外的 $lod/$shadowlod 整块注释停用；
        ///  - toolBody 非空 → 宿主文件末尾追加"空行+开始标记+body+结束标记"，为空 → 只移除所有旧块；
        ///  - 每个要改的文件先备份（备份失败 → 整个 QC 不改）；
        ///  - 写失败时把已写文件恢复成原内容；内容没变不写盘（NoChange=true）。
        /// hostFile 为空时按 <see cref="ResolveHostFile"/> 自动判定；独立 _LOD.qc 模式由调用方传根 QC。
        /// 不抛异常：所有失败都写在 QcWriteResult.Error（可直接展示给用户）。
        /// </summary>
        public static QcWriteResult Apply(QcInfo info, string toolBody, bool force, QcBackupRequest backup)
        {
            return Apply(info, toolBody, force, backup, null);
        }

        /// <summary>同上；hostFile 非空时强制把工具块写在该文件（独立 _LOD.qc 模式传根 QC）。</summary>
        public static QcWriteResult Apply(QcInfo info, string toolBody, bool force, QcBackupRequest backup,
                                          string hostFile)
        {
            var result = new QcWriteResult();
            try { ApplyCore(info, toolBody, force, backup, hostFile, result); }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = "修改 QC 失败: " + ex.Message;
            }
            return result;
        }

        /// <summary>
        /// 判定 $lod 工具块该写在哪个文件：
        ///   1) 可参与块生成的模型条目只在一个文件里声明（真实工程常见：根 QC 只 include，main.qc 写 $bodygroup）
        ///      → 就写那个文件；
        ///   2) 已有工具块的文件唯一 → 沿用旧位置（接管旧工程，避免搬家）；
        ///   3) 其它情况（模型分散在多个文件 / 多个旧块）→ 根 QC（兜底，与旧行为一致）。
        /// skipMainModel=true 时主模型条目不参与判定（与 BuildLodBlock 的过滤保持一致）。
        /// </summary>
        public static string ResolveHostFile(QcInfo info, bool skipMainModel)
        {
            string root = "";
            try { root = Path.GetFullPath(info == null ? "" : (info.QcPath ?? "")); } catch { root = info == null ? "" : (info.QcPath ?? ""); }
            if (info == null) return root;

            var declared = new List<string>();
            if (info.Studios != null)
            {
                foreach (StudioRef s in info.Studios)
                {
                    if (s == null || string.IsNullOrEmpty(s.DeclaredInQc)) continue;
                    if (skipMainModel && s.IsMainModel) continue;
                    if (QcPathResolver.IneligibleReason(s) != null) continue;   // 不会进 $lod 块
                    string f;
                    try { f = Path.GetFullPath(s.DeclaredInQc); } catch { continue; }
                    if (!ContainsPath(declared, f)) declared.Add(f);
                }
            }
            if (declared.Count == 1) return declared[0];

            if (info.ToolMarkerFiles != null && info.ToolMarkerFiles.Count == 1)
            {
                try { return Path.GetFullPath(info.ToolMarkerFiles[0]); } catch { }
            }
            return root;
        }

        /// <summary>
        /// 生成一个 QC 的 $lod 文本块；没有可写条目时返回 null。
        /// generatedLodFiles：本次真正生成成功的 LOD 文件绝对路径集合（只引用其中存在的级）。
        /// 格式逐字固定（见规格 §6.2）：注释行 → $lod 距离 → { → replacemodel… → （可选 nofacial）→ }；
        /// 数字一律 InvariantCulture；某级没有条目就整级不写。
        /// </summary>
        public static string BuildLodBlock(QcInfo info, LodPlan plan, HashSet<string> generatedLodFiles,
                                           bool skipMainModel, bool noFacial)
        {
            if (info == null || plan == null || plan.Levels == null || plan.Levels.Count == 0) return null;

            HashSet<string> generated = NormalizeGenerated(generatedLodFiles);

            // 每级一个有序替换表：键 = QC 里原样写的字符串（replacemodel 第一参数必须逐字一致）
            int levelCount = plan.Levels.Count;
            var replacements = new List<List<KeyValuePair<string, string>>>(levelCount);
            var keyIndex = new List<Dictionary<string, int>>(levelCount);
            for (int i = 0; i < levelCount; i++)
            {
                replacements.Add(new List<KeyValuePair<string, string>>());
                keyIndex.Add(new Dictionary<string, int>(StringComparer.Ordinal));
            }

            foreach (StudioRef s in info.Studios)
            {
                if (s == null) continue;
                if (skipMainModel && s.IsMainModel) continue;          // 跳过主模型的 QC 不引用主模型
                if (QcPathResolver.IneligibleReason(s) != null) continue;   // fallback / 缺失 / 非 glb-smd / innode

                string dir, baseName, ext;
                try
                {
                    dir = Path.GetDirectoryName(s.ResolvedPath) ?? "";
                    baseName = Path.GetFileNameWithoutExtension(s.ResolvedPath);
                    ext = Path.GetExtension(s.ResolvedPath).ToLowerInvariant();
                }
                catch { continue; }
                if (string.IsNullOrEmpty(baseName)) continue;

                for (int li = 0; li < levelCount; li++)
                {
                    int level = li + 1;
                    string lodFile;
                    try { lodFile = Path.Combine(dir, baseName + "_lod" + level.ToString(Inv) + ext); }
                    catch { continue; }

                    string fullLod;
                    try { fullLod = Path.GetFullPath(lodFile); } catch { fullLod = lodFile; }
                    if (!generated.Contains(fullLod)) continue;        // 这一级没生成 → 让引擎继续用上一级

                    string rel = FileUtil.MakeRelative(info.QcDir, lodFile);
                    AddReplacement(replacements[li], keyIndex[li], s.RawFile ?? "", rel);
                }
            }

            bool any = false;
            for (int i = 0; i < levelCount; i++) if (replacements[i].Count > 0) { any = true; break; }
            if (!any) return null;

            var sb = new StringBuilder();
            for (int li = 0; li < levelCount; li++)
            {
                var list = replacements[li];
                if (list.Count == 0) continue;      // 某级没有条目 → 整级不写

                LodLevel lv = plan.Levels[li];
                int level = li + 1;
                string distance = lv.Distance.ToString("0.###", Inv);
                string percent = (lv.Ratio * 100.0).ToString("0.#", Inv);

                sb.Append("// LOD").Append(level.ToString(Inv))
                  .Append("  切换距离 ").Append(distance)
                  .Append("  目标 ").Append(percent).Append('%')
                  .Append("  条目 ").Append(list.Count.ToString(Inv)).Append('\n');
                sb.Append("$lod ").Append(distance).Append('\n');
                sb.Append("{\n");
                foreach (var kv in list)
                    sb.Append("\treplacemodel \"").Append(kv.Key).Append("\" \"").Append(kv.Value).Append("\"\n");
                if (noFacial) sb.Append("\tnofacial\n");
                sb.Append("}\n");
            }
            return sb.ToString();
        }

        /// <summary>把 body 包进工具标记块（标记由调用方拼接；这里只生成头部注释 + body）。</summary>
        public static string WrapMarker(string body, string presetName, LodPlan plan, bool isIncludeLine)
        {
            var sb = new StringBuilder();
            string summary = plan != null ? plan.Summary() : "（未设置）";
            sb.Append("// 预设: ").Append(presetName ?? "").Append('（').Append(summary).Append('）').Append('\n');
            if (isIncludeLine)
                sb.Append("// $lod 写在旁边那个 _LOD.qc 里，本文件只加一行 include。重新运行会整块替换，不要手工改这一段。\n");
            else
                sb.Append("// 由「L4D2 自动 LOD 工具」生成；重新运行会整块替换，不要手工改这一段。\n");
            sb.Append(body ?? "");
            return sb.ToString();
        }

        /// <summary>独立 QC 路径：&lt;原QC目录&gt;\&lt;QC名&gt;_LOD.qc。</summary>
        public static string SeparateQcPath(string rootQcPath)
        {
            string dir = "";
            string baseName = "";
            try { dir = Path.GetDirectoryName(rootQcPath) ?? ""; } catch { }
            try { baseName = Path.GetFileNameWithoutExtension(rootQcPath) ?? ""; } catch { baseName = rootQcPath ?? ""; }
            return Path.Combine(dir, baseName + "_LOD.qc");
        }

        /// <summary>写独立 _LOD.qc（沿用根 QC 的编码/BOM/换行）；成功返回 null，失败返回错误文本。</summary>
        public static string WriteSeparateQc(string path, QcInfo info, string presetName, LodPlan plan, string block)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return "独立 QC 路径为空。";
                if (info == null) return "QC 信息为空，无法生成独立 QC。";

                string nl = string.IsNullOrEmpty(info.NewLine) ? "\r\n" : info.NewLine;
                var sb = new StringBuilder();
                sb.Append("// L4D2 自动 LOD 工具 —— 独立 $lod 文件（被 ")
                  .Append(Path.GetFileName(info.QcPath ?? ""))
                  .Append(" 通过 $include 引用）").Append(nl);
                sb.Append("// 生成时间: ")
                  .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv)).Append(nl);
                sb.Append("// 涉及模型: ")
                  .Append(string.IsNullOrEmpty(info.ModelName) ? "(未声明)" : info.ModelName).Append(nl);
                sb.Append("// 预设: ").Append(presetName ?? "").Append(nl);
                sb.Append("// 参数: ").Append(plan != null ? plan.Summary() : "（未设置）").Append(nl);
                sb.Append("//").Append(nl);
                sb.Append("// 不想用 LOD 时：把原 QC 末尾那一行 $include \"")
                  .Append(Path.GetFileName(path)).Append("\" 删掉即可，").Append(nl);
                sb.Append("// 或者直接删掉这个文件（原 QC 会编译失败，记得把 include 行也删）。").Append(nl);
                sb.Append(nl);

                string body = (block ?? "").Replace("\r\n", "\n").Replace("\n", nl);
                sb.Append(body);

                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                QcTextFile.WriteAllText(path, sb.ToString(), info.TextEncoding, info.HasBom);
                return null;
            }
            catch (Exception ex)
            {
                return "写独立 QC 失败: " + ex.Message;
            }
        }

        /// <summary>
        /// 备份文件（.lodbak 永不覆盖规则）；成功返回备份路径，失败返回 null + error。
        /// request 为空或 CentralBaseDir 为空 = 备份在原文件旁；否则集中备份到
        /// CentralBaseDir 下并按文件相对 ProjectRoot 的路径镜像子目录。
        /// 是否"启用备份"由调用方（Apply）判断；本方法只要被调用就一定真的备份。
        /// </summary>
        public static string BackupFile(string path, QcBackupRequest request, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(path)) { error = "文件路径为空"; return null; }
                string full = Path.GetFullPath(path);
                if (!File.Exists(full)) { error = "文件不存在: " + full; return null; }

                string targetDir;
                if (request == null || string.IsNullOrEmpty(request.CentralBaseDir))
                {
                    targetDir = Path.GetDirectoryName(full) ?? "";
                }
                else
                {
                    string relDir = null;
                    if (!string.IsNullOrEmpty(request.ProjectRoot))
                    {
                        string rel = FileUtil.RelativeDisplay(request.ProjectRoot, full);
                        if (!string.IsNullOrEmpty(rel) && !Path.IsPathRooted(rel)) relDir = Path.GetDirectoryName(rel);
                    }
                    targetDir = string.IsNullOrEmpty(relDir)
                        ? request.CentralBaseDir
                        : Path.Combine(request.CentralBaseDir, relDir);
                    try { Directory.CreateDirectory(targetDir); } catch { }
                }

                string baseName = Path.GetFileName(full);
                string bak = Path.Combine(targetDir, baseName + ".lodbak");
                if (File.Exists(bak))
                {
                    // .lodbak 只保留"第一次修改前的原件"；后续每次修改的版本另存时间戳副本，绝不覆盖
                    string stamp = DateTime.Now.ToString("MMddHHmmss", Inv);
                    bak = Path.Combine(targetDir, baseName + ".lodbak_" + stamp);
                    int k = 2;
                    while (File.Exists(bak))
                    {
                        bak = Path.Combine(targetDir, baseName + ".lodbak_" + stamp + "_" + k.ToString(Inv));
                        k++;
                    }
                }

                File.Copy(full, bak, false);
                if (!File.Exists(bak)) { error = "备份文件未生成"; return null; }
                return bak;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        // ───────────────────────────── 私有实现 ─────────────────────────────

        private static void ApplyCore(QcInfo info, string toolBody, bool force, QcBackupRequest backup,
                                      string hostFile, QcWriteResult result)
        {
            if (info == null) { Fail(result, "QC 信息为空，无法修改。"); return; }
            if (info.ReadFailed) { Fail(result, "读取 QC 失败，未做任何修改。"); return; }
            if (info.ToolBlockBroken)
            {
                Fail(result, BrokenBlockMessage + "（" + JoinFileNames(info.ToolBlockBrokenFiles) + "）");
                return;
            }

            string root;
            try { root = Path.GetFullPath(info.QcPath ?? ""); }
            catch (Exception ex) { Fail(result, "QC 路径无效: " + ex.Message); return; }
            if (!File.Exists(root)) { Fail(result, "QC 文件不存在: " + root); return; }

            // 1) 宿主文件：$lod 工具块写这里。模型声明文件唯一时是那个 include 文件（真实工程 main.qc 布局），
            //    根 QC 只 include、不会被改动；其它情况写根 QC（与旧行为一致）。
            string host;
            if (!string.IsNullOrEmpty(hostFile))
            {
                try { host = Path.GetFullPath(hostFile); } catch { host = root; }
            }
            else
            {
                host = ResolveHostFile(info, false);
            }
            if (string.IsNullOrEmpty(host)) host = root;
            result.HostFile = host;

            // 2) 候选文件：宿主 + 各含标记块的文件 + force 时的各手写 $lod 文件 + 根 QC（根固定最后写）
            var candidates = new List<string>();
            AddCandidateFile(candidates, host);
            if (info.ToolMarkerFiles != null)
                foreach (string f in info.ToolMarkerFiles) AddCandidateFile(candidates, f);
            if (force && info.ForeignLodFiles != null)
                foreach (string f in info.ForeignLodFiles) AddCandidateFile(candidates, f);
            // 根 QC 固定最后写（与旧实现"include 先写、根最后写"的顺序一致）
            candidates.RemoveAll(delegate (string f) { return PathsEqual(f, root); });
            candidates.Add(root);

            // 3) 逐文件算出最终文本：force 注释手写 $lod → 擦旧工具块 → 宿主追加新块；有变化才排队写
            var writes = new List<PendingWrite>();
            foreach (string file in candidates)
            {
                bool isHost = PathsEqual(file, host);
                QcTextFile textFile;
                try { textFile = QcTextFile.Read(file); }
                catch (Exception ex)
                {
                    Fail(result, "读取 QC 文件 " + Path.GetFileName(file) + " 失败，未做任何修改: " + ex.Message);
                    return;
                }

                string text = textFile.Text;
                int disabled = 0;
                if (force)
                {
                    // 手写 $lod 可能落在根 QC，也可能落在 include 文件里
                    text = QcLexer.CommentOutForeignLod(text, out disabled);
                    if (disabled < 0) { Fail(result, BrokenBlockMessage + "（" + Path.GetFileName(file) + "）"); return; }
                }

                // 旧工具块可能在根 QC，也可能在 include 文件里（旧版工具的布局）：全部擦掉，保证展开后只有一套
                bool removedBlock = false;
                if (text.IndexOf(QcLexer.MarkerBegin, StringComparison.Ordinal) >= 0)
                {
                    string withoutBlock;
                    if (!QcLexer.TryRemoveToolBlock(text, out withoutBlock))
                    {
                        Fail(result, BrokenBlockMessage + "（" + Path.GetFileName(file) + "）");
                        return;
                    }
                    // TryRemoveToolBlock 保留原文原始换行（含混合换行）；这里原样使用，绝不全文归一化
                    text = withoutBlock;
                    removedBlock = true;
                }

                string nl = textFile.NewLine == "\r\n" ? "\r\n" : "\n";
                if (removedBlock)
                    text = text.TrimEnd('\r', '\n') + nl;   // 与旧实现一致：移除块后恢复"原文末尾单个换行"
                if (isHost && !string.IsNullOrEmpty(toolBody))
                    text = AppendToolBlock(text, toolBody, nl);

                if (!string.Equals(text, textFile.Text, StringComparison.Ordinal))
                    writes.Add(new PendingWrite { File = textFile, NewText = text, Disabled = disabled });
            }

            // 4) 内容没变 → 不写盘、不备份（幂等重跑）
            if (writes.Count == 0)
            {
                result.Success = true;
                result.NoChange = true;
                return;
            }

            // 5) 预检查：只读文件一律拒绝（在备份之前检查，避免留下无用的 .lodbak）
            foreach (var pending0 in writes)
            {
                try
                {
                    if (File.Exists(pending0.File.FilePath))
                    {
                        FileAttributes attrs = File.GetAttributes(pending0.File.FilePath);
                        if ((attrs & FileAttributes.ReadOnly) != 0)
                        {
                            Fail(result, "文件是只读的，已拒绝修改：" + Path.GetFileName(pending0.File.FilePath) +
                                         "（请先去掉只读属性）");
                            return;
                        }
                    }
                }
                catch { }
            }

            // 5b) 先把所有要改的文件备份成功，再动第一个文件（任何一个失败 → 一个都不改）
            bool backupEnabled = backup == null || backup.Enabled;
            if (backupEnabled)
            {
                foreach (PendingWrite w in writes)
                {
                    string backupError;
                    if (BackupFile(w.File.FilePath, backup, out backupError) == null)
                    {
                        Fail(result, "备份 " + Path.GetFileName(w.File.FilePath) +
                                     " 失败，未做任何修改: " + backupError);
                        return;
                    }
                }
            }

            // 6) 写入（include/声明文件在前、根 QC 最后）；失败时把已写的恢复成读入时内容
            foreach (PendingWrite w in writes)
            {
                try
                {
                    w.File.Write(w.NewText);
                    w.Written = true;
                }
                catch (Exception ex)
                {
                    foreach (PendingWrite done in writes)
                    {
                        if (!done.Written) continue;
                        try { done.File.Write(done.File.Text); } catch { }
                    }
                    Fail(result, "写 " + Path.GetFileName(w.File.FilePath) + " 失败（已回滚其它文件）: " + ex.Message);
                    return;
                }
            }

            // 7) 成功：汇总
            int disabledTotal = 0;
            foreach (PendingWrite w in writes)
            {
                result.WrittenFiles.Add(w.File.FilePath);
                if (w.Disabled > 0) disabledTotal += w.Disabled;
            }
            result.DisabledForeignLodBlocks = disabledTotal;
            result.Success = true;
        }

        private static string AppendToolBlock(string cleaned, string toolBody, string nl)
        {
            var sb = new StringBuilder(cleaned.Length + toolBody.Length + 256);
            sb.Append(cleaned.TrimEnd('\r', '\n'));
            sb.Append(nl).Append(nl);
            sb.Append(QcLexer.MarkerBegin).Append(nl);

            string body = toolBody.Replace("\r\n", "\n").Replace("\n", nl);
            // 块内容必须以换行结束，否则结束标记会粘在最后一行指令后面
            if (!body.EndsWith("\n", StringComparison.Ordinal)) body += nl;
            sb.Append(body);
            sb.Append(QcLexer.MarkerEnd).Append(nl);
            return sb.ToString();
        }

        private static void AddCandidateFile(List<string> list, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string full;
            try { full = Path.GetFullPath(path); } catch { return; }
            if (ContainsPath(list, full)) return;
            list.Add(full);
        }

        private static bool ContainsPath(List<string> list, string path)
        {
            if (list == null || string.IsNullOrEmpty(path)) return false;
            foreach (string x in list) if (PathsEqual(x, path)) return true;
            return false;
        }

        private static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static HashSet<string> NormalizeGenerated(HashSet<string> files)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (files == null) return set;
            foreach (string f in files)
            {
                if (string.IsNullOrEmpty(f)) continue;
                try { set.Add(Path.GetFullPath(f)); }
                catch { set.Add(f); }
            }
            return set;
        }

        private static void AddReplacement(List<KeyValuePair<string, string>> list, Dictionary<string, int> index,
                                           string key, string value)
        {
            int at;
            if (index.TryGetValue(key, out at))
            {
                // 同一个 replacemodel 键出现多次：保留首次出现的位置，值以最后一次为准（与旧版字典语义一致）
                list[at] = new KeyValuePair<string, string>(key, value);
                return;
            }
            index[key] = list.Count;
            list.Add(new KeyValuePair<string, string>(key, value));
        }

        private static string JoinFileNames(List<string> files)
        {
            if (files == null || files.Count == 0) return "?";
            var names = new List<string>(files.Count);
            foreach (string f in files)
            {
                try { names.Add(Path.GetFileName(f)); }
                catch { names.Add(f ?? ""); }
            }
            return string.Join("、", names.ToArray());
        }

        private static void Fail(QcWriteResult result, string error)
        {
            result.Success = false;
            result.Error = error;
        }
    }
}
