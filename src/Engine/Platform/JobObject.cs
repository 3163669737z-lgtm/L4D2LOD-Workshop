using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LodStudio.Engine.Platform
{
    /// <summary>
    /// 进程级单例 Job Object（JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE）：
    /// 每个 减面 / 编译器子进程都 Assign 进来，句柄只在本进程退出时由系统关闭。
    /// 因此主程序无论正常退出、崩溃还是被任务管理器结束，系统都会把子进程一并结束，不留孤儿。
    /// </summary>
    public static class JobObject
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        private static readonly object Gate = new object();
        private static IntPtr _handle = IntPtr.Zero;   // 故意永不关闭：随进程退出由系统关闭
        private static bool _initFailed;
        private static string _lastError;

        public static string LastError { get { return _lastError; } }

        private static IntPtr Handle()
        {
            lock (Gate)
            {
                if (_handle != IntPtr.Zero || _initFailed) return _handle;
                IntPtr mem = IntPtr.Zero;
                try
                {
                    IntPtr h = CreateJobObject(IntPtr.Zero, null);
                    if (h == IntPtr.Zero)
                    {
                        _lastError = "CreateJobObject 失败，错误码 " + Marshal.GetLastWin32Error();
                        _initFailed = true;
                        return IntPtr.Zero;
                    }
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                    int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                    mem = Marshal.AllocHGlobal(size);
                    Marshal.StructureToPtr(info, mem, false);
                    if (!SetInformationJobObject(h, JobObjectExtendedLimitInformation, mem, (uint)size))
                    {
                        _lastError = "SetInformationJobObject 失败，错误码 " + Marshal.GetLastWin32Error();
                        _initFailed = true;
                        return IntPtr.Zero;
                    }
                    _handle = h;
                    return _handle;
                }
                catch (Exception ex)
                {
                    _lastError = "Job Object 初始化异常: " + ex.Message;
                    _initFailed = true;
                    return IntPtr.Zero;
                }
                finally
                {
                    if (mem != IntPtr.Zero) Marshal.FreeHGlobal(mem);
                }
            }
        }

        /// <summary>把进程放进作业对象；失败返回 false（不影响运行，只是失去"随主程序退出"的保障）</summary>
        public static bool Assign(Process process)
        {
            if (process == null) return false;
            try
            {
                IntPtr h = Handle();
                if (h == IntPtr.Zero) return false;
                if (!AssignProcessToJobObject(h, process.Handle))
                {
                    _lastError = "AssignProcessToJobObject 失败，错误码 " + Marshal.GetLastWin32Error();
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _lastError = "AssignProcessToJobObject 异常: " + ex.Message;
                return false;
            }
        }
    }
}
