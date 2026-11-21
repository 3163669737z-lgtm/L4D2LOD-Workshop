using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LodStudio.Controls;
using LodStudio.Engine;

namespace LodStudio.ViewModels
{
    /// <summary>
    /// 视图（ShellWindow）提供给 ViewModel 的能力：对话框、Toast、文件选择、打开资源管理器等。
    /// ViewModel 不直接碰窗口，便于测试/替换。
    /// </summary>
    public interface IShellInteraction
    {
        /// <summary>模态确认；detail 可空。danger=true 时默认焦点在取消。</summary>
        Task<bool> ConfirmAsync(string title, string message, string okText, string cancelText, bool danger, string detail);

        Task ShowInfoAsync(string title, string message, string detail);

        /// <summary>文本输入（如"保存为我的预设"）；返回 null = 取消。</summary>
        Task<string> PromptAsync(string title, string label, string initial, Func<string, string> validate);

        void ShowToast(string title, string subtitle, ToastTone tone);

        /// <summary>选择文件（可多选，QC / GLB / SMD 同一入口）；返回空列表 = 取消。</summary>
        Task<IReadOnlyList<string>> PickFilesAsync();

        Task<string> PickFolderAsync();

        Task<string> PickFileAsync(string title, string filter, string initialDir);

        /// <summary>在资源管理器中显示/打开</summary>
        void RevealInExplorer(string path);

        void OpenFile(string path);

        void OpenSettings(int page);

        /// <summary>把焦点移到 LOD 第 n 行（0 起）的数字框</summary>
        void FocusLodLevel(int levelIndex);

        /// <summary>把焦点移到搜索框</summary>
        void FocusSearch();

        void FlashWindow();

        /// <summary>系统「显示动画效果」是否开启</summary>
        bool AnimationsEnabled { get; }
    }
}
