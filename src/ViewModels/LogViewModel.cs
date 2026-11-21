using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Data;
using LodStudio.Engine;

namespace LodStudio.ViewModels
{
    /// <summary>一行运行日志。</summary>
    public sealed class LogLineVm
    {
        public int Seq;
        public DateTime At;
        public LogLevel Level;
        public string Text { get; set; }

        public string SeqText { get { return Seq.ToString("000", CultureInfo.InvariantCulture); } }
        public string TimeText { get { return At.ToString("HH:mm:ss", CultureInfo.InvariantCulture); } }

        public string LevelText
        {
            get
            {
                switch (Level)
                {
                    case LogLevel.Error: return "ERR";
                    case LogLevel.Warn: return "WRN";
                    case LogLevel.Stage: return "──";
                    default: return "INF";
                }
            }
        }

        public string Tone
        {
            get
            {
                switch (Level)
                {
                    case LogLevel.Error: return "Err";
                    case LogLevel.Warn: return "Warn";
                    case LogLevel.Stage: return "Stage";
                    default: return "Normal";
                }
            }
        }

        public bool IsStage { get { return Level == LogLevel.Stage; } }
        public bool IsDetail { get { return Level == LogLevel.Detail; } }
    }

    /// <summary>
    /// 运行日志：数据 + 全部/错误过滤 + 计数 + 自动跟随状态。
    /// 自动跟随/新日志提示由视图根据滚动位置驱动（见 ShellWindow）。
    /// </summary>
    public sealed class LogViewModel : VmBase
    {
        private const int MaxLines = 4000;
        private readonly ObservableCollection<LogLineVm> _lines = new ObservableCollection<LogLineVm>();
        private readonly ICollectionView _view;
        private int _filter;       // 0 全部 / 1 错误
        private int _seq;
        private int _errorCount;
        private int _warnCount;
        private int _newCount;
        private bool _autoFollow = true;

        public LogViewModel()
        {
            _view = CollectionViewSource.GetDefaultView(_lines);
            _view.Filter = o =>
            {
                var l = o as LogLineVm;
                if (l == null) return false;
                return _filter == 0 || l.Level == LogLevel.Error;
            };
        }

        public ObservableCollection<LogLineVm> Lines { get { return _lines; } }
        public ICollectionView View { get { return _view; } }

        public int Filter
        {
            get { return _filter; }
            set { if (Set(ref _filter, Math.Max(0, Math.Min(1, value)))) { try { _view.Refresh(); } catch { } Raise("CountText"); } }
        }

        public bool AutoFollow
        {
            get { return _autoFollow; }
            set { if (Set(ref _autoFollow, value)) { if (value) NewCount = 0; } }
        }

        public int NewCount { get { return _newCount; } set { if (Set(ref _newCount, value)) Raise("HasNew"); } }
        public bool HasNew { get { return _newCount > 0; } }

        public string CountText
        {
            get
            {
                int total = _lines.Count;
                if (_filter == 1) return _errorCount + " 条错误";
                if (total == 0) return "暂无日志";
                string s = total + " 条";
                if (_errorCount > 0) s += " · " + _errorCount + " 错误";
                else if (_warnCount > 0) s += " · " + _warnCount + " 提示";
                else s += " · 无错误";
                return s;
            }
        }

        public bool IsEmpty { get { return _lines.Count == 0; } }

        /// <summary>有日志时复制 / 清空才可用。</summary>
        public bool HasLines { get { return _lines.Count > 0; } }

        /// <summary>追加一条。必须在 UI 线程调用（ShellViewModel 负责批量封送）。</summary>
        public void Append(LogLevel level, string text)
        {
            if (text == null) return;
            _seq++;
            var line = new LogLineVm { Seq = _seq, At = DateTime.Now, Level = level, Text = text };
            _lines.Add(line);
            if (level == LogLevel.Error) _errorCount++;
            else if (level == LogLevel.Warn) _warnCount++;
            if (_autoFollow) NewCount = 0;
            else if (level != LogLevel.Detail) NewCount = _newCount + 1;

            if (_lines.Count > MaxLines)
            {
                int drop = _lines.Count - MaxLines;
                for (int i = 0; i < drop; i++)
                {
                    var old = _lines[0];
                    if (old.Level == LogLevel.Error) _errorCount--;
                    else if (old.Level == LogLevel.Warn) _warnCount--;
                    _lines.RemoveAt(0);
                }
            }
            Raise("CountText"); Raise("IsEmpty"); Raise("HasLines"); Raise("HasNew");
        }

        public void Clear()
        {
            _lines.Clear();
            _seq = 0; _errorCount = 0; _warnCount = 0; NewCount = 0;
            Raise("CountText"); Raise("IsEmpty"); Raise("HasLines");
        }

        /// <summary>复制当前过滤视图的文本</summary>
        public string BuildCopyText()
        {
            var sb = new StringBuilder();
            foreach (var o in _view)
            {
                var l = o as LogLineVm;
                if (l == null) continue;
                sb.AppendLine(l.TimeText + " " + l.LevelText + " " + l.Text);
            }
            return sb.ToString();
        }

        public void ResetNewCount() { NewCount = 0; }
    }
}
