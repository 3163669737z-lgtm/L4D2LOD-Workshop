using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace LodStudio.Services
{
    public enum ThemeKind { Light, Dark }

    /// <summary>
    /// 主题切换：只替换索引 0 的令牌字典（Tokens.Light/Dark），其余字典（Typography/Controls）保持。
    /// 控件全部用 DynamicResource 引用，因此切换不需要重建控件、不会有半透明过渡。
    /// </summary>
    public static class ThemeService
    {
        private static readonly Uri LightUri = new Uri("/Themes/Tokens.Light.xaml", UriKind.Relative);
        private static readonly Uri DarkUri = new Uri("/Themes/Tokens.Dark.xaml", UriKind.Relative);
        private static bool _initialized;

        public static ThemeKind Current { get; private set; } = ThemeKind.Light;

        /// <summary>系统"应用模式"是否深色（跟随系统用；真正的读取在引擎层，CLI 也要用）</summary>
        public static bool SystemPrefersDark()
        {
            return Engine.SystemTheme.PrefersDark();
        }

        /// <summary>启动时装配：令牌主题 + 字体阶梯 + 控件样式</summary>
        public static void Initialize(ThemeKind kind)
        {
            var app = Application.Current;
            if (app == null) throw new InvalidOperationException("ThemeService.Initialize 需要在 Application 之后调用");
            var dicts = app.Resources.MergedDictionaries;
            dicts.Clear();
            dicts.Add(new ResourceDictionary { Source = kind == ThemeKind.Dark ? DarkUri : LightUri });
            dicts.Add(new ResourceDictionary { Source = new Uri("/Themes/Typography.xaml", UriKind.Relative) });
            dicts.Add(new ResourceDictionary { Source = new Uri("/Themes/Controls.xaml", UriKind.Relative) });
            // 可选：控件/浮层专用样式（由 UI 控件任务维护；文件不存在时跳过，保证骨架可编译）
            try { dicts.Add(new ResourceDictionary { Source = new Uri("/Themes/Widgets.xaml", UriKind.Relative) }); }
            catch { }
            Current = kind;
            _initialized = true;
        }

        /// <summary>切换主题（只换令牌字典，不重建窗口）</summary>
        public static void Apply(ThemeKind kind)
        {
            if (!_initialized) { Initialize(kind); return; }
            if (kind == Current) return;
            var app = Application.Current;
            var dicts = app.Resources.MergedDictionaries;
            var tokens = new ResourceDictionary { Source = kind == ThemeKind.Dark ? DarkUri : LightUri };
            if (dicts.Count == 0) dicts.Add(tokens);
            else dicts[0] = tokens;
            Current = kind;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        public static event EventHandler ThemeChanged;

        /// <summary>取当前主题里的颜色（自绘控件用；不存在时返回透明）</summary>
        public static Color GetColor(string key)
        {
            object v = null;
            try { v = Application.Current?.TryFindResource(key); } catch { }
            if (v is Color c) return c;
            if (v is SolidColorBrush b) return b.Color;
            return Colors.Transparent;
        }

        public static SolidColorBrush GetBrush(string key)
        {
            object v = null;
            try { v = Application.Current?.TryFindResource(key); } catch { }
            if (v is SolidColorBrush b) return b;
            if (v is Color c) return new SolidColorBrush(c);
            return new SolidColorBrush(Colors.Transparent);
        }
    }
}
