using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace LodStudio.Engine
{
    /// <summary>
    /// 环境诊断：把"出问题时最需要知道的事实"拼成一段可复制的纯文本，供用户发给开发者。
    ///
    /// 内容：版本/时间/用户/管理员/低完整性/位数/系统/.NET/处理器/界面缩放、目录可写测试（✔/✘ + 原因）、
    /// 引擎（Blender 路径+版本、编译器路径+存在性）、当前方案摘要、设置文件路径、临时目录、
    /// 磁盘剩余空间、长路径支持提示，最后请用户"把这段发给开发者"。
    ///
    /// 任何一项读取失败都退化成说明文字，Build 本身绝不抛异常；Engine 层不引用 WPF
    /// （界面缩放用 GetDpiForSystem / 注册表 / 96 DPI 兜底，不读 SystemParameters）。
    /// </summary>
    public static class EnvironmentDiagnostics
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Build(AppSettings settings, IReadOnlyList<string> probeDirs, string blenderVersion,
                                   bool isAdmin, bool isLowIntegrity)
        {
            var sb = new StringBuilder(4096);
            try
            {
                sb.AppendLine("═══════════ 环境诊断 ═══════════");
                sb.AppendLine("工具版本: v" + AppInfo.Version + "（远景工坊 · L4D2 自动 LOD 工具）");
                sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv));

                Section(sb, "身份与系统", b => WriteIdentity(b, isAdmin, isLowIntegrity));
                Section(sb, "目录写入测试", b => WriteDirectoryTests(b, probeDirs));
                Section(sb, "引擎", b => WriteEngines(b, settings, blenderVersion));
                Section(sb, "当前方案", b => WritePlan(b, settings));
                Section(sb, "路径与存储", b => WritePaths(b, probeDirs));
                Section(sb, "磁盘剩余空间", b => WriteDisks(b, probeDirs));
                Section(sb, "长路径支持", b => WriteLongPaths(b));

                sb.AppendLine("──── 提示 ────");
                try
                {
                    if (isLowIntegrity)
                        sb.AppendLine("⚠ 当前是**低完整性**运行：程序写不进普通目录，QC / 报告 / 备份都会失败。" +
                                      "请从资源管理器正常双击启动，不要从下载器/沙箱/某些启动器里运行。");
                    if (isAdmin)
                        sb.AppendLine("· 当前以管理员身份运行（正常）。注意：用管理员改过的 QC 文件属主可能变化，之后普通权限运行会写不进去。");
                    else
                        sb.AppendLine("· 当前不是管理员：如果工程在 Program Files 下、或试编译要写游戏目录，" +
                                      "可能需要右键「以管理员身份运行」。");
                    sb.AppendLine("· 如果上面有 ✘ 不可写目录，先给该目录写权限（或换到用户目录下），再重试。");
                }
                catch { sb.AppendLine("（提示生成失败）"); }
                sb.AppendLine();

                sb.AppendLine("把这段发给开发者：复制以上全部内容（含版本、系统、目录与引擎信息）发给他，可以更快定位问题。");
                sb.AppendLine("═══════════════════════════════");
            }
            catch (Exception ex)
            {
                try { sb.AppendLine("（诊断生成时发生错误：" + ex.GetType().Name + ": " + ex.Message + "）"); } catch { }
            }
            return sb.ToString();
        }

        // ───────────────────────────── 各节 ─────────────────────────────

        private static void Section(StringBuilder sb, string title, Action<StringBuilder> body)
        {
            sb.AppendLine();
            sb.AppendLine("──── " + title + " ────");
            try { body(sb); }
            catch (Exception ex) { sb.AppendLine("（这一节读取失败：" + ex.GetType().Name + ": " + ex.Message + "）"); }
        }

        private static void WriteIdentity(StringBuilder sb, bool isAdmin, bool isLowIntegrity)
        {
            string user = "（无法读取）";
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    if (id != null) user = id.Name;
            }
            catch { }

            sb.AppendLine("用户: " + user);
            sb.AppendLine("管理员: " + (isAdmin ? "是" : "否"));
            sb.AppendLine("低完整性运行: " +
                          (isLowIntegrity ? "是（会导致哪里都写不了，必须换正常方式启动）" : "否"));
            sb.AppendLine("进程位数: " + AppInfo.Bitness + "（" + (IntPtr.Size == 8 ? "x64" : "x86") + "）");
            try { sb.AppendLine("系统: " + Environment.OSVersion.VersionString); }
            catch { sb.AppendLine("系统: （无法读取）"); }
            sb.AppendLine(".NET: " + DotNetText());
            try { sb.AppendLine("逻辑处理器: " + Environment.ProcessorCount.ToString(Inv) + " 个"); }
            catch { sb.AppendLine("逻辑处理器: （无法读取）"); }
            sb.AppendLine("界面缩放: " + ScaleText());
        }

        private static string DotNetText()
        {
            string clr;
            try { clr = Environment.Version.ToString(); } catch { clr = "未知"; }
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (k != null)
                    {
                        object release = k.GetValue("Release");
                        int n;
                        if (release != null && int.TryParse(release.ToString(), NumberStyles.Integer, Inv, out n))
                            return "4.x（Release " + n.ToString(Inv) + "，CLR " + clr + "）";
                    }
                }
            }
            catch { }
            return "CLR " + clr;
        }

        /// <summary>界面缩放：GetDpiForSystem → 注册表 AppliedDPI → LogPixels → 96 DPI 兜底（不引 WPF）。</summary>
        private static string ScaleText()
        {
            int dpi = 0;
            string source = "";
            try
            {
                if (Environment.OSVersion.Version >= new Version(6, 3))
                {
                    dpi = (int)GetDpiForSystem();
                    if (dpi > 0) source = "系统 DPI " + dpi.ToString(Inv);
                }
            }
            catch { dpi = 0; }

            if (dpi <= 0)
            {
                dpi = ReadDword(@"Control Panel\Desktop\WindowMetrics", "AppliedDPI");
                if (dpi > 0) source = "注册表 AppliedDPI " + dpi.ToString(Inv);
            }
            if (dpi <= 0)
            {
                dpi = ReadDword(@"Control Panel\Desktop", "LogPixels");
                if (dpi > 0) source = "注册表 LogPixels " + dpi.ToString(Inv);
            }

            if (dpi <= 0) return "100%（按 96 DPI 计算；无法读取当前 DPI，可忽略）";
            int pct = (int)Math.Round(dpi * 100.0 / 96.0, MidpointRounding.AwayFromZero);
            return pct.ToString(Inv) + "%（" + source + "）";
        }

        private static int ReadDword(string subKey, string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(subKey))
                {
                    if (k == null) return 0;
                    object v = k.GetValue(name);
                    if (v == null) return 0;
                    int n;
                    if (int.TryParse(v.ToString(), NumberStyles.Integer, Inv, out n)) return n;
                }
            }
            catch { }
            return 0;
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        private static void WriteDirectoryTests(StringBuilder sb, IReadOnlyList<string> probeDirs)
        {
            var dirs = new List<string>();
            try
            {
                if (probeDirs != null)
                {
                    foreach (string d in probeDirs)
                    {
                        AddDistinct(dirs, d);
                        if (dirs.Count >= 8) break;
                    }
                }
            }
            catch { }

            AddDistinct(dirs, AppInfo.ExeDir);
            try { AddDistinct(dirs, AppInfo.TempRoot); } catch { }
            try { AddDistinct(dirs, Environment.GetFolderPath(Environment.SpecialFolder.Desktop)); } catch { }
            try { AddDistinct(dirs, AppInfo.SettingsDir); } catch { }

            if (dirs.Count == 0)
            {
                sb.AppendLine("（没有可测试的目录）");
                return;
            }

            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string reason = FileUtil.TestWritable(dir);
                if (reason == null)
                {
                    sb.AppendLine("✔ 可写   " + dir);
                }
                else
                {
                    sb.AppendLine("✘ 不可写 " + dir);
                    sb.AppendLine("          原因: " + reason);
                }
            }
        }

        private static void WriteEngines(StringBuilder sb, AppSettings settings, string blenderVersion)
        {
            bool gpMode = settings != null && settings.ReduceBackend == (int)ReduceBackend.Gltfpack;
            sb.AppendLine("减面后端: " + (gpMode ? "内置 gltfpack（无需 Blender，实验）" : "Blender"));

            string blender = settings == null ? "" : (settings.BlenderPath ?? "");
            if (string.IsNullOrEmpty(blender))
                sb.AppendLine("Blender: 未指定" + (gpMode ? "（内置 gltfpack 后端无需 Blender；装了可作回退）"
                                                       : "（请在设置里指到 blender.exe，或点「自动查找」）"));
            else
                sb.AppendLine("Blender: " + blender + "  " +
                              (string.IsNullOrEmpty(blenderVersion) ? "(无法运行或未检测到版本)" : "(" + blenderVersion + ")"));

            string nekomdl = settings == null ? "" : (settings.NekomdlPath ?? "");
            bool auto = false;
            if (string.IsNullOrEmpty(nekomdl))
            {
                try { nekomdl = Nekomdl.Find(null) ?? ""; } catch { nekomdl = ""; }
                auto = nekomdl.Length > 0;
            }
            if (string.IsNullOrEmpty(nekomdl))
            {
                sb.AppendLine("编译器: 未指定（自动查找也没找到 nekomdl.exe；只有开「试编译」才需要它）");
            }
            else
            {
                bool exists;
                try { exists = File.Exists(nekomdl); } catch { exists = false; }
                sb.AppendLine("编译器: " + nekomdl + (auto ? "（自动找到）" : "") +
                              (exists ? "（存在）" : "（不存在！请在设置里重新指定）"));
            }
        }

        private static void WritePlan(StringBuilder sb, AppSettings settings)
        {
            if (settings == null)
            {
                sb.AppendLine("（设置未加载，显示默认方案）");
                settings = new AppSettings();
            }

            string preset = string.IsNullOrEmpty(settings.PresetName) ? "自定义" : settings.PresetName;
            LodPlan plan = null;
            string planErr = null;
            try
            {
                plan = settings.GetPlan();
                planErr = plan == null ? "参数为空" : plan.Validate();
            }
            catch (Exception ex) { planErr = ex.Message; }

            sb.AppendLine("LOD: " + preset + "  " + (plan == null ? "" : plan.Summary()) +
                          (planErr == null ? "" : "（参数不合法：" + planErr + "）"));
            sb.AppendLine("写入方式: " + ModeText(settings.OutputMode));
            sb.AppendLine("质量优先: " + B(settings.QualityFirst) +
                          "　覆盖手写 $lod: " + B(settings.ForceOverwriteLod) +
                          "　试编译: " + B(settings.CompileAfter) +
                          "　nofacial: " + B(settings.NoFacial));
            sb.AppendLine("并行: " + (settings.Workers <= 0 ? "自动" : settings.Workers.ToString(Inv)) +
                          "　面数门槛: " + settings.MinTriangles.ToString(Inv) + " 面");
            string backupText;
            try { backupText = settings.BackupEnabled ? "开（" + OutputLocations.DescribeBackup(settings) + "）" : "关"; }
            catch { backupText = settings.BackupEnabled ? "开" : "关"; }
            sb.AppendLine("备份: " + backupText + "　报告位置: " + ReportLocationText(settings));
        }

        private static string ModeText(int mode)
        {
            switch (mode)
            {
                case 1: return "独立 _LOD.qc（原 QC 只加一行 $include）";
                case 2: return "仅合集（未修改任何原 QC）";
                default: return "写入原 QC";
            }
        }

        private static string ReportLocationText(AppSettings s)
        {
            try { return OutputLocations.DescribeReport(s); } catch { return "QC 目录旁"; }
        }

        private static void WritePaths(StringBuilder sb, IReadOnlyList<string> probeDirs)
        {
            try { sb.AppendLine("设置文件: " + AppInfo.SettingsFile); }
            catch { sb.AppendLine("设置文件: （无法读取）"); }

            bool settingsDirOk = false;
            try { settingsDirOk = Directory.Exists(AppInfo.SettingsDir); } catch { }
            sb.AppendLine("设置目录存在: " + (settingsDirOk ? "是" : "否（首次保存时会自动创建）"));

            try { sb.AppendLine("临时目录: " + AppInfo.TempRoot); }
            catch { sb.AppendLine("临时目录: （无法读取）"); }

            try { sb.AppendLine("程序目录: " + AppInfo.ExeDir); }
            catch { }

            try
            {
                if (probeDirs != null)
                {
                    foreach (string d in probeDirs)
                    {
                        if (string.IsNullOrEmpty(d)) continue;
                        sb.AppendLine("任务目录: " + d);
                        break;
                    }
                }
            }
            catch { }
        }

        private static void WriteDisks(StringBuilder sb, IReadOnlyList<string> probeDirs)
        {
            var roots = new List<string>();
            AddDrive(roots, AppInfo.ExeDir);
            try { AddDrive(roots, Path.GetTempPath()); } catch { }
            try
            {
                if (probeDirs != null)
                    foreach (string d in probeDirs) AddDrive(roots, d);
            }
            catch { }
            if (roots.Count == 0) AddDrive(roots, "C:\\");

            if (roots.Count == 0)
            {
                sb.AppendLine("（无法获取磁盘信息）");
                return;
            }

            foreach (string name in roots)
            {
                try
                {
                    var di = new DriveInfo(name);
                    if (!di.IsReady)
                    {
                        sb.AppendLine(name + " （盘未就绪或不可访问）");
                        continue;
                    }
                    long free = di.AvailableFreeSpace;
                    long total = di.TotalSize;
                    double pct = total > 0 ? free * 100.0 / total : 0;
                    sb.AppendLine(name + " 剩余 " + FileUtil.FormatBytes(free) + " / 共 " + FileUtil.FormatBytes(total) +
                                  "（可用 " + pct.ToString("0.#", Inv) + "%）");
                }
                catch (Exception ex)
                {
                    sb.AppendLine(name + " （读取失败：" + ex.Message + "）");
                }
            }
        }

        private static void AddDrive(List<string> roots, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return;
                foreach (string r in roots)
                    if (string.Equals(r, root, StringComparison.OrdinalIgnoreCase)) return;
                roots.Add(root);
            }
            catch { }
        }

        private static void WriteLongPaths(StringBuilder sb)
        {
            bool? enabled = null;
            try
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (var bk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        using (var k = bk.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem"))
                        {
                            if (k == null) continue;
                            object v = k.GetValue("LongPathsEnabled");
                            if (v == null) continue;
                            int n;
                            if (int.TryParse(v.ToString(), NumberStyles.Integer, Inv, out n))
                            {
                                enabled = n != 0;
                                break;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (enabled == null)
                sb.AppendLine("长路径: 无法读取注册表 LongPathsEnabled；本程序清单声明了 longPathAware=true。" +
                              "工程路径超过 260 字符时仍建议放在较短路径下。");
            else if (enabled.Value)
                sb.AppendLine("长路径: 系统已启用（LongPathsEnabled=1），本程序清单声明 longPathAware=true；超长路径通常没问题。");
            else
                sb.AppendLine("长路径: 系统**未启用**（LongPathsEnabled=0）。本程序清单声明 longPathAware=true，但超过 260 字符的路径仍可能在部分 API 失败：" +
                              "建议把工程放在较短路径（例如 D:\\L4D2\\工程）下再试。");
        }

        // ───────────────────────────── 小工具 ─────────────────────────────

        private static void AddDistinct(List<string> list, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (string s in list)
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(value);
        }

        private static string B(bool v) { return v ? "开" : "关"; }
    }
}
