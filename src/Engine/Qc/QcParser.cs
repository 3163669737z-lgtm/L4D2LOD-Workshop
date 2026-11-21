using System;
using System.Collections.Generic;
using System.IO;

namespace LodStudio.Engine
{
    /// <summary>
    /// QC 解析：递归展开 $include 链，收集模型引用、手写 $lod、工具块信息、编码。
    /// 实现负责人：QC 模块。契约不可变。
    ///
    /// 真实工程里 $bodygroup 常写在被 include 的 main.qc 里，只读根文件看不到任何 studio，
    /// 所以必须递归。状态（$cd 之后的当前目录、bodypart 序号）整个 include 链共享 —— 与旧版一致。
    /// </summary>
    public static class QcParser
    {
        /// <summary>$include 最大嵌套层数（与旧版一致；修复无限包含靠 Visited 防环）。</summary>
        public const int MaxIncludeDepth = 8;

        private sealed class CollectState
        {
            public QcInfo Info;
            public HashSet<string> Visited;
            public string RootDir;
            public string CurDir;
            public Stack<string> DirStack = new Stack<string>();
            public int BgIndex;
        }

        /// <summary>解析根 QC（不会抛异常；读取失败记在 QcInfo.ReadFailed + Warnings）。</summary>
        public static QcInfo Parse(string qcPath)
        {
            var info = new QcInfo();
            string root;
            try { root = Path.GetFullPath(qcPath); }
            catch (Exception ex)
            {
                info.QcPath = qcPath;
                info.QcDir = "";
                info.ReadFailed = true;
                info.Warnings.Add("读取 QC 失败: " + ex.Message);
                return info;
            }

            info.QcPath = root;
            info.QcDir = Path.GetDirectoryName(root) ?? "";

            try
            {
                var rootText = QcTextFile.Read(root);
                info.TextEncoding = rootText.Encoding;
                info.HasBom = rootText.Bom;
                info.NewLine = rootText.NewLine;
            }
            catch (Exception ex)
            {
                info.ReadFailed = true;
                info.Warnings.Add("读取 QC 失败: " + ex.Message);
                return info;
            }

            var st = new CollectState
            {
                Info = info,
                Visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                RootDir = info.QcDir,
                CurDir = info.QcDir
            };

            try { Collect(root, 0, st); }
            catch (Exception ex) { info.Warnings.Add("解析 QC 时出错: " + ex.Message); }

            info.FilesRead = st.Visited.Count;

            if (string.IsNullOrEmpty(info.ModelName))
                info.Warnings.Add("没有找到 $modelname —— 编译时 studiomdl 会报错，确认这是编译用的 QC。");

            return info;
        }

