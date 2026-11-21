using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Data;
using LodStudio.Controls;
using LodStudio.Engine;

namespace LodStudio.ViewModels
{
    /// <summary>任务运行/扫描状态（界面 8 态）。</summary>
    public enum TaskStatus
    {
        Pending = 0,     // 待处理 / 未扫描
        Scanning = 1,    // 扫描中
        Running = 2,     // 处理中
        Ok = 3,          // 完成
        Partial = 4,     // 部分完成
        Failed = 5,      // 失败
        Skipped = 6,     // 跳过（全部合理跳过）
        Cancelled = 7    // 已停止
    }

    /// <summary>队列里的一行。</summary>
    public sealed class TaskItemVm : VmBase
    {
        private TaskStatus _status = TaskStatus.Pending;
        private bool _scanned;
        private int _qcCount;
        private int _modelCount;
        private bool _skip;
        private string _note = "";
        private string _hint = "";
        private string _runDetail = "";
        private string _runErrors = "";
        private int _index;

        public TaskKind Kind { get; private set; }
        public string Path { get; private set; }

        public TaskItemVm(TaskKind kind, string path)
        {
            Kind = kind;
            Path = path;
        }

        public int Index { get { return _index; } set { Set(ref _index, value); } }
        public bool Scanned { get { return _scanned; } set { Set(ref _scanned, value); RaiseAll(); } }
        public int QcCount { get { return _qcCount; } set { Set(ref _qcCount, value); Raise("ScaleText"); } }
        public int ModelCount { get { return _modelCount; } set { Set(ref _modelCount, value); Raise("ScaleText"); } }
        public bool Skip { get { return _skip; } set { Set(ref _skip, value); Raise("ScaleText"); } }
        public string Note { get { return _note; } set { Set(ref _note, value); Raise("ScaleText"); } }
        public string Hint { get { return _hint; } set { Set(ref _hint, value); Raise("HintVisible"); } }
        public bool HintVisible { get { return !string.IsNullOrEmpty(_hint); } }
        public string RunDetail { get { return _runDetail; } set { Set(ref _runDetail, value); Raise("Tooltip"); } }
        public string RunErrors { get { return _runErrors; } set { Set(ref _runErrors, value); Raise("Tooltip"); } }

        public TaskStatus Status
        {
            get { return _status; }
            set { if (Set(ref _status, value)) { RaiseAll(); } }
        }

