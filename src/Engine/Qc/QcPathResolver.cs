using System;
using System.Collections.Generic;
using System.IO;

namespace LodStudio.Engine
{
    /// <summary>
    /// QC 里写的模型路径 → 真实文件路径（旧版规则的精确复刻，决定 replacemodel 是否可用）。
    ///
    /// 旧版规则（真实工程验证过，不能"优化"掉，否则会漏模型）：
    ///  * 基目录候选：$cd/$pushd 后的当前目录 → 根 QC 目录 → 声明这个条目的文件所在目录；
    ///  * 每个基目录下依次尝试：base\路径（有扩展名）或 base\路径.glb、base\路径.smd（无扩展名，.glb 优先）；
    ///    base\文件名（去掉目录）；base\SMD\文件名；无扩展名再 base\SMD\文件名.glb；
    ///  * 最后追加根目录的上一级：up1\路径、up1\SMD\文件名；
    ///  * 全都找不到时，在根 QC 目录下按文件名递归搜：找到也只算 Fallback（Exists=false，不处理）并给警告。
    /// 第一步命中的候选即生效，顺序敏感。
    /// </summary>
    internal static class QcPathResolver
    {
        /// <summary>
        /// 解析一条 studio 引用的真实路径，把结果写进 <paramref name="entry"/>：
        /// RawFile/Name/ResolvedPath/Exists/Fallback。找不到时 Exists=false（Fallback 视是否按文件名兜底命中）。
        /// warnings 非空时追加"按文件名在别处找到"的警告。
        /// </summary>
        public static void Resolve(StudioRef entry, string raw, string declFile, string curDir, string rootDir,
                                   List<string> warnings)
        {
            if (entry == null) return;
            entry.RawFile = raw ?? "";
            string norm = entry.RawFile.Replace('/', '\\');
            entry.Name = SafeFileName(norm);
            entry.ResolvedPath = null;
            entry.Exists = false;
            entry.Fallback = false;

            bool noExt;
            try { noExt = Path.GetExtension(norm).Length == 0; }
            catch { noExt = true; }

            var bases = new List<string>();
            AddBase(bases, curDir);      // $cd / $pushd 之后的当前目录（注意：状态跨 include 文件共享）
            AddBase(bases, rootDir);     // 根 QC 目录（编译工作目录）
            if (!string.IsNullOrEmpty(declFile))
            {
                try { AddBase(bases, Path.GetDirectoryName(Path.GetFullPath(declFile))); }
                catch { }
            }

            var cands = new List<string>();
            var nameOnly = new List<bool>();   // true = 这个候选只用文件名（不是 QC 里写的路径）
            try
            {
                foreach (string b in bases)
                {
                    if (!noExt) { cands.Add(Path.Combine(b, norm)); nameOnly.Add(false); }
                    else
                    {
                        // 真实工程里有 "SMD\Acc_Kemomimi" 这种不写扩展名的；studiomdl 按 .smd 找，这里 .glb 优先
                        cands.Add(Path.Combine(b, norm + ".glb")); nameOnly.Add(false);
                        cands.Add(Path.Combine(b, norm + ".smd")); nameOnly.Add(false);
                    }
                    cands.Add(Path.Combine(b, entry.Name)); nameOnly.Add(true);
                    cands.Add(Path.Combine(Path.Combine(b, "SMD"), entry.Name)); nameOnly.Add(true);
                    if (noExt) { cands.Add(Path.Combine(Path.Combine(b, "SMD"), entry.Name + ".glb")); nameOnly.Add(true); }
                }

                string up1 = string.IsNullOrEmpty(rootDir) ? null : Path.GetDirectoryName(rootDir.TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(up1))
                {
                    cands.Add(Path.Combine(up1, norm)); nameOnly.Add(false);
                    cands.Add(Path.Combine(Path.Combine(up1, "SMD"), entry.Name)); nameOnly.Add(true);
                }
            }
            catch { }

            for (int ci = 0; ci < cands.Count; ci++)
            {
                string c = cands[ci];
                try
                {
                    if (File.Exists(c))
                    {
                        entry.ResolvedPath = Path.GetFullPath(c);
                        entry.Exists = true;
                        if (nameOnly[ci] && norm.IndexOf('\\') >= 0 && warnings != null)
                        {
                            warnings.Add("QC 里写的「" + entry.RawFile + "」不存在，按文件名在 " +
                                         Path.GetDirectoryName(entry.ResolvedPath) + " 找到 " + entry.Name +
                                         "（请确认不是同名错文件）");
                        }
                        return;
                    }
                }
                catch { }
            }

            // 兜底：在根 QC 目录下按文件名递归找 —— 可能是别处的同名文件，只提示、绝不参与处理
            try
            {
                if (!string.IsNullOrEmpty(rootDir) && Directory.Exists(rootDir))
                {
                    string wantName = noExt ? entry.Name + ".glb" : entry.Name;
                    string found = FirstFile(Directory.EnumerateFiles(rootDir, wantName, SearchOption.AllDirectories));
                    if (found != null)
                    {
                        entry.ResolvedPath = Path.GetFullPath(found);
                        entry.Fallback = true;
                        entry.Exists = false;
                        if (warnings != null)
                            warnings.Add("「" + entry.RawFile + "」按 QC 路径没找到；按文件名在别处找到同名文件 " +
                                         entry.ResolvedPath + "，为安全起见未处理");
                    }
                }
            }
            catch { }

            entry.Exists = false;
        }

        /// <summary>studio 是否"可参与批量处理"；不可处理时返回中文原因，否则 null。</summary>
        public static string IneligibleReason(StudioRef s)
        {
            if (s == null) return "空条目";
            if (s.Fallback) return "fallback";
            if (!s.Exists || string.IsNullOrEmpty(s.ResolvedPath)) return "missing";
            string ext;
            try { ext = Path.GetExtension(s.ResolvedPath).ToLowerInvariant(); }
            catch { ext = ""; }
            if (ext != ".glb" && ext != ".smd") return "nonglb";
            if (!string.IsNullOrEmpty(s.Innnode)) return "innode";
            return null;
        }

        /// <summary>两个路径是否指向同一文件（大小写不敏感；用于去重）。</summary>
        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddBase(List<string> bases, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            foreach (string x in bases)
                if (string.Equals(x, dir, StringComparison.OrdinalIgnoreCase)) return;
            bases.Add(dir);
        }

        private static string SafeFileName(string path)
        {
            try { return Path.GetFileName(path); }
            catch { return path; }
        }

        private static string FirstFile(IEnumerable<string> files)
        {
            if (files == null) return null;
            foreach (string f in files) return f;
            return null;
        }
    }
}