        /// <summary>本工具生成的附属 QC（_LOD.qc / _autoLOD.qc / _LOD合集*.qc / 自动LOD_*.qc），不能当根 QC，也不跟进 include。</summary>
        public static bool IsToolGeneratedQc(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string name;
            try { name = Path.GetFileName(path); }
            catch { name = path; }
            if (name.Length == 0) return false;

            if (name.EndsWith("_LOD.qc", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith("_autoLOD.qc", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith("_auto.qc", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("_LOD合集", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("自动LOD_", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>智能读文本（沿用 QcTextFile 的编码探测；供扫描/编译器使用）。</summary>
        public static string ReadTextSmart(string path)
        {
            return QcTextFile.Read(path).Text;
        }

        /// <summary>沿 $include 链找 $modelname；找不到返回空串。maxDepth&lt;=0 时用默认 8 层。</summary>
        public static string FindModelName(string qcPath, int maxDepth)
        {
            try
            {
                if (string.IsNullOrEmpty(qcPath)) return "";
                string root;
                try { root = Path.GetFullPath(qcPath); } catch { return ""; }
                if (!File.Exists(root)) return "";
                if (maxDepth <= 0) maxDepth = MaxIncludeDepth;

                string rootDir = Path.GetDirectoryName(root) ?? "";
                var st = new CollectState
                {
                    RootDir = rootDir,
                    CurDir = rootDir,
                    Visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                };
                string found = FindModelNameIn(root, 0, maxDepth, st);
                return found ?? "";
            }
            catch { return ""; }
        }

        // ───────────────────────────── 私有实现 ─────────────────────────────

        private static string FindModelNameIn(string file, int depth, int maxDepth, CollectState st)
        {
            if (depth > maxDepth) return null;

            string full;
            try { full = Path.GetFullPath(file); } catch { return null; }
            if (!File.Exists(full)) return null;
            if (depth > 0 && IsToolGeneratedQc(full)) return null;
            if (!st.Visited.Add(full)) return null;

            string raw;
            try { raw = QcTextFile.Read(full).Text; } catch { return null; }

            bool broken;
            string masked = QcLexer.MaskComments(QcLexer.MaskToolBlocks(raw, out broken));
            var tokens = QcLexer.Tokenize(masked);
            string fileDir;
            try { fileDir = Path.GetDirectoryName(full) ?? st.RootDir; } catch { fileDir = st.RootDir; }

            for (int i = 0; i < tokens.Count; i++)
            {
                QcToken t = tokens[i];
                if (t.Quoted || t.Text.Length == 0 || t.Text[0] != '$') continue;
                string directive = t.Text.ToLowerInvariant();
                bool hasArg = i + 1 < tokens.Count && !tokens[i + 1].IsBrace;

                switch (directive)
                {
                    case "$modelname":
                        if (hasArg) return tokens[i + 1].Text.Replace('\\', '/');
                        break;
                    case "$cd":
                        if (hasArg) st.CurDir = CombineDir(st.CurDir, tokens[i + 1].Text);
                        break;
                    case "$pushd":
                        if (hasArg)
                        {
                            st.DirStack.Push(st.CurDir);
                            st.CurDir = CombineDir(st.CurDir, tokens[i + 1].Text);
                        }
                        break;
                    case "$popd":
                        if (st.DirStack.Count > 0) st.CurDir = st.DirStack.Pop();
                        break;
                    case "$include":
                        if (hasArg)
                        {
                            string inc = FindInclude(tokens[i + 1].Text, st.CurDir, st.RootDir, fileDir);
                            if (inc != null)
                            {
                                string m = FindModelNameIn(inc, depth + 1, maxDepth, st);
                                if (!string.IsNullOrEmpty(m)) return m;
                            }
                            i++;   // 跳过参数 token
                        }
                        break;
                }
            }
            return null;
        }

        /// <summary>$include 候选顺序：当前目录 → 根目录 → 声明文件目录 → 根目录+纯文件名；第一个存在的生效。</summary>
        private static string FindInclude(string relArg, string curDir, string rootDir, string fileDir)
        {
            string rel;
            try { rel = (relArg ?? "").Replace('/', '\\'); }
            catch { rel = relArg ?? ""; }

            var cands = new List<string>();
            AddCandidate(cands, curDir, rel);
            AddCandidate(cands, rootDir, rel);
            AddCandidate(cands, fileDir, rel);

            try
            {
                string baseName = Path.GetFileName(rel);
                if (!string.IsNullOrEmpty(rootDir) && !string.IsNullOrEmpty(baseName))
                    cands.Add(Path.Combine(rootDir, baseName));
            }
            catch { }

            foreach (string c in cands)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            return null;
        }

        private static void AddCandidate(List<string> cands, string dir, string rel)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try { cands.Add(Path.Combine(dir, rel)); } catch { }
        }

        private static void Collect(string file, int depth, CollectState st)
        {
            if (depth > MaxIncludeDepth) return;
            QcInfo info = st.Info;

            string full;
            try { full = Path.GetFullPath(file); } catch { return; }
            if (!File.Exists(full)) return;
            if (depth > 0 && IsToolGeneratedQc(full)) return;      // 不跟进本工具生成的 _LOD.qc 等
            if (!st.Visited.Add(full)) return;                     // 防环

            string raw;
            try { raw = QcTextFile.Read(full).Text; }
            catch (Exception ex)
            {
                info.Warnings.Add("读取 include 文件失败 " + Path.GetFileName(full) + ": " + ex.Message);
                return;
            }

            if (depth > 0) AddDistinct(info.IncludedFiles, full);

            if (raw.IndexOf(QcLexer.MarkerBegin, StringComparison.Ordinal) >= 0)
                AddDistinct(info.ToolMarkerFiles, full);

            bool broken;
            string masked = QcLexer.MaskComments(QcLexer.MaskToolBlocks(raw, out broken));
            if (broken) AddDistinct(info.ToolBlockBrokenFiles, full);

            var tokens = QcLexer.Tokenize(masked);
            var lineStarts = QcLexer.LineStarts(masked);
            string fileDir;
            try { fileDir = Path.GetDirectoryName(full) ?? st.RootDir; } catch { fileDir = st.RootDir; }

            for (int i = 0; i < tokens.Count; i++)
            {
                QcToken t = tokens[i];
                if (t.Quoted || t.Text.Length == 0 || t.Text[0] != '$') continue;
                string directive = t.Text.ToLowerInvariant();
                bool hasArg = i + 1 < tokens.Count && !tokens[i + 1].IsBrace;

                switch (directive)
                {
                    case "$modelname":
                        if (hasArg && string.IsNullOrEmpty(info.ModelName))
                            info.ModelName = tokens[i + 1].Text.Replace('\\', '/');
                        break;

                    case "$staticprop":
                        info.IsStaticProp = true;
                        break;

                    case "$cd":
                        if (hasArg) st.CurDir = CombineDir(st.CurDir, tokens[i + 1].Text);
                        break;

                    case "$pushd":
                        if (hasArg)
                        {
                            st.DirStack.Push(st.CurDir);
                            st.CurDir = CombineDir(st.CurDir, tokens[i + 1].Text);
                        }
                        break;

                    case "$popd":
                        if (st.DirStack.Count > 0) st.CurDir = st.DirStack.Pop();
                        break;

                    case "$lod":
                    case "$shadowlod":
                        // 工具块已被 MaskToolBlocks 遮蔽，所以能扫到的就是用户手写的
                        AddDistinct(info.ForeignLodFiles, full);
                        break;

                    case "$include":
                        if (hasArg)
                        {
                            string inc = FindInclude(tokens[i + 1].Text, st.CurDir, st.RootDir, fileDir);
                            if (inc != null) Collect(inc, depth + 1, st);
                            else if (st.Info != null)
                                st.Info.Warnings.Add("$include 引用的文件找不到：「" + tokens[i + 1].Text + "」");
                            i++;
                        }
                        break;

                    case "$body":
                        // $body <name> "<file>"
                        if (i + 2 < tokens.Count && !tokens[i + 1].IsBrace && !tokens[i + 2].IsBrace)
                        {
                            var e = MakeEntry(tokens[i + 2].Text, full, st);
                            e.Bodygroup = tokens[i + 1].Text;
                            e.BodygroupIndex = st.BgIndex++;
                            e.IncludeDepth = depth;
                            e.DeclaredLine = QcLexer.LineOf(lineStarts, tokens[i + 2].Pos) + 1;
                            st.Info.Studios.Add(e);
                            i += 2;
                        }
                        break;

                    case "$bodygroup":
                        i = ParseBodygroup(tokens, i, st, full, lineStarts, depth);
                        break;

                    case "$model":
                        i = ParseModel(tokens, i, st, full, lineStarts, depth);
                        break;
                }
            }
        }

        /// <summary>解析 $bodygroup 块；返回主循环接下来要处理的下标（最后一个已消费的 token）。</summary>
        private static int ParseBodygroup(List<QcToken> tokens, int i, CollectState st, string declFile,
                                          List<int> lineStarts, int depth)
        {
            if (!(i + 2 < tokens.Count && !tokens[i + 1].IsBrace && tokens[i + 2].IsWord("{"))) return i;

            string bgName = tokens[i + 1].Text;
            int j = i + 3, braceDepth = 1;

            while (j < tokens.Count && braceDepth > 0)
            {
                QcToken tj = tokens[j];
                if (tj.IsWord("{")) { braceDepth++; j++; continue; }
                if (tj.IsWord("}")) { braceDepth--; j++; continue; }

                // 只在最外层（bodygroup 直接子级）识别 studio；更深的嵌套留给 studiomdl 自己处理
                if (braceDepth == 1 && tj.IsWord("studio") && j + 1 < tokens.Count && !tokens[j + 1].IsBrace)
                {
                    QcToken ft = tokens[j + 1];
                    var e = MakeEntry(ft.Text, declFile, st);
                    int line = QcLexer.LineOf(lineStarts, ft.Pos);

                    // 同一行里紧跟的 innode <arg>（多子节点网格需要拆分；批量不支持 → 会跳过）
                    int k = j + 2;
                    while (k < tokens.Count && QcLexer.LineOf(lineStarts, tokens[k].Pos) == line && !tokens[k].IsBrace)
                    {
                        if (tokens[k].IsWord("innode") && k + 1 < tokens.Count && !tokens[k + 1].IsBrace)
                        {
                            e.Innnode = tokens[k + 1].Text;
                            k++;
                        }
                        k++;
                    }

                    e.Bodygroup = bgName;
                    e.BodygroupIndex = st.BgIndex;   // 与旧版一致：一个 bodygroup 内所有 studio 共享同一个序号
                    e.IncludeDepth = depth;
                    e.DeclaredLine = line + 1;
                    st.Info.Studios.Add(e);

                    j = k;
                    continue;
                }
                j++;
            }

            st.BgIndex++;
            return j - 1;
        }

        /// <summary>解析 $model 块；返回主循环接下来要处理的下标。</summary>
        private static int ParseModel(List<QcToken> tokens, int i, CollectState st, string declFile,
                                      List<int> lineStarts, int depth)
        {
            // $model <name> "<file>" [ { ... } ] —— 参数之后到 { 之间只允许空白/注释（已被遮蔽）
            if (!(i + 2 < tokens.Count && !tokens[i + 1].IsBrace && !tokens[i + 2].IsBrace)) return i;

            string modelName = tokens[i + 1].Text;
            QcToken ft = tokens[i + 2];
            var e = MakeEntry(ft.Text, declFile, st);

            int next = i + 3;
            if (next < tokens.Count && tokens[next].IsWord("{"))
            {
                int j = next + 1, braceDepth = 1;
                while (j < tokens.Count && braceDepth > 0)
                {
                    QcToken tj = tokens[j];
                    if (tj.IsWord("{")) braceDepth++;
                    else if (tj.IsWord("}")) braceDepth--;
                    else if (!tj.Quoted)
                    {
                        string w = tj.Text.ToLowerInvariant();
                        // flexfile/flexcontroller/localvar（以及 $include 进来可能带 flex 的）→ 加 nofacial 会丢表情
                        if (w == "flexfile" || w == "flexcontroller" || w == "localvar" || w == "$include" ||
                            w == "flex" || w == "flexpair")
                            e.HasFacial = true;
                        else if (w == "innode" && j + 1 < tokens.Count && !tokens[j + 1].IsBrace)
                            e.Innnode = tokens[j + 1].Text;
                    }
                    j++;
                }
                next = j;
            }

            e.Bodygroup = "(主模型 " + modelName + ")";
            e.BodygroupIndex = -1;
            e.IsMainModel = true;
            e.IncludeDepth = depth;
            e.DeclaredLine = QcLexer.LineOf(lineStarts, ft.Pos) + 1;
            st.Info.Studios.Add(e);

            return next - 1;
        }

        private static StudioRef MakeEntry(string raw, string declFile, CollectState st)
        {
            var e = new StudioRef();
            e.DeclaredInQc = declFile;
            QcPathResolver.Resolve(e, raw, declFile, st.CurDir, st.RootDir, st.Info.Warnings);
            return e;
        }

        private static string CombineDir(string cur, string arg)
        {
            try
            {
                if (string.IsNullOrEmpty(arg)) return cur;
                return Path.GetFullPath(Path.Combine(cur ?? "", arg.Replace('/', '\\')));
            }
            catch { return cur; }
        }

        private static void AddDistinct(List<string> list, string value)
        {
            foreach (string x in list)
                if (string.Equals(x, value, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(value);
        }
    }
}
