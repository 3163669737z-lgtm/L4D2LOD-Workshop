using System.Windows;
using System.Windows.Controls;

namespace LodStudio.Views.Settings
{
    /// <summary>
    /// 设置行：左侧「标题 + 说明」，右侧放控件（内容）。
    /// 设置页里所有"一行一个设置"的布局都用它，保证行高 44、间距、说明换行一致。
    /// 模板在 Views/Settings/SettingsKit.xaml 里（隐式样式）。
    /// </summary>
    public sealed class SettingRow : ContentControl
    {
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            "Title", typeof(string), typeof(SettingRow), new PropertyMetadata(""));

        public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
            "Hint", typeof(string), typeof(SettingRow), new PropertyMetadata(""));

        /// <summary>说明文字的语气："Normal"（默认弱字）/ "Warn" / "Err"</summary>
        public static readonly DependencyProperty HintToneProperty = DependencyProperty.Register(
            "HintTone", typeof(string), typeof(SettingRow), new PropertyMetadata("Normal"));

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public string Hint
        {
            get { return (string)GetValue(HintProperty); }
            set { SetValue(HintProperty, value); }
        }

        public string HintTone
        {
            get { return (string)GetValue(HintToneProperty); }
            set { SetValue(HintToneProperty, value); }
        }

        public SettingRow()
        {
            MinHeight = 44;
            Focusable = false;
            HorizontalContentAlignment = HorizontalAlignment.Right;
            VerticalContentAlignment = VerticalAlignment.Center;
        }
    }
}
