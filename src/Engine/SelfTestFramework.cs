using System;
using System.Collections.Generic;
using System.Reflection;

namespace LodStudio.Engine
{
    /// <summary>
    /// 自测框架：各模块在自己文件里写
    ///   [SelfTest("QC：工具块幂等")]
    ///   public static void ToolBlockIdempotent(SelfTestContext ctx) { ... }
    /// 入口 --selftest 用反射收集全部方法，按类名+方法名稳定排序执行。
    /// 好处：新增用例不需要改共享文件（并行开发不会冲突）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class SelfTestAttribute : Attribute
    {
        public string Name { get; private set; }
        public SelfTestAttribute(string name) { Name = name; }
    }

    public sealed class SelfTestFailureException : Exception
    {
        public SelfTestFailureException(string message) : base(message) { }
    }

    /// <summary>自测上下文：临时目录、断言、日志。</summary>
    public sealed class SelfTestContext
    {
        public readonly string Root;
        private readonly Action<string> _write;
        private int _seq;
        private string _currentCase = "";

        public SelfTestContext(string root, Action<string> write) { Root = root; _write = write; }

        public void SetCase(string name) { _currentCase = name ?? ""; }

        /// <summary>新建一个隔离的临时目录（每个用例独立，互不影响）</summary>
        public string NewDir(string hint)
        {
            _seq++;
            string safe = (hint ?? "case").Replace(":", "_").Replace("\\", "_").Replace("/", "_").Replace(" ", "_");
            if (safe.Length > 40) safe = safe.Substring(0, 40);
            string d = System.IO.Path.Combine(Root, _seq.ToString("000") + "_" + safe);
            System.IO.Directory.CreateDirectory(d);
            return d;
        }

        public void Log(string s) { _write("        " + s); }

        public void Assert(bool condition, string message)
        {
            if (!condition) throw new SelfTestFailureException(message);
        }

        public void AssertEqual(string expected, string actual, string what)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new SelfTestFailureException(what + "：期望=" + Preview(expected) + " 实际=" + Preview(actual));
        }

        public void AssertEqual(int expected, int actual, string what)
        {
            if (expected != actual)
                throw new SelfTestFailureException(what + "：期望=" + expected + " 实际=" + actual);
        }

        public void AssertEqual(double expected, double actual, double tolerance, string what)
        {
            if (Math.Abs(expected - actual) > tolerance)
                throw new SelfTestFailureException(what + "：期望=" + expected + " 实际=" + actual);
        }

        public void AssertContains(string haystack, string needle, string what)
        {
            if (haystack == null || haystack.IndexOf(needle, StringComparison.Ordinal) < 0)
                throw new SelfTestFailureException(what + "：未包含 " + Preview(needle) + "；实际=" + Preview(haystack));
        }

        public void AssertNotContains(string haystack, string needle, string what)
        {
            if (haystack != null && haystack.IndexOf(needle, StringComparison.Ordinal) >= 0)
                throw new SelfTestFailureException(what + "：不应包含 " + Preview(needle));
        }

        private static string Preview(string s)
        {
            if (s == null) return "(null)";
            s = s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return s.Length <= 260 ? "[" + s + "]" : "[" + s.Substring(0, 260) + "…]";
        }
    }

    public sealed class SelfTestCaseInfo
    {
        public string Name;
        public string Owner;          // 类名.方法名
        public Action<SelfTestContext> Body;
    }

    public static class SelfTestRegistry
    {
        /// <summary>反射发现程序集里所有 [SelfTest] 静态方法，按 Owner 排序。</summary>
        public static List<SelfTestCaseInfo> Discover()
        {
            var list = new List<SelfTestCaseInfo>();
            var asm = typeof(SelfTestRegistry).Assembly;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types ?? new Type[0]; }

            foreach (var t in types)
            {
                if (t == null) continue;
                MethodInfo[] methods;
                try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static); }
                catch { continue; }
                foreach (var m in methods)
                {
                    SelfTestAttribute attr = null;
                    try { attr = m.GetCustomAttribute<SelfTestAttribute>(); } catch { }
                    if (attr == null) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1 || ps[0].ParameterType != typeof(SelfTestContext)) continue;
                    if (m.ReturnType != typeof(void)) continue;
                    list.Add(new SelfTestCaseInfo
                    {
                        Name = attr.Name,
                        Owner = t.Name + "." + m.Name,
                        Body = ctx => m.Invoke(null, new object[] { ctx })
                    });
                }
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Owner, b.Owner));
            return list;
        }
    }
}
