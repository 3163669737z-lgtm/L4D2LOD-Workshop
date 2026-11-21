using System;
using System.Collections.Generic;

namespace LodStudio.Engine
{
    /// <summary>日志级别（引擎产生，界面着色）。</summary>
    public enum LogLevel
    {
        /// <summary>细节：只进日志文件，界面默认也显示但颜色最弱。</summary>
        Detail = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
        /// <summary>阶段标题（"── 3/6 简化 ──" 这类），界面加粗显示。</summary>
        Stage = 4
    }

    /// <summary>批量运行的阶段（进度/状态展示用）。</summary>
    public enum BatchStage
    {
        None = 0,
        Planning = 1,
        Blender = 2,
        ConvertingSmd = 3,
        Injecting = 4,
        Compiling = 5,
        Reporting = 6,
        Done = 7
    }

    /// <summary>$lod 写到哪里。</summary>
    public enum OutputMode
    {
        /// <summary>追加到每个原 QC（默认；编译脚本不用改）。</summary>
        InPlace = 0,
        /// <summary>每个 QC 旁生成 xxx_LOD.qc，原 QC 只加一行 $include。</summary>
        SeparateQc = 1,
        /// <summary>不动任何 QC，只在工程根输出 _LOD合集.qc 供手工粘贴。</summary>
        CollectionOnly = 2
    }

    /// <summary>备份策略。</summary>
    public sealed class BackupPolicy
    {
        public bool Enabled = true;
        /// <summary>null/空 = 备份在原文件旁；非空 = 集中备份到该目录（镜像工程相对路径）。</summary>
        public string CentralBaseDir;
        /// <summary>工程根目录（集中备份时算相对路径用）。</summary>
        public string ProjectRoot;

        public static BackupPolicy Disabled { get { return new BackupPolicy { Enabled = false }; } }
    }

    /// <summary>批量观察者：引擎回调，UI / CLI 各自实现。所有方法可能来自后台线程。</summary>
    public interface IBatchObserver
    {
        void Log(LogLevel level, string text);
        /// <summary>done/total 为 Blender 任务粒度；total=0 表示未知。</summary>
        void Progress(int done, int total, TimeSpan elapsed);
        void Stage(BatchStage stage, string detail);
    }

    /// <summary>什么都不做的观察者（测试/内部用）。</summary>
    public sealed class NullObserver : IBatchObserver
    {
        public static readonly NullObserver Instance = new NullObserver();
        public void Log(LogLevel level, string text) { }
        public void Progress(int done, int total, TimeSpan elapsed) { }
        public void Stage(BatchStage stage, string detail) { }
    }

    /// <summary>收集日志的观察者（CLI / 测试用）。</summary>
    public sealed class CollectingObserver : IBatchObserver
    {
        private readonly object _gate = new object();
        public readonly List<string> Lines = new List<string>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        private readonly Action<string> _echo;

        public CollectingObserver(Action<string> echo = null) { _echo = echo; }

        public void Log(LogLevel level, string text)
        {
            string line = (level == LogLevel.Error ? "[错误] " : level == LogLevel.Warn ? "[提示] " : "") + text;
            lock (_gate)
            {
                Lines.Add(line);
                if (level == LogLevel.Error) Errors.Add(text);
                else if (level == LogLevel.Warn) Warnings.Add(text);
            }
            if (_echo != null) { try { _echo(line); } catch { } }
        }

        public void Progress(int done, int total, TimeSpan elapsed) { }
        public void Stage(BatchStage stage, string detail)
        {
            if (detail != null) Log(LogLevel.Stage, detail);
        }
    }

    /// <summary>进度回调的最小实现（UI 封送用）。</summary>
    public sealed class ProgressReporter : IProgress<int>
    {
        private readonly Action<int> _action;
        public ProgressReporter(Action<int> action) { _action = action; }
        public void Report(int value) { if (_action != null) _action(value); }
    }
}
