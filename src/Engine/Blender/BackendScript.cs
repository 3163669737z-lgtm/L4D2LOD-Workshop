using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>
    /// 内嵌 Blender 后端脚本的释放。
    ///
    /// 脚本唯一源码是 src/Assets/blender_backend.py，构建时以嵌入资源
    /// LodStudio.blender_backend.py 编进 exe；运行时按内容 SHA-1 前 8 位释放到
    /// %TEMP%\L4D2LOD远景工坊\backend\blender_backend_&lt;hash&gt;.py：
    ///  - 同名且内容相同 → 直接复用（多线程/多进程并发安全）；
    ///  - 内容不同（旧版本残留/被截断）→ 先写唯一临时文件再 File.Move 原子替换；
    ///  - 并发竞态（别的进程刚写好同名文件）→ 校验内容，相同则删自己的临时文件复用，不同则退用唯一命名副本；
    ///  - 每个进程启动后清理超过 7 天的旧脚本（一次性），避免临时目录无限膨胀。
    /// </summary>
    public static class BackendScript
    {
        /// <summary>嵌入资源名（csproj 的 LogicalName，改动要同步改 csproj —— 由主控统一改）</summary>
        public const string ResourceName = "LodStudio.blender_backend.py";

        /// <summary>旧脚本保留天数（超过即清理；当前版本永不清理）</summary>
        public const int StaleDays = 7;

        private static readonly object Gate = new object();
        private static byte[] _bytes;
        private static string _hash;
        private static string _cachedPath;
        private static bool _staleCleaned;

        // ─────────────────────────────────────────────────────────────────────
        //  公开接口
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 读取嵌入的后端脚本字节。读不到时抛 InvalidOperationException（调用方 EnsureExtracted 转成可读错误）。
        /// 同一进程内只读一次并缓存。
        /// </summary>
        public static byte[] LoadBytes()
        {
            lock (Gate)
            {
                if (_bytes != null) return _bytes;
                Assembly asm = typeof(BackendScript).Assembly;
                using (Stream s = asm.GetManifestResourceStream(ResourceName))
                {
                    if (s == null)
                        throw new InvalidOperationException(
                            "exe 内缺少嵌入资源 " + ResourceName + "（构建时需把 src/Assets/blender_backend.py 编入程序集）");
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        _bytes = ms.ToArray();
                    }
                }
                if (_bytes.Length == 0)
                    throw new InvalidOperationException("嵌入资源 " + ResourceName + " 为空");
                _hash = Sha1Short(_bytes);
                return _bytes;
            }
        }

        /// <summary>后端脚本内容 SHA-1 前 8 位（小写十六进制，用于文件名）</summary>
        public static string ContentHash
        {
            get
            {
                LoadBytes();
                lock (Gate) return _hash;
            }
        }

        /// <summary>
        /// 释放到 %TEMP%\L4D2LOD远景工坊\backend\blender_backend_&lt;hash&gt;.py 并返回路径；
        /// 失败返回 null，error 为可读中文原因。
        /// </summary>
        public static string EnsureExtracted(out string error)
        {
            error = null;
            lock (Gate)
            {
                if (_cachedPath != null)
                {
                    try { if (File.Exists(_cachedPath)) return _cachedPath; } catch { }
                    _cachedPath = null;
                }

                byte[] data;
                try { data = LoadBytes(); }
                catch (Exception ex) { error = ex.Message; return null; }

                string hash = _hash;
                try
                {
                    string dir = AppInfo.TempBackendDir;
                    Directory.CreateDirectory(dir);
                    CleanupStaleOnce(dir, hash);

                    string path = Path.Combine(dir, "blender_backend_" + hash + ".py");
                    if (File.Exists(path) && SameBytes(path, data))
                    {
                        _cachedPath = path;
                        return path;
                    }

                    // 写唯一临时文件，再 File.Move 到目标名（Move 在同一卷内是原子替换；
                    // 目标已存在时 Move 会抛 → 走竞态处理）
                    string tmp = Path.Combine(dir, "blender_backend_" + hash + "_" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllBytes(tmp, data);
                    try
                    {
                        if (File.Exists(path)) FileUtil.TryDelete(path);   // 内容不同：旧文件（截断/被篡改）
                        File.Move(tmp, path);
                    }
                    catch
                    {
                        // 并发竞态：别的进程/线程刚写好同名文件
                        if (SameBytes(path, data))
                        {
                            FileUtil.TryDelete(tmp);
                        }
                        else
                        {
                            // 目标被占用且内容不同：退用唯一命名的副本（扩展名改成 .py 才能被 Blender 执行）
                            string alt = Path.ChangeExtension(tmp, ".py");
                            try { File.Move(tmp, alt); }
                            catch
                            {
                                try { File.Copy(tmp, alt, true); } catch { }
                                FileUtil.TryDelete(tmp);
                            }
                            if (!File.Exists(alt))
                            {
                                error = "无法释放后端脚本：临时文件写入失败（" + dir + "）";
                                return null;
                            }
                            _cachedPath = alt;
                            return alt;
                        }
                    }

                    if (!File.Exists(path) || new FileInfo(path).Length != data.Length)
                    {
                        error = "后端脚本释放不完整：" + path;
                        return null;
                    }
                    _cachedPath = path;
                    return path;
                }
                catch (Exception ex)
                {
                    error = "无法释放后端脚本到临时目录: " + ex.Message;
                    return null;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  内部实现
        // ─────────────────────────────────────────────────────────────────────

        private static string Sha1Short(byte[] data)
        {
            using (var sha = SHA1.Create())
            {
                string hex = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
                return hex.Substring(0, 8).ToLowerInvariant();
            }
        }

        private static bool SameBytes(string path, byte[] data)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length != data.Length) return false;
                byte[] onDisk = File.ReadAllBytes(path);
                if (onDisk.Length != data.Length) return false;
                for (int i = 0; i < data.Length; i++) if (onDisk[i] != data[i]) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>每个进程只清理一次：删除 backend 目录里超过 7 天且不是当前版本的脚本/临时文件</summary>
        private static void CleanupStaleOnce(string dir, string keepHash)
        {
            if (_staleCleaned) return;
            _staleCleaned = true;
            DateTime now = DateTime.UtcNow;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "blender_backend_*"))
                {
                    try
                    {
                        string name = Path.GetFileName(f);
                        if (name.Equals("blender_backend_" + keepHash + ".py", StringComparison.OrdinalIgnoreCase)) continue;
                        if ((now - File.GetLastWriteTimeUtc(f)).TotalDays > StaleDays) FileUtil.TryDelete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>（自测/诊断）当前内容哈希对应的目标路径（不释放）</summary>
        public static string ExpectedPath
        {
            get { return Path.Combine(AppInfo.TempBackendDir, "blender_backend_" + ContentHash + ".py"); }
        }

        /// <summary>（自测/诊断）脚本的 UTF-8 文本行数</summary>
        public static int SourceLineCount
        {
            get
            {
                byte[] b = LoadBytes();
                int n = 0;
                foreach (byte x in b) if (x == (byte)'\n') n++;
                return n;
            }
        }

        /// <summary>后端脚本的 UTF-8 文本（自测断言协议关键字用）</summary>
        public static string SourceText
        {
            get { return Encoding.UTF8.GetString(LoadBytes()); }
        }
    }
}
