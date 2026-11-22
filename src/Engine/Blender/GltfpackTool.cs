using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace LodStudio.Engine
{
    /// <summary>
    /// 内嵌 gltfpack 的释放。
    ///
    /// gltfpack（meshoptimizer 项目，MIT）负责拓扑感知减面：只折叠原网格已有的边，
    /// 不做按空间位置的全量焊接，因此左右袜筒这类互不连通的部件不会被桥接。
    /// 二进制作为嵌入资源 LodStudio.gltfpack.exe 编进 exe，运行时按内容 SHA-1 前 8 位
    /// 释放到 %TEMP%\L4D2LOD远景工坊\backend\gltfpack_&lt;hash&gt;.exe。
    /// </summary>
    public static class GltfpackTool
    {
        public const string ResourceName = "LodStudio.gltfpack.exe";
        public const int StaleDays = 7;

        private static readonly object Gate = new object();
        private static byte[] _bytes;
        private static string _hash;
        private static string _cachedPath;
        private static bool _staleCleaned;

        public static byte[] LoadBytes()
        {
            lock (Gate)
            {
                if (_bytes != null) return _bytes;
                Assembly asm = typeof(GltfpackTool).Assembly;
                using (Stream s = asm.GetManifestResourceStream(ResourceName))
                {
                    if (s == null)
                        throw new InvalidOperationException(
                            "exe 内缺少嵌入资源 " + ResourceName + "（构建时需把 src/Assets/gltfpack.exe 编入程序集）");
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

        public static string ContentHash
        {
            get
            {
                LoadBytes();
                lock (Gate) return _hash;
            }
        }

        /// <summary>释放 gltfpack.exe 并返回路径；失败返回 null，error 为可读原因。</summary>
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

                    string path = Path.Combine(dir, "gltfpack_" + hash + ".exe");
                    if (File.Exists(path) && SameBytes(path, data))
                    {
                        _cachedPath = path;
                        return path;
                    }

                    string tmp = Path.Combine(dir, "gltfpack_" + hash + "_" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllBytes(tmp, data);
                    try
                    {
                        if (File.Exists(path)) FileUtil.TryDelete(path);
                        File.Move(tmp, path);
                    }
                    catch
                    {
                        if (SameBytes(path, data))
                        {
                            FileUtil.TryDelete(tmp);
                        }
                        else
                        {
                            string alt = Path.ChangeExtension(tmp, ".exe");
                            try { File.Move(tmp, alt); }
                            catch
                            {
                                try { File.Copy(tmp, alt, true); } catch { }
                                FileUtil.TryDelete(tmp);
                            }
                            if (!File.Exists(alt))
                            {
                                error = "无法释放 gltfpack：临时文件写入失败（" + dir + "）";
                                return null;
                            }
                            _cachedPath = alt;
                            return alt;
                        }
                    }

                    if (!File.Exists(path) || new FileInfo(path).Length != data.Length)
                    {
                        error = "gltfpack 释放不完整：" + path;
                        return null;
                    }
                    _cachedPath = path;
                    return path;
                }
                catch (Exception ex)
                {
                    error = "无法释放 gltfpack 到临时目录: " + ex.Message;
                    return null;
                }
            }
        }

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

        private static void CleanupStaleOnce(string dir, string keepHash)
        {
            if (_staleCleaned) return;
            _staleCleaned = true;
            DateTime now = DateTime.UtcNow;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "gltfpack_*"))
                {
                    try
                    {
                        string name = Path.GetFileName(f);
                        if (name.Equals("gltfpack_" + keepHash + ".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if ((now - File.GetLastWriteTimeUtc(f)).TotalDays > StaleDays) FileUtil.TryDelete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static string ExpectedPath
        {
            get { return Path.Combine(AppInfo.TempBackendDir, "gltfpack_" + ContentHash + ".exe"); }
        }
    }
}
