using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>
    /// 极简 JSON 读写（无第三方依赖）：
    ///  - JsonWriter：写任务文件 / 计划输出（控制字符串转义与数字格式，InvariantCulture）
    ///  - Json.Parse：解析 Blender 回传的 RESULT 行（对象/数组/字符串/数字/bool/null）
    /// 只做本项目需要的部分，但保证对恶意/截断输入不抛（返回 null 或抛 JsonException，由调用方兜住）。
    /// </summary>
    public static class Json
    {
        public sealed class JsonException : Exception
        {
            public JsonException(string message) : base(message) { }
        }

        // ───────────────────────── 写 ─────────────────────────

        public static string Escape(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c > 0x7E)
                        {
                            // 非 ASCII 用 \uXXXX，避免编码问题（Python 侧 json.loads 能正确还原）
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            return v.ToString("0.########", CultureInfo.InvariantCulture);
        }

        public sealed class Writer
        {
            private readonly StringBuilder _sb = new StringBuilder();
            private bool _needComma;

            public Writer BeginObject() { Sep(); _sb.Append('{'); _needComma = false; return this; }
            public Writer EndObject() { _sb.Append('}'); _needComma = true; return this; }
            public Writer BeginArray() { Sep(); _sb.Append('['); _needComma = false; return this; }
            public Writer EndArray() { _sb.Append(']'); _needComma = true; return this; }
            public Writer Name(string n) { Sep(); _sb.Append(Escape(n)).Append(':'); _needComma = false; return this; }
            public Writer Value(string s) { Sep(); _sb.Append(Escape(s)); _needComma = true; return this; }
            public Writer Value(double v) { Sep(); _sb.Append(Num(v)); _needComma = true; return this; }
            public Writer Value(long v) { Sep(); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); _needComma = true; return this; }
            public Writer Value(int v) { return Value((long)v); }
            public Writer Value(bool v) { Sep(); _sb.Append(v ? "true" : "false"); _needComma = true; return this; }
            public Writer Null() { Sep(); _sb.Append("null"); _needComma = true; return this; }
            public Writer Prop(string n, string v) { return Name(n).Value(v); }
            public Writer Prop(string n, double v) { return Name(n).Value(v); }
            public Writer Prop(string n, int v) { return Name(n).Value(v); }
            public Writer Prop(string n, long v) { return Name(n).Value(v); }
            public Writer Prop(string n, bool v) { return Name(n).Value(v); }
            public Writer PropNull(string n) { return Name(n).Null(); }
            public override string ToString() { return _sb.ToString(); }

            private void Sep() { if (_needComma) _sb.Append(','); }
        }

        // ───────────────────────── 读 ─────────────────────────

        /// <summary>解析为 Dictionary&lt;string,object&gt; / List&lt;object&gt; / string / double / bool / null</summary>
        public static object Parse(string text)
        {
            if (text == null) return null;
            var p = new Parser(text);
            p.SkipWs();
            object v = p.ParseValue();
            p.SkipWs();
            if (!p.AtEnd) throw new JsonException("JSON 末尾有多余内容 @" + p.Pos);
            return v;
        }

        /// <summary>解析对象；失败返回 null（不抛）——Blender 输出可能被截断</summary>
        public static Dictionary<string, object> ParseObjectSafe(string text)
        {
            try { return Parse(text) as Dictionary<string, object>; }
            catch { return null; }
        }

        public static string GetString(Dictionary<string, object> o, string key)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return null;
            return v as string ?? v.ToString();
        }

        public static bool GetBool(Dictionary<string, object> o, string key, bool dflt = false)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return dflt;
            if (v is bool) return (bool)v;
            if (v is string) return string.Equals((string)v, "true", StringComparison.OrdinalIgnoreCase);
            if (v is double) return Math.Abs((double)v) > 0.5;
            return dflt;
        }

        public static int GetInt(Dictionary<string, object> o, string key, int dflt = 0)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return dflt;
            if (v is int) return (int)v;
            if (v is long) return (int)(long)v;
            if (v is double) return (int)Math.Round((double)v);
            if (v is string) { int n; return int.TryParse((string)v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : dflt; }
            return dflt;
        }

        public static double GetDouble(Dictionary<string, object> o, string key, double dflt = 0)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return dflt;
            if (v is double) return (double)v;
            if (v is long) return (long)v;
            if (v is string) { double d; return double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : dflt; }
            return dflt;
        }

        private sealed class Parser
        {
            private readonly string _s;
            public int Pos;
            public bool AtEnd { get { return Pos >= _s.Length; } }

            public Parser(string s) { _s = s; }

            public void SkipWs()
            {
                while (Pos < _s.Length)
                {
                    char c = _s[Pos];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') Pos++;
                    else break;
                }
            }

            private char Peek()
            {
                if (Pos >= _s.Length) throw new JsonException("意外结束");
                return _s[Pos];
            }

            private void Expect(char c)
            {
                if (Pos >= _s.Length || _s[Pos] != c) throw new JsonException("期望 " + c + " @" + Pos);
                Pos++;
            }

            private const int MaxDepth = 64;
            private int _depth;

            public object ParseValue()
            {
                SkipWs();
                if (AtEnd) throw new JsonException("空输入");
                if (_depth >= MaxDepth) throw new JsonException("JSON 嵌套层数超过 " + MaxDepth + "（拒绝解析，文件可能已损坏）");
                _depth++;
                try
                {
                    char c = Peek();
                    switch (c)
                    {
                        case '{': return ParseObj();
                        case '[': return ParseArr();
                        case '"': return ParseString();
                        case 't': Match("true"); return true;
                        case 'f': Match("false"); return false;
                        case 'n': Match("null"); return null;
                        default: return ParseNumber();
                    }
                }
                finally { _depth--; }
            }

            private void Match(string word)
            {
                if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0) throw new JsonException("非法字面量 @" + Pos);
                Pos += word.Length;
            }

            private Dictionary<string, object> ParseObj()
            {
                Expect('{');
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                SkipWs();
                if (Peek() == '}') { Pos++; return d; }
                while (true)
                {
                    SkipWs();
                    string k = ParseString();
                    SkipWs();
                    Expect(':');
                    object v = ParseValue();
                    d[k] = v;
                    SkipWs();
                    char c = Peek();
                    if (c == ',') { Pos++; continue; }
                    if (c == '}') { Pos++; return d; }
                    throw new JsonException("对象缺少 , 或 } @" + Pos);
                }
            }

            private List<object> ParseArr()
            {
                Expect('[');
                var list = new List<object>();
                SkipWs();
                if (Peek() == ']') { Pos++; return list; }
                while (true)
                {
                    list.Add(ParseValue());
                    SkipWs();
                    char c = Peek();
                    if (c == ',') { Pos++; continue; }
                    if (c == ']') { Pos++; return list; }
                    throw new JsonException("数组缺少 , 或 ] @" + Pos);
                }
            }

            private string ParseString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw new JsonException("字符串未结束");
                    char c = _s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw new JsonException("转义未结束");
                    char e = _s[Pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Pos + 4 > _s.Length) throw new JsonException("\\u 不足 4 位");
                            sb.Append((char)int.Parse(_s.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            Pos += 4;
                            break;
                        default: throw new JsonException("未知转义 \\" + e);
                    }
                }
            }

            private object ParseNumber()
            {
                int start = Pos;
                if (Pos < _s.Length && (_s[Pos] == '-' || _s[Pos] == '+')) Pos++;
                while (Pos < _s.Length)
                {
                    char c = _s[Pos];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') Pos++;
                    else break;
                }
                string tok = _s.Substring(start, Pos - start);
                double d;
                if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                throw new JsonException("非法数字 " + tok);
            }
        }
    }
}
