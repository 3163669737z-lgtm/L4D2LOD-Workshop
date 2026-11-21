using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LodStudio.Engine.Platform;
using Microsoft.Win32;

namespace LodStudio.Engine
{
    /// <summary>
    /// Blender 定位与探测：
    ///  - Find：hint → 注册表 App Paths（HKLM/HKCU × 32/64 视图）→ 常见安装目录（Blender Foundation、
    ///    D:\ 下的 Blender*，新版本优先）→ Steam 库（libraryfolders.vdf 解析）；
    ///  - ProbeVersion：后台跑 `blender --factory-startup --version`，stdout/stderr 都异步读防死锁，
    ///    15s 超时；失败返回 null；
    ///  - IsUsable：存在且 ProbeVersion 成功。
    /// 全部方法都不抛异常（失败返回 null/false），可在任意后台线程调用。
    /// </summary>
    public static class BlenderLocator
    {
        /// <summary>版本探测默认超时（毫秒）</summary>
        public const int DefaultProbeTimeoutMs = 15000;

        // ─────────────────────────────────────────────────────────────────────
        //  Find
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>查找 blender.exe：hint → 注册表 App Paths → 常见安装目录 → Steam 库；找不到返回 null。</summary>
        public static string Find(string hint)
        {
            var cands = new List<string>();
            AddHint(cands, hint);
            AddRegistryAppPaths(cands);

            // Windows 上 GetDirectories 的通配符不区分大小写，"blender 4.1" 也会命中 "Blender*"
            foreach (var root in new[]
            {
                @"C:\Program Files\Blender Foundation",
                @"D:\",
                @"C:\Program Files",
                @"D:\Program Files\Blender Foundation"
            })
            {
                AddVersionDirs(cands, root);
            }

            foreach (var lib in SteamLibraries())
            {
                try { cands.Add(Path.Combine(lib, @"steamapps\common\Blender\blender.exe")); } catch { }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cands)
            {
                if (string.IsNullOrEmpty(c)) continue;
                string p;
                try { p = Path.GetFullPath(c); } catch { continue; }
                if (!seen.Add(p)) continue;
                try { if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }

        private static void AddHint(List<string> cands, string hint)
        {
            if (string.IsNullOrEmpty(hint)) return;
            string h;
            try { h = Environment.ExpandEnvironmentVariables(hint.Trim().Trim('"')); }
            catch { h = hint; }
            if (string.IsNullOrEmpty(h)) return;
            try
            {
                if (Directory.Exists(h))
                {
                    cands.Add(Path.Combine(h, "blender.exe"));
                    return;
                }
            }
            catch { }
            cands.Add(h);
        }

        private static void AddVersionDirs(List<string> cands, string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                var dirs = Directory.GetDirectories(root, "Blender*");
                // 新版本优先：先按目录名里的版本号降序，再按名字降序兜底（"Blender 4.10" 这类也能稳定比较）
                Array.Sort(dirs, delegate (string a, string b)
                {
                    int c = CompareVersion(Path.GetFileName(b), Path.GetFileName(a));
                    if (c != 0) return c;
                    return string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a));
                });
                foreach (var d in dirs) cands.Add(Path.Combine(d, "blender.exe"));
            }
            catch { }
        }

