using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace LodStudio
{
    /// <summary>
    /// 极简的告知/错误提示（不依赖主题资源，任何崩溃状态下都能显示）。
    /// 主界面正式的错误提示走 Views 里的对话框。
    /// </summary>
    internal static class MessageBar
    {
        public static void ShowUnhandled(Exception ex)
        {
            string text = "程序出现未处理的错误，已记录日志，可以继续使用。\r\n\r\n" +
                          ex.GetType().Name + "：" + ex.Message + "\r\n\r\n" +
                          "日志目录：" + AppInfo.TempRoot;
            try
            {
                MessageBox.Show(text, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch
            {
                try { Console.WriteLine(text); } catch { }
            }
        }
    }
}