        public string Name
        {
            get
            {
                try
                {
                    string p = (Path ?? "").TrimEnd('\\', '/');
                    int i = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
                    return i >= 0 ? p.Substring(i + 1) : p;
                }
                catch { return Path ?? ""; }
            }
        }

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case TaskKind.Folder: return "工程";
                    case TaskKind.Qc: return "QC";
                    case TaskKind.Smd: return "SMD";
                    default: return "GLB";
                }
            }
        }

        public IconKind KindIcon
        {
            get
            {
                switch (Kind)
                {
                    case TaskKind.Folder: return IconKind.Folder;
                    case TaskKind.Qc: return IconKind.File;
                    default: return IconKind.Cube;
                }
            }
        }

        public string ParentPath
        {
            get
            {
                try
                {
                    if (Kind == TaskKind.Folder) return Path;
                    return System.IO.Path.GetDirectoryName(Path) ?? "";
                }
                catch { return ""; }
            }
        }

        public string ScaleText
        {
            get
            {
                if (!Scanned) return Status == TaskStatus.Scanning ? "扫描中…" : "…";
                if (Skip) return Note;
                if (Kind == TaskKind.Folder)
                {
                    if (QcCount == 0 && ModelCount > 0) return "无 QC · " + ModelCount + " 个模型";
                    return QcCount + " QC · " + ModelCount + " 模型";
                }
                if (Kind == TaskKind.Qc) return ModelCount + " 模型";
                return "包装 QC";
            }
        }

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case TaskStatus.Scanning: return "扫描中";
                    case TaskStatus.Running: return "处理中";
                    case TaskStatus.Ok: return "完成";
                    case TaskStatus.Partial: return "部分完成";
                    case TaskStatus.Failed: return "失败";
                    case TaskStatus.Skipped: return "跳过";
                    case TaskStatus.Cancelled: return "已停止";
                    default: return Scanned ? "待处理" : "未扫描";
                }
            }
        }

        /// <summary>状态色调（XAML DataTrigger 用）：Faint/Signal/Ok/Warn/Err</summary>
        public string StatusTone
        {
            get
            {
                switch (Status)
                {
                    case TaskStatus.Running: return "Signal";
                    case TaskStatus.Ok: return "Ok";
                    case TaskStatus.Partial: return "Warn";
                    case TaskStatus.Failed: return "Err";
                    case TaskStatus.Skipped: return "Faint";
                    case TaskStatus.Cancelled: return "Warn";
                    default: return "Faint";
                }
            }
        }

        public bool ShowActions { get { return true; } }

        public string Tooltip
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(KindText + "：" + Name);
                sb.AppendLine(Path);
                if (Scanned)
                {
                    sb.AppendLine("规模：" + ScaleText);
                    if (!string.IsNullOrEmpty(Hint)) sb.AppendLine("提示：" + Hint);
                }
                if (!string.IsNullOrEmpty(RunDetail)) sb.AppendLine("上次运行：" + RunDetail);
                if (!string.IsNullOrEmpty(RunErrors)) sb.AppendLine(RunErrors.Replace("\n", "\n    "));
                sb.Append("双击查看详情；右键更多操作");
                return sb.ToString();
            }
        }

        public TaskRef ToRef() { return new TaskRef(Kind, Path); }

        /// <summary>用于队列摘要统计</summary>
        public bool IsDone { get { return Status == TaskStatus.Ok || Status == TaskStatus.Partial || Status == TaskStatus.Skipped; } }
        public bool IsIssue { get { return Status == TaskStatus.Failed || Status == TaskStatus.Partial; } }
    }

    /// <summary>
    /// 队列：数据 + 5 个筛选页签 + 搜索 + 计数 + 摘要。
    /// 选择状态由视图（ListBox.SelectedItems）维护后写回 Selected。
    /// </summary>
    public sealed class QueueViewModel : VmBase
    {
        private readonly ObservableCollection<TaskItemVm> _items = new ObservableCollection<TaskItemVm>();
        private readonly ICollectionView _view;
        private int _filterIndex;
        private string _searchText = "";
        private bool _locked;
        private List<TaskItemVm> _selected = new List<TaskItemVm>();

        public QueueViewModel()
        {
            _view = CollectionViewSource.GetDefaultView(_items);
            _view.Filter = FilterPredicate;
            _items.CollectionChanged += delegate { Renumber(); Raise("SummaryText"); UpdateCounts(); };
        }

        public ObservableCollection<TaskItemVm> Items { get { return _items; } }
        public ICollectionView View { get { return _view; } }
        public bool Locked { get { return _locked; } set { Set(ref _locked, value); Raise("CanEdit"); } }
        public bool CanEdit { get { return !_locked; } }
        public bool IsEmpty { get { return _items.Count == 0; } }
        public bool HasItems { get { return _items.Count > 0; } }
        public bool IsFiltered { get { return _filterIndex != 0 || !string.IsNullOrEmpty(_searchText); } }
        public int Count { get { return _items.Count; } }

        public List<TaskItemVm> Selected
        {
            get { return _selected; }
            set
            {
                _selected = value ?? new List<TaskItemVm>();
                Raise("SelectionText");
            }
        }

        public string SelectionText
        {
            get
            {
                if (_selected.Count == 0) return "";
                if (_selected.Count == 1) return _selected[0].Name;
                return "已选 " + _selected.Count + " 项";
            }
        }

        public int FilterIndex
        {
            get { return _filterIndex; }
            set
            {
                if (Set(ref _filterIndex, Math.Max(0, Math.Min(4, value))))
                {
                    RefreshView();
                    Raise("IsFiltered");
                }
            }
        }

        public string SearchText
        {
            get { return _searchText; }
            set
            {
                if (Set(ref _searchText, value ?? ""))
                {
                    RefreshView();
                    Raise("IsFiltered");
                }
            }
        }

        public int CountAll { get { return _items.Count; } }
        public int CountUnfinished { get { return _items.Count(x => !x.IsDone && !x.IsIssue && x.Status != TaskStatus.Skipped); } }
        public int CountDone { get { return _items.Count(x => x.Status == TaskStatus.Ok); } }
        public int CountIssue { get { return _items.Count(x => x.Status == TaskStatus.Failed || x.Status == TaskStatus.Partial); } }
        public int CountSkipped { get { return _items.Count(x => x.Status == TaskStatus.Skipped || x.Skip); } }

        /// <summary>视图是否为空但队列本身有内容（无匹配态）</summary>
        public bool IsNoMatch
        {
            get
            {
                if (_items.Count == 0) return false;
                int visible = 0;
                foreach (var o in _view) { visible++; if (visible > 0) break; }
                return visible == 0;
            }
        }

        public string SummaryText
        {
            get
            {
                if (_items.Count == 0) return "还没有任务";
                int qc = 0, models = 0;
                foreach (var t in _items) { qc += t.QcCount; models += t.ModelCount; }
                string s = _items.Count + " 项";
                if (qc > 0) s += " · " + qc + " QC";
                if (models > 0) s += " · " + models + " 模型";
                return s;
            }
        }

        public string TopBarSummary
        {
            get
            {
                if (_items.Count == 0) return "还没有任务";
                var folders = _items.Where(x => x.Kind == TaskKind.Folder).ToList();
                if (folders.Count == 1) return "工程：" + folders[0].Name;
                if (_items.Count == 1) return _items[0].KindText + "：" + _items[0].Name;
                return _items.Count + " 个来源";
            }
        }

        private bool FilterPredicate(object obj)
        {
            var t = obj as TaskItemVm;
            if (t == null) return false;
            switch (_filterIndex)
            {
                case 1: if (t.IsDone || t.IsIssue || t.Status == TaskStatus.Skipped || t.Skip) return false; break;
                case 2: if (!(t.Status == TaskStatus.Ok)) return false; break;
                case 3: if (!t.IsIssue) return false; break;
                case 4: if (!(t.Status == TaskStatus.Skipped || t.Skip)) return false; break;
            }
            if (!string.IsNullOrEmpty(_searchText))
            {
                string q = _searchText.Trim();
                if (q.Length > 0 &&
                    (t.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (t.Path ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }
            return true;
        }

        public void RefreshView()
        {
            try { _view.Refresh(); } catch { }
            Raise("IsNoMatch");
        }

        public void UpdateCounts()
        {
            Raise("CountAll"); Raise("CountUnfinished"); Raise("CountDone"); Raise("CountIssue"); Raise("CountSkipped");
            Raise("SummaryText"); Raise("TopBarSummary"); Raise("IsEmpty"); Raise("HasItems"); Raise("IsNoMatch");
        }

        public void Renumber()
        {
            for (int i = 0; i < _items.Count; i++) _items[i].Index = i + 1;
        }

        public void Add(TaskRef r, out bool duplicate)
        {
            duplicate = false;
            if (r == null || string.IsNullOrEmpty(r.Path)) return;
            foreach (var t in _items)
            {
                if (t.Kind == r.Kind && string.Equals(t.Path, r.Path, StringComparison.OrdinalIgnoreCase))
                {
                    duplicate = true;
                    return;
                }
            }
            _items.Add(new TaskItemVm(r.Kind, r.Path));
        }

        public List<TaskItemVm> Snapshot()
        {
            return _items.ToList();
        }

        public void Remove(IEnumerable<TaskItemVm> items)
        {
            foreach (var t in items.ToList()) _items.Remove(t);
        }

        public void Clear() { _items.Clear(); }

        public void MoveItem(TaskItemVm item, int delta)
        {
            int i = _items.IndexOf(item);
            if (i < 0) return;
            int j = i + delta;
            if (j < 0 || j >= _items.Count) return;
            _items.Move(i, j);
            Renumber();
        }

        public void ResetRunState()
        {
            foreach (var t in _items)
            {
                if (t.Status == TaskStatus.Scanning) continue;
                t.Status = t.Skip ? TaskStatus.Skipped : TaskStatus.Pending;
                t.RunDetail = "";
                t.RunErrors = "";
            }
            UpdateCounts();
            RefreshView();
        }
    }
}
