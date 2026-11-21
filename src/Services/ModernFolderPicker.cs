using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LodStudio.Services
{
    /// <summary>
    /// 现代文件夹选择器（Vista+ 资源管理器风格，IFileOpenDialog + FOS_PICKFOLDERS）：
    /// 和「导入文件」用的是同一类系统对话框——地址栏可以直接输入/粘贴路径、有搜索/侧边栏，
    /// 不再是老式 WinForms FolderBrowserDialog 的树形窗口。
    /// </summary>
    internal static class ModernFolderPicker
    {
        private const uint FOS_NOCHANGEDIR = 0x00000008;
        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint FOS_PATHMUSTEXIST = 0x00000800;
        private const int SIGDN_FILESYSPATH = unchecked((int)0x80058000);
        private const int ERROR_CANCELLED = unchecked((int)0x800704C7);

        /// <summary>选择文件夹；取消返回 null。</summary>
        public static string Pick(Window owner, string title, string initialPath)
        {
            IntPtr hwnd = IntPtr.Zero;
            try { if (owner != null) hwnd = new WindowInteropHelper(owner).Handle; }
            catch { }

            try
            {
                return PickNative(hwnd, title, initialPath);
            }
            catch
            {
                // 极少数系统/权限问题下退回老对话框，保证功能不丢
                try
                {
                    using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                    {
                        dlg.Description = title;
                        dlg.ShowNewFolderButton = false;
                        if (!string.IsNullOrEmpty(initialPath))
                        {
                            try { if (System.IO.Directory.Exists(initialPath)) dlg.SelectedPath = initialPath; } catch { }
                        }
                        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
                    }
                }
                catch { return null; }
            }
        }

        private static string PickNative(IntPtr hwnd, string title, string initialPath)
        {
            var dialog = (IFileDialog)new FileOpenDialogRCW();
            uint options;
            dialog.GetOptions(out options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
            if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
            if (!string.IsNullOrEmpty(initialPath))
            {
                try
                {
                    Guid iid = typeof(IShellItem).GUID;
                    IShellItem folder;
                    int hrItem = SHCreateItemFromParsingName(initialPath, IntPtr.Zero, ref iid, out folder);
                    if (hrItem >= 0 && folder != null) dialog.SetFolder(folder);
                }
                catch { }
            }

            int hr = dialog.Show(hwnd);
            if (hr == ERROR_CANCELLED) return null;
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            IShellItem item;
            dialog.GetResult(out item);
            if (item == null) return null;
            IntPtr ptr;
            item.GetDisplayName(SIGDN_FILESYSPATH, out ptr);
            try { return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUni(ptr); }
            finally { if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr); }
        }

        // ─────────── COM 互操作（只用到文件对话框的少量方法） ───────────

        [ComImport]
        [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close([MarshalAs(UnmanagedType.Error)] int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(int sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport]
        [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        [ClassInterface(ClassInterfaceType.None)]
        private class FileOpenDialogRCW
        {
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
    }
}
