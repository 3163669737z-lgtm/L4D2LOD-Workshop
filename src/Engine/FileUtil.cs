using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>
    /// 文件层工具：原子写、共享读、路径关系、可写测试、占用检测。
    /// 这一层被引擎所有模块使用，改动要非常谨慎（QC 安全依赖它）。
    /// </summary>
    public static class FileUtil
    {
        /// <summary>
        /// 原子替换目标文件：先写同目录临时文件，再 File.Replace；失败走保守路径。
        /// 任何失败都保证原文件仍在原处（或以 .old_xxx 保留），绝不删除唯一副本。
        /// 注意：目标目录必须已存在（调用方负责建目录）。
        /// </summary>
        public static void WriteAtomic(string targetPath, byte[] data)
        {
            if (targetPath == null) throw new ArgumentNullException("targetPath");
            if (data == null) data = new byte[0];
            string target = Path.GetFullPath(targetPath);
            // 只读文件一律拒绝修改（否则"改名后写入"的兜底会悄悄覆盖用户只读文件）
            try
            {
                if (File.Exists(target))
                {
                    FileAttributes attrs = File.GetAttributes(target);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        throw new IOException("目标是只读文件，已拒绝修改：" + Path.GetFileName(target) + "（请先去掉只读属性）");
                }
            }
            catch (IOException) { throw; }
            catch { }
            string tag = Guid.NewGuid().ToString("N").Substring(0, 8);
            string tmp = target + ".tmp_" + tag;

            try { File.WriteAllBytes(tmp, data); }
            catch
            {
                TryDelete(tmp);
                throw;
            }

            if (!File.Exists(target))
            {
                try { File.Move(tmp, target); return; }
                catch { TryDelete(tmp); throw; }
            }

            Exception first;
            try
            {
                File.Replace(tmp, target, null);
                return;
            }
            catch (Exception ex) { first = ex; }

            if (!File.Exists(target) && File.Exists(tmp))
            {
                try { File.Move(tmp, target); return; } catch { }
            }

            string old = target + ".old_" + tag;
            bool movedAway = false;
            try
            {
                File.Move(target, old);
                movedAway = true;
                File.Move(tmp, target);
                TryDelete(old);
                return;
            }
            catch (Exception second)
            {
                bool restored = true;
                if (movedAway && !File.Exists(target))
                {
                    try { File.Move(old, target); }
                    catch { restored = false; }
                }
                if (File.Exists(target) || File.Exists(old)) TryDelete(tmp);
                string msg = "写入 " + Path.GetFileName(target) + " 失败（文件可能被占用或只读），原文件未改动：" +
                             first.Message + " / " + second.Message;
                if (!restored) msg += "。原文件已保留为 " + Path.GetFileName(old) + "，请手动改回原名。";
                throw new IOException(msg, second);
            }
        }

        /// <summary>路径的稳定短哈希（用于临时文件名，避免不同目录同名模型互相覆盖）。</summary>
        public static string ShortPathHash(string path)
        {
            try
            {
                string full = Path.GetFullPath(path ?? "").ToLowerInvariant();
                using (var sha = System.Security.Cryptography.SHA1.Create())
                {
                    byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(full));
                    var sb = new StringBuilder(8);
                    for (int i = 0; i < 4; i++) sb.Append(h[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return Guid.NewGuid().ToString("N").Substring(0, 8);
            }
        }

        public static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>允许其它程序同时读写的读取（QC 可能正被编辑器打开）</summary>
        public static byte[] ReadAllBytesShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public static string[] ReadAllLinesShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true))
            {
                var list = new List<string>();
                string line;
                while ((line = sr.ReadLine()) != null) list.Add(line);
                return list.ToArray();
            }
        }

        /// <summary>目录是否可写（创建并删除一个探针文件；不留下垃圾）</summary>
        public static string TestWritable(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return "目录为空";
                if (!Directory.Exists(dir)) return "目录不存在";
                string probe = Path.Combine(dir, ".lodstudio_write_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) fs.WriteByte(1);
                TryDelete(probe);
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>文件是否被其他进程以写方式占用（以读写独占打开探测，不改内容）</summary>
        public static bool IsLocked(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return false; }
            catch { return false; }
        }

        /// <summary>
        /// 安全写文件（给模型/LOD 输出用）：目标被占用时给可读原因。
        /// 返回 null 成功，否则中文错误。
        /// </summary>
        public static string WriteBytesSafe(string path, byte[] data)
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(path))
                {
                    try
                    {
                        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                            fs.Write(data, 0, data.Length);
                        return null;
                    }
                    catch (IOException)
                    {
                        try { File.Delete(path); }
                        catch
                        {
                            return "文件被占用，请关闭正在使用它的程序（Blender / 资源管理器预览 / 杀软扫描）后重试：" + Path.GetFileName(path);
                        }
                    }
                }
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    fs.Write(data, 0, data.Length);
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return "没有写入权限：" + Path.GetFileName(path) + "（目录只读或被安全软件拦截）";
            }
            catch (IOException ex) { return "写入失败：" + Path.GetFileName(path) + " —— " + ex.Message; }
            catch (Exception ex) { return "写入失败：" + Path.GetFileName(path) + " —— " + ex.GetType().Name + ": " + ex.Message; }
        }

        /// <summary>从 fromDir 到 target 的相对路径（Windows 风格反斜杠，用于 QC 里的 replacemodel）</summary>
        public static string MakeRelative(string fromDir, string targetPath)
        {
            try
            {
                string from = Path.GetFullPath(fromDir).TrimEnd('\\') + "\\";
                string to = Path.GetFullPath(targetPath);

                string fromRoot = Path.GetPathRoot(from) ?? "";
                string toRoot = Path.GetPathRoot(to) ?? "";
                if (!string.Equals(fromRoot, toRoot, StringComparison.OrdinalIgnoreCase)) return to;

                string[] fp = from.Substring(fromRoot.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                string[] tp = to.Substring(toRoot.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                int i = 0;
                while (i < fp.Length && i < tp.Length && string.Equals(fp[i], tp[i], StringComparison.OrdinalIgnoreCase)) i++;

                var sb = new StringBuilder();
                for (int k = i; k < fp.Length; k++) sb.Append("..\\");
                for (int k = i; k < tp.Length; k++)
                {
                    sb.Append(tp[k]);
                    if (k < tp.Length - 1) sb.Append('\\');
                }
                string rel = sb.ToString();
                return rel.Length == 0 ? to : rel;
            }
            catch { return targetPath; }
        }

        /// <summary>相对 baseDir 的展示路径（报告/日志用；不在其下则返回绝对路径）</summary>
        public static string RelativeDisplay(string baseDir, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(path)) return path;
                string b = Path.GetFullPath(baseDir).TrimEnd('\\') + "\\";
                string p = Path.GetFullPath(path);
                if (p.StartsWith(b, StringComparison.OrdinalIgnoreCase)) return p.Substring(b.Length);
                return p;
            }
            catch { return path; }
        }

        /// <summary>确保目录存在；失败返回 false（不抛）</summary>
        public static bool EnsureDir(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return false;
                if (Directory.Exists(dir)) return true;
                Directory.CreateDirectory(dir);
                return true;
            }
            catch { return false; }
        }

        /// <summary>规范化路径：绝对路径 + 去掉末尾分隔符；失败返回原值</summary>
        public static string Normalize(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return path;
                return Path.GetFullPath(path).TrimEnd('\\', '/');
            }
            catch { return path; }
        }

        /// <summary>拼接长路径前缀，避免 &gt;260 字符失败（Win10 1607+ 且 manifest longPathAware 时通常无需，但兜底）</summary>
        public static string LongPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return path;
                string full = Path.GetFullPath(path);
                if (full.Length < 250 || full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
                return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
            }
            catch { return path; }
        }

        /// <summary>读文本文件时探测每行结尾：返回 "\r\n" 或 "\n"</summary>
        public static string DetectNewLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "\r\n";
            return text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
        }

        /// <summary>把文本统一到指定换行</summary>
        public static string NormalizeNewLines(string text, string newLine)
        {
            if (text == null) return "";
            return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", newLine ?? "\r\n");
        }

        /// <summary>目录大小（诊断用；失败返回 0）</summary>
        public static long DirectorySize(string dir)
        {
            try
            {
                long total = 0;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                return total;
            }
            catch { return 0; }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        }
    }
}
