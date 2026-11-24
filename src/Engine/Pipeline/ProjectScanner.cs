using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LodStudio.Engine
{
    /// <summary>
    /// 工程扫描：找出"真正会被编译"的根 QC。
    ///
    /// 优先级（与旧版一致，behavior 见 docs 规格 §2）：
    ///   1) 递归解析工程里的 *.bat，取 nekomdl 命令行的 QC 参数 —— 这是用户实际编译的清单；
    ///   2) 没有可用 bat 时，扫描含 $modelname 且没有被其它 QC $include 的 .qc。
    /// 两套扫描都排除本工具生成的附属 QC（_LOD.qc / _autoLOD.qc / _auto.qc / _LOD合集*.qc / 自动LOD_*.qc）
    /// 与备份目录（_LOD备份 / LOD备份）。
    ///
    /// 所有枚举/读取异常都容错：不抛异常，只记日志。
    /// </summary>
    public static class ProjectScanner
    {
        // ───────────────────────────── 公开契约 ─────────────────────────────

        /// <summary>
        /// 找根 QC：优先解析目录内 *.bat 里的 nekomdl 命令行参数；没有 bat 时扫描含 $modelname
        /// 且未被其它 QC $include 的 QC。排除本工具生成的 QC 与备份目录。返回绝对路径列表（去重、稳定顺序）。
        /// </summary>
        public static List<string> FindRootQcs(string rootDir, Action<string> log)
        {
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(rootDir))
            {
                L(log, "[提示] 工程目录为空，无法扫描 QC。");
                return ordered;
            }

            string root;
            try { root = Path.GetFullPath(rootDir); }
            catch (Exception ex)
            {
                L(log, "[提示] 工程目录无效：" + ex.Message);
                return ordered;
            }
            if (!Directory.Exists(root))
            {
                L(log, "[提示] 工程目录不存在：" + root);
                return ordered;
            }

            // ── 第一级：从 .bat 读真实编译清单 ──
            List<string> bats = SafeEnumerateFiles(root, "*.bat", log);
            bats.RemoveAll(IsUnderBackup);
            bats.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (string bat in bats)
            {
                List<string> qcs;
                try { qcs = ParseBat(bat); }
                catch (Exception ex)
                {
                    L(log, "[提示] 解析 .bat 失败（" + Path.GetFileName(bat) + "）：" + ex.Message);
                    continue;
                }
                if (qcs.Count == 0) continue;

                L(log, "  从 " + Path.GetFileName(bat) + " 读到 " + qcs.Count + " 个 QC");
                foreach (string q in qcs)
                {
                    if (IsUnderBackup(q))
                    {
                        L(log, "  忽略备份目录里的 " + Path.GetFileName(q));
                        continue;
                    }
                    if (IsGeneratedQc(q))
                    {
                        L(log, "  忽略本工具生成的 " + Path.GetFileName(q));
                        continue;
                    }
                    if (seen.Add(q)) ordered.Add(q);
                }
            }

            if (ordered.Count > 0)
            {
                L(log, "  使用 .bat 清单：" + ordered.Count + " 个根 QC（已按路径去重）");
                return ordered;
            }

            // ── 第二级：启发式（无可用 bat）──
            L(log, "  没找到带 Nekomdl 命令的 .bat，改为扫描含 $modelname 的 QC");

            List<string> allQc = SafeEnumerateFiles(root, "*.qc", log);
            allQc.RemoveAll(IsUnderBackup);
            allQc.RemoveAll(IsGeneratedQc);
            allQc.Sort(StringComparer.OrdinalIgnoreCase);

            var withModel = new List<string>();
            var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int readFailed = 0;

            foreach (string qc in allQc)
            {
                string text;
                try { text = QcParser.ReadTextSmart(qc); }
                catch (Exception ex)
                {
                    readFailed++;
                    L(log, "[提示] 读取 QC 失败（" + Path.GetFileName(qc) + "）：" + ex.Message);
                    continue;
                }
                string stripped;
                try { stripped = QcLexer.StripComments(text); }
                catch { stripped = text; }

                if (HasModelName(stripped)) withModel.Add(qc);

                foreach (Match m in IncludeRegex.Matches(stripped))
                {
                    string target = FindIncludeTarget(qc, root, m.Groups[1].Value);
                    if (target != null) included.Add(target);
                }
            }

            int excluded = 0;
            foreach (string qc in withModel)
            {
                string full;
                try { full = Path.GetFullPath(qc); } catch { full = qc; }
                if (included.Contains(full))
                {
                    excluded++;
                    continue;
                }
                if (seen.Add(full)) ordered.Add(full);
            }

            if (excluded > 0)
                L(log, "  剔除被其它 QC $include 的片段：" + excluded + " 个（它们随父 QC 一起编译）");
            if (readFailed > 0)
                L(log, "  有 " + readFailed + " 个 QC 读取失败，已跳过（不影响其余 QC）");
            L(log, "  找到含 $modelname 的根 QC：" + ordered.Count + " 个");
            return ordered;
        }

        /// <summary>解析 .bat 里 nekomdl 调用的 QC 参数（展开 %~dp0/%CD%，跳过 rem/:: 注释）。</summary>
        public static List<string> ParseBat(string batPath)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(batPath)) return list;

            string batFull, dir;
            try
            {
                batFull = Path.GetFullPath(batPath);
                dir = Path.GetDirectoryName(batFull) ?? "";
            }
            catch { return list; }

            string text;
            try { text = QcTextFile.Read(batFull).Text; }   // 自动探测编码：bat 常见 ANSI/GBK
            catch { return list; }

            foreach (string rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("@", StringComparison.Ordinal)) line = line.Substring(1).TrimStart();
                if (line.Length == 0) continue;
                if (IsBatComment(line)) continue;
                if (line.IndexOf("nekomdl", StringComparison.OrdinalIgnoreCase) < 0) continue;

                line = ExpandBatVars(line, dir);

                string qc = PickQcArgument(line);
                if (qc == null) continue;

                string full;
                try { full = Path.GetFullPath(Path.Combine(dir, qc.Replace('/', '\\'))); }
                catch { continue; }
                try { if (!File.Exists(full)) continue; } catch { continue; }
                if (!ContainsIgnoreCase(list, full)) list.Add(full);
            }
            return list;
        }

        /// <summary>是否本工具生成的附属 QC（不能当根 QC）。</summary>
        public static bool IsGeneratedQc(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string name;
            try { name = Path.GetFileName(path); }
            catch { name = path; }
            if (string.IsNullOrEmpty(name)) return false;

            if (name.EndsWith("_LOD.qc", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith("_autoLOD.qc", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith("_auto.qc", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("_LOD合集", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("自动LOD_", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>递归找散落的 GLB、SMD、DMX，跳过 LOD 产物，用于没有 QC 的文件夹。</summary>
        public static List<string> FindLooseModels(string dir, int maxDepth, Action<string> log)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(dir)) return found;
            string root;
            try { root = Path.GetFullPath(dir); } catch { return found; }
            if (!Directory.Exists(root)) return found;
            if (maxDepth < 0) maxDepth = 3;

            CollectLooseModels(root, 0, maxDepth, found, log, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            found.Sort(StringComparer.OrdinalIgnoreCase);
            if (found.Count > 0) L(log, "  找到 " + found.Count + " 个散落模型（GLB、SMD、DMX）");
            return found;
        }

        /// <summary>包装 QC 路径：&lt;模型同目录&gt;\自动LOD_&lt;模型名&gt;.qc（绝对路径）。</summary>
        public static string WrapperQcPath(string modelPath)
        {
            string full;
            try { full = Path.GetFullPath(modelPath); } catch { full = modelPath ?? ""; }
            string dir;
            try { dir = Path.GetDirectoryName(full); } catch { dir = null; }
            if (string.IsNullOrEmpty(dir)) dir = ".";
            string name = Path.GetFileNameWithoutExtension(full);
            return Path.Combine(dir, "自动LOD_" + name + ".qc");
        }

        /// <summary>
        /// 给单个 glb/smd 建包装 QC（已存在则沿用，绝不覆盖用户改过的内容）。
        /// 用系统 ANSI 编码写（studiomdl 按 ANSI 读 QC，中文文件名才能对上）。返回 QC 绝对路径。
        /// </summary>
        public static string EnsureWrapperQc(string modelPath, Action<string, LogLevel> log)
        {
            if (string.IsNullOrEmpty(modelPath))
                throw new IOException("建立包装 QC 失败：模型路径为空。");

            string modelFull;
            try { modelFull = Path.GetFullPath(modelPath); }
            catch (Exception ex) { throw new IOException("建立包装 QC 失败：模型路径无效 —— " + ex.Message, ex); }

            try
            {
                if (!File.Exists(modelFull))
                    throw new IOException("建立包装 QC 失败：模型文件不存在 —— " + modelFull);
            }
            catch (IOException) { throw; }
            catch (Exception ex) { throw new IOException("建立包装 QC 失败：无法访问模型文件 —— " + ex.Message, ex); }

            string ext = Path.GetExtension(modelFull);
            if (!QcPathResolver.IsSupportedModelExtension(ext))
                throw new IOException("建立包装 QC 失败：只支持 GLB、SMD、DMX 模型 —— " + Path.GetFileName(modelFull));

            string qc;
            try { qc = WrapperQcPath(modelFull); }
            catch (Exception ex) { throw new IOException("建立包装 QC 失败：无法计算目标路径 —— " + ex.Message, ex); }

            if (File.Exists(qc))
            {
                Log(log, "  沿用已有的包装 QC：" + Path.GetFileName(qc), LogLevel.Info);
                return qc;
            }

            string name = Path.GetFileNameWithoutExtension(modelFull);
            string file = Path.GetFileName(modelFull);
            var sb = new StringBuilder();
            sb.AppendLine("// 由 L4D2 自动 LOD 工具生成（源模型没有 QC，自动创建）");
            sb.AppendLine("// 编译前请按需修改 $modelname / $cdmaterials；本文件已存在时工具不会覆盖它");
            sb.AppendLine();
            sb.AppendLine("$modelname \"props/" + name + ".mdl\"");
            sb.AppendLine("$bodygroup \"" + name + "\"");
            sb.AppendLine("{");
            sb.AppendLine("\tstudio \"" + file + "\"");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("$cdmaterials \"models/" + name + "\"");
            sb.AppendLine("$surfaceprop \"default\"");
            sb.AppendLine();
            sb.AppendLine("$sequence \"idle\" {");
            sb.AppendLine("\t\"" + file + "\"");
            sb.AppendLine("\tfps 30");
            sb.AppendLine("\tactivity \"act_idle\" 1");
            sb.AppendLine("}");

            try
            {
                // studiomdl 按系统 ANSI（中文 Windows = GBK 936）读 QC；必须无 BOM。
                // 注意不能用 Encoding.Default：本程序清单 activeCodePage=UTF-8 会让它为 UTF-8（见 SystemAnsi）。
                QcTextFile.WriteAllText(qc, sb.ToString(), SystemAnsi.Encoding, false);
            }
            catch (Exception ex)
            {
                throw new IOException("建立包装 QC 失败（" + Path.GetFileName(qc) + "）：" +
                                      "目标目录可能只读或被安全软件拦截 —— " + ex.Message, ex);
            }
            Log(log, "  已为 " + file + " 建立包装 QC：" + Path.GetFileName(qc), LogLevel.Info);
            return qc;
        }

        // ───────────────────────────── 私有实现 ─────────────────────────────

        private static readonly Regex IncludeRegex =
            new Regex("\\$include\\s+\"([^\"]+\\.qc)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ModelNameRegex =
            new Regex("\\$modelname\\s+\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex QuotedRegex = new Regex("\"([^\"]+)\"", RegexOptions.CultureInvariant);
        private static readonly Regex BareQcRegex =
            new Regex("([^\\s\"]+\\.qc)(?=\\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex BatCommentRegex =
            new Regex("^rem($|[\\s.:,;=(])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static void L(Action<string> log, string message)
        {
            if (log == null || message == null) return;
            try { log(message); } catch { }
        }

        private static void Log(Action<string, LogLevel> log, string message, LogLevel level)
        {
            if (log == null || message == null) return;
            try { log(message, level); } catch { }
        }

        private static bool HasModelName(string stripped)
        {
            try { return ModelNameRegex.IsMatch(stripped ?? ""); } catch { return false; }
        }

        /// <summary>把一条 $include 解析到真实文件：QC 目录 → 工程根 → 同名兜底；都不存在返回 null。</summary>
        private static string FindIncludeTarget(string declQc, string rootDir, string relArg)
        {
            try
            {
                string rel = (relArg ?? "").Replace('/', '\\');
                if (rel.Length == 0) return null;
                string qcDir = Path.GetDirectoryName(Path.GetFullPath(declQc)) ?? rootDir;
                string baseName = Path.GetFileName(rel);
                var cands = new List<string>();
                try { cands.Add(Path.Combine(qcDir, rel)); } catch { }
                try { cands.Add(Path.Combine(rootDir, rel)); } catch { }
                if (!string.IsNullOrEmpty(baseName))
                {
                    try { cands.Add(Path.Combine(qcDir, baseName)); } catch { }
                    try { cands.Add(Path.Combine(rootDir, baseName)); } catch { }
                }
                foreach (string c in cands)
                {
                    try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { }
                }
            }
            catch { }
            return null;
        }

        private static void CollectLooseModels(string dir, int depth, int maxDepth, List<string> found,
                                               Action<string> log, HashSet<string> visited)
        {
            if (depth > maxDepth) return;
            string full;
            try { full = Path.GetFullPath(dir); } catch { return; }
            if (!visited.Add(full)) return;

            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.glb"))
                    AddLoose(f, found);
                foreach (string f in Directory.GetFiles(dir, "*.smd"))
                    AddLoose(f, found);
                foreach (string f in Directory.GetFiles(dir, "*.dmx"))
                    AddLoose(f, found);
            }
            catch (Exception ex)
            {
                L(log, "[提示] 枚举模型目录失败（" + full + "）：" + ex.Message);
            }

            if (depth >= maxDepth) return;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception ex)
            {
                L(log, "[提示] 枚举子目录失败（" + full + "）：" + ex.Message);
                return;
            }

            foreach (string sub in subs)
            {
                string subName;
                try { subName = Path.GetFileName(sub); } catch { subName = sub; }
                if (subName.StartsWith("_LOD", StringComparison.OrdinalIgnoreCase)) continue;   // 跳过工具备份/产物目录
                try
                {
                    var attr = new DirectoryInfo(sub).Attributes;
                    if ((attr & FileAttributes.ReparsePoint) != 0)
                    {
                        L(log, "[提示] 跳过符号链接/联接点目录：" + subName);
                        continue;
                    }
                }
                catch { }
                CollectLooseModels(sub, depth + 1, maxDepth, found, log, visited);
            }
        }

        private static readonly Regex LodProductRegex =
            new Regex("_lod\\d+(?:_[0-9a-f]{16})?\\.(glb|smd|dmx)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static void AddLoose(string file, List<string> found)
        {
            try
            {
                string name = Path.GetFileName(file);
                if (LodProductRegex.IsMatch(name)) return;   // 排除本工具生成的 *_lodN.glb/smd
                found.Add(Path.GetFullPath(file));
            }
            catch { }
        }

        private static bool IsBatComment(string line)
        {
            if (line.StartsWith("::", StringComparison.Ordinal)) return true;
            try { return BatCommentRegex.IsMatch(line); } catch { return false; }
        }

        /// <summary>展开 %~dp0（bat 所在目录 + 结尾反斜杠）与 %CD%（bat 所在目录），并修掉 "%~dp0\x.qc" 的双反斜杠。</summary>
        private static string ExpandBatVars(string line, string dir)
        {
            string d = (dir ?? "").TrimEnd('\\') + "\\";
            string dNoSlash = d.TrimEnd('\\');
            try
            {
                line = Regex.Replace(line, "%~dp0", d.Replace("$", "$$"), RegexOptions.IgnoreCase);
                line = Regex.Replace(line, "%CD%", dNoSlash.Replace("$", "$$"), RegexOptions.IgnoreCase);
                line = line.Replace(d + "\\", d);
            }
            catch { }
            return line;
        }

        /// <summary>
        /// 从一行 nekomdl 命令里挑 QC 参数：优先取最后一个以 .qc 结尾的引号串；
        /// 没有引号 QC 时取最后一个裸 .qc 参数（多 token 取后面的那个）。
        /// </summary>
        private static string PickQcArgument(string line)
        {
            string lastQuoted = null;
            try
            {
                MatchCollection quoted = QuotedRegex.Matches(line ?? "");
                for (int i = quoted.Count - 1; i >= 0; i--)
                {
                    string v = quoted[i].Groups[1].Value;
                    if (v.EndsWith(".qc", StringComparison.OrdinalIgnoreCase)) { lastQuoted = v; break; }
                }
            }
            catch { }
            if (lastQuoted != null) return lastQuoted;

            string lastBare = null;
            try
            {
                foreach (Match m in BareQcRegex.Matches(line ?? ""))
                    lastBare = m.Groups[1].Value;
            }
            catch { }
            return lastBare;
        }

        private static bool IsUnderBackup(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (path.IndexOf("_LOD备份", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                string full = Path.GetFullPath(path);
                string dir = Path.GetDirectoryName(full) ?? "";
                foreach (string seg in dir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (string.Equals(seg, "LOD备份", StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(seg, "_LOD备份", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            foreach (string s in list)
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 递归枚举文件（比 Directory.EnumerateFiles(AllDirectories) 更容错）：
        /// 每个目录单独 try/catch；跳过联接点目录防环；结果按路径排序。
        /// </summary>
        private static List<string> SafeEnumerateFiles(string root, string pattern, Action<string> log)
        {
            var result = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(root, 0));

            while (stack.Count > 0)
            {
                KeyValuePair<string, int> item = stack.Pop();
                string dir = item.Key;
                int depth = item.Value;
                string full;
                try { full = Path.GetFullPath(dir); } catch { continue; }
                if (!visited.Add(full)) continue;
                if (depth > 64) continue;

                try
                {
                    foreach (string f in Directory.GetFiles(dir, pattern)) result.Add(f);
                }
                catch (Exception ex)
                {
                    L(log, "[提示] 枚举目录失败（" + full + "）：" + ex.Message);
                }

                try
                {
                    foreach (string d in Directory.GetDirectories(dir))
                    {
                        try
                        {
                            var attr = new DirectoryInfo(d).Attributes;
                            if ((attr & FileAttributes.ReparsePoint) != 0)
                            {
                                L(log, "[提示] 跳过符号链接/联接点目录：" + Path.GetFileName(d));
                                continue;
                            }
                        }
                        catch { }
                        stack.Push(new KeyValuePair<string, int>(d, depth + 1));
                    }
                }
                catch (Exception ex)
                {
                    L(log, "[提示] 枚举子目录失败（" + full + "）：" + ex.Message);
                }
            }
            return result;
        }
    }
}
