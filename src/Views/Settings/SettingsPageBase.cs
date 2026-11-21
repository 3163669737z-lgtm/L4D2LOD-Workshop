using System.Windows.Controls;
using LodStudio.Engine;

namespace LodStudio.Views.Settings
{
    /// <summary>
    /// 设置子页面基类：统一拿到宿主窗口（Settings / Shell / Apply），并提供同步保护。
    /// 所有改动流程都是：控件事件 → 写 <see cref="Settings"/> 字段 → <see cref="Apply"/>（保存 + 通知主窗口 + 闪"已保存"）。
    /// </summary>
    public abstract class SettingsPageBase : UserControl
    {
        internal SettingsWindow Win { get; private set; }

        /// <summary>同步 UI 期间为 true：此时控件事件不应回写设置（防止初始化触发保存/递归）。</summary>
        protected bool Syncing { get; set; }

        protected AppSettings Settings
        {
            get { return Win == null ? null : Win.Settings; }
        }

        internal void Attach(SettingsWindow win)
        {
            Win = win;
            OnAttached();
            SyncFromSettings();
        }

        /// <summary>构造完成后调用一次（此时 Win 已可用），用于挂事件。</summary>
        protected virtual void OnAttached() { }

        /// <summary>把设置值回填到控件（切页 / 主题变化 / 跨页联动时调用）。子类必须用 Syncing 保护。</summary>
        public abstract void SyncFromSettings();

        /// <summary>页面变为可见时调用（默认重新同步一次）。</summary>
        public virtual void OnPageShown()
        {
            if (Win != null) SyncFromSettings();
        }

        /// <summary>写设置 + 通知主窗口（保存、刷新摘要、重查引擎）+ 标题栏"已保存"脉冲。</summary>
        protected void Apply(bool warn)
        {
            if (Syncing || Win == null) return;
            Win.Apply(warn);
        }
    }
}
