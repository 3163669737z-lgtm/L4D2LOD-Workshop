using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>
    /// SMD（Source Model Data）文本编解码：<c>version / nodes / skeleton / triangles</c> 四段。
    ///
    /// 行为约定（与旧版管线的对外格式兼容）：
    ///  - 读取：容忍 CRLF/LF、UTF-8（含 BOM）/GBK、多余空白、缺 links 数量、材质与顶点行之间夹空行/注释、
    ///    links 声明数多于实际字段；无法解析的行跳过并写入 <see cref="SmdModel.Warnings"/>（带行号），不静默丢数据；
    ///    某段缺少 end 时遇到下一个段头会自动恢复，避免一段吞掉后面的段；
    ///  - 写出：UTF-8 无 BOM、CRLF、InvariantCulture、浮点固定至少 6 位小数（"0.000000###"），
    ///    顶点行始终写 links 数量（含 0，修正旧版静态顶点 9 字段写法）；skeleton 段保留源姿态行（若有）；
    ///  - 坐标/单位：原样读写，不做任何轴系或缩放换算（上层的 Blender 导入导出互为逆变换）。
    /// </summary>
    public static class SmdCodec
    {
        /// <summary>写出文件头注释（studiomdl 忽略 // 注释；保留“自动 LOD 工具生成”语义便于人工识别）。</summary>
        internal const string HeaderComment = "// 远景工坊 · L4D2 自动 LOD 工具 生成";

        private const string Nl = "\r\n";
        private const int MaxWarnings = 120;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);
        private static readonly char[] TokenSeparators = { ' ', '\t' };

        // ══════════════════════════════════════════════════════════════════
        //  公开 API
        // ══════════════════════════════════════════════════════════════════

        /// <summary>读取并解析 SMD 文件；文件不存在/为空/没有 triangles 段时抛可读的 <see cref="InvalidDataException"/>。</summary>
        public static SmdModel Parse(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("SMD 路径为空", "path");
            byte[] data;
            try
            {
                data = FileUtil.ReadAllBytesShared(path);
            }
            catch (FileNotFoundException ex) { throw new InvalidDataException("SMD 文件不存在：" + path, ex); }
            catch (DirectoryNotFoundException ex) { throw new InvalidDataException("SMD 目录不存在：" + path, ex); }
            catch (Exception ex) { throw new InvalidDataException("读取 SMD 失败：" + path + " —— " + ex.Message, ex); }
            return ParseBytes(data, path);
        }

        /// <summary>从字节解析 SMD。displayName 只用于错误/警告文案（传文件路径即可）。</summary>
        public static SmdModel ParseBytes(byte[] data, string displayName)
        {
            string name = string.IsNullOrEmpty(displayName) ? "(SMD)" : displayName;
            if (data == null) throw new ArgumentNullException("data");
            if (data.Length == 0) throw new InvalidDataException("SMD 文件为空：" + name);

            string text = DecodeText(data);
            string[] lines = SplitLines(text);
            var model = new SmdModel { SourcePath = name };

            bool sawVersion = false, sawNodes = false, sawTriangles = false;
            int i = 0;
            while (i < lines.Length)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal)) { i++; continue; }

                if (t.Equals("nodes", StringComparison.OrdinalIgnoreCase))
                {
                    sawNodes = true;
                    i = ParseNodes(lines, i + 1, model);
                    continue;
                }
                if (t.Equals("skeleton", StringComparison.OrdinalIgnoreCase))
                {
                    i = ParseSkeleton(lines, i + 1, model);
                    continue;
                }
                if (t.Equals("triangles", StringComparison.OrdinalIgnoreCase))
                {
                    sawTriangles = true;
                    i = ParseTriangles(lines, i + 1, model);
                    continue;
                }
                if (t.StartsWith("version", StringComparison.OrdinalIgnoreCase))
                {
                    sawVersion = true;
                    i++;
                    continue;
                }
                // $collisionmodel / $modelname 等其他指令：跳过并记录（旧版静默忽略）
                AddWarning(model, "第 " + (i + 1) + " 行无法识别，已跳过：" + Truncate(t, 90));
                i++;
            }

            if (!sawVersion) AddWarning(model, "缺少 version 行（按 SMD v1 继续解析）");
            if (!sawTriangles)
                throw new InvalidDataException(
                    "SMD 中没有 triangles 段（可能是动画 SMD；本工具只处理 reference 网格）：" + name);
            if (!sawNodes && model.Bones.Count == 0 && model.HasSkinWeights)
                AddWarning(model, "SMD 有顶点骨骼权重但缺少 nodes 段，骨骼索引无法校验");
            if (sawNodes && model.Bones.Count == 0)
                AddWarning(model, "nodes 段存在但没有解析出任何骨骼");

            // skeleton 姿态行数与骨骼数不一致（多帧动画或损坏）
            if (model.SkeletonPose.Count > 0 && model.Bones.Count > 0 &&
                model.SkeletonPose.Count % model.Bones.Count != 0)
            {
                AddWarning(model, "skeleton 姿态行数（" + model.SkeletonPose.Count + "）不是骨骼数（" +
                                  model.Bones.Count + "）的整数倍，可能不是单帧 reference 姿态");
            }

            return model;
        }

        /// <summary>写出 SMD 文件（UTF-8 无 BOM；原子写，失败给可读异常）。</summary>
        public static void Write(string path, SmdModel model)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("SMD 输出路径为空", "path");
            byte[] bytes = WriteBytes(model);
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                FileUtil.WriteAtomic(path, bytes);
            }
            catch (IOException ex)
            {
                throw new IOException("写入 SMD 失败（文件可能被占用或只读）：" + path + " —— " + ex.Message, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new IOException("写入 SMD 失败（没有写入权限）：" + path + " —— " + ex.Message, ex);
            }
        }

        /// <summary>把模型写成 SMD 文本字节（UTF-8 无 BOM，CRLF）。</summary>
        public static byte[] WriteBytes(SmdModel model)
        {
            if (model == null) throw new ArgumentNullException("model");

            var sb = new StringBuilder(96 + (model.Triangles == null ? 0 : model.Triangles.Count * 110));
            sb.Append(HeaderComment).Append(Nl);
            sb.Append("version 1").Append(Nl);

            if (model.Bones != null && model.Bones.Count > 0)
            {
                sb.Append("nodes").Append(Nl);
                foreach (var b in model.Bones)
                {
                    if (b == null) continue;
                    sb.Append("  ").Append(b.Index.ToString(Inv))
                      .Append(" \"").Append(CleanName(b.Name)).Append("\" ")
                      .Append(b.Parent.ToString(Inv)).Append(Nl);
                }
                sb.Append("end").Append(Nl);

                sb.Append("skeleton").Append(Nl);
                sb.Append("time 0").Append(Nl);
                if (model.SkeletonPose != null)
                {
                    foreach (var pose in model.SkeletonPose)
                    {
                        if (pose == null) continue;
                        string t = pose.Trim();
                        if (t.Length > 0) sb.Append(t).Append(Nl);
                    }
                }
                sb.Append("end").Append(Nl);
            }

            sb.Append("triangles").Append(Nl);
            if (model.Triangles != null)
            {
                foreach (var tri in model.Triangles)
                {
                    if (tri == null) continue;
                    sb.Append(NormalizeMaterial(tri.Material)).Append(Nl);
                    AppendVertex(sb, tri.V0);
                    AppendVertex(sb, tri.V1);
                    AppendVertex(sb, tri.V2);
                }
            }
            sb.Append("end").Append(Nl);

            return Utf8NoBom.GetBytes(sb.ToString());
        }

        // ══════════════════════════════════════════════════════════════════
        //  解析实现
        // ══════════════════════════════════════════════════════════════════

        /// <summary>解码文本：BOM 优先；无 BOM 先按严格 UTF-8，失败回退 GBK(936)（老工程 ANSI），再失败按 UTF-8 替换字节。</summary>
        private static string DecodeText(byte[] data)
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                return Encoding.UTF8.GetString(data, 3, data.Length - 3);
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
                return Encoding.Unicode.GetString(data, 2, data.Length - 2);
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            try { return Utf8Strict.GetString(data); }
            catch (DecoderFallbackException)
            {
                try { return Encoding.GetEncoding(936).GetString(data); }
                catch { return Encoding.UTF8.GetString(data); }
            }
        }

        private static string[] SplitLines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        /// <summary>解析 nodes 段；返回下一行索引（end 之后）。</summary>
        private static int ParseNodes(string[] lines, int start, SmdModel model)
        {
            var seenIndex = new HashSet<int>();
            var seenName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int i = start;
            bool closed = false;
            while (i < lines.Length)
            {
                string t = lines[i].Trim();
                if (t.Equals("end", StringComparison.OrdinalIgnoreCase)) { i++; closed = true; break; }
                if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal)) { i++; continue; }
                if (IsSectionOrVersionHeader(t))
                {
                    AddWarning(model, "nodes 段缺少 end，遇到段头 \"" + t + "\" 自动结束（第 " + (i + 1) + " 行）");
                    return i;
                }

                SmdBone bone;
                string err;
                if (!TryParseBoneLine(t, out bone, out err))
                {
                    AddWarning(model, "nodes 段第 " + (i + 1) + " 行格式错误，已跳过（" + err + "）：" + Truncate(t, 90));
                    i++;
                    continue;
                }
                if (!seenIndex.Add(bone.Index))
                    AddWarning(model, "nodes 段骨骼索引重复（Index=" + bone.Index + "，第 " + (i + 1) + " 行），映射可能不唯一");
                if (string.IsNullOrEmpty(bone.Name))
                    AddWarning(model, "nodes 段第 " + (i + 1) + " 行骨骼名为空");
                else if (!seenName.Add(bone.Name))
                    AddWarning(model, "nodes 段骨骼重名（\"" + bone.Name + "\"），GLB 往返后可能无法按名字还原");

                model.Bones.Add(bone);
                i++;
            }
            if (!closed) AddWarning(model, "nodes 段缺少 end（已读到文件末尾）");
            return i;
        }

        private static bool TryParseBoneLine(string line, out SmdBone bone, out string error)
        {
            bone = null;
            error = null;
            int q1 = line.IndexOf('"');
            if (q1 < 0) { error = "骨骼名缺少双引号"; return false; }
            int q2 = line.LastIndexOf('"');
            if (q2 <= q1) { error = "骨骼名缺少结束双引号"; return false; }

            int index;
            if (!int.TryParse(line.Substring(0, q1).Trim(), NumberStyles.Integer, Inv, out index))
            {
                error = "骨骼索引不是整数";
                return false;
            }
            string name = line.Substring(q1 + 1, q2 - q1 - 1);
            string rest = line.Substring(q2 + 1).Trim();
            int parent = -1;
            if (rest.Length > 0)
            {
                string[] parts = rest.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, Inv, out parent))
                {
                    error = "父骨骼索引不是整数";
                    return false;
                }
            }
            bone = new SmdBone { Index = index, Name = name, Parent = parent };
            return true;
        }

        /// <summary>解析 skeleton 段（保留原始姿态行，透传给写回）。返回下一行索引。</summary>
        private static int ParseSkeleton(string[] lines, int start, SmdModel model)
        {
            int i = start;
            bool closed = false;
            while (i < lines.Length)
            {
                string t = lines[i].Trim();
                if (t.Equals("end", StringComparison.OrdinalIgnoreCase)) { i++; closed = true; break; }
                if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal)) { i++; continue; }
                if (IsSectionOrVersionHeader(t))
                {
                    AddWarning(model, "skeleton 段缺少 end，遇到段头 \"" + t + "\" 自动结束（第 " + (i + 1) + " 行）");
                    return i;
                }
                if (t.StartsWith("time", StringComparison.OrdinalIgnoreCase)) { i++; continue; }

                if (!LooksLikePoseLine(t))
                {
                    AddWarning(model, "skeleton 段第 " + (i + 1) + " 行不是 6 个数字的姿态行，已跳过：" + Truncate(t, 90));
                    i++;
                    continue;
                }
                model.SkeletonPose.Add(t);
                i++;
            }
            if (!closed) AddWarning(model, "skeleton 段缺少 end（已读到文件末尾）");
            return i;
        }

        private static bool LooksLikePoseLine(string line)
        {
            string[] tk = line.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (tk.Length < 6) return false;
            for (int k = 0; k < 6; k++)
            {
                float f;
                if (!float.TryParse(tk[k], NumberStyles.Float, Inv, out f)) return false;
            }
            return true;
        }

        /// <summary>是否是段关键字（nodes/skeleton/triangles）。</summary>
        private static bool IsSectionKeyword(string trimmedLine)
        {
            return trimmedLine.Equals("nodes", StringComparison.OrdinalIgnoreCase) ||
                   trimmedLine.Equals("skeleton", StringComparison.OrdinalIgnoreCase) ||
                   trimmedLine.Equals("triangles", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>段关键字或 version 行（用于 nodes/skeleton 缺 end 时自动恢复）。</summary>
        private static bool IsSectionOrVersionHeader(string trimmedLine)
        {
            return IsSectionKeyword(trimmedLine) ||
                   trimmedLine.StartsWith("version", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 解析 triangles 段：材质行 + 3 个顶点行。
        /// 段内先收集所有非空/非注释行，再按“材质行 → 3 顶点行”分组；空行、注释、坏行都不会让整组错位。
        /// </summary>
        private static int ParseTriangles(string[] lines, int start, SmdModel model)
        {
            var candidateLines = new List<int>();
            int i = start;
            bool closed = false;
            while (i < lines.Length)
            {
                string t = lines[i].Trim();
                if (t.Equals("end", StringComparison.OrdinalIgnoreCase)) { i++; closed = true; break; }
                if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal)) { i++; continue; }
                if (IsSectionKeyword(t))
                {
                    AddWarning(model, "triangles 段缺少 end，遇到段头 \"" + t + "\" 自动结束（第 " + (i + 1) + " 行）");
                    break;
                }
                candidateLines.Add(i);
                i++;
            }
            if (!closed) AddWarning(model, "triangles 段缺少 end（已读到文件末尾）");

            int k = 0;
            while (k < candidateLines.Count)
            {
                int matLineNo = candidateLines[k] + 1;
                string matLine = lines[candidateLines[k]].Trim();

                SmdVertex stray;
                string strayErr;
                if (TryParseVertex(matLine, model, out stray, out strayErr))
                {
                    AddWarning(model, "第 " + matLineNo + " 行是顶点行但前面没有材质行，已跳过");
                    k++;
                    continue;
                }

                var verts = new List<SmdVertex>(3);
                int j = k + 1;
                while (j < candidateLines.Count && verts.Count < 3)
                {
                    int vLineNo = candidateLines[j] + 1;
                    string vLine = lines[candidateLines[j]].Trim();
                    SmdVertex v;
                    string verr;
                    if (TryParseVertex(vLine, model, out v, out verr))
                    {
                        verts.Add(v);
                        j++;
                        continue;
                    }
                    if (LooksLikeVertexLine(vLine))
                    {
                        AddWarning(model, "第 " + vLineNo + " 行顶点格式错误，已跳过（" + verr + "）：" + Truncate(vLine, 90));
                        j++;
                        continue;
                    }
                    break; // 下一组的材质行
                }

                if (verts.Count == 3)
                {
                    model.Triangles.Add(new SmdTriangle { Material = matLine, V0 = verts[0], V1 = verts[1], V2 = verts[2] });
                    k = j;
                }
                else
                {
                    AddWarning(model, "第 " + matLineNo + " 行的三角形不完整（材质 \"" + Truncate(matLine, 40) +
                                      "\" 后只找到 " + verts.Count + " 个顶点行），已跳过该三角形");
                    k = j;
                }
            }
            return i;
        }

        /// <summary>解析一行顶点：<c>bone x y z nx ny nz u v [links [bone weight]...]</c>。</summary>
        private static bool TryParseVertex(string line, SmdModel model, out SmdVertex vertex, out string error)
        {
            vertex = null;
            error = null;
            string[] tk = line.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (tk.Length < 9)
            {
                error = "字段不足（需要至少 9 个，实际 " + tk.Length + " 个）";
                return false;
            }

            int bone;
            if (!int.TryParse(tk[0], NumberStyles.Integer, Inv, out bone))
            {
                error = "主骨骼索引不是整数";
                return false;
            }

            var nums = new float[8];
            for (int n = 0; n < 8; n++)
            {
                if (!TryParseFloat(tk[1 + n], out nums[n]))
                {
                    error = "第 " + (n + 2) + " 个字段不是数字（" + Truncate(tk[1 + n], 30) + "）";
                    return false;
                }
            }

            var weights = new List<SmdWeight>();
            if (tk.Length >= 10)
            {
                int links;
                if (!int.TryParse(tk[9], NumberStyles.Integer, Inv, out links))
                {
                    error = "links 数量不是整数";
                    return false;
                }
                if (links < 0)
                {
                    AddWarning(model, "有顶点行的 links 数量为负数（" + links + "），按 0 处理");
                    links = 0;
                }
                int availablePairs = (tk.Length - 10) / 2;
                int pairs = Math.Min(links, availablePairs);
                if (pairs < links)
                    AddWarning(model, "有顶点行声明 " + links + " 个权重但只提供 " + availablePairs +
                                      " 组（骨骼/权重）字段，已读取前 " + pairs + " 组");

                for (int p = 0; p < pairs; p++)
                {
                    int bi;
                    float w;
                    if (!int.TryParse(tk[10 + p * 2], NumberStyles.Integer, Inv, out bi))
                    {
                        error = "第 " + (11 + p * 2) + " 个字段（权重骨骼）不是整数";
                        return false;
                    }
                    if (!TryParseFloat(tk[11 + p * 2], out w))
                    {
                        error = "第 " + (12 + p * 2) + " 个字段（权重）不是数字";
                        return false;
                    }
                    weights.Add(new SmdWeight { BoneIndex = bi, Weight = w });
                }
            }

            vertex = new SmdVertex
            {
                Bone = bone,
                X = nums[0],
                Y = nums[1],
                Z = nums[2],
                Nx = nums[3],
                Ny = nums[4],
                Nz = nums[5],
                U = nums[6],
                V = nums[7],
                Weights = weights
            };
            return true;
        }

        private static bool LooksLikeVertexLine(string line)
        {
            string[] tk = line.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (tk.Length < 4) return false;
            int b;
            if (!int.TryParse(tk[0], NumberStyles.Integer, Inv, out b)) return false;
            float f;
            return TryParseFloat(tk[1], out f) && TryParseFloat(tk[2], out f);
        }

        private static bool TryParseFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, Inv, out value);
        }

        // ══════════════════════════════════════════════════════════════════
        //  写出实现
        // ══════════════════════════════════════════════════════════════════

        private static void AppendVertex(StringBuilder sb, SmdVertex v)
        {
            if (v == null)
            {
                sb.Append("  0 0.000000 0.000000 0.000000 0.000000 0.000000 0.000000 0.000000 0.000000 0").Append(Nl);
                return;
            }

            var weights = new List<SmdWeight>();
            if (v.Weights != null)
            {
                foreach (var w in v.Weights) if (w != null) weights.Add(w);
            }

            int primary = weights.Count > 0 ? weights[0].BoneIndex : v.Bone;
            sb.Append("  ").Append(primary.ToString(Inv));
            sb.Append(' ').Append(F(v.X)).Append(' ').Append(F(v.Y)).Append(' ').Append(F(v.Z));
            sb.Append(' ').Append(F(v.Nx)).Append(' ').Append(F(v.Ny)).Append(' ').Append(F(v.Nz));
            sb.Append(' ').Append(F(v.U)).Append(' ').Append(F(v.V));
            // 始终写 links 数量（含 0）：标准 SMD 写法，修正旧版静态顶点 9 字段写法
            sb.Append(' ').Append(weights.Count.ToString(Inv));
            foreach (var w in weights)
                sb.Append(' ').Append(w.BoneIndex.ToString(Inv)).Append(' ').Append(F(w.Weight));
            sb.Append(Nl);
        }

        /// <summary>浮点格式：至少 6 位小数（最多 9 位）、InvariantCulture；NaN/Infinity 写 0（旧版行为）。</summary>
        internal static string F(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
            return value.ToString("0.000000###", Inv);
        }

        private static string NormalizeMaterial(string material)
        {
            if (string.IsNullOrEmpty(material)) return "material0";
            string m = material.Trim();
            return m.Length == 0 ? "material0" : m;
        }

        private static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(c == '"' || c == '\r' || c == '\n' || c == '\t' ? ' ' : c);
            return sb.ToString();
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static void AddWarning(SmdModel model, string message)
        {
            if (model == null || model.Warnings == null) return;
            if (model.Warnings.Count >= MaxWarnings)
            {
                if (model.Warnings.Count == MaxWarnings) model.Warnings.Add("（警告过多，后续省略）");
                return;
            }
            model.Warnings.Add(message);
        }
    }
}
