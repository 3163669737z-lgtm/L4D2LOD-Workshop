using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace LodStudio.Cli
{
    /// <summary>
    /// --selftest：引擎自测。全部在临时目录造数据，不碰用户任何真实文件。
    /// 用例通过 [SelfTest("...")] 特性登记在各模块文件里，欢迎并行新增。
    /// 输出最后一行固定：SELFTEST: N passed, M failed
    /// </summary>
    public static class SelfTestRunner
    {
        public static int Run(CommandLine cli)
        {
            string outDir = cli.OutDir;
            if (string.IsNullOrEmpty(outDir)) outDir = System.IO.Path.Combine(AppInfo.TempRoot, "selftest");
            try { System.IO.Directory.CreateDirectory(outDir); } catch { }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var log = new StringBuilder();
            Action<string> write = delegate (string s)
            {
                log.AppendLine(s);
                try { Console.WriteLine(s); } catch { }
            };

            write(AppInfo.TitleText + "  —  selftest");
            write("time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            write("out : " + outDir);
            write("");

            int failed = 0, passed = 0;
            var cases = new List<Engine.SelfTestCaseInfo>();
            try { cases = Engine.SelfTestRegistry.Discover(); }
            catch (Exception ex) { write("FATAL 反射发现用例失败: " + ex); return 3; }

            string filter = cli.GetOption("filter");
            if (!string.IsNullOrEmpty(filter))
            {
                var filtered = new List<Engine.SelfTestCaseInfo>();
                foreach (var c in cases)
                    if (c.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        c.Owner.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) filtered.Add(c);
                cases = filtered;
            }

            write("发现 " + cases.Count + " 个用例" + (string.IsNullOrEmpty(filter) ? "" : "（过滤: " + filter + "）"));
            write("");
            var ctx = new Engine.SelfTestContext(outDir, write);
            foreach (var c in cases)
            {
                ctx.SetCase(c.Name);
                try
                {
                    c.Body(ctx);
                    passed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    string msg = ex is Engine.SelfTestFailureException || ex is TargetInvocationException
                        ? (ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException.Message : ex.Message)
                        : ex.GetType().Name + ": " + ex.Message;
                    write("  FAIL  " + c.Name + "  [" + c.Owner + "]");
                    write("        " + msg);
                }
            }

            sw.Stop();
            write("");
            write("SELFTEST: " + passed + " passed, " + failed + " failed   (" +
                  sw.Elapsed.TotalSeconds.ToString("0.0") + "s)");
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "selftest.txt"), log.ToString(), new UTF8Encoding(false)); } catch { }
            return failed == 0 ? 0 : 1;
        }
    }
}
