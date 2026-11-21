using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using LodStudio.Engine;

namespace LodStudio.Engine
{
    /// <summary>报告/日志与备份的落点解析（跟随设置；任何失败都退回安全默认）。</summary>
    public static class OutputLocations
    {
        public static string ResolveReportDir(AppSettings s, string qcDir)
        {
            try
            {
                if (s == null) return qcDir;
                switch (s.ReportLocation)
                {
                    case 1:
                        {
                            string sub = Path.Combine(AppInfo.ExeDir, AppInfo.ReportFolderName);
                            if (FileUtil.EnsureDir(sub)) return sub;
                            return qcDir;
                        }
                    case 2:
                        {
                            string c = s.ReportCustomDir;
                            if (!string.IsNullOrEmpty(c) && FileUtil.EnsureDir(c)) return c;
                            return qcDir;
                        }
                    default:
                        return qcDir;
                }
            }
            catch { return qcDir; }
        }

        /// <summary>null = 备份在原文件旁</summary>
        public static string ResolveBackupBaseDir(AppSettings s)
        {
            try
            {
                if (s == null) return null;
                switch (s.BackupLocation)
                {
                    case 1:
                        {
                            string sub = Path.Combine(AppInfo.ExeDir, AppInfo.BackupFolderName);
                            if (FileUtil.EnsureDir(sub)) return sub;
                            return null;
                        }
                    case 2:
                        {
                            string c = s.BackupCustomDir;
                            if (!string.IsNullOrEmpty(c) && FileUtil.EnsureDir(c)) return c;
                            return null;
                        }
                    default:
                        return null;
                }
            }
            catch { return null; }
        }

        public static string DescribeReport(AppSettings s)
        {
            switch (s.ReportLocation)
            {
                case 1: return "exe 旁的「" + AppInfo.ReportFolderName + "」文件夹";
                case 2: return string.IsNullOrEmpty(s.ReportCustomDir) ? "QC 目录旁" : s.ReportCustomDir;
                default: return "QC 目录旁";
            }
        }

        public static string DescribeBackup(AppSettings s)
        {
            if (!s.BackupEnabled) return "已关闭备份";
            switch (s.BackupLocation)
            {
                case 1: return "exe 旁的「" + AppInfo.BackupFolderName + "」文件夹";
                case 2: return string.IsNullOrEmpty(s.BackupCustomDir) ? "QC 旁边" : s.BackupCustomDir;
                default: return "QC 旁边";
            }
        }
    }

    /// <summary>运行环境：管理员 / 低完整性 / 目录可写。</summary>
    public static class RuntimeContext
    {
        public static bool IsAdmin
        {
            get
            {
                try
                {
                    using (var id = WindowsIdentity.GetCurrent())
                    {
                        var p = new WindowsPrincipal(id);
                        return p.IsInRole(WindowsBuiltInRole.Administrator);
                    }
                }
                catch { return false; }
            }
        }

        private const int LowIntegrityRid = 0x1000;   // SECURITY_MANDATORY_LOW_RID

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct SID_AND_ATTRIBUTES
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct TOKEN_MANDATORY_LABEL
        {
            public SID_AND_ATTRIBUTES Label;
        }

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>是否低完整性运行（写不进普通目录，必须提示用户）</summary>
        public static bool IsLowIntegrity()
        {
            IntPtr token = IntPtr.Zero;
            IntPtr info = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /*TOKEN_QUERY*/, out token)) return false;
                uint size = 0;
                GetTokenInformation(token, 25 /*TokenIntegrityLevel*/, IntPtr.Zero, 0, out size);
                if (size == 0) return false;
                info = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)size);
                if (!GetTokenInformation(token, 25, info, size, out size)) return false;
                var label = (TOKEN_MANDATORY_LABEL)System.Runtime.InteropServices.Marshal.PtrToStructure(info, typeof(TOKEN_MANDATORY_LABEL));
                IntPtr sub = GetSidSubAuthority(label.Label.Sid, 0);
                uint rid = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(sub);
                return rid <= LowIntegrityRid;
            }
            catch { return false; }
            finally
            {
                if (info != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(info);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }
    }
}
