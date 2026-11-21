using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LodStudio.Cli.UiAudit
{
    /// <summary>
    /// 审计报告：所有检查都走 <see cref="Check"/>（PASS/FAIL）或 <see cref="Note"/>（允许标注，不计失败）。
    /// 退出码只看 <see cref="FailCount"/>。文本同时写 audit.txt 与 stdout。
    /// </summary>
    internal sealed class AuditReport
    {
        private readonly StringBuilder _sb = new StringBuilder(64 * 1024);
        private readonly List<string> _failures = new List<string>();

        public int PassCount { get; private set; }
        public int FailCount { get; private set; }
        public int NoteCount { get; private set; }

        /// <summary>失败明细（写进报告末尾的"失败明细"段）。</summary>
        public IReadOnlyList<string> Failures { get { return _failures; } }

        public void Line(string text)
        {
            _sb.AppendLine(text ?? "");
        }

        public void Section(string title)
        {
            _sb.AppendLine();
            _sb.AppendLine("── " + title + " " + new string('─', Math.Max(4, 64 - DisplayWidth(title))) );
        }

        /// <summary>普通信息行（不计入检查数）。</summary>
        public void Info(string text)
        {
            _sb.AppendLine("    " + (text ?? ""));
        }

        /// <summary>一条 PASS/FAIL 检查。annotationOnly=true 时失败只记 NOTE。</summary>
        public bool Check(string name, bool ok, string detail = null)
        {
            if (ok)
            {
                PassCount++;
                _sb.AppendLine("[PASS] " + name + (string.IsNullOrEmpty(detail) ? "" : "  —  " + detail));
                return true;
            }
            FailCount++;
            string line = name + (string.IsNullOrEmpty(detail) ? "" : "  —  " + detail);
            _sb.AppendLine("[FAIL] " + line);
            _failures.Add(line);
            return false;
        }

        /// <summary>直接记一条失败（用于捕获到异常等无法归入某个检查的场景）。</summary>
        public void Fail(string name, string detail = null)
        {
            FailCount++;
            string line = name + (string.IsNullOrEmpty(detail) ? "" : "  —  " + detail);
            _sb.AppendLine("[FAIL] " + line);
            _failures.Add(line);
        }

        /// <summary>允许标注项：打印 PASS/NOTE，不计失败（对比度里的"辅助网格"等）。</summary>
        public bool Annotation(string name, bool ok, string detail = null)
        {
            string body = name + (string.IsNullOrEmpty(detail) ? "" : "  —  " + detail);
            if (ok)
            {
                PassCount++;
                _sb.AppendLine("[PASS] " + body);
                return true;
            }
            NoteCount++;
            _sb.AppendLine("[NOTE] " + body);
            return false;
        }

        /// <summary>一条说明性 NOTE（不计失败）。</summary>
        public void Note(string text)
        {
            NoteCount++;
            _sb.AppendLine("[NOTE] " + (text ?? ""));
        }

        /// <summary>FAIL 但已单独 Check 过的原始细节行（缩进打印）。</summary>
        public void Detail(string text)
        {
            _sb.AppendLine("        " + (text ?? ""));
        }

        public string ToText()
        {
            return _sb.ToString();
        }

        private static int DisplayWidth(string s)
        {
            // 粗略：中日韩字符按 2 个宽度算，用于对齐分隔线长度
            int w = 0;
            foreach (char c in s ?? "") w += c > 0x2E7F ? 2 : 1;
            return w;
        }
    }
}
