using System;
using System.IO;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>
    /// QC 文本文件：编码/BOM/换行探测与保留，原子写。
    /// 实现负责人：QC 模块。契约不可变（其它模块调用这些成员）。
    ///
    /// 编码探测顺序（与旧版一致，写回必须逐字节还原用户原来的编码风格）：
    ///   1) 前三字节 EF BB BF → UTF-8 带 BOM；
    ///   2) 严格 UTF-8 能解开 → UTF-8 无 BOM；
    ///   3) GBK(936)；
    ///   4) 系统默认编码。
    /// 换行：正文里出现 "\r\n" 就用 CRLF，否则 LF（与旧版一致，单文件单风格，不做混合保留）。
    /// </summary>
    public sealed class QcTextFile
    {
        /// <summary>绝对路径</summary>
        public string FilePath;
        /// <summary>写回时使用的编码（读入时探测得到）</summary>
        public Encoding Encoding = new UTF8Encoding(false);
        /// <summary>读入时是否有 UTF-8 BOM（GBK 等编码永远不写 BOM）</summary>
        public bool Bom;
        /// <summary>换行风格："\r\n" 或 "\n"</summary>
        public string NewLine = "\r\n";
        /// <summary>正文（不含 BOM）</summary>
        public string Text = "";

        /// <summary>读取并探测编码/BOM/换行；失败抛异常（调用方记录 ReadFailed）。</summary>
        public static QcTextFile Read(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("QC 路径为空。", "path");
            var f = new QcTextFile();
            f.FilePath = Path.GetFullPath(path);
            byte[] bytes = FileUtil.ReadAllBytesShared(f.FilePath);
            Encoding enc;
            bool bom;
            f.Text = Decode(bytes, out enc, out bom);
            // 无损往返校验：解出来的文本用同一编码写回必须与原始字节完全一致，
            // 否则说明存在非法/怪异的字节序列，拒绝修改（宁可让用户手动处理，也不悄悄损坏文件）。
            byte[] roundTrip = Encode(f.Text, enc, bom);
            if (!BytesEqual(roundTrip, bytes))
                throw new InvalidDataException("无法无损解码该 QC（编码/字节序列不规整，写回可能损坏文件）。" +
                                               "为避免改坏，本工具不会修改它。");
            f.Encoding = enc;
            f.Bom = bom;
            f.NewLine = FileUtil.DetectNewLine(f.Text);
            return f;
        }

        /// <summary>按读入时的编码/BOM 原子写回。</summary>
        public void Write(string text)
        {
            WriteAllText(FilePath, text, Encoding, Bom);
        }

        /// <summary>按指定编码/BOM 原子写；bom 仅对 UTF-8 生效（GBK 等永不加 BOM）。</summary>
        public static void WriteAllText(string path, string text, Encoding encoding, bool bom)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("写入路径为空。", "path");
            FileUtil.WriteAtomic(Path.GetFullPath(path), Encode(text, encoding, bom));
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>文本 → 字节（含 BOM 规则）。</summary>
        public static byte[] Encode(string text, Encoding encoding, bool bom)
        {
            var enc = encoding ?? new UTF8Encoding(false);
            byte[] body = enc.GetBytes(text ?? "");
            if (!bom || !(enc is UTF8Encoding)) return body;

            var all = new byte[body.Length + 3];
            all[0] = 0xEF;
            all[1] = 0xBB;
            all[2] = 0xBF;
            Buffer.BlockCopy(body, 0, all, 3, body.Length);
            return all;
        }

        /// <summary>字节 → 文本；探测顺序：UTF-8 BOM → 严格 UTF-8 → GBK(936) → 系统默认。</summary>
        public static string Decode(byte[] bytes, out Encoding encoding, out bool bom)
        {
            encoding = new UTF8Encoding(false);
            bom = false;
            if (bytes == null || bytes.Length == 0) return "";

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                // 有 BOM 但正文不是合法 UTF-8（或与 BOM 不一致）：拒绝修改，绝不把非法字节替换成 U+FFFD 写回
                try
                {
                    string s = new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
                    bom = true;
                    return s;
                }
                catch (Exception)
                {
                    throw new InvalidDataException("QC 带 UTF-8 BOM，但正文不是合法的 UTF-8（编码与 BOM 不一致）。" +
                                                   "为避免写坏文件，本工具不会修改它；请用编辑器统一编码后重试。");
                }
            }

            try
            {
                // throwOnInvalidBytes=true：GBK 文本通常解不成合法 UTF-8，会在这里被拒绝
                var strict = new UTF8Encoding(false, true);
                string s = strict.GetString(bytes);
                encoding = new UTF8Encoding(false);
                return s;
            }
            catch
            {
                // 不是合法 UTF-8，继续尝试 GBK
            }

            try
            {
                var gbk = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                string s = gbk.GetString(bytes);
                encoding = gbk;
                return s;
            }
            catch
            {
                // 系统没有 936 代码页（极少见），退回系统默认
            }

            encoding = Encoding.Default;
            return encoding.GetString(bytes);
        }
    }
}
