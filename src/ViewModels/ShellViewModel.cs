using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LodStudio.Controls;
using LodStudio.Engine;
using LodStudio.Services;

namespace LodStudio.ViewModels
{
    /// <summary>
    /// 主界面 ViewModel：把队列 / LOD 方案 / 日志 / 环境检测 / 扫描 / 计划 / 运行 / 报告 串起来。
    /// 所有引擎调用都在后台线程，进度与日志通过观察者封送回 UI 线程（批量刷新，避免卡界面）。
    /// </summary>
    public sealed partial class ShellViewModel : VmBase
    {
        private readonly IShellInteraction _ui;
        private readonly DispatcherTimer _pump;
        private readonly ConcurrentQueue<KeyValuePair<LogLevel, string>> _logQueue =
            new ConcurrentQueue<KeyValuePair<LogLevel, string>>();
        private readonly object _progressGate = new object();

        private int _pendingDone = -1;
        private int _pendingTotal = -1;
        private TimeSpan _pendingElapsed;
        private BatchStage _pendingStage;
        private BatchStage _runStage;

        private bool _isRunning;
        private bool _isBusy;
        private bool _isPlanVisible;
        private bool _isStopping;
        private bool _loadingTaskDetails;
        private CancellationTokenSource _cts;

        private string _statusTitle = "准备就绪";
        private string _statusSub = "导入后可扫描预览或开始生成";
        private string _statusTone = "Normal";
        private string _readoutMainText = "0";
        private string _readoutSub = "项在队列";
        private bool _progressVisible;
        private double _progressValue;
        private bool _progressIndeterminate;
        private bool _progressFailed;

        private string _reductionText = "检测中";
        private string _reductionTone = "Faint";
        private string _compilerText = "未设置";
        private string _compilerTone = "Faint";
        private string _permissionText = "…";
        private string _permissionTone = "Faint";
        private string _envText = "环境检测中";
        private string _envTone = "Faint";
        private string _envTip = "正在检测减面后端 / 编译器 / 权限…";

        private string _lastReportPath;
        private string _lastOutputDir;
        private DateTime _lastRunAt = DateTime.MinValue;
        private int _lastOk, _lastPartial, _lastFailed;

        private string _advancedSummary = "";
        private DispatcherTimer _envDebounce;
        private bool _isLogExpanded;
        private PlanPreviewViewModel _planPreview;

