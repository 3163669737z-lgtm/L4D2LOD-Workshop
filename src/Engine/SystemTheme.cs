using System;
using Microsoft.Win32;

namespace LodStudio.Engine
{
    /// <summary>
    /// 系统外观偏好（引擎层也给 CLI/诊断用，所以不放在 Services 里）。
    /// 读 HKCU\...\Themes\Personalize\AppsUseLightTheme；失败按浅色处理。
    /// </summary>
    public static class SystemTheme
    {
        public static bool PrefersDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("AppsUseLightTheme");
                        if (v is int) return (int)v == 0;
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
