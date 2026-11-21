using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LodStudio.Cli
{
    public enum CliMode
    {
        Gui,
        Version,
        SelfTest,
        UiAudit,
        Plan,
        Batch,
        IconSheet,
        WidgetPreview,
        SettingsPreview,
        Help
    }

    /// <summary>
    /// 命令行解析。GUI 是主形态；以下模式是为了自动化测试与高级用户：
    ///   --version                     打印版本后退出
    ///   --selftest [--out DIR]        引擎自测（约 120 项断言），退出码 0/1
    ///   --ui-audit --out DIR          界面审计：截图 + 布局 + 对比度 + 主题往返
    ///   --plan  &lt;工程目录&gt;           只读扫描并输出作业计划（JSON/文本）
    ///   --batch &lt;工程目录&gt; [参数]    无界面执行一次完整批量（脚本/回归用）
    ///   --gui [路径...]               强制 GUI，并把路径加入队列（默认行为）
    /// 不带参数、或只带文件/文件夹路径时 = GUI。
    /// </summary>
    public sealed class CommandLine
    {
        public CliMode Mode = CliMode.Gui;
        public string OutDir;
        public List<string> Paths = new List<string>();
        public Dictionary<string, string> Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool Verbose;
        public string Error;

        private static readonly string[] Modes = { "--version", "--selftest", "--ui-audit", "--plan", "--batch", "--icon-sheet", "--help", "-h", "-?", "--gui" };

        public static CommandLine Parse(string[] args)
        {
            var c = new CommandLine();
            if (args == null || args.Length == 0) return c;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                switch (a.ToLowerInvariant())
                {
                    case "--version": c.Mode = CliMode.Version; break;
                    case "--selftest": c.Mode = CliMode.SelfTest; break;
                    case "--ui-audit": c.Mode = CliMode.UiAudit; break;
                    case "--plan": c.Mode = CliMode.Plan; break;
                    case "--batch": c.Mode = CliMode.Batch; break;
                    case "--icon-sheet": c.Mode = CliMode.IconSheet; break;
                    case "--widget-preview": c.Mode = CliMode.WidgetPreview; break;
                    case "--settings-preview": c.Mode = CliMode.SettingsPreview; break;
                    case "--help":
                    case "-h":
                    case "-?":
                    case "/?": c.Mode = CliMode.Help; break;
                    case "--gui": if (c.Mode == CliMode.Gui) c.Mode = CliMode.Gui; break;
                    case "--verbose": c.Verbose = true; break;
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal))
                        {
                            string key = a.Substring(2);
                            string val = null;
                            int eq = key.IndexOf('=');
                            if (eq >= 0) { val = key.Substring(eq + 1); key = key.Substring(0, eq); }
                            if (val == null && i + 1 < args.Length && !(args[i + 1] ?? "").StartsWith("--", StringComparison.Ordinal))
                            {
                                val = args[i + 1];
                                i++;
                            }
                            if (string.Equals(key, "out", StringComparison.OrdinalIgnoreCase)) c.OutDir = val;
                            c.Options[key] = val;
                        }
                        else
                        {
                            c.Paths.Add(a);
                        }
                        break;
                }
            }
            return c;
        }

        public string GetOption(string name, string fallback = null)
        {
            string v;
            return Options.TryGetValue(name, out v) && v != null ? v : fallback;
        }

        public int GetIntOption(string name, int fallback)
        {
            string v = GetOption(name);
            int n;
            return !string.IsNullOrEmpty(v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : fallback;
        }

        public bool HasFlag(string name) { return Options.ContainsKey(name); }

        public string Usage
        {
            get
            {
                var sb = new StringBuilder();
                sb.AppendLine(AppInfo.TitleText);
                sb.AppendLine("用法:");
                sb.AppendLine("  L4D2LOD远景工坊.exe                    打开界面");
                sb.AppendLine("  L4D2LOD远景工坊.exe <文件或文件夹>...   打开界面并加入队列");
                sb.AppendLine("  L4D2LOD远景工坊.exe --selftest [--out 目录]   引擎自测");
                sb.AppendLine("  L4D2LOD远景工坊.exe --ui-audit --out 目录     界面审计（截图/布局/对比度）");
                sb.AppendLine("  L4D2LOD远景工坊.exe --plan <工程目录>         只读计划（不写任何文件）");
                sb.AppendLine("  L4D2LOD远景工坊.exe --batch <工程目录> [选项] 无界面执行一次批量");
                sb.AppendLine("  L4D2LOD远景工坊.exe --version          打印版本");
                sb.AppendLine();
                sb.AppendLine("--batch 选项: --preset N | --levels N --dist 10,15,25 --ratio 99.7,65.3,34");
                sb.AppendLine("              --mode 0|1|2 --workers N --force --compile --facial --skip-main --no-backup");
                sb.AppendLine("              --report-dir 目录 --blender 路径 --nekomdl 路径 --min-triangles N");
                sb.AppendLine();
                sb.AppendLine("  --icon-sheet / --widget-preview / --settings-preview   设计/验收用截图（需 --out）");
                sb.AppendLine("              --blender 路径 --nekomdl 路径 --min-triangles N");
                return sb.ToString();
            }
        }
    }
}
