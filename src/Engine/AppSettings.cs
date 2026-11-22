using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>任务行的类型（与界面/持久化共用）。</summary>
    public enum TaskKind { Folder = 0, Qc = 1, Glb = 2, Smd = 3 }

    /// <summary>队列里的一个任务（持久化格式：kind|path）。</summary>
    public sealed class TaskRef
    {
        public TaskKind Kind;
        public string Path;

        public TaskRef() { }
        public TaskRef(TaskKind kind, string path) { Kind = kind; Path = path; }

        public string Serialize() { return ((int)Kind).ToString(CultureInfo.InvariantCulture) + "|" + (Path ?? ""); }

        public static TaskRef Deserialize(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int bar = s.IndexOf('|');
            if (bar <= 0) return null;
            int k;
            if (!int.TryParse(s.Substring(0, bar), NumberStyles.Integer, CultureInfo.InvariantCulture, out k)) return null;
            if (k < 0 || k > 3) return null;
            string p = s.Substring(bar + 1);
            if (string.IsNullOrWhiteSpace(p)) return null;
            return new TaskRef((TaskKind)k, p);
        }
    }

    /// <summary>
    /// 全部持久化设置。字段语义见 docs/00 方案 §6。
    /// 读取永远容错：坏值回默认、缺节回默认；写入原子替换。
    /// </summary>
    public sealed class AppSettings
    {
        // ── 引擎 ──
        public string BlenderPath = "";
        public string NekomdlPath = "";
        /// <summary>减面后端：0=Blender（默认）1=内置 gltfpack（无 Blender 模式）</summary>
        public int ReduceBackend = 0;

        // ── 外观 ──
        /// <summary>0=浅色 1=暗色 2=跟随系统</summary>
        public int ThemeMode = 2;

        // ── 行为 ──
        public bool RestoreTasks = true;
        /// <summary>界面动效（跟随系统"显示动画效果"之外的手动开关）</summary>
        public bool Animations = true;
        public bool PopupOnSuccess = false;
        public int MinTriangles = 200;

        // ── 报告 / 日志位置 ──
        /// <summary>0=QC 目录旁 1=exe 旁 LOD日志报告 2=自定义</summary>
        public int ReportLocation = 0;
        public string ReportCustomDir = "";

        // ── 备份 ──
        public bool BackupEnabled = true;
        /// <summary>0=QC 旁 1=exe 旁 LOD备份 2=自定义</summary>
        public int BackupLocation = 0;
        public string BackupCustomDir = "";

        // ── 方案（当前界面参数） ──
        public string PresetName = "";
        public int Levels = 4;
        public List<double> Distances = new List<double> { 10, 15, 25, 50 };
        /// <summary>百分比数值（99.7），与界面一致；读回不要再乘 100。</summary>
        public List<double> Ratios = new List<double> { 99.7, 65.3, 34.0, 16.1 };
        /// <summary>0=原 QC 1=独立 _LOD.qc 2=仅合集</summary>
        public int OutputMode = 0;

        // ── 高级 ──
        public bool QualityFirst = true;
        public bool ForceOverwriteLod = false;
        public bool CompileAfter = false;
        public bool NoFacial = true;
        public bool SkipMainModel = false;
        public List<string> SkipMainQcs = new List<string>();
        /// <summary>0=自动</summary>
        public int Workers = 0;

        // ── 窗口状态 ──
        public int WinWidth = 0;
        public int WinHeight = 0;
        public int WinLeft = int.MinValue;
        public int WinTop = int.MinValue;
        public bool Maximized = false;
        public bool LogExpanded = false;
        public int LastAddKind = 0;

        // ── 列表 ──
        public List<TaskRef> Tasks = new List<TaskRef>();
        public List<LodPreset> UserPresets = new List<LodPreset>();
        /// <summary>首次启动从旧版导入过一次（"v4"），避免重复导入</summary>
        public string MigratedFrom = "";

        // ── 派生 ──

        public bool DarkNow { get { return ThemeMode == 1 || (ThemeMode == 2 && SystemTheme.PrefersDark()); } }

        public LodPlan GetPlan()
        {
            var plan = new LodPlan();
            for (int i = 0; i < Levels; i++)
            {
                double d = i < Distances.Count ? Distances[i] : (i == 0 ? 30 : 60 * (i + 1));
                double r = i < Ratios.Count ? Ratios[i] / 100.0 : Math.Max(0.05, 0.9 * Math.Pow(0.6, i));
                plan.Levels.Add(new LodLevel(d, r));
            }
            return plan;
        }

        public void SetPlan(LodPlan plan)
        {
            if (plan == null) return;
            Levels = plan.Count;
            Distances = new List<double>(plan.Distances);
            Ratios = new List<double>(plan.Ratios.Select(x => Math.Round(x * 100.0, 4)).ToArray());
        }

        /// <summary>当前参数对应的预设（名字与参数完全一致才返回内置/用户预设，否则 null = 已修改）。</summary>
        public LodPreset MatchPreset(LodPlan plan)
        {
            if (plan == null) return null;
            foreach (var p in PresetLibrary.Builtins())
                if (p.SameAs(plan)) return p;
            foreach (var p in UserPresets)
                if (p.SameAs(plan)) return p;
            return null;
        }

        public void Normalize()
        {
            Levels = Math.Max(LodPlan.MinLevels, Math.Min(LodPlan.MaxLevels, Levels));
            Workers = Math.Max(0, Math.Min(16, Workers));
            OutputMode = Math.Max(0, Math.Min(2, OutputMode));
            ReduceBackend = Math.Max(0, Math.Min(1, ReduceBackend));
            ThemeMode = Math.Max(0, Math.Min(2, ThemeMode));
            MinTriangles = Math.Max(0, Math.Min(2000000, MinTriangles));
            ReportLocation = Math.Max(0, Math.Min(2, ReportLocation));
            BackupLocation = Math.Max(0, Math.Min(2, BackupLocation));
            LastAddKind = Math.Max(0, Math.Min(3, LastAddKind));

            while (Distances.Count < Levels) Distances.Add(Levels == 1 ? 40 : Math.Max(1, Distances.Count == 0 ? 40 : Distances[Distances.Count - 1] * 2));
            while (Ratios.Count < Levels) Ratios.Add(Math.Max(5, (Ratios.Count == 0 ? 90 : Ratios[Ratios.Count - 1]) * 0.5));
            if (Distances.Count > Levels) Distances.RemoveRange(Levels, Distances.Count - Levels);
            if (Ratios.Count > Levels) Ratios.RemoveRange(Levels, Ratios.Count - Levels);

            var plan = GetPlan();
            if (plan.Validate() != null)
            {
                var p = PresetLibrary.Builtins()[0];
                SetPlan(LodPlan.FromPreset(p));
                PresetName = p.Name;
            }

            // 预设名与参数不一致时不要谎报预设名（保存时由界面决定是否写"已修改"）
            if (!string.IsNullOrEmpty(PresetName) && MatchPreset(GetPlan()) == null)
                PresetName = "";
        }
    }

    /// <summary>
    /// settings.ini 读写。目录可用环境变量 L4D2LODSTUDIO_SETTINGS_DIR 覆盖（测试用）。
    /// 首次启动且没有本程序设置文件时，只读导入旧版设置（%APPDATA%\L4D2LodTool\settings.ini）。
    /// </summary>
    public static class SettingsStore
    {
        private static readonly object Gate = new object();

        public static string DirectoryPath { get { return AppInfo.SettingsDir; } }
        public static string FilePath { get { return AppInfo.SettingsFile; } }

        public static AppSettings Load()
        {
            lock (Gate)
            {
                var s = new AppSettings();
                bool exists = false;
                try { exists = File.Exists(FilePath); } catch { }
                try
                {
                    if (exists) ParseIni(s, File.ReadAllLines(FilePath, Encoding.UTF8));
                    else TryImportLegacy(s);
                }
                catch { }
                try { s.Normalize(); } catch { }
                return s;
            }
        }

        public static void Save(AppSettings s)
        {
            if (s == null) return;
            lock (Gate)
            {
                try { s.Normalize(); } catch { }
                try
                {
                    Directory.CreateDirectory(DirectoryPath);
                    string text = ComposeIni(s);
                    string tmp = FilePath + ".tmp_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    File.WriteAllText(tmp, text, new UTF8Encoding(false));
                    if (File.Exists(FilePath))
                    {
                        try { File.Replace(tmp, FilePath, null); }
                        catch
                        {
                            try { File.Delete(FilePath); } catch { }
                            File.Move(tmp, FilePath);
                        }
                    }
                    else File.Move(tmp, FilePath);
                }
                catch { }
            }
        }

        private static string ComposeIni(AppSettings s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("; 远景工坊 · L4D2 自动 LOD 工具 —— 设置文件");
            sb.AppendLine("; 可以直接用记事本改；删掉本文件即恢复默认（首次启动会重新尝试导入旧版设置）");
            sb.AppendLine("; 版本: " + AppInfo.Version + "    生成: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("[General]");
            sb.AppendLine("BlenderPath=" + s.BlenderPath);
            sb.AppendLine("NekomdlPath=" + s.NekomdlPath);
            sb.AppendLine("ReduceBackend=" + s.ReduceBackend);
            sb.AppendLine("ThemeMode=" + s.ThemeMode);
            sb.AppendLine("RestoreTasks=" + B(s.RestoreTasks));
            sb.AppendLine("Animations=" + B(s.Animations));
            sb.AppendLine("PopupOnSuccess=" + B(s.PopupOnSuccess));
            sb.AppendLine("MinTriangles=" + s.MinTriangles);
            sb.AppendLine("ReportLocation=" + s.ReportLocation);
            sb.AppendLine("ReportCustomDir=" + s.ReportCustomDir);
            sb.AppendLine("BackupEnabled=" + B(s.BackupEnabled));
            sb.AppendLine("BackupLocation=" + s.BackupLocation);
            sb.AppendLine("BackupCustomDir=" + s.BackupCustomDir);
            sb.AppendLine("MigratedFrom=" + s.MigratedFrom);
            sb.AppendLine();
            sb.AppendLine("[Plan]");
            sb.AppendLine("PresetName=" + s.PresetName);
            sb.AppendLine("Levels=" + s.Levels);
            sb.AppendLine("Distances=" + Join(s.Distances));
            sb.AppendLine("Ratios=" + Join(s.Ratios));
            sb.AppendLine("OutputMode=" + s.OutputMode);
            sb.AppendLine();
            sb.AppendLine("[Advanced]");
            sb.AppendLine("QualityFirst=" + B(s.QualityFirst));
            sb.AppendLine("ForceOverwriteLod=" + B(s.ForceOverwriteLod));
            sb.AppendLine("CompileAfter=" + B(s.CompileAfter));
            sb.AppendLine("NoFacial=" + B(s.NoFacial));
            sb.AppendLine("SkipMainModel=" + B(s.SkipMainModel));
            sb.AppendLine("Workers=" + s.Workers);
            sb.AppendLine();
            sb.AppendLine("[Window]");
            sb.AppendLine("Width=" + s.WinWidth);
            sb.AppendLine("Height=" + s.WinHeight);
            sb.AppendLine("Left=" + s.WinLeft);
            sb.AppendLine("Top=" + s.WinTop);
            sb.AppendLine("Maximized=" + B(s.Maximized));
            sb.AppendLine("LogExpanded=" + B(s.LogExpanded));
            sb.AppendLine("LastAddKind=" + s.LastAddKind);
            sb.AppendLine();
            sb.AppendLine("[SkipMain]");
            foreach (var q in s.SkipMainQcs) sb.AppendLine("qc=" + q);
            sb.AppendLine();
            sb.AppendLine("[UserPresets]");
            foreach (var p in s.UserPresets)
            {
                var plan = new LodPlan();
                for (int i = 0; i < p.Distances.Length; i++) plan.Levels.Add(new LodLevel(p.Distances[i], p.Ratios[i]));
                sb.AppendLine("preset=" + PresetLibrary.EncodeUserPreset(p.Name, plan));
            }
            sb.AppendLine();
            sb.AppendLine("[Tasks]");
            foreach (var t in s.Tasks) sb.AppendLine("task=" + t.Serialize());
            return sb.ToString();
        }

        private static void ParseIni(AppSettings s, string[] lines)
        {
            string section = "";
            foreach (var raw in lines)
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();

                try
                {
                    if (section == "tasks")
                    {
                        if (k == "task") { var t = TaskRef.Deserialize(v); if (t != null) s.Tasks.Add(t); }
                        continue;
                    }
                    if (section == "userpresets")
                    {
                        if (k == "preset") { var p = PresetLibrary.DecodeUserPreset(v); if (p != null) s.UserPresets.Add(p); }
                        continue;
                    }
                    if (section == "skipmain")
                    {
                        if (k == "qc" && v.Length > 0) s.SkipMainQcs.Add(v);
                        continue;
                    }

                    switch (k)
                    {
                        case "blenderpath": s.BlenderPath = v; break;
                        case "nekomdlpath": s.NekomdlPath = v; break;
                        case "reducebackend": s.ReduceBackend = I(v, s.ReduceBackend); break;
                        case "thememode": s.ThemeMode = I(v, s.ThemeMode); break;
                        case "restoretasks": s.RestoreTasks = B(v, s.RestoreTasks); break;
                        case "popuponsuccess": s.PopupOnSuccess = B(v, s.PopupOnSuccess); break;
                        case "mintriangles": s.MinTriangles = I(v, s.MinTriangles); break;
                        case "reportlocation": s.ReportLocation = I(v, s.ReportLocation); break;
                        case "reportcustomdir": s.ReportCustomDir = v; break;
                        case "backupenabled": s.BackupEnabled = B(v, s.BackupEnabled); break;
                        case "backuplocation": s.BackupLocation = I(v, s.BackupLocation); break;
                        case "backupcustomdir": s.BackupCustomDir = v; break;
                        case "migratedfrom": s.MigratedFrom = v; break;
                        case "presetname": s.PresetName = v; break;
                        case "levels": s.Levels = I(v, s.Levels); break;
                        case "distances": { var d = Doubles(v); if (d.Count > 0) s.Distances = d; break; }
                        case "ratios": { var r = Doubles(v); if (r.Count > 0) s.Ratios = r; break; }
                        case "outputmode": s.OutputMode = I(v, s.OutputMode); break;
                        case "qualityfirst": s.QualityFirst = B(v, s.QualityFirst); break;
                        case "forceoverwritelod": s.ForceOverwriteLod = B(v, s.ForceOverwriteLod); break;
                        case "compileafter": s.CompileAfter = B(v, s.CompileAfter); break;
                        case "nofacial": s.NoFacial = B(v, s.NoFacial); break;
                        case "skipmainmodel": s.SkipMainModel = B(v, s.SkipMainModel); break;
                        case "workers": s.Workers = I(v, s.Workers); break;
                        case "width": s.WinWidth = I(v, s.WinWidth); break;
                        case "height": s.WinHeight = I(v, s.WinHeight); break;
                        case "left": s.WinLeft = I(v, s.WinLeft); break;
                        case "top": s.WinTop = I(v, s.WinTop); break;
                        case "maximized": s.Maximized = B(v, s.Maximized); break;
                        case "logexpanded": s.LogExpanded = B(v, s.LogExpanded); break;
                        case "lastaddkind": s.LastAddKind = I(v, s.LastAddKind); break;
                    }
                }
                catch { }
            }
        }

        // ── 旧版设置导入（只读；失败静默） ──

        private static void TryImportLegacy(AppSettings s)
        {
            try
            {
                string oldFile = AppInfo.LegacySettingsFile;
                if (!File.Exists(oldFile)) return;
                string section = "";
                int levels = 0;
                List<double> dist = new List<double>(), ratios = new List<double>();
                var tasks = new List<TaskRef>();
                var presets = new List<LodPreset>();
                string presetName = "";
                int outputMode = -1;
                foreach (var raw in File.ReadAllLines(oldFile, Encoding.UTF8))
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    try
                    {
                        if (section == "tasks")
                        {
                            if (k == "task")
                            {
                                int bar = v.IndexOf('|');
                                if (bar > 0)
                                {
                                    string kind = v.Substring(0, bar);
                                    string path = v.Substring(bar + 1);
                                    if (path.Length > 0)
                                    {
                                        // 旧版枚举：0 文件夹 1 QC 2 GLB 3 SMD —— 与本版一致
                                        int kk;
                                        if (int.TryParse(kind, NumberStyles.Integer, CultureInfo.InvariantCulture, out kk) && kk >= 0 && kk <= 3)
                                            tasks.Add(new TaskRef((TaskKind)kk, path));
                                    }
                                }
                            }
                            continue;
                        }
                        if (section == "presets")
                        {
                            if (k == "preset")
                            {
                                var p = PresetLibrary.DecodeUserPreset(v);
                                if (p != null) presets.Add(p);
                            }
                            continue;
                        }
                        if (section == "skipmain")
                        {
                            if (k == "qc" && v.Length > 0) s.SkipMainQcs.Add(v);
                            continue;
                        }
                        switch (k)
                        {
                            case "blenderpath": if (v.Length > 0) s.BlenderPath = v; break;
                            case "nekomdlpath": if (v.Length > 0) s.NekomdlPath = v; break;
                            case "quality": s.QualityFirst = B(v, s.QualityFirst); break;
                            case "compileafter": s.CompileAfter = B(v, s.CompileAfter); break;
                            case "forcelod": s.ForceOverwriteLod = B(v, s.ForceOverwriteLod); break;
                            case "levels": levels = I(v, levels); break;
                            case "distances": { var d = Doubles(v); if (d.Count > 0) dist = d; break; }
                            case "ratios": { var r = Doubles(v); if (r.Count > 0) ratios = r; break; }
                            case "presetname": presetName = v; break;
                            case "workers": s.Workers = I(v, s.Workers); break;
                            case "outputmode": outputMode = I(v, outputMode); break;
                            case "thememode": s.ThemeMode = I(v, s.ThemeMode); break;
                            case "restoretasks": s.RestoreTasks = B(v, s.RestoreTasks); break;
                            case "animations": s.Animations = B(v, s.Animations); break;
                            case "popuponsuccess": s.PopupOnSuccess = B(v, s.PopupOnSuccess); break;
                            case "mintriangles": s.MinTriangles = I(v, s.MinTriangles); break;
                            case "reportlocation": s.ReportLocation = I(v, s.ReportLocation); break;
                            case "reportcustomdir": s.ReportCustomDir = v; break;
                            case "skipmainmodel": s.SkipMainModel = B(v, s.SkipMainModel); break;
                            case "nofacial": s.NoFacial = B(v, s.NoFacial); break;
                            case "backupenabled": s.BackupEnabled = B(v, s.BackupEnabled); break;
                            case "backuplocation": s.BackupLocation = I(v, s.BackupLocation); break;
                            case "backupcustomdir": s.BackupCustomDir = v; break;
                            case "logexpanded": s.LogExpanded = B(v, s.LogExpanded); break;
                            case "lastaddkind": s.LastAddKind = I(v, s.LastAddKind); break;
                        }
                    }
                    catch { }
                }

                if (levels >= LodPlan.MinLevels && levels <= LodPlan.MaxLevels && dist.Count >= levels && ratios.Count >= levels)
                {
                    s.Levels = levels;
                    s.Distances = dist.Take(levels).ToList();
                    s.Ratios = ratios.Take(levels).ToList();
                }
                if (!string.IsNullOrEmpty(presetName)) s.PresetName = presetName;
                if (outputMode >= 0 && outputMode <= 2) s.OutputMode = outputMode;
                foreach (var p in presets) if (!s.UserPresets.Any(x => x.Name == p.Name)) s.UserPresets.Add(p);
                foreach (var t in tasks) s.Tasks.Add(t);
                s.MigratedFrom = "v4";
                Save(s);
            }
            catch { }
        }

        // ── 小工具 ──

        private static string B(bool v) { return v ? "1" : "0"; }

        private static bool B(string v, bool fallback)
        {
            v = (v ?? "").Trim().ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            return fallback;
        }

        private static int I(string v, int fallback)
        {
            int n;
            return int.TryParse((v ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : fallback;
        }

        private static List<double> Doubles(string v)
        {
            var list = new List<double>();
            if (string.IsNullOrEmpty(v)) return list;
            foreach (var part in v.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double d;
                if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) list.Add(d);
            }
            return list;
        }

        private static string Join(List<double> xs)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < xs.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(xs[i].ToString("0.####", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
