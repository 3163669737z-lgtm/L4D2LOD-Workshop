using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LodStudio.Engine
{
    /// <summary>
    /// nekomdl.exe（L4D2 的 studiomdl 分支）定位与试编译。
    ///
    /// 定位顺序：设置里的 hint → 注册表 Valve\Steam 的 InstallPath/SteamPath + steamapps\libraryfolders.vdf
    /// 里的所有 Steam 库 → 三个常见 Steam 安装路径（E:\SteamLibrary、C:\Program Files (x86)\Steam、D:\SteamLibrary）。
    /// 全部失败返回 null（试编译是可选功能，找不到不算运行失败）。
    ///
    /// Compile：工作目录 = QC 目录，参数只有 "&lt;QC 绝对路径&gt;"，stdout/stderr 按系统 ANSI（中文 GBK）读，
    /// 10 分钟超时；退出码 0 才算成功。编译后沿 $include 链找 $modelname，按候选布局定位**本次新生成**
    /// 的 .mdl 与同名 .dx90.vtx（时间戳 ≥ 编译开始 - 2s，大小写不敏感，递归兜底），再交给 VtxInspector 解析。
    /// </summary>
    public static class Nekomdl
    {
        /// <summary>编译超时（毫秒）：10 分钟</summary>
        public const int CompileTimeoutMs = 600000;

        private const string GameSubDir = @"steamapps\common\Left 4 Dead 2\bin\nekomdl.exe";

        // ───────────────────────────── 查找 ─────────────────────────────

        /// <summary>查找 nekomdl.exe（hint → Steam 库/注册表 → 常见路径）；找不到返回 null。</summary>
        public static string Find(string hint)
        {
            var cands = new List<string>();
            AddHint(cands, hint);

            // 常见安装位置（旧工具的用户群主要在这些盘）
            cands.Add(@"E:\SteamLibrary\" + GameSubDir);
            cands.Add(@"C:\Program Files (x86)\Steam\" + GameSubDir);
            cands.Add(@"D:\SteamLibrary\" + GameSubDir);

            // 注册表 + libraryfolders.vdf 里的 Steam 库
            foreach (string lib in SteamLibraries())
            {
                try { cands.Add(Path.Combine(lib, GameSubDir)); } catch { }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string c in cands)
            {
                if (string.IsNullOrEmpty(c)) continue;
                string full;
                try { full = Path.GetFullPath(c); } catch { continue; }
                if (!seen.Add(full)) continue;
                try { if (File.Exists(full)) return full; } catch { }
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
                    cands.Add(Path.Combine(h, "nekomdl.exe"));
                    return;
                }
            }
            catch { }
            cands.Add(h);
        }

        /// <summary>Steam 库根目录（注册表 InstallPath/SteamPath + libraryfolders.vdf 的 path 项；全部容错）。</summary>
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

            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var bk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = bk.OpenSubKey(@"SOFTWARE\Valve\Steam") ??
                                   bk.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                    {
                        if (k == null) continue;
                        var s = k.GetValue("InstallPath") as string;
                        if (string.IsNullOrEmpty(s)) s = k.GetValue("SteamPath") as string;
                        if (!string.IsNullOrEmpty(s)) roots.Add(s.Replace('/', '\\'));
                    }
                }
                catch { }
            }

            var libs = new List<string>();
            foreach (string r in roots)
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
                        if (p.Length >= 2 && p[1] == ':' && !ContainsIgnoreCase(libs, p)) libs.Add(p);
                    }
                }
                catch { }
            }
            return libs;
        }

        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            foreach (string s in list) if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ───────────────────────────── 试编译 ─────────────────────────────

        /// <summary>
        /// 编译一个 QC（工作目录 = QC 目录，超时 10 分钟），并定位本次产物 mdl/vtx 解析级数。
        /// 不抛异常：失败原因写在 Errors / Warnings 里。
        /// </summary>
        public static CompileResult Compile(string nekomdlExe, string qcPath)
        {
            var res = new CompileResult();
            if (string.IsNullOrEmpty(nekomdlExe) || !File.Exists(nekomdlExe))
            {
                res.Errors.Add("找不到编译器（nekomdl.exe），请在设置里指定路径");
                return res;
            }
            if (string.IsNullOrEmpty(qcPath))
            {
                res.Errors.Add("QC 路径为空");
                return res;
            }

            string qcFull;
            try { qcFull = Path.GetFullPath(qcPath); }
            catch (Exception ex)
            {
                res.Errors.Add("QC 路径无效：" + ex.Message);
                return res;
            }
            if (!File.Exists(qcFull))
            {
                res.Errors.Add("QC 文件不存在：" + qcFull);
                return res;
            }
            string qcDir;
            try { qcDir = Path.GetDirectoryName(qcFull) ?? ""; }
            catch (Exception ex)
            {
                res.Errors.Add("无法定位 QC 目录：" + ex.Message);
                return res;
            }

            // 只认本次编译产生（或更新）的 mdl/vtx；留 2 秒余量兼容 FAT/网络盘的时间戳精度
            DateTime compileStart = DateTime.Now.AddSeconds(-2);

            var outSb = new StringBuilder();
            var errSb = new StringBuilder();
            var ioLock = new object();
            bool timedOut = false;
            int exitCode = int.MinValue;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = nekomdlExe,
                    Arguments = "\"" + qcFull + "\"",
                    WorkingDirectory = qcDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    // nekomdl/studiomdl 输出走系统 ANSI 代码页（中文系统为 GBK）。
                    // 不能用 Encoding.Default：本程序清单 activeCodePage=UTF-8 会让它为 UTF-8（见 SystemAnsi）。
                    StandardOutputEncoding = SystemAnsi.Encoding,
                    StandardErrorEncoding = SystemAnsi.Encoding
                };

                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (ioLock) outSb.AppendLine(e.Data);
                    };
                    p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (ioLock) errSb.AppendLine(e.Data);
                    };

                    p.Start();
                    p.BeginOutputReadLine();                    p.BeginErrorReadLine();

                    if (!p.WaitForExit(CompileTimeoutMs))
                    {
                        timedOut = true;
                        try { if (!p.HasExited) p.Kill(); } catch { }
                        try { p.WaitForExit(5000); } catch { }
                    }
                    // 带超时的 WaitForExit 返回后异步输出可能还没排空，再等一次把管道读干净
                    try { if (!timedOut) p.WaitForExit(); } catch { }
                    try { exitCode = p.HasExited ? p.ExitCode : int.MinValue; } catch { exitCode = int.MinValue; }
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("启动编译器失败: " + ex.Message);
                return res;
            }

            lock (ioLock) res.StdOut = errSb.Length > 0 ? outSb.ToString() + "\n" + errSb.ToString() : outSb.ToString();

            if (timedOut)
            {
                res.Errors.Add("编译超时（10 分钟）");
                res.Success = false;
            }
            else
            {
                res.Success = exitCode == 0;
            }

            // 收集 ERROR / WARNING 行（大小写不敏感；只做子串匹配，不解析错误码/行号）
            foreach (string line in res.StdOut.Split('\n'))
            {
                string t = line.TrimEnd('\r', ' ', '\t');
                if (t.Length == 0) continue;
                if (t.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0) res.Errors.Add(t.Trim());
                else if (t.IndexOf("WARNING", StringComparison.OrdinalIgnoreCase) >= 0) res.Warnings.Add(t.Trim());
            }

            // 沿 $include 链找 $modelname（SeparateQc 模式的 _LOD.qc 可能只有一行 include）
            string modelName = "";
            try { modelName = QcParser.FindModelName(qcFull, 0); } catch { }

            if (string.IsNullOrEmpty(modelName))
            {
                if (res.Success)
                    res.Warnings.Add("没有找到 $modelname（沿 $include 链），无法定位本次编译产物，未做 vtx 校验");
                return res;
            }

            LocateProducts(res, qcDir, modelName, compileStart);
            return res;
        }

        /// <summary>
        /// 定位本次编译产物：qcDir\models\&lt;modelname&gt; 等四种布局 → 递归兜底；
        /// vtx = 同名 .dx90.vtx（大小写不敏感），同样要求"本次新生成"。
        /// </summary>
        private static void LocateProducts(CompileResult res, string qcDir, string modelName, DateTime compileStart)
        {
            string rel;
            try { rel = modelName.Replace('/', '\\'); }
            catch { rel = modelName; }
            string fileName;
            try { fileName = Path.GetFileName(rel); }
            catch { fileName = rel; }

            var cands = new List<string>();
            try { cands.Add(Path.Combine(qcDir, "models", rel)); } catch { }   // 最常见：studiomdl 展开到 models\ 下
            try { cands.Add(Path.Combine(qcDir, rel)); } catch { }             // QC 本身就在 models 目录里
            if (!string.IsNullOrEmpty(fileName))
            {
                try { cands.Add(Path.Combine(qcDir, "models", fileName)); } catch { }
                try { cands.Add(Path.Combine(qcDir, fileName)); } catch { }
            }

            bool sawStale = false;
            foreach (string c in cands)
            {
                try
                {
                    if (!File.Exists(c)) continue;
                    if (!IsFresh(c, compileStart)) { sawStale = true; continue; }
                    res.MdlPath = Path.GetFullPath(c);
                    break;
                }
                catch { }
            }

            // 兜底：按文件名在 QC 目录下递归找（编译产物可能改写大小写，Windows 大小写不敏感但这里显式比较）
            if (string.IsNullOrEmpty(res.MdlPath) && !string.IsNullOrEmpty(fileName))
            {
                try
                {
                    string hit = null;
                    foreach (string mdl in Directory.EnumerateFiles(qcDir, "*.mdl", SearchOption.AllDirectories))
                    {
                        try
                        {
                            if (!string.Equals(Path.GetFileName(mdl), fileName, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!IsFresh(mdl, compileStart)) { sawStale = true; continue; }
                            hit = mdl;
                            break;
                        }
                        catch { }
                    }
                    if (hit != null) res.MdlPath = Path.GetFullPath(hit);
                }
                catch { }
            }

            if (string.IsNullOrEmpty(res.MdlPath))
            {
                if (sawStale)
                    res.Warnings.Add("只找到旧的 mdl（修改时间早于本次编译），不是本次编译产物，未做 vtx 校验");
                else
                    res.Warnings.Add("没有找到本次编译生成的 .mdl（nekomdl 可能把产物写到游戏目录，本工具不做全盘搜索）");
                return;
            }

            // 找到 mdl 后，vtx 文件名以 mdl 为基础（编译产物可能改写大小写）
            string baseNoExt = Path.ChangeExtension(res.MdlPath, null);
            string vtx = baseNoExt + ".dx90.vtx";
            try
            {
                if (!File.Exists(vtx))
                {
                    string dir = Path.GetDirectoryName(res.MdlPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        string want = Path.GetFileName(baseNoExt) + ".dx90.vtx";
                        foreach (string f in Directory.EnumerateFiles(dir, "*.dx90.vtx"))
                        {
                            if (string.Equals(Path.GetFileName(f), want, StringComparison.OrdinalIgnoreCase)) { vtx = f; break; }
                        }
                    }
                }
            }
            catch { }

            try
            {
                if (File.Exists(vtx) && IsFresh(vtx, compileStart))
                {
                    res.VtxPath = vtx;
                    res.Vtx = VtxInspector.Read(vtx);
                }
                else
                {
                    res.Warnings.Add("找到本次编译的 mdl，但没有找到本次新生成/更新的 .dx90.vtx，未做 vtx 校验");
                }
            }
            catch (Exception ex)
            {
                res.Warnings.Add("读取 vtx 失败：" + ex.Message);
            }
        }

        private static bool IsFresh(string path, DateTime since)
        {
            try { return File.GetLastWriteTime(path) >= since; } catch { return false; }
        }
    }
}
