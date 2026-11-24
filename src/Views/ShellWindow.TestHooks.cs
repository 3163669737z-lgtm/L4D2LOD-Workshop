using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LodStudio.Controls;
using LodStudio.ViewModels;

namespace LodStudio.Views
{
    /// <summary>仅给 --ui-audit 用的窗口级测试钩子（同程序集 internal）。</summary>
    public partial class ShellWindow
    {
        internal ShellViewModel Vm { get { return _vm; } }

        internal void TestShowToast(string title, string sub, ToastTone tone) { Toasts.Show(title, sub, tone, 4000); }
        internal Task<bool> TestConfirmAsync(string title, string message, bool danger, string detail)
            { return Dialogs.ConfirmAsync(new DialogOptions { Title = title, Message = message, OkText = danger ? "删除" : "确定", CancelText = "取消", Danger = danger, Detail = detail }); }
        internal Task TestInfoAsync(string title, string message, string detail)
            { return Dialogs.ShowInfoAsync(new DialogOptions { Title = title, Message = message, Detail = detail }); }
        internal Task<string> TestPromptAsync(string title, string label, string initial)
            { return Dialogs.PromptAsync(new DialogOptions { Title = title, Message = "", InputLabel = label, InputInitial = initial }); }
        internal void TestCloseDialog() { if (Dialogs.IsOpen) Dialogs.ForceCloseForTest(); }
        internal void TestShowDropOverlay(bool show) { DropOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed; }
        internal void TestSetLogExpanded(bool expanded) { _vm.IsLogExpanded = expanded; ApplyLogExpanded(expanded, false); }
        internal void TestSetSize(double w, double h)
        {
            MinWidth = Math.Min(1120, w);
            MinHeight = Math.Min(720, h);
            Width = w; Height = h;
            UpdateLayout();
        }
        internal Visual RootVisual { get { return RootBorder; } }
        internal ScrollViewer QueueScroll { get { return FindChild<ScrollViewer>(QueueList); } }
        internal Window WindowSelf { get { return this; } }
    }
}