        private static readonly Regex VersionInName = new Regex(@"(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant);

        private static int CompareVersion(string a, string b)
        {
            int[] va = ParseVersion(a), vb = ParseVersion(b);
            for (int i = 0; i < 3; i++)
            {
                if (va[i] != vb[i]) return va[i].CompareTo(vb[i]);
            }
            return 0;
        }

        private static int[] ParseVersion(string s)
        {
            var r = new int[3];
            if (!string.IsNullOrEmpty(s))
            {
                var m = VersionInName.Match(s);
                if (m.Success)
                {
                    int.TryParse(m.Groups[1].Value, out r[0]);
                    int.TryParse(m.Groups[2].Value, out r[1]);
                    if (m.Groups[3].Success) int.TryParse(m.Groups[3].Value, out r[2]);
                }
            }
            return r;
        }

        private static void AddRegistryAppPaths(List<string> cands)
        {
            const string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\blender.exe";
            var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
            foreach (var h in hives)
            {
                foreach (var v in views)
                {
                    try
                    {
                        using (var bk = RegistryKey.OpenBaseKey(h, v))
                        using (var k = bk.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            var exe = k.GetValue("") as string;
                            if (!string.IsNullOrEmpty(exe)) cands.Add(Environment.ExpandEnvironmentVariables(exe.Trim().Trim('"')));
                            var dir = k.GetValue("Path") as string;
                            if (!string.IsNullOrEmpty(dir)) cands.Add(Path.Combine(dir.Trim().Trim('"'), "blender.exe"));
                        }
                    }
                    catch { }
                }
            }
        }

        /// <summary>Steam 库根目录（注册表 SteamPath/InstallPath + libraryfolders.vdf 的 path 项）</summary>
        private static List<string> SteamLibraries()
        {
            var roots = new List<string>();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (k != null)
                    {
                        var s = k.GetValue("SteamPath") as string;
                        if (!string.IsNullOrEmpty(s)) roots.Add(s.Replace('/', '\\'));
                    }
                }
            }
            catch { }
            foreach (var v in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var bk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, v))
                    using (var k = bk.OpenSubKey(@"SOFTWARE\Valve\Steam") ?? bk.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                    {
                        if (k == null) continue;
                        var s = k.GetValue("InstallPath") as string;
                        if (!string.IsNullOrEmpty(s)) roots.Add(s);
                    }
                }
                catch { }
            }

            var libs = new List<string>();
            foreach (var r in roots)
            {
                if (string.IsNullOrEmpty(r) || ContainsIgnoreCase(libs, r)) continue;
                libs.Add(r);
                try
                {
                    string vdf = Path.Combine(r, @"steamapps\libraryfolders.vdf");
                    if (!File.Exists(vdf)) continue;
                    string txt = File.ReadAllText(vdf);
                    // 新格式: "path"  "D:\\SteamLibrary"；旧格式: "1"  "D:\\SteamLibrary"
                    foreach (Match m in Regex.Matches(txt, "\"(?:path|\\d+)\"\\s+\"([^\"]+)\""))
                    {
                        string p = m.Groups[1].Value.Replace("\\\\", "\\");
                        // 只认盘符路径（排除 "contentid"、"totalsize" 之类的数字键误配）
                        if (p.Length >= 2 && p[1] == ':' && !ContainsIgnoreCase(libs, p)) libs.Add(p);
                    }
                }
                catch { }
            }
            return libs;
        }

        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            foreach (var s in list) if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ProbeVersion / IsUsable
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>跑 `blender --factory-startup --version` 读版本行（如 "Blender 4.1.1"）；失败返回 null。</summary>
        public static string ProbeVersion(string exe, int timeoutMs)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            try { if (!File.Exists(exe)) return null; } catch { return null; }
            if (timeoutMs <= 0) timeoutMs = DefaultProbeTimeoutMs;

            var lines = new List<string>();
            Process p = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--factory-startup --version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                p = new Process { StartInfo = psi };
                p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (lines) lines.Add(e.Data);
                };
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { };
                p.Start();
                JobObject.Assign(p);
                BlenderScheduler.TrackProcess(p);
                try { p.BeginOutputReadLine(); p.BeginErrorReadLine(); } catch { }
                if (p.WaitForExit(timeoutMs)) p.WaitForExit();   // 无参再等一次：把异步输出排空
                else KillAndWait(p, 3000);
            }
            catch { return null; }
            finally
            {
                if (p != null)
                {
                    BlenderScheduler.UntrackProcess(p);
                    try { p.Dispose(); } catch { }
                }
            }

            lock (lines)
            {
                // 优先取 "Blender x.y.z" 行；否则第一条非 TBBmalloc 的非空行（老版本/异常输出兜底）
                foreach (var l in lines)
                {
                    var t = (l ?? "").Trim();
                    if (t.StartsWith("Blender ", StringComparison.OrdinalIgnoreCase)) return t;
                }
                foreach (var l in lines)
                {
                    var t = (l ?? "").Trim();
                    if (t.Length > 0 && !t.StartsWith("TBBmalloc", StringComparison.OrdinalIgnoreCase)) return t;
                }
            }
            return null;
        }

        /// <summary>exe 是否存在且能启动（版本探测成功）</summary>
        public static bool IsUsable(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            try { if (!File.Exists(exe)) return false; } catch { return false; }
            return ProbeVersion(exe, DefaultProbeTimeoutMs) != null;
        }

        private static void KillAndWait(Process p, int waitMs)
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
            try { p.WaitForExit(waitMs); } catch { }
        }
    }
}
