using System;
using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace LodStudio.Engine
{
    /// <summary>
    /// "系统 ANSI 代码页"（studiomdl / nekomdl 等外部工具实际使用的编码）。
    ///
    /// 为什么不能直接用 <see cref="Encoding.Default"/>：本程序清单声明了
    /// <c>activeCodePage=UTF-8</c>（Win10 1903+ 生效），这会让进程的 ANSI 代码页（GetACP）变成 65001，
    /// 于是 Encoding.Default 变成 UTF-8；而 nekomdl/studiomdl 没有这个清单、仍按系统 ANSI（简体中文 = GBK 936）
    /// 读写文本。包装 QC 用 UTF-8 写、studiomdl 用 GBK 读 → 中文文件名/模型名乱码，编译失败。
    ///
    /// 所以这里读注册表 HKLM\SYSTEM\CurrentControlSet\Control\Nls\CodePage\ACP（真正的系统 ANSI 代码页），
    /// 拿不到才退回 Encoding.Default。只在 Pipeline 内部使用（包装 QC 写入 / nekomdl 管道编码）。
    /// </summary>
    public static class SystemAnsi
    {
        private static readonly Encoding CachedEncoding;
        private static readonly int CachedCodePage;

        static SystemAnsi()
        {
            Encoding enc = null;
            int cp = 0;
            try
            {
                string acp = null;
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Nls\CodePage"))
                {
                    if (key != null) acp = key.GetValue("ACP") as string;
                }
                int n;
                if (!string.IsNullOrEmpty(acp) &&
                    int.TryParse(acp.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0)
                {
                    enc = System.Text.Encoding.GetEncoding(n);
                    cp = n;
                }
            }
            catch { }

            if (enc == null)
            {
                try { enc = System.Text.Encoding.Default; } catch { enc = new UTF8Encoding(false); }
                cp = enc.CodePage;
            }
            CachedEncoding = enc;
            CachedCodePage = cp;
        }

        /// <summary>系统 ANSI 编码（中文 Windows = GBK 936；不受本程序 activeCodePage=UTF-8 清单影响）</summary>
        public static Encoding Encoding
        {
            get { return CachedEncoding ?? System.Text.Encoding.Default; }
        }

        /// <summary>系统 ANSI 代码页（例如 936）</summary>
        public static int CodePage
        {
            get { return CachedCodePage; }
        }
    }
}
