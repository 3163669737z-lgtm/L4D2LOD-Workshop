using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LodStudio.Controls;
using LodStudio.Services;

namespace LodStudio.Views.Settings
{
    /// <summary>
    /// 外观页：主题三选 + 动效开关（与常规页共享设置状态，切页保持同步）+ 主题示意。
    /// 下方色块全部 DynamicResource 绑定令牌，切换主题时实时变色；右侧十六进制值在同步时刷新。
    /// </summary>
    public partial class SettingsPageAppearance : SettingsPageBase
    {
        private readonly List<SegOption> _themeOptions = new List<SegOption>
        {
            new SegOption("浅色"),
            new SegOption("暗色"),
            new SegOption("跟随系统")
        };

        public SettingsPageAppearance()
        {
            InitializeComponent();
            SegTheme.SetItems(_themeOptions, 2);
        }

        protected override void OnAttached()
        {
            SegTheme.SelectedIndexChanged += OnThemeChanged;
            ChkAnimations.Checked += OnAnimationsChanged;
            ChkAnimations.Unchecked += OnAnimationsChanged;
        }

        public override void SyncFromSettings()
        {
            if (Settings == null) return;
            Syncing = true;
            try
            {
                SegTheme.SetItems(_themeOptions, Math.Max(0, Math.Min(2, Settings.ThemeMode)));
                ChkAnimations.IsChecked = Settings.Animations;
                UpdateAnimationsHint();
                UpdateHex();
            }
            finally { Syncing = false; }
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            if (Syncing || Settings == null) return;
            int mode = Math.Max(0, Math.Min(2, SegTheme.SelectedIndex));
            Settings.ThemeMode = mode;
            Win.Shell.SetThemeMode(mode);      // 立即应用主题 + 保存
            Win.FlashSaved(false);
        }

        private void OnAnimationsChanged(object sender, RoutedEventArgs e)
        {
            if (Syncing || Settings == null) return;
            Settings.Animations = ChkAnimations.IsChecked == true;
            UpdateAnimationsHint();
            Apply(false);
        }

        private void UpdateAnimationsHint()
        {
            bool on = ChkAnimations.IsChecked == true;
            TxtAnimHint.Text = on
                ? "开启玻璃高光追踪、界面切换滑入、背景毛玻璃虚化与弹性控件动效。"
                : "已关闭：系统关闭「显示动画效果」时也会自动减少动画。";
            TxtAnimHint.SetResourceReference(TextBlock.ForegroundProperty,
                on ? "Brush.InkFaint" : "Brush.InkSoft");
        }

        private void UpdateHex()
        {
            HexCanvas.Text = Hex("Color.Canvas");
            HexPanel.Text = Hex("Color.Panel");
            HexLine.Text = Hex("Color.Line");
            HexInk.Text = Hex("Color.Ink");
            HexSignal.Text = Hex("Color.Signal");
        }

        private static string Hex(string key)
        {
            var c = ThemeService.GetColor(key);
            return "#" + c.R.ToString("X2", CultureInfo.InvariantCulture)
                     + c.G.ToString("X2", CultureInfo.InvariantCulture)
                     + c.B.ToString("X2", CultureInfo.InvariantCulture);
        }
    }
}