        public ShellViewModel(AppSettings settings, IShellInteraction ui)
        {
            Settings = settings ?? new AppSettings();
            _ui = ui;

            Queue = new QueueViewModel();
            LodPlan = new LodPlanViewModel();
            Log = new LogViewModel();

            LodPlan.PlanChanged += delegate { InvalidatePlan(); };
            LodPlan.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(LodPlan.WriteMode)) InvalidatePlan();
                if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(LodPlan.IsAutomatic) ||
                    e.PropertyName == nameof(LodPlan.WriteMode)) RefreshAdvancedSummary();
            };
            LodPlan.EditRequested += delegate (object s, int idx) { _ui.FocusLodLevel(idx); };
            Queue.Items.CollectionChanged += delegate { InvalidatePlan(); };

            Commands = new ShellCommands(this);

            _pump = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(45) };
            _pump.Tick += OnPump;

            LoadFromSettings();
            AppendInitialLog();
        }

        // ─────────────────────────── 公开状态 ───────────────────────────

        public AppSettings Settings { get; private set; }
        public QueueViewModel Queue { get; private set; }
        public LodPlanViewModel LodPlan { get; private set; }
        public LogViewModel Log { get; private set; }
        public ShellCommands Commands { get; private set; }

        public PlanPreviewViewModel PlanPreview
        {
            get { return _planPreview; }
            private set { if (Set(ref _planPreview, value)) UpdateActions(); }
        }

        public bool IsPlanVisible
        {
            get { return _isPlanVisible; }
            private set { if (Set(ref _isPlanVisible, value)) UpdateActions(); }
        }

        public bool IsRunning
        {
            get { return _isRunning; }
            private set { if (Set(ref _isRunning, value)) UpdateOperationState(); }
        }

        public bool IsBusy { get { return _isBusy; } private set { if (Set(ref _isBusy, value)) UpdateOperationState(); } }
        public bool IsStopping { get { return _isStopping; } private set { if (Set(ref _isStopping, value)) Raise(nameof(StopButtonText)); } }
        public bool CanEdit => !IsBusy && !IsRunning;

        private void UpdateOperationState()
        {
            Queue.Locked = !CanEdit;
            LodPlan.IsLocked = !CanEdit;
            Raise(nameof(CanEdit));
            Raise(nameof(ShowStartButton));
            Raise(nameof(ShowStopButton));
            Raise(nameof(StopButtonText));
            UpdateActions();
        }

        public string StatusTitle { get { return _statusTitle; } private set { Set(ref _statusTitle, value); } }
        public string StatusSub { get { return _statusSub; } private set { Set(ref _statusSub, value); } }
        public string StatusTone { get { return _statusTone; } private set { if (Set(ref _statusTone, value)) Raise(nameof(ShowResultLog)); } }
        public string ReadoutMainText { get { return _readoutMainText; } private set { Set(ref _readoutMainText, value); } }
        public string ReadoutSub { get { return _readoutSub; } private set { Set(ref _readoutSub, value); } }

        public bool ProgressVisible { get { return _progressVisible; } private set { Set(ref _progressVisible, value); } }
        public double ProgressValue { get { return _progressValue; } private set { Set(ref _progressValue, value); } }
        public bool ProgressIndeterminate { get { return _progressIndeterminate; } private set { Set(ref _progressIndeterminate, value); } }
        public bool ProgressFailed { get { return _progressFailed; } private set { Set(ref _progressFailed, value); } }

        public string ReductionText { get { return _reductionText; } private set { Set(ref _reductionText, value); } }
        public string ReductionTone { get { return _reductionTone; } private set { Set(ref _reductionTone, value); } }
        public string CompilerText { get { return _compilerText; } private set { Set(ref _compilerText, value); } }
        public string CompilerTone { get { return _compilerTone; } private set { Set(ref _compilerTone, value); } }
        public string CompilerTip { get { return "编译器（nekomdl.exe）可选：只有开启「生成后试编译」才用（只读状态；设置 → 引擎页）"; } }
        public string PermissionText { get { return _permissionText; } private set { Set(ref _permissionText, value); } }
        public string PermissionTone { get { return _permissionTone; } private set { Set(ref _permissionTone, value); } }
        public string PermissionTip
        {
            get
            {
                if (RuntimeContext.IsLowIntegrity()) return "低完整性运行：哪里都写不了，请把 exe 复制到桌面/其它盘或解除锁定（只读）";
                if (!RuntimeContext.IsAdmin) return "普通权限。若杀软拦截写文件，可把工具加入信任区或右键以管理员身份运行（只读）";
                return "管理员权限（只读）";
            }
        }

        /// <summary>顶栏只显示一个环境状态：绿点=全部正常，红点=任意一项异常（减面后端不可用 / 低完整性 / 编译器已配置但不可用）。</summary>
        public string EnvText { get { return _envText; } private set { Set(ref _envText, value); } }
        public string EnvTone { get { return _envTone; } private set { Set(ref _envTone, value); } }
        public string EnvTip { get { return _envTip; } private set { Set(ref _envTip, value); } }

        public string AdvancedSummary { get { return _advancedSummary; } private set { Set(ref _advancedSummary, value); } }

        public bool IsLogExpanded
        {
            get { return _isLogExpanded; }
            set { if (Set(ref _isLogExpanded, value)) { Settings.LogExpanded = value; SaveSettings(); } }
        }

        public bool HasLastRun { get { return _lastRunAt != DateTime.MinValue; } }
        public string LastRunText
        {
            get
            {
                if (!HasLastRun) return "还没有运行记录";
                string rel = DescribeRelative(_lastRunAt);
                var parts = new List<string> { "上次运行：" + rel };
                if (_lastOk > 0) parts.Add(_lastOk + " 完成");
                if (_lastPartial > 0) parts.Add(_lastPartial + " 部分完成");
                if (_lastFailed > 0) parts.Add(_lastFailed + " 失败");
                return string.Join(" · ", parts.ToArray());
            }
        }
        public bool CanOpenLastReport { get { return !string.IsNullOrEmpty(_lastReportPath) && File.Exists(_lastReportPath); } }
        public bool CanOpenOutputFolder => !string.IsNullOrEmpty(_lastOutputDir) && Directory.Exists(_lastOutputDir);
        public bool ShowResultLog => StatusTone == "Err";

        public string StartHint
        {
            get
            {
                if (LodPlan != null && !LodPlan.IsValid && !string.IsNullOrEmpty(LodPlan.ValidationError))
                    return LodPlan.ValidationError;
                if (Queue.Items.Count == 0) return "先导入工程文件夹、QC 或模型";
                if (IsPlanVisible && PlanPreview != null)
                {
                    if (PlanPreview.IsStale) return "请重新扫描后再生成";
                    if (!PlanPreview.CanStartFromPlan) return "没有可执行的作业，可查看预览中的原因";
                }
                return StartButtonText + "（F5）";
            }
        }

        public bool CanStart => CanPreview && (!IsPlanVisible || PlanPreview?.CanStartFromPlan == true);
        public bool CanPreview => CanEdit && Queue.HasItems && LodPlan.IsValid;
        public bool ShowStartButton => CanEdit;
        public bool ShowStopButton => !CanEdit;
        public string PreviewButtonText => IsPlanVisible ? "重新扫描" : "扫描预览";
        public string StartButtonText => !IsPlanVisible ? "开始生成" : PlanPreview?.CleanupOnly == true ? "按计划清理" : "按计划生成";
        public string StopButtonText => IsStopping ? "正在停止" : IsRunning ? "停止生成" : "取消";

        private void UpdateActions()
        {
            Raise(nameof(CanStart));
            Raise(nameof(CanPreview));
            Raise(nameof(PreviewButtonText));
            Raise(nameof(StartButtonText));
            Raise(nameof(StartHint));
        }

        // ─────────────────────────── 设置装载 ───────────────────────────

        private void LoadFromSettings()
        {
            var plan = Settings.GetPlan();
            LodPlan.LoadFrom(plan, Settings.PresetName, Settings.GetCustomPlan());
            LodPlan.WriteMode = Settings.OutputMode;
            IsLogExpanded = Settings.LogExpanded;

            if (Settings.RestoreTasks)
            {
                foreach (var t in Settings.Tasks)
                {
                    bool dup;
                    Queue.Add(t, out dup);
                }
            }
            RefreshAdvancedSummary();
            UpdateReadiness();
            UpdateQueueStatus();
        }

        private void AppendInitialLog()
        {
            Log.Append(LogLevel.Info, AppInfo.TitleText + " 已启动（" + AppInfo.Bitness + "）");
            Log.Append(LogLevel.Detail, "设置文件：" + AppInfo.SettingsFile);
            if (!string.IsNullOrEmpty(Settings.MigratedFrom))
                Log.Append(LogLevel.Detail, "已从旧版导入设置（" + Settings.MigratedFrom + "）");
        }

        public void SaveSettings()
        {
            try
            {
                Settings.SetPlan(LodPlan.Plan);
                Settings.PresetName = LodPlan.PresetDisplay;
                Settings.SetCustomPlan(LodPlan.CloneCustomPlan());
                Settings.OutputMode = LodPlan.WriteMode;
                Settings.LogExpanded = IsLogExpanded;
                Settings.Tasks = Queue.Items.Select(x => x.ToRef()).ToList();
                SettingsStore.Save(Settings);
            }
            catch { }
        }

        public void RefreshAdvancedSummary()
        {
            var parts = new List<string>();
            parts.Add(Settings.QualityFirst ? "严格几何预算" : "标准几何预算");
            if (Settings.AdaptiveTriangleBudget) parts.Add("均匀减面");
            if (Settings.AllowVertexUpdates) parts.Add("顶点微调");
            parts.Add(Settings.Workers <= 0 ? "自动并行" : Settings.Workers.ToString(CultureInfo.InvariantCulture) + " 路并行");
            if (!LodPlan.IsAutomatic)
            {
                if (!LodPlan.IsCollectionOnly && Settings.ForceOverwriteLod) parts.Add("覆盖手写");
                if (!LodPlan.IsCollectionOnly && Settings.CompileAfter) parts.Add("试编译");
                if (Settings.NoFacial) parts.Add("写入 nofacial");
            }
            if (Settings.SkipMainModel) parts.Add("跳过主模型");
            AdvancedSummary = string.Join(" · ", parts.ToArray());
        }

        /// <summary>设置窗口改动后调用：写盘 + 刷新摘要/就绪 + 计划失效</summary>
        public void OnSettingsChanged()
        {
            RefreshAdvancedSummary();
            SaveSettings();
            InvalidatePlan();
            UpdateReadiness();
            DebounceEnvironmentCheck();
        }

        /// <summary>连续改设置时只做一次环境探测（300ms 防抖）。</summary>
        private void DebounceEnvironmentCheck()
        {
            try
            {
                if (_envDebounce == null)
                {
                    _envDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(320) };
                    _envDebounce.Tick += delegate
                    {
                        _envDebounce.Stop();
                        _ = RunEnvironmentCheckAsync(true);
                    };
                }
                _envDebounce.Stop();
                _envDebounce.Start();
            }
            catch { }
        }

        // ─────────────────────────── 环境检测 ───────────────────────────

        private void RefreshEnvStatus()
        {
            bool detecting = ReductionText == "检测中";
            bool ok = ReductionTone == "Ok" && PermissionTone != "Err" && CompilerTone != "Err";
            EnvTone = detecting ? "Faint" : (ok ? "Ok" : "Err");
            EnvText = detecting ? "环境检测中" : (ok ? "环境正常" : "环境异常");
            EnvTip = "减面后端：" + ReductionText + "\n" +
                     "编译器：" + CompilerText + "（可选，仅试编译用）\n" +
                     "权限：" + PermissionText + "\n\n点击打开「运行环境诊断」";
        }

        private static bool? _gltfpackEmbeddedOk;

        /// <summary>内嵌 gltfpack 资源是否可用（只查资源，不写盘；结果缓存）。</summary>
        private static bool GltfpackEmbeddedOk()
        {
            if (_gltfpackEmbeddedOk == null)
            {
                try
                {
                    byte[] b = GltfpackTool.LoadBytes();
                    _gltfpackEmbeddedOk = b != null && b.Length > 100000;
                }
                catch { _gltfpackEmbeddedOk = false; }
            }
            return _gltfpackEmbeddedOk.Value;
        }

        public Task RunEnvironmentCheckAsync(bool silent = false)
        {
            PermissionText = RuntimeContext.IsLowIntegrity() ? "低完整性" : (RuntimeContext.IsAdmin ? "管理员" : "普通权限");
            PermissionTone = RuntimeContext.IsLowIntegrity() ? "Err" : (RuntimeContext.IsAdmin ? "Ok" : "Faint");

            bool backendOk = GltfpackEmbeddedOk();
            ReductionText = backendOk ? "内置 meshoptimizer" : "内嵌减面组件缺失";
            ReductionTone = backendOk ? "Ok" : "Err";

            string compiler = Settings.NekomdlPath;
            if (string.IsNullOrEmpty(compiler) || !File.Exists(compiler))
                compiler = Nekomdl.Find(Settings.NekomdlPath);
            if (!string.IsNullOrEmpty(compiler) && File.Exists(compiler))
            {
                Settings.NekomdlPath = compiler;
                CompilerText = "已设置";
                CompilerTone = "Ok";
            }
            else
            {
                CompilerText = "未设置";
                CompilerTone = "Faint";
            }
            RefreshEnvStatus();
            UpdateReadiness();
            if (!silent)
            {
                Log.Append(LogLevel.Info, "引擎检测：减面后端 " + ReductionText +
                                          "，编译器 " + CompilerText + "，权限 " + PermissionText);
            }
            return Task.CompletedTask;
        }

        private void UpdateReadiness()
        {
            UpdateActions();
            if (!HasLastRun) UpdateReadoutIdle();
        }

        private void UpdateReadoutIdle()
        {
            SetReadout(Queue.Count, "项在队列");
        }

        // ─────────────────────────── 队列操作 ───────────────────────────

        public Task AddPathsAsync(IEnumerable<string> paths)
        {
            if (!CanEdit)
            {
                _ui.ShowToast("当前操作尚未结束", "结束后可继续导入", ToastTone.Warn);
                return Task.CompletedTask;
            }

            var inputs = paths?.ToArray();
            if (inputs == null || inputs.Length == 0) return Task.CompletedTask;
            return PerformOperationAsync("导入失败", async token =>
            {
                SetStatus("正在导入", "检查文件路径与格式", "Signal", true);
                var candidates = await Task.Run(() =>
                {
                    var found = new List<TaskRef>();
                    foreach (string path in inputs)
                    {
                        token.ThrowIfCancellationRequested();
                        var task = TaskRef.FromPath(path?.Trim().Trim('"'));
                        if (task != null) found.Add(task);
                    }
                    return found;
                }, token);
                token.ThrowIfCancellationRequested();

                var added = new List<TaskItemVm>();
                foreach (var candidate in candidates)
                {
                    var item = Queue.Add(candidate, out _);
                    if (item != null) added.Add(item);
                }
                int duplicates = candidates.Count - added.Count;
                int ignored = inputs.Length - candidates.Count;
                var summary = new List<string>();
                if (duplicates > 0) summary.Add("重复 " + duplicates + " 项");
                if (ignored > 0) summary.Add("路径或格式无法识别 " + ignored + " 项");

                if (added.Count == 0)
                {
                    FinishActivity("没有导入新任务", string.Join(" · ", summary), "Warn");
                    return;
                }

                Settings.LastAddKind = (int)added[0].Kind;
                SaveSettings();
                SetStatus("正在扫描导入项", "读取 " + added.Count + " 项的工程规模", "Signal", true);
                SetReadout(Queue.Count, "项在队列");
                foreach (var item in added) item.Status = TaskStatus.Scanning;
                try
                {
                    var tasks = added.Select(item => item.ToRef()).ToList();
                    bool automatic = LodPlan.IsAutomatic;
                    var results = await Task.Run(() => tasks.Select(t => ScanOne(t, automatic, token)).ToList(), token);
                    token.ThrowIfCancellationRequested();
                    for (int i = 0; i < results.Count; i++)
                    {
                        var item = added[i];
                        var result = results[i];
                        item.Scanned = true;
                        item.QcCount = result.Qc;
                        item.ModelCount = result.Models;
                        item.Skip = result.Skip;
                        item.Note = result.Note;
                        item.Hint = result.Hint;
                        item.Status = result.Skip ? TaskStatus.Skipped : TaskStatus.Pending;
                        Log.Append(result.Skip ? LogLevel.Warn : LogLevel.Info,
                            item.Name + " · " + item.ScaleText + (result.Hint.Length > 0 ? " · " + result.Hint : ""));
                        foreach (var line in result.Lines) Log.Append(LogLevel.Detail, line);
                    }
                    int skipped = results.Count(r => r.Skip);
                    if (skipped > 0) summary.Add(skipped + " 项未找到可处理模型");
                    summary.Add(IsPlanVisible ? "重新扫描作业计划" : "可扫描预览或开始生成");
                    FinishActivity("已导入 " + added.Count + " 项", string.Join(" · ", summary), skipped > 0 ? "Warn" : "Ok");
                }
                finally
                {
                    foreach (var item in added.Where(t => t.Status == TaskStatus.Scanning)) item.Status = TaskStatus.Pending;
                    Queue.UpdateCounts();
                    Queue.RefreshView();
                }
            });
        }

        public void RemoveSelected()
        {
            RemoveTasks(Queue.Selected);
        }

        public void RemoveTask(TaskItemVm item) => RemoveTasks(new[] { item });

        private void RemoveTasks(IReadOnlyCollection<TaskItemVm> tasks)
        {
            if (!CanEdit || tasks.Count == 0) return;
            Queue.Remove(tasks);
            RefreshQueueCollections();
            SaveSettings();
            if (Queue.IsEmpty && IsPlanVisible) ClosePlan();
        }

        public async Task ClearQueueAsync()
        {
            if (!CanEdit || Queue.IsEmpty) return;
            bool ok = await _ui.ConfirmAsync("清空任务队列",
                "将从列表移除 " + Queue.Count + " 项。", "清空", "取消", false, null);
            if (!ok || !CanEdit) return;
            Queue.Clear();
            Queue.Selected = new List<TaskItemVm>();
            ClosePlan();
            RefreshQueueCollections();
            SaveSettings();
        }

        public void MoveSelected(int delta)
        {
            if (!CanEdit || Queue.Selected.Count == 0) return;
            var selected = new HashSet<TaskItemVm>(Queue.Selected);
            var ordered = Queue.Items.Where(selected.Contains);
            var moving = (delta > 0 ? ordered.Reverse() : ordered).ToList();
            bool moved = false;
            foreach (var item in moving)
            {
                int target = Queue.Items.IndexOf(item) + delta;
                if (target < 0 || target >= Queue.Count || selected.Contains(Queue.Items[target])) continue;
                Queue.MoveItem(item, delta);
                moved = true;
            }
            if (moved) SaveSettings();
        }

        public void RefreshQueueCollections()
        {
            Queue.Renumber();
            Queue.UpdateCounts();
            UpdateQueueStatus();
            UpdateActions();
        }

        private void UpdateQueueStatus()
        {
            UpdateReadoutIdle();
            if (IsPlanVisible && PlanPreview?.IsStale == true)
                ShowStalePlanStatus();
            else if (Queue.IsEmpty)
                FinishActivity("准备就绪", "导入后可扫描预览或开始生成", "Normal");
            else
                FinishActivity("准备就绪", "可扫描预览或开始生成", "Normal");
        }

        private sealed class ScanOneResult
        {
            public int Qc, Models;
            public bool Skip;
            public string Note = "";
            public string Hint = "";
            public List<string> Lines = new List<string>();
        }

        private static ScanOneResult ScanOne(TaskRef t, bool automatic, CancellationToken token)
        {
            var r = new ScanOneResult();
            try
            {
                if (token.IsCancellationRequested) return r;
                if (t.Kind == TaskKind.Folder)
                {
                    if (!Directory.Exists(t.Path)) { r.Skip = true; r.Note = "文件夹不存在"; return r; }
                    var qcs = ProjectScanner.FindRootQcs(t.Path, null);
                    r.Qc = qcs.Count;
                    if (qcs.Count == 0)
                    {
                        var loose = ProjectScanner.FindLooseModels(t.Path, 3, null);
                        if (loose.Count == 0) { r.Skip = true; r.Note = "没有 QC，也没有 GLB、SMD 或 DMX 模型"; return r; }
                        r.Models = loose.Count;
                        r.Lines.Add(automatic ? "没有 QC：将为 " + loose.Count + " 个模型生成网格"
                            : "没有 QC：将为 " + loose.Count + " 个模型各建一个包装 QC");
                        return r;
                    }
                    var uniq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int foreign = 0;
                    foreach (var q in qcs)
                    {
                        if (token.IsCancellationRequested) return r;
                        try
                        {
                            var info = QcParser.Parse(q);
                            if (info.HasForeignLod) foreign++;
                            foreach (var st in info.Studios)
                            {
                                if (!st.Exists || !string.IsNullOrEmpty(st.Innnode)) continue;
                                string ext = Path.GetExtension(st.ResolvedPath).ToLowerInvariant();
                                if (QcPathResolver.IsSupportedModelExtension(ext)) uniq.Add(st.ResolvedPath);
                            }
                        }
                        catch (Exception ex) { r.Lines.Add("解析失败 " + Path.GetFileName(q) + "：" + ex.Message); }
                    }
                    r.Models = uniq.Count;
                    if (foreign > 0) r.Hint = foreign + " 个 QC 已有手写 $lod";
                    if (uniq.Count == 0) { r.Skip = true; r.Note = "QC 里没有可处理的模型"; }
                }
                else if (t.Kind == TaskKind.Qc)
                {
                    if (!File.Exists(t.Path)) { r.Skip = true; r.Note = "文件不存在"; return r; }
                    r.Qc = 1;
                    var info = QcParser.Parse(t.Path);
                    r.Models = info.Studios.Count(x => QcPathResolver.IneligibleReason(x) == null);
                    if (info.Studios.Count == 0) { r.Skip = true; r.Note = "QC 里没有 studio 条目"; }
                    else if (r.Models == 0) { r.Skip = true; r.Note = "没有可处理的 GLB、SMD 或 DMX 模型"; }
                    else if (info.HasForeignLod) r.Hint = "已有手写 $lod";
                }
                else
                {
                    if (!File.Exists(t.Path)) { r.Skip = true; r.Note = "文件不存在"; return r; }
                    r.Models = 1;
                }
            }
            catch (Exception ex)
            {
                r.Skip = true;
                r.Note = "读取失败";
                r.Lines.Add(t.Path + " · " + ex.Message);
            }
            return r;
        }

        // ─────────────────────────── 运行 ───────────────────────────

        private async Task PerformOperationAsync(string errorTitle, Func<CancellationToken, Task> action)
        {
            using (var source = new CancellationTokenSource())
            {
                _cts = source;
                IsBusy = true;
                ProgressFailed = false;
                try
                {
                    await action(source.Token);
                }
                catch (OperationCanceledException) when (source.IsCancellationRequested)
                {
                    UpdateReadoutIdle();
                    FinishActivity("已取消", Queue.IsEmpty ? "可继续导入任务" : "可继续扫描或生成", "Warn");
                }
                catch (Exception ex)
                {
                    Log.Append(LogLevel.Error, errorTitle + "：" + ex);
                    FinishActivity(errorTitle, "展开日志可查看详情", "Err");
                }
                finally
                {
                    _cts = null;
                    IsStopping = false;
                    IsBusy = false;
                }
            }
        }

        private async Task RunAsync(BatchPlan plan, BatchRunOptions options, CancellationToken token)
        {
            _runStage = BatchStage.None;
            IsRunning = true;
            IsPlanVisible = false;
            PlanPreview = null;
            _previewRequest = null;
            ProgressVisible = true;
            ProgressFailed = false;
            ProgressValue = 0;
            ProgressIndeterminate = true;
            SetStatus("正在生成…", "准备减面任务", "Signal", true);
            Log.Append(LogLevel.Stage, "═══ 开始生成：" + plan.PresetName + " · " + plan.QcPlans.Count + " 个 QC · " + plan.JobCount + " 个任务 ═══");
            Queue.ResetRunState();
            var runningItems = new HashSet<string>(plan.Tasks.Select(x => x.Task.Serialize()), StringComparer.OrdinalIgnoreCase);
            foreach (var t in Queue.Items)
                if (runningItems.Contains(t.ToRef().Serialize()))
                    t.Status = TaskStatus.Running;
            Queue.RefreshView();
            _pump.Start();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            BatchOutcome outcome = null;
            try
            {
                var observer = new UiObserver(this);
                outcome = await Task.Run(() => BatchRunner.Run(plan, options, observer, token), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log.Append(LogLevel.Error, "运行失败：" + ex.Message);
                outcome = new BatchOutcome { Ok = false, Errors = { ex.GetType().Name + ": " + ex.Message }, Cancelled = token.IsCancellationRequested };
            }
            finally
            {
                sw.Stop();
                _pump.Stop();
                while (!_logQueue.IsEmpty)
                {
                    DrainLog();
                    if (!_logQueue.IsEmpty) await Dispatcher.Yield(DispatcherPriority.Background);
                }
                lock (_progressGate)
                {
                    _pendingDone = _pendingTotal = -1;
                    _pendingStage = BatchStage.None;
                }
                IsRunning = false;
            }
            await FinishRunAsync(plan, options, outcome, sw.Elapsed, outcome?.Cancelled ?? token.IsCancellationRequested);
        }

        /// <summary>关窗时强制取消（不弹确认）</summary>
        public void ForceCancel() => RequestStopInternal();

        private void RequestStopInternal()
        {
            if (_cts == null || IsStopping) return;
            IsStopping = true;
            _cts.Cancel();
            SetStatus("正在停止", IsRunning ? "等待当前步骤收尾" : "等待当前读取步骤结束", "Warn", true);
            Log.Append(LogLevel.Warn, "用户请求停止");
        }

        public async Task StopAsync()
        {
            if (_cts == null || IsStopping) return;
            if (!IsRunning) { RequestStopInternal(); return; }
            var source = _cts;
            bool ok = await _ui.ConfirmAsync("停止处理",
                "程序将在当前步骤收尾后停止。已完成的写入和发布结果会保留。",
                "停止", "继续处理", true, null);
            if (ok && IsRunning && ReferenceEquals(_cts, source)) RequestStopInternal();
        }

        private async Task FinishRunAsync(BatchPlan plan, BatchRunOptions options, BatchOutcome outcome, TimeSpan elapsed, bool cancelled)
        {
            var stats = outcome != null ? outcome.Stats : new BatchStats();
            var ocByQc = new Dictionary<string, QcOutcome>(StringComparer.OrdinalIgnoreCase);
            if (outcome != null) foreach (var q in outcome.QcOutcomes) ocByQc[FileUtil.Normalize(q.QcPath)] = q;
            var injected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (outcome != null) foreach (var p in outcome.InjectedQcPaths) injected.Add(FileUtil.Normalize(p));
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (outcome != null) foreach (var p in outcome.SkippedQcPaths) skipped.Add(FileUtil.Normalize(p));

            int okCount = 0, partialCount = 0, failedCount = 0, skipCount = 0, cancelledCount = 0;
            bool collection = options.OutputMode == OutputMode.CollectionOnly;
            var items = Queue.Items.ToDictionary(t => t.ToRef().Serialize(), StringComparer.OrdinalIgnoreCase);
            foreach (var exp in plan.Tasks)
            {
                if (!items.TryGetValue(exp.Task.Serialize(), out var item)) continue;
                int total = exp.QcPaths.Count;
                int done = 0, esc = 0, errCount = 0;
                bool writesModels = false;
                var errs = new List<string>();
                var reasons = new List<string>();
                foreach (var q in exp.QcPaths)
                {
                    string key = FileUtil.Normalize(q);
                    QcOutcome qo;
                    if (ocByQc.TryGetValue(key, out qo))
                    {
                        // 以 QC 结果为唯一事实来源（否则 injected 集合与 QcOutcome 会重复计数，出现"已写入 2/1 个 QC"）
                        if (qo.Errors.Count > 0) errCount += qo.Errors.Count;
                        foreach (var e in qo.Errors) errs.Add(Path.GetFileName(q) + "：" + e);
                        writesModels |= qo.Standalone;
                        if (qo.Injected || qo.Generated || (qo.Skipped && !string.IsNullOrEmpty(qo.LodBlockFile))) done++;
                        else if (qo.Skipped)
                        {
                            esc++;
                            if (!string.IsNullOrEmpty(qo.SkipReason)) reasons.Add(Path.GetFileName(q) + " · " + qo.SkipReason);
                        }
                    }
                    else if (injected.Contains(key)) done++;
                    else if (skipped.Contains(key)) esc++;
                }
                if (!string.IsNullOrEmpty(exp.Error)) { errs.Add(exp.Error); errCount++; }
                if (errs.Count == 0 && outcome != null && done + esc < total && outcome.Errors.Count > 0)
                {
                    foreach (var e in outcome.Errors.Take(3)) errs.Add(e);
                }

                TaskStatus state;
                if (cancelled && done + esc < total) state = TaskStatus.Cancelled;
                else if (total == 0) state = TaskStatus.Failed;
                else if (done == 0 && esc >= total && errCount == 0) state = TaskStatus.Skipped;
                else if (done == 0) state = TaskStatus.Failed;
                else if (errCount > 0 || done + esc < total) state = TaskStatus.Partial;
                else state = TaskStatus.Ok;

                item.Status = state;
                item.Skip = state == TaskStatus.Skipped;
                item.SkipReason = string.Join("\n", reasons.Take(20));
                string verb = writesModels ? "已生成" : collection ? "已汇总" : "已处理";
                if (total == 0)
                    item.RunDetail = "未找到可处理内容";
                else if (state == TaskStatus.Skipped)
                    item.RunDetail = reasons.Count == 1 && total == 1 ? reasons[0] : "跳过 " + esc + " 项";
                else
                    item.RunDetail = verb + " " + done + " / " + total + " 项" + (esc > 0 ? " · 跳过 " + esc : "");
                item.RunErrors = errs.Count > 0 ? string.Join("\n", errs.Take(20).ToArray()) : "";
                if (state == TaskStatus.Ok) okCount++;
                else if (state == TaskStatus.Partial) partialCount++;
                else if (state == TaskStatus.Failed) failedCount++;
                else if (state == TaskStatus.Skipped) skipCount++;
                else if (state == TaskStatus.Cancelled) cancelledCount++;
            }
            // 未被计划覆盖的项恢复为待处理
            foreach (var t in Queue.Items)
                if (t.Status == TaskStatus.Running) t.Status = TaskStatus.Pending;

            Queue.UpdateCounts();
            Queue.RefreshView();

            _lastRunAt = DateTime.Now;
            _lastOk = okCount;
            _lastPartial = partialCount;
            _lastFailed = failedCount;
            _lastReportPath = outcome != null ? outcome.ReportPath : null;
            _lastOutputDir = ResolveOutputDir(plan, options, outcome);
            Raise(nameof(HasLastRun)); Raise(nameof(LastRunText)); Raise(nameof(CanOpenLastReport)); Raise(nameof(CanOpenOutputFolder));

            int generatedModels = outcome?.GeneratedModels ?? 0;
            int cleaned = outcome?.QcOutcomes.Count(q => q.Skipped && !string.IsNullOrEmpty(q.LodBlockFile)) ?? 0;
            string summary = BuildSummaryLine(stats, elapsed, skipCount, generatedModels, cleaned, plan.Automatic, collection);
            ProgressIndeterminate = false;
            ProgressFailed = !cancelled && (failedCount > 0 || partialCount > 0 || outcome?.Ok != true);
            ProgressVisible = !cancelled;
            ProgressValue = 1;
            SetReadout(okCount, "项完成");
            string title;
            string tone;
            if (cancelled)
            {
                title = "已停止";
                tone = "Warn";
                summary = cancelledCount + " 项未完成 · " + summary;
            }
            else if (ProgressFailed)
            {
                title = failedCount > 0 ? failedCount + " 项失败" : partialCount > 0 ? partialCount + " 项部分完成" : "生成遇到错误";
                tone = "Err";
            }
            else
            {
                title = okCount == 0 && skipCount > 0 ? "没有发布新结果"
                    : cleaned > 0 && plan.JobCount == 0 ? "清理完成" : "生成完成";
                tone = okCount == 0 && skipCount > 0 ? "Warn" : "Ok";
            }

            if (outcome != null)
            {
                foreach (var e in outcome.Errors.Take(30)) Log.Append(LogLevel.Error, e);
                foreach (var qc in outcome.QcOutcomes.Where(q => q.Errors.Count > 0).Take(30))
                    Log.Append(LogLevel.Error, Path.GetFileName(qc.QcPath) + " · " + string.Join(" · ", qc.Errors.Take(3)));
                if (!string.IsNullOrEmpty(outcome.ReportPath)) Log.Append(LogLevel.Info, "总报告：" + outcome.ReportPath);
            }
            SetStatus(title, summary, tone, false);
            Log.Append(LogLevel.Stage, "════ " + title + " · " + summary + " ════");
            SaveSettings();

            _ui.FlashWindow();
            if (tone == "Ok" && Settings.PopupOnSuccess)
                await _ui.ShowInfoAsync(title, summary, CanOpenLastReport ? _lastReportPath : null);
        }

        private string BuildSummaryLine(BatchStats stats, TimeSpan elapsed, int skipTasks, int generatedModels, int cleaned,
            bool automatic, bool collection)
        {
            var parts = new List<string>();
            if (stats.InjectedQcCount > 0) parts.Add((collection ? "汇总 " : "写入 ") + stats.InjectedQcCount + " 个 QC");
            if (generatedModels > 0) parts.Add("生成 " + generatedModels + " 个模型的 LOD");
            if (stats.PublishedLods > 0) parts.Add(stats.PublishedLods + " 个 LOD 网格");
            if (cleaned > 0) parts.Add("清理 " + cleaned + " 个旧工具块");
            if (stats.CompileOk + stats.CompileFail > 0) parts.Add("试编译 " + stats.CompileOk + "/" + (stats.CompileOk + stats.CompileFail));
            if (!automatic && stats.TotalSourceTriangles > 0)
                parts.Add("原始 " + stats.TotalSourceTriangles.ToString("N0", CultureInfo.InvariantCulture) + " 面 → 末级 " +
                          stats.TotalLastLodTriangles.ToString("N0", CultureInfo.InvariantCulture) + " 面");
            if (skipTasks > 0) parts.Add("跳过 " + skipTasks + " 项");
            parts.Add("用时 " + FormatSpan(elapsed));
            return string.Join(" · ", parts.ToArray());
        }

        /// <summary>计划里第一个 QC 的目录（报告/日志落点用）。</summary>
        private static string FirstQcDir(BatchPlan plan)
        {
            if (plan != null)
            {
                foreach (var q in plan.QcPlans)
                {
                    try
                    {
                        string d = Path.GetDirectoryName(q.QcPath);
                        if (!string.IsNullOrEmpty(d)) return d;
                    }
                    catch { }
                }
                if (!string.IsNullOrEmpty(plan.RootDir)) return plan.RootDir;
            }
            return AppInfo.ExeDir;
        }

        private static string ResolveOutputDir(BatchPlan plan, BatchRunOptions options, BatchOutcome outcome)
        {
            if (outcome == null) return null;
            if (options.OutputMode == OutputMode.CollectionOnly && outcome.Stats.InjectedQcCount > 0)
                return plan.RootDir;
            foreach (var qc in outcome.QcOutcomes)
            {
                string file = qc.LodBlockFile ?? qc.OutputFiles.FirstOrDefault();
                if (!string.IsNullOrEmpty(file)) return Path.GetDirectoryName(file);
            }
            return null;
        }

        // ─────────────────────────── 报告 / 细节 / 诊断 ───────────────────────────

        public void OpenLastReport()
        {
            if (CanOpenLastReport) _ui.OpenFile(_lastReportPath);
            else _ui.ShowToast("还没有报告", "先生成一次，完成后面板会出现「打开报告」", ToastTone.Warn);
        }

        public void OpenOutputFolder()
        {
            if (CanOpenOutputFolder) _ui.RevealInExplorer(_lastOutputDir);
            else _ui.ShowToast("还没有输出目录", "先导入任务并运行一次", ToastTone.Warn);
        }

        public async Task ShowTaskDetailsAsync(TaskItemVm item)
        {
            if (item == null || _loadingTaskDetails) return;
            _loadingTaskDetails = true;
            try
            {
                var task = item.ToRef();
                string heading = item.KindText + " · " + item.Name;
                var details = new List<string>();
                if (item.RunDetail.Length > 0) details.Add(item.StatusText + " · " + item.RunDetail);
                if (item.RunErrors.Length > 0) details.Add(item.RunErrors);
                if (item.SkipReason.Length > 0 && item.RunDetail != item.SkipReason) details.Add(item.SkipReason);
                var options = BuildRunOptions(null);
                string body = await Task.Run(() => BuildTaskDetails(task, options, details));
                bool reveal = await _ui.ConfirmAsync(heading, task.Path,
                    "在资源管理器中显示", "关闭", false, body);
                if (reveal) _ui.RevealInExplorer(task.Path);
            }
            finally { _loadingTaskDetails = false; }
        }

        private static string BuildTaskDetails(TaskRef t, BatchRunOptions options, List<string> details)
        {
            var sb = new StringBuilder();
            if (details.Count > 0)
            {
                foreach (string line in details) sb.AppendLine(line);
                sb.AppendLine();
            }
            try
            {
                List<string> qcs;
                if (t.Kind == TaskKind.Folder)
                {
                    if (!Directory.Exists(t.Path)) return sb.AppendLine("文件夹不存在。").ToString();
                    qcs = ProjectScanner.FindRootQcs(t.Path, null);
                    if (qcs.Count == 0)
                    {
                        var loose = ProjectScanner.FindLooseModels(t.Path, 3, null);
                        sb.AppendLine(options.Automatic ? "没有 QC。可单独生成下列模型的 LOD 网格。" : "没有 QC。下列模型会各自使用包装 QC。");
                        foreach (var x in loose.Take(60)) sb.AppendLine("  " + x);
                        return sb.ToString();
                    }
                }
                else if (t.Kind == TaskKind.Qc) qcs = new List<string> { t.Path };
                else
                {
                    if (options.Automatic) return sb.AppendLine("自动模式将为这个模型估算并生成 LOD 网格。").ToString();
                    string wrapper = ProjectScanner.WrapperQcPath(t.Path);
                    sb.AppendLine(File.Exists(wrapper) ? "会沿用已有的包装 QC：" + wrapper : "运行时会建立包装 QC：" + wrapper);
                    return sb.ToString();
                }

                sb.AppendLine("共 " + qcs.Count + " 个 QC：");
                foreach (var q in qcs.Take(40))
                {
                    sb.AppendLine();
                    sb.AppendLine("■ " + q);
                    try
                    {
                        var info = QcParser.Parse(q);
                        if (info.HasForeignLod)
                            sb.AppendLine(options.Force ? "  已有手写 $lod，生成时会注释停用" : "  已有手写 $lod，处理方式见扫描预览");
                        if (info.HasToolMarker)
                        {
                            // 模型声明在 include 文件里时，工具块写在那个文件，根 QC 不动
                            string markerHost = "";
                            try
                            {
                                string h = QcWriter.ResolveHostFile(info, false);
                                if (!string.IsNullOrEmpty(h) && !string.Equals(h, q, StringComparison.OrdinalIgnoreCase))
                                    markerHost = Path.GetFileName(h);
                            }
                            catch { }
                            if (markerHost.Length > 0)
                                sb.AppendLine("  已有本工具写入的 $lod 块 → 整块替换（块写入 " + markerHost + "，根 QC 不改动）");
                            else
                                sb.AppendLine("  已有本工具写入的 $lod 块 → 整块替换");
                        }
                        foreach (var st in info.Studios)
                        {
                            string state;
                            if (!st.Exists) state = "✘ 文件不存在";
                            else
                            {
                                string ext = Path.GetExtension(st.ResolvedPath).ToLowerInvariant();
                                if (!QcPathResolver.IsSupportedModelExtension(ext)) state = "· 格式暂未支持";
                                else if (!string.IsNullOrEmpty(st.Innnode)) state = "· 带 innode，跳过";
                                else state = "✓ 生成 LOD";
                            }
                            sb.AppendLine("  " + state + "　" + st.RawFile);
                        }
                    }
                    catch (Exception ex) { sb.AppendLine("  解析失败：" + ex.Message); }
                }
                if (qcs.Count > 40) sb.AppendLine("…（只列出前 40 个）");
            }
            catch (Exception ex) { sb.AppendLine("读取失败：" + ex.Message); }
            return sb.ToString();
        }

        public async Task RunDiagnosticsAsync()
        {
            SetStatus("正在运行环境诊断…", "检查权限 / 目录 / 引擎 / 设置", "Signal", true);
            string text;
            try
            {
                var probeDirs = new List<string>();
                foreach (var t in Queue.Items.Take(8))
                {
                    try
                    {
                        string d = t.Kind == TaskKind.Folder ? t.Path : Path.GetDirectoryName(FileUtil.Normalize(t.Path));
                        if (!string.IsNullOrEmpty(d) && !probeDirs.Contains(d, StringComparer.OrdinalIgnoreCase)) probeDirs.Add(d);
                    }
                    catch { }
                }
                text = await Task.Run(() => EnvironmentDiagnostics.Build(Settings, probeDirs,
                    RuntimeContext.IsAdmin, RuntimeContext.IsLowIntegrity()));
            }
            catch (Exception ex) { text = "诊断失败：" + ex; }
            await _ui.ShowInfoAsync("环境诊断", "把下面的内容复制给开发者可以更快定位问题。", text);
            SetStatus("准备就绪", "环境诊断完成", "Normal", false);
        }

        // ─────────────────────────── 给设置窗口的协作接口 ───────────────────────────

        public void Toast(string title, string sub, Controls.ToastTone tone) { _ui.ShowToast(title, sub, tone); }
        public Task ShowInfo(string title, string message, string detail) { return _ui.ShowInfoAsync(title, message, detail); }
        public Task<bool> Confirm(string title, string message, string okText, string cancelText, bool danger, string detail)
        { return _ui.ConfirmAsync(title, message, okText, cancelText, danger, detail); }
        public Task<string> PickFile(string title, string filter, string initialDir) { return _ui.PickFileAsync(title, filter, initialDir); }
        public Task<string> PickFolder() { return _ui.PickFolderAsync(); }
        public void OpenSettingsFolder() { _ui.RevealInExplorer(AppInfo.SettingsDir); }
        public void SetAnimations(bool on) { Settings.Animations = on; SaveSettings(); }

        /// <summary>收集当前队列里能找到的根 QC（"跳过主模型"清单用）。</summary>
        public Task<List<string>> CollectQcCandidatesAsync()
        {
            var tasks = Queue.Items.Select(x => x.ToRef()).ToList();
            var existing = Settings.SkipMainQcs.ToList();
            return Task.Run(() =>
            {
                var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
                var list = new List<string>(existing);
                foreach (var t in tasks)
                {
                    try
                    {
                        if (t.Kind == TaskKind.Folder)
                        {
                            foreach (var q in ProjectScanner.FindRootQcs(t.Path, null))
                                if (set.Add(q)) list.Add(q);
                        }
                        else if (t.Kind == TaskKind.Qc)
                        {
                            string full = FileUtil.Normalize(t.Path);
                            if (set.Add(full)) list.Add(full);
                        }
                    }
                    catch { }
                }
                list.Sort(StringComparer.OrdinalIgnoreCase);
                return list;
            });
        }
        // ─────────────────────────── 主题 ───────────────────────────

        public void SetThemeMode(int mode)
        {
            Settings.ThemeMode = Math.Max(0, Math.Min(2, mode));
            bool dark = Settings.ThemeMode == 1 || (Settings.ThemeMode == 2 && Engine.SystemTheme.PrefersDark());
            ThemeService.Apply(dark ? ThemeKind.Dark : ThemeKind.Light);
            SaveSettings();
            Raise("ThemeMenuIndex");
        }

        public void ToggleTheme()
        {
            SetThemeMode(ThemeService.Current == ThemeKind.Light ? 1 : 0);
        }

        // ─────────────────────────── 预设 / 级别 ───────────────────────────

        public void RestorePreset()
        {
            if (!CanEdit) return;
            LodPlan.RestorePreset();
        }

        public void AddLevel()
        {
            if (!CanEdit) return;
            LodPlan.AddLevel();
        }

        public void RemoveLevel(int index)
        {
            if (!CanEdit) return;
            LodPlan.RemoveLevel(index);
        }

        public void OnWriteModeChanged(int mode)
        {
            if (!CanEdit) return;
            LodPlan.WriteMode = mode;
            SaveSettings();
        }

        // ─────────────────────────── 日志与状态 ───────────────────────────

        internal void PostLog(LogLevel level, string text)
        {
            if (text == null) return;
            _logQueue.Enqueue(new KeyValuePair<LogLevel, string>(level, text));
        }

        internal void PostProgress(int done, int total, TimeSpan elapsed)
        {
            lock (_progressGate)
            {
                _pendingDone = done;
                _pendingTotal = total;
                _pendingElapsed = elapsed;
            }
        }

        private void PostStage(BatchStage stage)
        {
            lock (_progressGate) _pendingStage = stage;
        }

        private void OnPump(object sender, EventArgs e)
        {
            DrainLog();
            int done, total;
            TimeSpan elapsed;
            BatchStage stage;
            lock (_progressGate)
            {
                done = _pendingDone;
                total = _pendingTotal;
                elapsed = _pendingElapsed;
                stage = _pendingStage;
                _pendingDone = _pendingTotal = -1;
                _pendingStage = BatchStage.None;
            }
            if (IsStopping) return;
            if (stage != BatchStage.None)
            {
                _runStage = stage;
                switch (stage)
                {
                    case BatchStage.Planning: SetStatus("正在准备生成", "创建所需的包装 QC", "Signal", true); break;
                    case BatchStage.Reducing: SetStatus("正在减面", "等待减面任务完成", "Signal", true); break;
                    case BatchStage.ConvertingSmd: SetStatus("正在整理网格", "检查实际面数和可用档位", "Signal", true); break;
                    case BatchStage.Injecting: SetStatus("正在发布结果", "保存有效档位并写入所选位置", "Signal", true); break;
                    case BatchStage.Compiling: SetStatus("正在试编译", "使用已配置的编译器检查 QC", "Signal", true); break;
                    case BatchStage.Reporting: SetStatus("正在保存报告", "汇总实际档位和错误记录", "Signal", true); break;
                }
            }
            if (_runStage == BatchStage.Reducing && total > 0 && done >= 0)
            {
                ProgressIndeterminate = false;
                ProgressValue = done / (double)total;
                SetStatus("正在减面 " + done + " / " + total, "已用 " + FormatSpan(elapsed) + EstimateEta(done, total, elapsed), "Signal", false);
                SetReadout(done, "个减面任务");
            }
        }

        private void DrainLog()
        {
            int n = 0;
            KeyValuePair<LogLevel, string> kv;
            while (n < 300 && _logQueue.TryDequeue(out kv))
            {
                Log.Append(kv.Key, kv.Value);
                n++;
            }
        }

        private static string EstimateEta(int done, int total, TimeSpan elapsed)
        {
            if (done < 2 || done >= total) return "";
            double sec = elapsed.TotalSeconds / done * (total - done);
            if (sec < 1) return "";
            return sec < 90 ? " · 剩余约 " + Math.Max(1, (int)sec) + " 秒"
                            : " · 剩余约 " + (int)Math.Ceiling(sec / 60) + " 分钟";
        }

        private void SetStatus(string title, string sub, string tone, bool progressIndeterminate)
        {
            StatusTitle = title;
            StatusSub = sub;
            StatusTone = tone;
            if (progressIndeterminate)
            {
                ProgressVisible = true;
                ProgressIndeterminate = true;
            }
        }

        private void FinishActivity(string title, string sub, string tone)
        {
            ProgressVisible = false;
            ProgressIndeterminate = false;
            ProgressFailed = false;
            SetStatus(title, sub, tone, false);
        }

        private void SetReadout(int value, string sub)
        {
            ReadoutMainText = value.ToString(CultureInfo.InvariantCulture);
            ReadoutSub = sub;
        }

        public static string FormatSpan(TimeSpan t)
        {
            if (t.TotalHours >= 1) return (int)t.TotalHours + " 小时 " + t.Minutes + " 分";
            if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + " 分 " + t.Seconds + " 秒";
            return Math.Max(0, (int)t.TotalSeconds) + " 秒";
        }

        private static string DescribeRelative(DateTime at)
        {
            var span = DateTime.Now - at;
            if (span.TotalSeconds < 60) return "刚刚";
            if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " 分钟前";
            if (span.TotalHours < 24) return (int)span.TotalHours + " 小时前";
            return at.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private static TaskKind GuessKind(IEnumerable<string> paths)
        {
            var first = (paths ?? Enumerable.Empty<string>()).FirstOrDefault();
            if (first == null) return TaskKind.Folder;
            try
            {
                if (Directory.Exists(first)) return TaskKind.Folder;
                string ext = Path.GetExtension(first).ToLowerInvariant();
                if (ext == ".qc") return TaskKind.Qc;
                if (ext == ".smd") return TaskKind.Smd;
                if (ext == ".dmx") return TaskKind.Dmx;
            }
            catch { }
            return TaskKind.Glb;
        }

        // ─────────────────────────── 命令表 ───────────────────────────

        public sealed class ShellCommands
        {
            private readonly ShellViewModel _vm;
            public ShellCommands(ShellViewModel vm)
            {
                _vm = vm;
                AddFolder = new RelayCommand(async () => await _vm.AddFolderAsync());
                AddFiles = new RelayCommand(async () => await _vm.AddFilesAsync());
                Remove = new RelayCommand(() => _vm.RemoveSelected());
                Clear = new RelayCommand(async () => await _vm.ClearQueueAsync());
                MoveUp = new RelayCommand(() => _vm.MoveSelected(-1));
                MoveDown = new RelayCommand(() => _vm.MoveSelected(1));
                Preview = new RelayCommand(async () => await _vm.PreviewPlanAsync());
                Start = new RelayCommand(async () => await _vm.StartRunAsync());
                Stop = new RelayCommand(async () => await _vm.StopAsync());
                OpenReport = new RelayCommand(() => _vm.OpenLastReport());
                OpenFolder = new RelayCommand(() => _vm.OpenOutputFolder());
                Settings = new RelayCommand(() => _vm._ui.OpenSettings(0));
                Diagnostics = new RelayCommand(async () => await _vm.RunDiagnosticsAsync());
                RestorePreset = new RelayCommand(() => _vm.RestorePreset());
                AddLevel = new RelayCommand(() => _vm.AddLevel());
                ToggleTheme = new RelayCommand(() => _vm.ToggleTheme());
                ToggleLog = new RelayCommand(() => _vm.IsLogExpanded = !_vm.IsLogExpanded);
                CopyLog = new RelayCommand(() => _vm.CopyLog());
                ClearLog = new RelayCommand(() => _vm.Log.Clear());
                ResumeFollow = new RelayCommand(() => { _vm.Log.AutoFollow = true; _vm.Raise("LogFollowChanged"); });
                Help = new RelayCommand(async () => await _vm.ShowShortcutHelpAsync());
                About = new RelayCommand(async () => await _vm.ShowAboutAsync());
                OpenManual = new RelayCommand(() => _vm.OpenManual());
            }

            public RelayCommand AddFolder { get; private set; }
            public RelayCommand AddFiles { get; private set; }
            public RelayCommand Remove { get; private set; }
            public RelayCommand Clear { get; private set; }
            public RelayCommand MoveUp { get; private set; }
            public RelayCommand MoveDown { get; private set; }
            public RelayCommand Preview { get; private set; }
            public RelayCommand Start { get; private set; }
            public RelayCommand Stop { get; private set; }
            public RelayCommand OpenReport { get; private set; }
            public RelayCommand OpenFolder { get; private set; }
            public RelayCommand Settings { get; private set; }
            public RelayCommand Diagnostics { get; private set; }
            public RelayCommand RestorePreset { get; private set; }
            public RelayCommand AddLevel { get; private set; }
            public RelayCommand ToggleTheme { get; private set; }
            public RelayCommand ToggleLog { get; private set; }
            public RelayCommand CopyLog { get; private set; }
            public RelayCommand ClearLog { get; private set; }
            public RelayCommand ResumeFollow { get; private set; }
            public RelayCommand Help { get; private set; }
            public RelayCommand About { get; private set; }
            public RelayCommand OpenManual { get; private set; }
        }

        // ─────────────────────────── 小动作 ───────────────────────────

        private async Task AddFolderAsync()
        {
            if (!CanEdit) return;
            string dir = await _ui.PickFolderAsync();
            if (!string.IsNullOrEmpty(dir)) await AddPathsAsync(new[] { dir });
        }

        private async Task AddFilesAsync()
        {
            if (!CanEdit) return;
            var files = await _ui.PickFilesAsync();
            if (files != null && files.Count > 0) await AddPathsAsync(files);
        }

        private void CopyLog()
        {
            try
            {
                string text = Log.BuildCopyText();
                if (string.IsNullOrEmpty(text)) { _ui.ShowToast("没有可复制的日志", "", ToastTone.Warn); return; }
                Clipboard.SetText(text);
                _ui.ShowToast("已复制日志", text.Split('\n').Length + " 行", ToastTone.Ok);
            }
            catch (Exception ex) { _ui.ShowToast("复制失败", ex.Message, ToastTone.Err); }
        }

        public async Task ShowShortcutHelpAsync()
        {
            const string text =
                "F5            开始生成或按当前预览生成\n" +
                "Esc           关闭对话框、返回队列、取消当前操作或清筛选\n" +
                "Ctrl+O        导入文件（QC / GLB / SMD / DMX）\n" +
                "Ctrl+Shift+O  导入工程文件夹\n" +
                "Ctrl+F        搜索任务（Esc 清空）\n" +
                "Ctrl+L        展开 / 折叠运行日志\n" +
                "Ctrl+,        打开设置\n" +
                "Ctrl+A        全选任务\n" +
                "Delete        移除选中任务\n" +
                "Ctrl+↑ / ↓    调整任务顺序\n" +
                "Enter         查看选中的队列或计划项\n" +
                "↑↓ / Home / End / PgUp / PgDn   在任务列表里移动\n" +
                "F1            本快捷键列表";
            await _ui.ShowInfoAsync("快捷键", "这些键在主窗口里随时可用。", text);
        }

        public async Task ShowAboutAsync()
        {
            string text = AppInfo.TitleText + "\n" +
                          "给 L4D2 模型工程自动生成多级 LOD（内置 meshoptimizer 减面 + $lod 写回 QC）。\n\n" +
                          "版本：" + AppInfo.FileVersionText + "（" + AppInfo.Bitness + "）\n" +
                          "运行时：.NET Framework " + Environment.Version + "\n" +
                          "设置文件：" + AppInfo.SettingsFile + "\n" +
                          "临时目录：" + AppInfo.TempRoot + "\n" +
                          "编译器：" + CompilerText;
            await _ui.ShowInfoAsync("关于 远景工坊", AppInfo.Subtitle, text);
        }

        private void OpenManual()
        {
            string[] cands =
            {
                Path.Combine(AppInfo.ExeDir, "README.md"),
                Path.Combine(AppInfo.ExeDir, "..", "README.md")
            };
            foreach (var c in cands)
            {
                try
                {
                    string full = Path.GetFullPath(c);
                    if (File.Exists(full)) { _ui.OpenFile(full); return; }
                }
                catch { }
            }
            _ui.ShowToast("没有找到使用说明", "部署目录里应带上 README.md", ToastTone.Warn);
        }

        /// <summary>LOD 参数在运行前的额外校验（含减面后端等）</summary>
        public void RaiseReady() { UpdateReadiness(); Raise("ShowStartButton"); Raise("ShowStopButton"); }

        // ─────────────────────────── 引擎观察者 ───────────────────────────

        private sealed class UiObserver : IBatchObserver
        {
            private readonly ShellViewModel _vm;
            public UiObserver(ShellViewModel vm) { _vm = vm; }
            public void Log(LogLevel level, string text) { _vm.PostLog(level, text); }
            public void Progress(int done, int total, TimeSpan elapsed) { _vm.PostProgress(done, total, elapsed); }
            public void Stage(BatchStage stage, string detail)
            {
                if (detail != null) _vm.PostLog(LogLevel.Stage, detail);
                if (stage != BatchStage.Done) _vm.PostStage(stage);
            }
        }
    }
}
