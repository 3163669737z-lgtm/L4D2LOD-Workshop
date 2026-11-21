using System;
using System.Collections.Generic;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>QC 切词结果。</summary>
    public struct QcToken
    {
        public string Text;
        public bool Quoted;
        public int Pos;

        public bool IsWord(string word)
        {
            return !Quoted && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
        }
        public bool IsBrace
        {
            get { return !Quoted && (Text == "{" || Text == "}"); }
        }
    }

    /// <summary>
    /// QC 词法/文本操作：注释遮蔽、标记块、手写 $lod 定位、切词、括号匹配。
    /// 实现负责人：QC 模块。契约不可变。
    ///
    /// 所有"遮蔽"类函数都保证**长度与字符位置一一对应**（注释内容替换成空格、换行原样保留），
    /// 这样遮蔽文本上找到的偏移量可以直接用回原文，用于精确插入/注释行。
    /// </summary>
    public static class QcLexer
    {
        /// <summary>本工具注入块的起止标记（与旧版文本一致，便于接管旧工程）。</summary>
        public const string MarkerBegin = "//>>> L4D2 自动 LOD 工具 生成块 开始 >>>";
        public const string MarkerEnd = "//<<< L4D2 自动 LOD 工具 生成块 结束 <<<";
        /// <summary>Force 模式注释手写 $lod 时加的行首前缀（与旧版一致）。</summary>
        public const string DisabledPrefix = "// [autoLOD 已停用] ";

        /// <summary>切词：引号串（不跨行）、{ }、其余按空白切。位置与原文一一对应。</summary>
        public static List<QcToken> Tokenize(string text)
        {
            var list = new List<QcToken>();
            if (string.IsNullOrEmpty(text)) return list;

            int i = 0, n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '{' || c == '}')
                {
                    list.Add(new QcToken { Text = c.ToString(), Pos = i });
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    // 引号串不跨行：遇到 \n / \r 就当字符串结束（未闭合也照样产出 token）
                    int j = i + 1;
                    while (j < n && text[j] != '"' && text[j] != '\n' && text[j] != '\r') j++;
                    list.Add(new QcToken { Text = text.Substring(i + 1, j - i - 1), Quoted = true, Pos = i });
                    i = (j < n && text[j] == '"') ? j + 1 : j;
                    continue;
                }

                int k = i;
                while (k < n && !char.IsWhiteSpace(text[k]) && text[k] != '{' && text[k] != '}' && text[k] != '"') k++;
                list.Add(new QcToken { Text = text.Substring(i, k - i), Pos = i });
                i = k;
            }
            return list;
        }

        /// <summary>把注释替换成空格（保留换行与长度，位置一一对应）。字符串里的 // 与 /* 不算注释。</summary>
        public static string MaskComments(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var sb = new StringBuilder(text);
            bool inLine = false, inBlock = false, inStr = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';

                if (inLine)
                {
                    if (c == '\n') inLine = false;
                    else if (c != '\r') sb[i] = ' ';
                    continue;
                }
                if (inBlock)
                {
                    if (c == '*' && next == '/') { sb[i] = ' '; sb[i + 1] = ' '; i++; inBlock = false; continue; }
                    if (c != '\n' && c != '\r') sb[i] = ' ';
                    continue;
                }
                if (inStr)
                {
                    // 字符串内原样保留；未闭合字符串在行尾自然结束
                    if (c == '"' || c == '\n' || c == '\r') inStr = false;
                    continue;
                }
                if (c == '/' && next == '/') { sb[i] = ' '; sb[i + 1] = ' '; i++; inLine = true; continue; }
                if (c == '/' && next == '*') { sb[i] = ' '; sb[i + 1] = ' '; i++; inBlock = true; continue; }
                if (c == '"') { inStr = true; }
            }
            return sb.ToString();
        }

        /// <summary>把完整的工具标记块（含起止行）替换成空格；有开始无结束 → broken=true 且不遮蔽。</summary>
        public static string MaskToolBlocks(string text, out bool broken)
        {
            broken = false;
            if (string.IsNullOrEmpty(text)) return text ?? "";
            if (text.IndexOf(MarkerBegin, StringComparison.Ordinal) < 0) return text;

            var sb = new StringBuilder(text);
            var lineStarts = LineStarts(text);
            int beginLine = -1;

            for (int li = 0; li < lineStarts.Count; li++)
            {
                int a = lineStarts[li];
                int b = li + 1 < lineStarts.Count ? lineStarts[li + 1] : text.Length;
                string line = text.Substring(a, b - a).Trim();

                if (beginLine < 0)
                {
                    if (line.StartsWith(MarkerBegin, StringComparison.Ordinal)) beginLine = li;
                }
                else if (line.StartsWith(MarkerEnd, StringComparison.Ordinal))
                {
                    int from = lineStarts[beginLine];
                    for (int p = from; p < b; p++)
                        if (sb[p] != '\n' && sb[p] != '\r') sb[p] = ' ';
                    beginLine = -1;
                }
            }

            if (beginLine >= 0) broken = true;   // 有开始没结束：保持原文，调用方必须拒绝处理
            return sb.ToString();
        }

        /// <summary>
        /// 移除所有工具标记块：**保留原文其余部分的原始字节（含混合换行）**，只删掉标记块所在行。
        /// 有开始无结束返回 false 且 result=原文（绝不删到文件尾）。
        /// </summary>
        public static bool TryRemoveToolBlock(string text, out string result)
        {
            if (text == null) { result = ""; return true; }
            var sb = new StringBuilder(text.Length);
            int pos = 0;
            while (pos <= text.Length)
            {
                int beginStart, beginAfter;
                int begin = FindMarkerLine(text, pos, MarkerBegin, out beginStart, out beginAfter);
                if (begin < 0)
                {
                    sb.Append(text, pos, text.Length - pos);
                    break;
                }
                int endStart, endAfter;
                int end = FindMarkerLine(text, beginAfter, MarkerEnd, out endStart, out endAfter);
                if (end < 0) { result = text; return false; }   // 缺结束标记：绝不删到文件尾

                sb.Append(text, pos, beginStart - pos);
                pos = endAfter;
            }
            result = sb.ToString();
            return true;
        }

        /// <summary>从 from 起找第一处"整行（去空白后）以 marker 开头"的行；返回行起点，否则 -1。lineAfter 为该行换行之后的位置。</summary>
        private static int FindMarkerLine(string text, int from, string marker, out int lineStart, out int lineAfter)
        {
            lineStart = -1;
            lineAfter = -1;
            if (text == null || from < 0 || from > text.Length) return -1;
            int p = from;
            while (p < text.Length)
            {
                int nl = text.IndexOf('\n', p);
                int lineEnd = nl < 0 ? text.Length : nl;
                int a = p, b = lineEnd;
                // 去掉行首空白（空格/Tab）与行尾 \r
                while (a < b && (text[a] == ' ' || text[a] == '\t')) a++;
                int trimmedEnd = b;
                if (trimmedEnd > a && text[trimmedEnd - 1] == '\r') trimmedEnd--;
                while (trimmedEnd > a && (text[trimmedEnd - 1] == ' ' || text[trimmedEnd - 1] == '\t')) trimmedEnd--;
                if (trimmedEnd - a >= marker.Length &&
                    string.CompareOrdinal(text, a, marker, 0, marker.Length) == 0)
                {
                    lineStart = p;
                    lineAfter = nl < 0 ? text.Length : nl + 1;
                    return a;
                }
                if (nl < 0) break;
                p = nl + 1;
            }
            return -1;
        }

        /// <summary>
        /// 把工具块以外的 $lod / $shadowlod 整块逐行加 DisabledPrefix 注释。
        /// count = 停用的块数；工具块不完整时返回 -1 且原文不动。
        /// </summary>
        public static string CommentOutForeignLod(string rawText, out int count)
        {
            count = 0;
            if (string.IsNullOrEmpty(rawText)) return rawText ?? "";

            bool broken;
            string masked = MaskComments(MaskToolBlocks(rawText, out broken));
            if (broken) { count = -1; return rawText; }

            var tokens = Tokenize(masked);
            var lineStarts = LineStarts(rawText);
            var mark = new bool[lineStarts.Count];

            for (int i = 0; i < tokens.Count; i++)
            {
                if (!(tokens[i].IsWord("$lod") || tokens[i].IsWord("$shadowlod"))) continue;

                int startLine = LineOf(lineStarts, tokens[i].Pos);
                int endLine = startLine;

                // 往后最多看 4 个 token 找块起始 '{'；遇到 '}' 或另一个 $ 指令就只注释这一行
                for (int j = i + 1; j < tokens.Count && j <= i + 4; j++)
                {
                    QcToken t = tokens[j];
                    if (t.IsWord("{"))
                    {
                        int close = FindMatchingBrace(masked, t.Pos);
                        if (close >= 0)
                        {
                            endLine = LineOf(lineStarts, close);
                            while (i + 1 < tokens.Count && tokens[i + 1].Pos <= close) i++;
                        }
                        break;
                    }
                    if (t.IsWord("}") || (!t.Quoted && t.Text.StartsWith("$", StringComparison.Ordinal))) break;
                }

                for (int l = startLine; l <= endLine && l < mark.Length; l++) mark[l] = true;
                count++;
            }

            if (count == 0) return rawText;

            var sb = new StringBuilder(rawText.Length + count * 64);
            for (int li = 0; li < lineStarts.Count; li++)
            {
                int a = lineStarts[li];
                int b = li + 1 < lineStarts.Count ? lineStarts[li + 1] : rawText.Length;
                if (mark[li]) sb.Append(DisabledPrefix);
                sb.Append(rawText, a, b - a);
            }
            return sb.ToString();
        }

        /// <summary>每行起始位置表（LineStarts[i] = 第 i 行起点）。</summary>
        public static List<int> LineStarts(string text)
        {
            var result = new List<int> { 0 };
            if (string.IsNullOrEmpty(text)) return result;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') result.Add(i + 1);
            return result;
        }

        /// <summary>字符位置 → 0 起行号。</summary>
        public static int LineOf(List<int> lineStarts, int pos)
        {
            if (lineStarts == null || lineStarts.Count == 0) return 0;
            int idx = lineStarts.BinarySearch(pos);
            if (idx >= 0) return idx;
            idx = ~idx - 1;
            return idx < 0 ? 0 : idx;
        }

        /// <summary>
        /// 从 openIndex 处的 '{' 找匹配的 '}'（需在已遮蔽注释的文本上调用）。
        /// 引号字符串里的花括号不算括号（避免 $sequence "a}b" 之类把块提前截断）。
        /// 找不到返回 -1。
        /// </summary>
        public static int FindMatchingBrace(string maskedText, int openIndex)
        {
            if (string.IsNullOrEmpty(maskedText)) return -1;
            if (openIndex < 0 || openIndex >= maskedText.Length || maskedText[openIndex] != '{') return -1;

            int depth = 0;
            bool inStr = false;
            for (int i = openIndex; i < maskedText.Length; i++)
            {
                char c = maskedText[i];
                if (inStr)
                {
                    if (c == '"' || c == '\n' || c == '\r') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        /// <summary>删除注释（保留字符串内容与换行）。</summary>
        public static string StripComments(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var sb = new StringBuilder(text.Length);
            bool inLine = false, inBlock = false, inStr = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';

                if (inLine)
                {
                    if (c == '\n' || c == '\r') { inLine = false; sb.Append(c); }
                    continue;
                }
                if (inBlock)
                {
                    if (c == '*' && next == '/') { inBlock = false; i++; }
                    else if (c == '\n' || c == '\r') sb.Append(c);
                    continue;
                }
                if (inStr)
                {
                    sb.Append(c);
                    if (c == '"' || c == '\n' || c == '\r') inStr = false;
                    continue;
                }
                if (c == '/' && next == '/') { inLine = true; i++; continue; }
                if (c == '/' && next == '*') { inBlock = true; i++; continue; }
                if (c == '"') { inStr = true; sb.Append(c); continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
