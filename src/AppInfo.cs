using System;
using System.IO;
using System.Reflection;

namespace LodStudio
{
    /// <summary>
    /// 程序版本与路径。显示版本来自项目生成的程序集元数据。
    /// </summary>
    public static class AppInfo
    {
        public const string ProductName = "远景工坊";
        public const string ProductLatin = "FARSIGHT";
        public const string Subtitle = "L4D2 自动 LOD 工具";
        public static readonly string Version = typeof(AppInfo).Assembly.GetName().Version.ToString(3);
        public static readonly string DisplayVersion = "v" + Version;
        public const string Build = "2026.11";

        /// <summary>窗口标题用</summary>
        public static string TitleText { get { return "远景工坊 · L4D2 自动 LOD 工具  " + DisplayVersion; } }

        /// <summary>用户可见的简短标识（顶栏/关于）</summary>
        public static string ShortTitle { get { return "远景工坊 " + DisplayVersion; } }

        /// <summary>--version 输出（ASCII，便于脚本解析）</summary>
        public static string VersionTextForCli
        {
            get { return "远景工坊 FARSIGHT L4D2 LOD Studio " + Version + " (" + Build + ", net48)"; }
        }

        /// <summary>设置/临时目录的固定前缀（ASCII，避免编码问题）</summary>
        public const string FolderStem = "L4D2LOD远景工坊";
        /// <summary>测试/便携场景覆盖设置目录的环境变量</summary>
        public const string SettingsDirEnvVar = "L4D2LODSTUDIO_SETTINGS_DIR";

        private static string _exeDir;

        /// <summary>exe 所在目录（不依赖 WinForms；测试宿主里也能用）</summary>
        public static string ExeDir
        {
            get
            {
                if (_exeDir != null) return _exeDir;
                try
                {
                    var asm = typeof(AppInfo).Assembly;
                    string loc = asm.Location;
                    if (!string.IsNullOrEmpty(loc)) _exeDir = Path.GetDirectoryName(loc);
                }
                catch { }
                if (string.IsNullOrEmpty(_exeDir))
                {
                    try { _exeDir = AppDomain.CurrentDomain.BaseDirectory; } catch { }
                }
                if (string.IsNullOrEmpty(_exeDir)) _exeDir = ".";
                return _exeDir;
            }
        }

        /// <summary>设置目录：可用环境变量覆盖（自测用临时目录，绝不动用户真实配置）</summary>
        public static string SettingsDir
        {
            get
            {
                string over = null;
                try { over = Environment.GetEnvironmentVariable(SettingsDirEnvVar); } catch { }
                if (!string.IsNullOrEmpty(over)) return over;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderStem);
            }
        }

        public static string SettingsFile { get { return Path.Combine(SettingsDir, "settings.ini"); } }

        /// <summary>旧版设置文件（只读导入用）</summary>
        public static string LegacySettingsFile
        {
            get
            {
                string over = null;
                try { over = Environment.GetEnvironmentVariable("L4D2LOD_SETTINGS_DIR"); } catch { }
                string dir = !string.IsNullOrEmpty(over)
                    ? over
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "L4D2LodTool");
                return Path.Combine(dir, "settings.ini");
            }
        }

        /// <summary>临时工作目录（任务 JSON / 释放的脚本 / SMD 转换中间文件）</summary>
        public static string TempRoot
        {
            get
            {
                string d = Path.Combine(Path.GetTempPath(), FolderStem);
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public static string TempBackendDir { get { return Path.Combine(TempRoot, "backend"); } }
        public static string TempSmdDir { get { return Path.Combine(TempRoot, "smd"); } }
        public static string TempRunDir { get { return Path.Combine(TempRoot, "run"); } }

        /// <summary>报告/日志在 exe 旁的默认集中目录名</summary>
        public const string ReportFolderName = "LOD日志报告";
        /// <summary>备份在 exe 旁的集中目录名</summary>
        public const string BackupFolderName = "LOD备份";

        /// <summary>完整程序集版本，供诊断和关于窗口显示。</summary>
        public static string FileVersionText
        {
            get
            {
                try
                {
                    var v = Assembly.GetExecutingAssembly().GetName().Version;
                    return v == null ? Version : v.ToString();
                }
                catch { return Version; }
            }
        }

        /// <summary>运行位数说明</summary>
        public static string Bitness { get { return IntPtr.Size == 8 ? "64 位" : "32 位"; } }
    }
}
