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

        private bool _isRunning;
        private bool _isBusy;
        private bool _isPlanVisible;
        private CancellationTokenSource _cts;
        private CancellationTokenSource _scanCts;

        private string _statusTitle = "准备就绪";
        private string _statusSub = "把工程文件夹、QC 或模型拖进窗口，或点「导入」";
        private string _statusTone = "Normal";
        private double _readoutMain;
        private string _readoutMainText = "0";
        private string _readoutSub = "项就绪";
        private bool _progressVisible;
        private double _progressValue;
        private bool _progressIndeterminate;
        private bool _progressFailed;

        private string _blenderText = "检测中";
        private string _blenderTone = "Faint";
        private string _blenderVersion = "";
        private string _compilerText = "未设置";
        private string _compilerTone = "Faint";
        private string _permissionText = "…";
        private string _permissionTone = "Faint";
        private string _envText = "环境检测中";
        private string _envTone = "Faint";
        private string _envTip = "正在检测 Blender / 编译器 / 权限…";

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
            LodPlan.EditRequested += delegate (object s, int idx) { _ui.FocusLodLevel(idx); };

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
            private set { if (Set(ref _planPreview, value)) { Raise("IsPlanVisible"); } }
        }

        public bool IsPlanVisible
        {
            get { return _isPlanVisible; }
            private set { if (Set(ref _isPlanVisible, value)) { Raise("ShowStartButton"); Raise("CanPreview"); } }
        }

        public bool IsRunning
        {
            get { return _isRunning; }
            private set
            {
                if (Set(ref _isRunning, value))
                {
                    Raise("ShowStartButton"); Raise("ShowStopButton"); Raise("CanPreview"); Raise("CanStart");
                    Queue.Locked = value;
                    LodPlan.IsLocked = value;
                    Raise("LodEnabled");
                }
            }
        }

        public bool IsBusy { get { return _isBusy; } private set { if (Set(ref _isBusy, value)) { Raise("ShowStartButton"); } } }
        public bool LodEnabled { get { return !IsRunning; } }

        public string StatusTitle { get { return _statusTitle; } private set { Set(ref _statusTitle, value); } }
        public string StatusSub { get { return _statusSub; } private set { Set(ref _statusSub, value); } }
        public string StatusTone { get { return _statusTone; } private set { Set(ref _statusTone, value); } }
        public double ReadoutMain { get { return _readoutMain; } private set { Set(ref _readoutMain, value); } }
        public string ReadoutMainText { get { return _readoutMainText; } private set { Set(ref _readoutMainText, value); } }
        public string ReadoutSub { get { return _readoutSub; } private set { Set(ref _readoutSub, value); } }

        public bool ProgressVisible { get { return _progressVisible; } private set { Set(ref _progressVisible, value); } }
        public double ProgressValue { get { return _progressValue; } private set { Set(ref _progressValue, value); } }
        public bool ProgressIndeterminate { get { return _progressIndeterminate; } private set { Set(ref _progressIndeterminate, value); } }
        public bool ProgressFailed { get { return _progressFailed; } private set { Set(ref _progressFailed, value); } }

        public string BlenderText { get { return _blenderText; } private set { Set(ref _blenderText, value); } }
        public string BlenderTone { get { return _blenderTone; } private set { Set(ref _blenderTone, value); } }
        public string BlenderTip
        {
            get
            {
                if (!string.IsNullOrEmpty(_blenderVersion)) return "Blender " + _blenderVersion + "（只读状态；要改成 菜单 → 打开设置 → 引擎）";
                if (Settings != null && Settings.ReduceBackend == (int)ReduceBackend.Gltfpack)
                    return "减面后端：内置 gltfpack（无需 Blender）。Blender 未安装，仅作回退时可不装（只读状态）";
                return "未找到 Blender。菜单 → 打开设置 → 引擎页指定 blender.exe（只读状态）";
            }
        }
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

        /// <summary>顶栏只显示一个环境状态：绿点=全部正常，红点=任意一项异常（Blender 不可用 / 低完整性 / 编译器已配置但不可用）。</summary>
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

        /// <summary>「开始生成」的悬停提示：参数不合法时说明原因（红字提示已按反馈移除）。</summary>
        public string StartHint
        {
            get
            {
                if (LodPlan != null && !LodPlan.IsValid && !string.IsNullOrEmpty(LodPlan.ValidationError))
                    return LodPlan.ValidationError;
                if (Queue != null && Queue.Items.Count == 0) return "先导入工程文件夹或 QC / 模型文件";
                return "开始生成（F5）";
            }
        }

        public bool CanStart
        {
            get
            {
                if (IsRunning || IsBusy) return false;
                if (Queue.Items.Count == 0) return false;
                if (!LodPlan.IsValid) return false;
                return HasReadyTasks;
            }
        }

        public bool CanPreview
        {
            get { return !IsRunning && !IsBusy && Queue.Items.Count > 0 && LodPlan.IsValid && !IsPlanVisible; }
        }

        public bool ShowStartButton { get { return !IsRunning; } }
        public bool ShowStopButton { get { return IsRunning; } }

        public bool HasReadyTasks
        {
            get
            {
                foreach (var t in Queue.Items) if (!t.Skip) return true;
                return false;
            }
        }

        public string ImportSummary { get { return Queue.SummaryText; } }
        public string ImportHint { get { return HasLastRun ? LastRunText : "导入工程文件夹 / QC / 模型（GLB、SMD）"; } }

        // ─────────────────────────── 设置装载 ───────────────────────────

        private void LoadFromSettings()
        {
            var plan = Settings.GetPlan();
            LodPlan.LoadFrom(plan, Settings.PresetName);
            LodPlan.WriteMode = Settings.OutputMode;
            LodPlan.SetUserPresets(Settings.UserPresets);
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
            UpdateReadoutIdle();
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
                Settings.PresetName = LodPlan.IsModified ? "" : (LodPlan.SelectedPreset != null ? LodPlan.SelectedPreset.Name : "");
                Settings.OutputMode = LodPlan.WriteMode;
                Settings.LogExpanded = IsLogExpanded;
                Settings.Tasks = Queue.Items.Select(x => x.ToRef()).ToList();
                Settings.UserPresets = LodPlan.Presets.Where(p => p.IsUser).Select(p => p.Clone()).ToList();
                SettingsStore.Save(Settings);
            }
            catch { }
        }

        public void RefreshAdvancedSummary()
        {
            var parts = new List<string>();
            parts.Add(Settings.QualityFirst ? "质量优先" : "速度优先");
            parts.Add("并行：" + (Settings.Workers <= 0 ? "自动" : Settings.Workers.ToString(CultureInfo.InvariantCulture)));
            if (Settings.ForceOverwriteLod) parts.Add("覆盖手写");
            if (Settings.CompileAfter) parts.Add("试编译");
            if (!Settings.NoFacial) parts.Add("不写 nofacial");
            if (Settings.SkipMainModel) parts.Add("跳过主模型");
            AdvancedSummary = string.Join(" · ", parts.ToArray());
        }

        /// <summary>设置窗口改动后调用：写盘 + 刷新摘要/就绪 + 计划失效</summary>
        public void OnSettingsChanged()
        {
            RefreshAdvancedSummary();
            SaveSettings();
            UpdateReadiness();
            DebounceEnvironmentCheck();
        }

        private void SettingsSet_Stale() { InvalidatePlan(); }

        private void InvalidatePlan()
        {
            Raise("StartHint");

            if (IsPlanVisible && IsRunning == false) { /* 计划视图保留，但开始按钮会重新构建 */ }
        }

        /// <summary>连续改设置时只做一次环境探测（300ms 防抖），避免反复启动 Blender --version。</summary>
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

        /// <summary>把三项检测合并成一个顶栏状态：Blender 后端时 Blender 必须可用（内置 gltfpack 后端只要求内嵌组件在）；
        /// 编译器可选但配了就必须能跑；低完整性算异常。</summary>
        private void RefreshEnvStatus()
        {
            bool gpMode = Settings != null && Settings.ReduceBackend == (int)ReduceBackend.Gltfpack;
            bool detecting = string.Equals(BlenderText, "检测中", StringComparison.Ordinal);
            bool engineOk = gpMode ? GltfpackEmbeddedOk()
                                     : string.Equals(BlenderTone, "Ok", StringComparison.Ordinal);
            bool ok = !detecting &&
                      engineOk &&
                      !string.Equals(PermissionTone, "Err", StringComparison.Ordinal) &&
                      !string.Equals(CompilerTone, "Err", StringComparison.Ordinal);

            EnvTone = detecting ? "Faint" : (ok ? "Ok" : "Err");
            EnvText = detecting ? "环境检测中" : (ok ? "环境正常" : "环境异常");
            string engineLine = gpMode
                ? "减面后端：内置 gltfpack（无需 Blender）" + (engineOk ? "" : "（内嵌组件缺失，异常）") + "\n" +
                  "Blender：" + BlenderText + "（可选，仅回退用）"
                : "Blender：" + BlenderText + (BlenderTone == "Ok" ? "" : "（异常，点这里去设置）");
            EnvTip = engineLine + "\n" +
                     "编译器：" + CompilerText + "（可选，仅试编译用）" + (CompilerTone == "Err" ? "（已配置但无法运行）" : "") + "\n" +
                     "权限：" + PermissionText + (PermissionTone == "Err" ? "（低完整性，无法写文件）" : "") + "\n\n" +
                     "点击打开「运行环境诊断」";
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

        public async Task RunEnvironmentCheckAsync(bool silent = false)
        {
            PermissionText = RuntimeContext.IsLowIntegrity() ? "低完整性" : (RuntimeContext.IsAdmin ? "管理员" : "普通权限");
            PermissionTone = RuntimeContext.IsLowIntegrity() ? "Err" : (RuntimeContext.IsAdmin ? "Ok" : "Faint");

            bool gpMode = Settings.ReduceBackend == (int)ReduceBackend.Gltfpack;
            string blender = Settings.BlenderPath;
            if (string.IsNullOrEmpty(blender) || !File.Exists(blender))
                blender = BlenderLocator.Find(Settings.BlenderPath);
            if (!string.IsNullOrEmpty(blender) && File.Exists(blender))
            {
                Settings.BlenderPath = blender;
                BlenderText = "检测中";
                BlenderTone = "Faint";
                RefreshEnvStatus();
                string ver = await Task.Run(() => BlenderLocator.ProbeVersion(blender, 15000));
                if (!string.IsNullOrEmpty(ver))
                {
                    _blenderVersion = ver.Replace("Blender ", "");
                    BlenderText = _blenderVersion;
                    BlenderTone = "Ok";
                }
                else
                {
                    BlenderText = gpMode ? "无法运行（可选）" : "无法运行";
                    BlenderTone = gpMode ? "Faint" : "Err";
                }
            }
            else
            {
                _blenderVersion = "";
                BlenderText = gpMode ? "未安装（可选）" : "未找到";
                BlenderTone = gpMode ? "Faint" : "Err";
            }

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
                Log.Append(LogLevel.Info, "引擎检测：减面后端 " + (gpMode ? "内置 gltfpack（无需 Blender）" : "Blender") +
                                          "；Blender " + (BlenderTone == "Ok" ? _blenderVersion : (gpMode ? "未安装（可选）" : "未找到")) +
                                          "；编译器 " + CompilerText + "；权限 " + PermissionText);
            }
        }

        private void UpdateReadiness()
        {
            Raise("CanStart"); Raise("CanPreview"); Raise("StartHint");
            if (!HasLastRun) UpdateReadoutIdle();
        }

        private void UpdateReadoutIdle()
        {
            int ready = Queue.Items.Count(t => !t.Skip);
            ReadoutMainText = ready.ToString(CultureInfo.InvariantCulture);
            ReadoutSub = ready > 0 ? "项就绪" : "项就绪";
        }

        // ─────────────────────────── 队列操作 ───────────────────────────

        public async Task AddPathsAsync(IEnumerable<string> paths)
        {
            if (IsRunning) { _ui.ShowToast("正在运行，不能添加任务", "停止后才能修改队列", ToastTone.Warn); return; }
            int added = 0, dup = 0, ignored = 0;
            var list = new List<string>();
            foreach (var raw in paths ?? Enumerable.Empty<string>())
            {
                string p = (raw ?? "").Trim().Trim('"');
                if (p.Length == 0) { ignored++; continue; }
                TaskKind kind;
                try
                {
                    if (Directory.Exists(p)) kind = TaskKind.Folder;
                    else if (File.Exists(p))
                    {
                        string ext = Path.GetExtension(p).ToLowerInvariant();
                        if (ext == ".qc") kind = TaskKind.Qc;
                        else if (ext == ".glb") kind = TaskKind.Glb;
                        else if (ext == ".smd") kind = TaskKind.Smd;
                        else { ignored++; continue; }
                    }
                    else { ignored++; continue; }
                }
                catch { ignored++; continue; }

                bool d;
                Queue.Add(new TaskRef(kind, FileUtil.Normalize(p)), out d);
                if (d) dup++; else { added++; list.Add(p); }
            }
            Queue.Renumber();
            Queue.UpdateCounts();
            Settings.LastAddKind = (int)GuessKind(paths);
            InvalidatePlan();
            Raise("ImportHint"); Raise("CanStart"); Raise("CanPreview"); Raise("StartHint");

            if (added > 0)
            {
                _ui.ShowToast("已加入 " + added + " 项", dup > 0 || ignored > 0
                    ? (dup > 0 ? "重复 " + dup : "") + (ignored > 0 ? (dup > 0 ? " · " : "") + "无法识别 " + ignored : "") : "正在扫描规模…",
                    ToastTone.Ok);
                await ScanAsync(false);
            }
            else
            {
                _ui.ShowToast("没有加入任何任务", dup > 0 ? "全部重复（" + dup + " 项）" : "拖入工程文件夹、.qc、.glb 或 .smd", ToastTone.Warn);
            }
            SaveSettings();
        }

        public void RemoveSelected()
        {
            if (IsRunning) { _ui.ShowToast("正在运行", "停止后才能移除任务", ToastTone.Warn); return; }
            var sel = Queue.Selected;
            if (sel.Count == 0) return;
            Queue.Remove(sel);
            Queue.Selected = new List<TaskItemVm>();
            Queue.UpdateCounts();
            InvalidatePlan();
            Raise("CanStart"); Raise("CanPreview"); Raise("StartHint"); Raise("ImportHint");
            SaveSettings();
        }

        public async Task ClearQueueAsync()
        {
            if (IsRunning) { _ui.ShowToast("正在运行", "停止后才能清空队列", ToastTone.Warn); return; }
            if (Queue.Items.Count == 0) return;
            bool ok = await _ui.ConfirmAsync("清空任务队列",
                "只从列表里移除这 " + Queue.Items.Count + " 项，不会删除磁盘上的任何文件。", "清空", "取消", false, null);
            if (!ok) return;
            Queue.Clear();
            Queue.Selected = new List<TaskItemVm>();
            Queue.UpdateCounts();
            InvalidatePlan();
            IsPlanVisible = false;
            Raise("CanStart"); Raise("CanPreview"); Raise("StartHint"); Raise("ImportHint");
            SaveSettings();
        }

        public void MoveSelected(int delta)
        {
            if (IsRunning) return;
            foreach (var t in Queue.Selected.ToList()) Queue.MoveItem(t, delta);
            Queue.UpdateCounts();
            InvalidatePlan();
        }

        public void RefreshQueueCollections()
        {
            Queue.Renumber();
            Queue.UpdateCounts();
            UpdateReadoutIdle();
            Raise("CanStart"); Raise("CanPreview"); Raise("StartHint"); Raise("ImportHint");
        }

        // ─────────────────────────── 扫描 ───────────────────────────

        public async Task ScanAsync(bool verbose)
        {
            if (IsRunning || Queue.Items.Count == 0) return;
            if (_scanCts != null) { try { _scanCts.Cancel(); } catch { } }
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;
            var items = Queue.Snapshot();
            foreach (var t in items) t.Status = TaskStatus.Scanning;
            Queue.RefreshView();
            if (verbose)
            {
                Log.Append(LogLevel.Stage, "═══ 扫描预览（只读，不会修改任何文件）═══");
                SetStatus("正在扫描…", "只读：读取 QC / 统计模型，不会写入", "Signal", true);
            }

            var results = await Task.Run(() => items.Select(t => ScanOne(t, token)).ToList(), token);
            if (token.IsCancellationRequested) return;

            int willRun = 0, skipped = 0;
            foreach (var r in results)
            {
                var t = r.Item;
                t.Scanned = true;
                t.QcCount = r.Qc;
                t.ModelCount = r.Models;
                t.Skip = r.Skip;
                t.Note = r.Note;
                t.Hint = r.Hint;
                t.Status = r.Skip ? TaskStatus.Skipped : TaskStatus.Pending;
                if (r.Skip) skipped++; else willRun++;
                if (verbose)
                {
                    Log.Append(r.Skip ? LogLevel.Warn : LogLevel.Info,
                        "[" + t.KindText + "] " + t.Name + " → " + (r.Skip ? "跳过：" + r.Note : t.ScaleText) +
                        (string.IsNullOrEmpty(r.Hint) ? "" : "　⚠ " + r.Hint));
                    foreach (var line in r.Lines) Log.Append(LogLevel.Detail, "    " + line);
                }
            }
            Queue.UpdateCounts();
            Queue.RefreshView();
            InvalidatePlan();
            Raise("CanStart"); Raise("CanPreview"); Raise("StartHint"); Raise("ImportHint");
            UpdateReadoutIdle();
            if (verbose)
            {
                Log.Append(LogLevel.Stage, "扫描完成：可以处理 " + willRun + " / " + items.Count + " 项" +
                                           (skipped > 0 ? "，" + skipped + " 项会跳过" : ""));
                SetStatus("扫描完成", willRun + " 项可处理" + (skipped > 0 ? " · " + skipped + " 项会跳过" : ""), "Ok", false);
                _ui.ShowToast("扫描完成", willRun + " 项可处理" + (skipped > 0 ? "，" + skipped + " 项会跳过" : ""), ToastTone.Ok);
            }
            SaveSettings();
        }

        private sealed class ScanOneResult
        {
            public TaskItemVm Item;
            public int Qc, Models;
            public bool Skip;
            public string Note = "";
            public string Hint = "";
            public List<string> Lines = new List<string>();
        }

        private static ScanOneResult ScanOne(TaskItemVm t, CancellationToken token)
        {
            var r = new ScanOneResult { Item = t };
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
                        if (loose.Count == 0) { r.Skip = true; r.Note = "没有 QC，也没有 .glb / .smd"; return r; }
                        r.Models = loose.Count;
                        r.Lines.Add("没有 QC：将为 " + loose.Count + " 个模型各建一个包装 QC");
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
                                if (ext == ".glb" || ext == ".smd") uniq.Add(st.ResolvedPath);
                            }
                        }
                        catch (Exception ex) { r.Lines.Add("解析失败 " + Path.GetFileName(q) + "：" + ex.Message); }
                    }
                    r.Models = uniq.Count;
                    if (foreign > 0) r.Hint = foreign + " 个 QC 已有手写 $lod，将跳过（可在高级设置里选择覆盖）";
                    if (uniq.Count == 0) { r.Skip = true; r.Note = "QC 里没有可处理的 glb/smd 模型"; }
                }
                else if (t.Kind == TaskKind.Qc)
                {
                    if (!File.Exists(t.Path)) { r.Skip = true; r.Note = "文件不存在"; return r; }
                    r.Qc = 1;
                    var info = QcParser.Parse(t.Path);
                    r.Models = info.Studios.Count(x => x.Exists &&
                        (Path.GetExtension(x.ResolvedPath).Equals(".glb", StringComparison.OrdinalIgnoreCase) ||
                         Path.GetExtension(x.ResolvedPath).Equals(".smd", StringComparison.OrdinalIgnoreCase)));
                    if (info.Studios.Count == 0) { r.Skip = true; r.Note = "QC 里没有 studio 条目"; }
                    else if (r.Models == 0) { r.Skip = true; r.Note = "没有可处理的 glb/smd（当前只支持 glb 和 smd）"; }
                    else if (info.HasForeignLod) r.Hint = "已有手写 $lod，将跳过（可在高级设置里选择覆盖）";
                }
                else
                {
                    if (!File.Exists(t.Path)) { r.Skip = true; r.Note = "文件不存在"; return r; }
                    r.Qc = 1; r.Models = 1;
                    string wrapper = ProjectScanner.WrapperQcPath(t.Path);
                    if (File.Exists(wrapper)) r.Lines.Add("沿用已有的包装 QC：" + Path.GetFileName(wrapper));
                }
            }
            catch (Exception ex) { r.Skip = true; r.Note = "扫描失败：" + ex.Message; }
            return r;
        }

        // ─────────────────────────── 计划预览 ───────────────────────────

        public async Task PreviewPlanAsync()
        {
            if (IsRunning || IsBusy) return;
            if (Queue.Items.Count == 0) { _ui.ShowToast("队列为空", "先导入工程文件夹或模型文件", ToastTone.Warn); return; }
            string err = LodPlan.ValidateForRun();
            if (err != null) { await _ui.ShowInfoAsync("LOD 参数有误", err, null); return; }

            IsBusy = true;
            SetStatus("正在生成作业计划…", "只读：统计 QC / 模型 / 减面任务，不会修改文件", "Signal", true);
            var collector = new CollectingObserver();
            try
            {
                var plan = await Task.Run(() => BuildPlan(collector), CancellationToken.None);
                if (plan == null) return;
                foreach (var line in collector.Lines) Log.Append(LogLevel.Detail, line);
                if (plan.QcPlans.Count == 0)
                {
                    SetStatus("计划为空", "没有可处理的 QC；看日志里的跳过原因", "Warn", false);
                    await _ui.ShowInfoAsync("没有可处理的 QC",
                        "所有任务都被跳过了。展开日志可以看到每项的原因；常见原因是：没有 QC、模型文件缺失、已有手写 $lod、面数低于门槛。",
                        string.Join("\n", collector.Lines.ToArray()));
                    return;
                }
                PlanPreview = new PlanPreviewViewModel(plan);
                PlanPreview.CloseRequested += delegate { IsPlanVisible = false; };
                PlanPreview.StartRequested += async delegate
                {
                    IsPlanVisible = false;
                    await StartRunAsync(plan);
                };
                IsPlanVisible = true;
                SetStatus("计划已生成", PlanPreview.Headline + " · " + PlanPreview.Subline, "Ok", false);
                Log.Append(LogLevel.Stage, "作业计划：" + PlanPreview.Headline);
            }
            catch (Exception ex)
            {
                Log.Append(LogLevel.Error, "生成计划失败：" + ex.Message);
                await _ui.ShowInfoAsync("生成计划失败", ex.Message, ex.ToString());
                SetStatus("生成计划失败", ex.Message, "Err", false);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private BatchPlan BuildPlan(IBatchObserver observer)
        {
            var tasks = Queue.Items.Where(t => !t.Skip).Select(t => t.ToRef()).ToList();
            if (tasks.Count == 0) tasks = Queue.Items.Select(t => t.ToRef()).ToList();
            string rootDir = ResolveRootDir(tasks);
            var options = BuildRunOptions(rootDir);
            return BatchPlanner.Build(tasks, rootDir, options, observer, CancellationToken.None);
        }

        private string ResolveRootDir(List<TaskRef> tasks)
        {
            foreach (var t in tasks)
            {
                if (t.Kind == TaskKind.Folder) return t.Path;
            }
            foreach (var t in tasks)
            {
                try { return Path.GetDirectoryName(FileUtil.Normalize(t.Path)); } catch { }
            }
            return AppInfo.ExeDir;
        }

        private BatchRunOptions BuildRunOptions(string rootDir)
        {
            var opt = new BatchRunOptions
            {
                Plan = LodPlan.ClonePlan(),
                PresetName = LodPlan.PresetDisplay,
                Backend = (ReduceBackend)Settings.ReduceBackend,
                BlenderPath = Settings.BlenderPath,
                Workers = Settings.Workers,
                QualityFirst = Settings.QualityFirst,
                Force = Settings.ForceOverwriteLod,
                CompileAfter = Settings.CompileAfter,
                NekomdlPath = Settings.NekomdlPath,
                NoFacial = Settings.NoFacial,
                SkipMainModel = Settings.SkipMainModel,
                SkipMainQcPaths = new HashSet<string>(Settings.SkipMainQcs, StringComparer.OrdinalIgnoreCase),
                MinTriangles = Settings.MinTriangles,
                OutputMode = (OutputMode)LodPlan.WriteMode,
                CollectionName = "_LOD合集.qc",
                BackupEnabled = Settings.BackupEnabled,
                BackupCentralBaseDir = Settings.BackupEnabled ? OutputLocations.ResolveBackupBaseDir(Settings) : null,
                ProjectRoot = rootDir,
                ReportDir = OutputLocations.ResolveReportDir(Settings, rootDir),
                WriteReport = true
            };
            return opt;
        }

        /// <summary>关闭计划视图（返回队列）</summary>
        public void ClosePlan() { IsPlanVisible = false; }

        /// <summary>按当前计划开始生成</summary>
        public async Task StartFromPlanAsync()
        {
            var p = PlanPreview;
            IsPlanVisible = false;
            if (p != null) await StartRunAsync(p.Plan);
        }
        // ─────────────────────────── 运行 ───────────────────────────

        public async Task StartRunAsync(BatchPlan existingPlan = null)
        {
            if (IsRunning || IsBusy) return;
            if (Queue.Items.Count == 0) { _ui.ShowToast("队列为空", "先导入工程文件夹或模型文件", ToastTone.Warn); return; }

            string err = LodPlan.ValidateForRun();
            if (err != null) { await _ui.ShowInfoAsync("LOD 参数有误", err, null); return; }

            // Blender：仅 Blender 后端强制；内置 gltfpack 后端无需 Blender（保留已找到的路径作回退）
            if (Settings.ReduceBackend == (int)ReduceBackend.Blender)
            {
                string blender = Settings.BlenderPath;
                if (string.IsNullOrEmpty(blender) || !File.Exists(blender)) blender = BlenderLocator.Find(Settings.BlenderPath);
                if (string.IsNullOrEmpty(blender) || !File.Exists(blender))
                {
                    bool go = await _ui.ConfirmAsync("还没有指定 Blender",
                        "生成 LOD 必须用 Blender 减面。请到「设置 → 引擎」指定 blender.exe（通常会自动找到）；" +
                        "没有 Blender 时可在「设置 → 引擎 → 减面后端」改用「内置 gltfpack」。",
                        "打开设置", "取消", false, null);
                    if (go) _ui.OpenSettings(0);
                    return;
                }
                Settings.BlenderPath = blender;
            }

            if (Settings.CompileAfter && (string.IsNullOrEmpty(Settings.NekomdlPath) || !File.Exists(Settings.NekomdlPath)))
            {
                bool go = await _ui.ConfirmAsync("开启了「生成后试编译」，但没有指定编译器",
                    "请到「设置 → 引擎」指定 nekomdl.exe，或关闭「生成后试编译」。", "打开设置", "取消", false, null);
                if (go) _ui.OpenSettings(0);
                return;
            }

            // 可写性
            string bad = CheckWritableDirs();
            if (bad != null)
            {
                bool go = await _ui.ConfirmAsync("有目录无法写入",
                    "以下目录写不进去，相关项会失败。通常是安全软件拦截了普通权限程序（也可把工具加入信任区）。\n\n" + bad,
                    "仍然开始", "取消", true, null);
                if (!go) return;
            }

            // 修改 QC 前的确认
            if (LodPlan.WriteMode != 2)
            {
                string what = LodPlan.WriteMode == 0
                    ? "会在每个 QC 末尾写入 $lod 块。"
                    : "会在每个 QC 旁生成 _LOD.qc，并在原 QC 里加一行 $include。";
                if (Settings.ForceOverwriteLod) what += "\n已开启「覆盖手写 $lod」：原有手写块会被注释停用。";
                what += Settings.BackupEnabled
                    ? "\n\n每个被修改的 QC 会先备份到" + OutputLocations.DescribeBackup(Settings) + "，可随时还原。"
                    : "\n\n⚠ 已关闭备份：本次不会生成 .lodbak，改完无法一键还原。";
                bool ok = await _ui.ConfirmAsync("确认修改 QC", what, "开始生成", "取消", !Settings.BackupEnabled, null);
                if (!ok) return;
            }

            BatchPlan plan = existingPlan;
            IsBusy = true;
            try
            {
                if (plan == null)
                {
                    SetStatus("正在准备…", "展开任务、解析 QC、生成减面任务", "Signal", true);
                    var collector = new CollectingObserver();
                    plan = await Task.Run(() => BuildPlan(collector));
                    foreach (var line in collector.Lines) Log.Append(LogLevel.Detail, line);
                    if (plan == null || plan.QcPlans.Count == 0)
                    {
                        await _ui.ShowInfoAsync("没有可处理的内容", "所有任务都被跳过了。看日志里的原因。",
                            string.Join("\n", collector.Lines.ToArray()));
                        SetStatus("没有可处理的内容", "看日志里的跳过原因", "Warn", false);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                await _ui.ShowInfoAsync("准备运行失败", ex.Message, ex.ToString());
                return;
            }
            finally
            {
                IsBusy = false;
            }

            await RunAsync(plan);
        }

        private string CheckWritableDirs()
        {
            var bad = new List<string>();
            foreach (var t in Queue.Items.Where(x => !x.Skip))
            {
                string dir;
                try { dir = t.Kind == TaskKind.Folder ? t.Path : Path.GetDirectoryName(FileUtil.Normalize(t.Path)); }
                catch { continue; }
                if (string.IsNullOrEmpty(dir) || bad.Contains(dir, StringComparer.OrdinalIgnoreCase)) continue;
                if (FileUtil.TestWritable(dir) != null) bad.Add(dir);
            }
            if (bad.Count == 0) return null;
            return string.Join("\n", bad.Take(6).Select(x => "· " + x).ToArray()) + (bad.Count > 6 ? "\n…" : "");
        }

        private async Task RunAsync(BatchPlan plan)
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            IsRunning = true;
            IsPlanVisible = false;
            ProgressVisible = true;
            ProgressFailed = false;
            ProgressValue = 0;
            ProgressIndeterminate = true;
            SetStatus("正在生成…", "准备减面任务", "Signal", true);
            Log.Append(LogLevel.Stage, "═══ 开始生成：" + plan.PresetName + " · " + plan.QcPlans.Count + " 个 QC · " + plan.JobCount + " 个任务 ═══");
            Queue.ResetRunState();
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var exp in plan.Tasks) foreach (var q in exp.QcPaths) touched.Add(FileUtil.Normalize(q));
            var runningItems = plan.Tasks.Select(x => x.Task).ToList();
            foreach (var t in Queue.Items)
                if (runningItems.Any(x => x.Kind == t.Kind && string.Equals(FileUtil.Normalize(x.Path), FileUtil.Normalize(t.Path), StringComparison.OrdinalIgnoreCase)))
                    t.Status = TaskStatus.Running;
            Queue.RefreshView();
            _pump.Start();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            BatchOutcome outcome = null;
            try
            {
                var options = BuildRunOptions(plan.RootDir);
                // 报告/日志位置按"QC 所在目录"解析（与设置里的文案一致），而不是工程根
                options.ReportDir = OutputLocations.ResolveReportDir(Settings, FirstQcDir(plan));
                // 以计划里的参数为准：用户在计划视图点"按此计划开始生成"时，参数就是预览时的那一份
                options.Plan = plan.Plan ?? LodPlan.ClonePlan();
                options.PresetName = plan.PresetName;
                var observer = new UiObserver(this);
                outcome = await Task.Run(() => BatchRunner.Run(plan, options, observer, token), token);
            }
            catch (OperationCanceledException) { Log.Append(LogLevel.Warn, "已请求停止"); }
            catch (Exception ex)
            {
                Log.Append(LogLevel.Error, "运行失败：" + ex.Message);
                outcome = new BatchOutcome { Ok = false, Errors = { ex.GetType().Name + ": " + ex.Message }, Cancelled = token.IsCancellationRequested };
            }
            finally
            {
                sw.Stop();
                _pump.Stop();
                DrainLog();
                IsRunning = false;
            }
            await FinishRunAsync(plan, outcome, sw.Elapsed, token.IsCancellationRequested);
        }

        /// <summary>关窗时强制取消（不弹确认）</summary>
        public void ForceCancel()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
        }

        private void RequestStopInternal()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { BlenderScheduler.KillAll(); } catch { }
            SetStatus("正在停止…", "结束所有 Blender 进程；已经生成的 LOD 保留，本次不再修改 QC", "Warn", true);
            ProgressIndeterminate = true;
            Log.Append(LogLevel.Warn, "用户请求停止");
        }

        public async Task StopAsync()
        {
            if (!IsRunning) return;
            bool ok = await _ui.ConfirmAsync("停止处理",
                "会立刻结束所有 Blender 进程。已经生成的 LOD 文件会保留，但本次不会再修改任何 QC。",
                "停止", "继续处理", true, null);
            if (ok) RequestStopInternal();
        }

        private async Task FinishRunAsync(BatchPlan plan, BatchOutcome outcome, TimeSpan elapsed, bool cancelled)
        {
            var stats = outcome != null ? outcome.Stats : new BatchStats();
            var ocByQc = new Dictionary<string, QcOutcome>(StringComparer.OrdinalIgnoreCase);
            if (outcome != null) foreach (var q in outcome.QcOutcomes) ocByQc[FileUtil.Normalize(q.QcPath)] = q;
            var injected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (outcome != null) foreach (var p in outcome.InjectedQcPaths) injected.Add(FileUtil.Normalize(p));
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (outcome != null) foreach (var p in outcome.SkippedQcPaths) skipped.Add(FileUtil.Normalize(p));

            int okCount = 0, partialCount = 0, failedCount = 0, skipCount = 0;
            foreach (var exp in plan.Tasks)
            {
                var item = Queue.Items.FirstOrDefault(x => x.Kind == exp.Task.Kind &&
                    string.Equals(FileUtil.Normalize(x.Path), FileUtil.Normalize(exp.Task.Path), StringComparison.OrdinalIgnoreCase));
                if (item == null) continue;
                int total = exp.QcPaths.Count;
                int done = 0, esc = 0, errCount = 0;
                var errs = new List<string>();
                foreach (var q in exp.QcPaths)
                {
                    string key = FileUtil.Normalize(q);
                    QcOutcome qo;
                    if (ocByQc.TryGetValue(key, out qo))
                    {
                        // 以 QC 结果为唯一事实来源（否则 injected 集合与 QcOutcome 会重复计数，出现"已写入 2/1 个 QC"）
                        if (qo.Errors.Count > 0) errCount += qo.Errors.Count;
                        foreach (var e in qo.Errors) errs.Add(Path.GetFileName(q) + "：" + e);
                        if (qo.Injected) done++;
                        else if (qo.Skipped) esc++;
                    }
                    else if (injected.Contains(key)) done++;
                    else if (skipped.Contains(key)) esc++;
                }
                if (!string.IsNullOrEmpty(exp.Error)) { errs.Add(exp.Error); errCount++; }
                if (errs.Count == 0 && outcome != null && outcome.Errors.Count > 0)
                {
                    foreach (var e in outcome.Errors.Take(3)) errs.Add(e);
                }

                TaskStatus state;
                if (cancelled && done < total) state = TaskStatus.Cancelled;
                else if (total == 0) state = TaskStatus.Failed;
                else if (done == 0 && esc >= total && errCount == 0) state = TaskStatus.Skipped;
                else if (done == 0) state = TaskStatus.Failed;
                else if (errCount > 0 || done + esc < total) state = TaskStatus.Partial;
                else state = TaskStatus.Ok;

                item.Status = state;
                string verb = LodPlan.WriteMode == 2 ? "已输出" : "已写入";
                item.RunDetail = verb + " " + done + " / " + total + " 个 QC" + (esc > 0 ? "，跳过 " + esc : "");
                item.RunErrors = errs.Count > 0 ? string.Join("\n", errs.Take(20).ToArray()) : "";
                if (state == TaskStatus.Ok) okCount++;
                else if (state == TaskStatus.Partial) partialCount++;
                else if (state == TaskStatus.Failed) failedCount++;
                else if (state == TaskStatus.Skipped) skipCount++;
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
            _lastOutputDir = ResolveOutputDir(plan);
            Raise("HasLastRun"); Raise("LastRunText"); Raise("CanOpenLastReport"); Raise("ImportHint");

            ProgressIndeterminate = false;
            if (cancelled)
            {
                ProgressVisible = false;
                SetStatus("已停止", "已生成的 LOD 保留；本次未继续修改 QC", "Warn", false);
                SetReadout(stats.InjectedQcCount, "QC 已写入");
                Log.Append(LogLevel.Stage, "════ 已停止 ════");
            }
            else if (failedCount == 0 && partialCount == 0)
            {
                ProgressValue = 1;
                ProgressVisible = true;
                ProgressFailed = false;
                SetStatus("全部完成", BuildSummaryLine(stats, elapsed, skipCount), "Ok", false);
                SetReadout(stats.InjectedQcCount, "QC 已写入");
                Log.Append(LogLevel.Stage, "════ 全部完成 ════");
            }
            else
            {
                ProgressFailed = failedCount > 0;
                ProgressVisible = true;
                SetStatus(failedCount > 0 ? "有失败项" : "部分完成", BuildSummaryLine(stats, elapsed, skipCount),
                          failedCount > 0 ? "Err" : "Warn", false);
                SetReadout(stats.InjectedQcCount, "QC 已写入");
                Log.Append(LogLevel.Stage, "════ 结束（有 " + failedCount + " 项失败）════");
            }

            if (outcome != null)
            {
                foreach (var e in outcome.Errors.Take(30)) Log.Append(LogLevel.Error, e);
                if (!string.IsNullOrEmpty(outcome.ReportPath)) Log.Append(LogLevel.Info, "总报告：" + outcome.ReportPath);
            }
            Raise("CanStart"); Raise("CanPreview"); Raise("StartHint");
            SaveSettings();

            // 结果提示
            if (cancelled)
            {
                _ui.ShowToast("已停止", "已生成的 LOD 保留，QC 未继续修改", ToastTone.Warn);
            }
            else if (failedCount == 0 && partialCount == 0)
            {
                _ui.ShowToast("完成 · " + okCount + " 项", BuildSummaryLine(stats, elapsed, skipCount), ToastTone.Ok);
                if (Settings.PopupOnSuccess)
                    await _ui.ShowInfoAsync("全部完成", BuildSummaryLine(stats, elapsed, skipCount), CanOpenLastReport ? "报告：" + _lastReportPath : null);
            }
            else
            {
                _ui.ShowToast(failedCount > 0 ? "有 " + failedCount + " 项失败" : "有 " + partialCount + " 项部分完成",
                    BuildSummaryLine(stats, elapsed, skipCount),
                    failedCount > 0 ? ToastTone.Err : ToastTone.Warn);
                await ShowFailureDialogAsync(plan, failedCount, partialCount);
            }
            _ui.FlashWindow();
        }

        private string BuildSummaryLine(BatchStats stats, TimeSpan elapsed, int skipTasks)
        {
            var parts = new List<string>();
            parts.Add("写入 " + stats.InjectedQcCount + " 个 QC");
            if (stats.UniqueModelCount > 0) parts.Add(stats.UniqueModelCount + " 个模型");
            if (stats.JobOk + stats.JobFail > 0) parts.Add("减面 " + stats.JobOk + "/" + (stats.JobOk + stats.JobFail));
            if (stats.CompileOk + stats.CompileFail > 0) parts.Add("试编译 " + stats.CompileOk + "/" + (stats.CompileOk + stats.CompileFail));
            if (stats.TotalSourceTriangles > 0)
                parts.Add("原始 " + stats.TotalSourceTriangles.ToString("N0", CultureInfo.InvariantCulture) + " 面 → 末级 " +
                          stats.TotalLastLodTriangles.ToString("N0", CultureInfo.InvariantCulture) + " 面");
            if (skipTasks > 0) parts.Add("跳过 " + skipTasks + " 项");
            parts.Add("用时 " + FormatSpan(elapsed));
            return string.Join(" · ", parts.ToArray());
        }

        private async Task ShowFailureDialogAsync(BatchPlan plan, int failed, int partial)
        {
            var sb = new StringBuilder();
            foreach (var t in Queue.Items.Where(x => x.Status == TaskStatus.Failed || x.Status == TaskStatus.Partial).Take(8))
            {
                sb.AppendLine("· " + t.Name + "（" + t.RunDetail + "）");
                if (!string.IsNullOrEmpty(t.RunErrors))
                    foreach (var line in t.RunErrors.Split('\n').Take(3)) sb.AppendLine("    " + line);
            }
            if (Queue.Items.Count(x => x.Status == TaskStatus.Failed || x.Status == TaskStatus.Partial) > 8) sb.AppendLine("…");
            bool open = await _ui.ConfirmAsync(
                failed > 0 ? "有 " + failed + " 项失败" : "有 " + partial + " 项只完成了一部分",
                "成功的项已经正常写入。下面是出问题的项（完整内容见报告）：",
                CanOpenLastReport ? "打开报告" : "知道了", "关闭", false, sb.ToString());
            if (open && CanOpenLastReport) OpenLastReport();
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

        private string ResolveOutputDir(BatchPlan plan)
        {
            foreach (var exp in plan.Tasks)
            {
                try
                {
                    if (exp.Task.Kind == TaskKind.Folder && Directory.Exists(exp.Task.Path)) return exp.Task.Path;
                }
                catch { }
            }
            foreach (var q in plan.QcPlans)
            {
                try
                {
                    string d = Path.GetDirectoryName(q.QcPath);
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
                }
                catch { }
            }
            return plan.RootDir;
        }

        // ─────────────────────────── 报告 / 细节 / 诊断 ───────────────────────────

        public void OpenLastReport()
        {
            if (CanOpenLastReport) _ui.OpenFile(_lastReportPath);
            else _ui.ShowToast("还没有报告", "先生成一次，完成后面板会出现「打开报告」", ToastTone.Warn);
        }

        public void OpenOutputFolder()
        {
            if (!string.IsNullOrEmpty(_lastOutputDir) && Directory.Exists(_lastOutputDir)) _ui.RevealInExplorer(_lastOutputDir);
            else _ui.ShowToast("还没有输出目录", "先导入任务并运行一次", ToastTone.Warn);
        }

        public async Task ShowTaskDetailsAsync(TaskItemVm item)
        {
            if (item == null) return;
            string body = await Task.Run(() => BuildTaskDetails(item));
            bool reveal = await _ui.ConfirmAsync(item.KindText + "：" + item.Name, item.Path,
                "在资源管理器中显示", "关闭", false, body);
            if (reveal) _ui.RevealInExplorer(item.Path);
        }

        private string BuildTaskDetails(TaskItemVm t)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(t.RunDetail))
            {
                sb.AppendLine("上次运行：" + t.StatusText + "　" + t.RunDetail);
                if (!string.IsNullOrEmpty(t.RunErrors)) foreach (var line in t.RunErrors.Split('\n')) sb.AppendLine("  · " + line);
                sb.AppendLine();
            }
            try
            {
                List<string> qcs;
                if (t.Kind == TaskKind.Folder)
                {
                    if (!Directory.Exists(t.Path)) return "文件夹不存在。";
                    qcs = ProjectScanner.FindRootQcs(t.Path, null);
                    if (qcs.Count == 0)
                    {
                        var loose = ProjectScanner.FindLooseModels(t.Path, 3, null);
                        sb.AppendLine("没有 QC。运行时会为下面每个模型建立包装 QC：");
                        foreach (var x in loose.Take(60)) sb.AppendLine("  " + x);
                        return sb.ToString();
                    }
                }
                else if (t.Kind == TaskKind.Qc) qcs = new List<string> { t.Path };
                else
                {
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
                        if (info.HasForeignLod) sb.AppendLine(Settings.ForceOverwriteLod ? "  已有手写 $lod → 会被注释停用并替换" : "  已有手写 $lod → 这个 QC 会被跳过");
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
                                if (ext != ".glb" && ext != ".smd") state = "· 不是 glb/smd，保持原样";
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
                text = await Task.Run(() => EnvironmentDiagnostics.Build(Settings, probeDirs, _blenderVersion,
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
            LodPlan.RestorePreset();
            InvalidatePlan();
        }

        public void AddLevel()
        {
            LodPlan.AddLevel();
            InvalidatePlan();
        }

        public void RemoveLevel(int index)
        {
            LodPlan.RemoveLevel(index);
            InvalidatePlan();
        }

        public void OnWriteModeChanged(int mode)
        {
            LodPlan.WriteMode = mode;
            InvalidatePlan();
            SaveSettings();
        }

        public async Task SavePresetAsync()
        {
            string name = await _ui.PromptAsync("保存为我的预设", "预设名称（会显示在预设下拉里）",
                LodPlan.PresetDisplay, v => string.IsNullOrWhiteSpace(v) ? "名称不能为空" : null);
            if (string.IsNullOrEmpty(name)) return;
            var existing = LodPlan.FindPresetByName(name);
            if (existing != null && !existing.IsUser)
            {
                await _ui.ShowInfoAsync("名称已被占用", "「" + name + "」是内置预设名，请换一个。", null);
                return;
            }
            var plan = LodPlan.ClonePlan();
            var preset = new LodPreset
            {
                Name = name.Trim(),
                Description = "我的预设",
                Distances = plan.Distances,
                Ratios = plan.Ratios,
                IsUser = true
            };
            var users = LodPlan.Presets.Where(p => p.IsUser && !string.Equals(p.Name, preset.Name, StringComparison.Ordinal))
                                       .Select(p => p.Clone()).ToList();
            users.Add(preset);
            var current = LodPlan.Plan.Clone();
            LodPlan.SetUserPresets(users);
            LodPlan.LoadFrom(current, preset.Name);
            SaveSettings();
            _ui.ShowToast("已保存预设", preset.Name + " · " + current.SummaryShort(), ToastTone.Ok);
        }

        public async Task DeletePresetAsync()
        {
            var p = LodPlan.SelectedPreset;
            if (p == null || !p.IsUser) { _ui.ShowToast("请先选中一个「我的预设」", "内置预设不能删除", ToastTone.Warn); return; }
            bool ok = await _ui.ConfirmAsync("删除预设", "删除「" + p.Name + "」？LOD 参数本身不会变。", "删除", "取消", false, null);
            if (!ok) return;
            var users = LodPlan.Presets.Where(x => x.IsUser && !string.Equals(x.Name, p.Name, StringComparison.Ordinal))
                                       .Select(x => x.Clone()).ToList();
            var current = LodPlan.Plan.Clone();
            LodPlan.SetUserPresets(users);
            LodPlan.LoadFrom(current, "");
            SaveSettings();
            _ui.ShowToast("已删除预设", p.Name, ToastTone.Ok);
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

        private void OnPump(object sender, EventArgs e)
        {
            DrainLog();
            int done, total;
            TimeSpan elapsed;
            lock (_progressGate) { done = _pendingDone; total = _pendingTotal; elapsed = _pendingElapsed; _pendingDone = -1; _pendingTotal = -1; }
            if (total > 0 && done >= 0)
            {
                ProgressIndeterminate = false;
                ProgressValue = done / (double)total;
                SetStatus("正在生成 " + done + " / " + total, "已用 " + FormatSpan(elapsed) + EstimateEta(done, total, elapsed), "Signal", true);
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
                SavePreset = new RelayCommand(async () => await _vm.SavePresetAsync());
                DeletePreset = new RelayCommand(async () => await _vm.DeletePresetAsync());
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
            public RelayCommand SavePreset { get; private set; }
            public RelayCommand DeletePreset { get; private set; }
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
            string dir = await _ui.PickFolderAsync();
            if (!string.IsNullOrEmpty(dir)) await AddPathsAsync(new[] { dir });
        }

        private async Task AddFilesAsync()
        {
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
                "F5            开始生成\n" +
                "Esc           停止运行 / 清筛选 / 关对话框\n" +
                "Ctrl+O        导入文件（QC / GLB / SMD）\n" +
                "Ctrl+Shift+O  导入工程文件夹\n" +
                "Ctrl+F        搜索任务（Esc 清空）\n" +
                "Ctrl+L        展开 / 折叠运行日志\n" +
                "Ctrl+,        打开设置\n" +
                "Ctrl+A        全选任务\n" +
                "Delete        移除选中任务\n" +
                "Ctrl+↑ / ↓    调整任务顺序\n" +
                "Enter         查看选中任务详情\n" +
                "↑↓ / Home / End / PgUp / PgDn   在任务列表里移动\n" +
                "F1            本快捷键列表";
            await _ui.ShowInfoAsync("快捷键", "这些键在主窗口里随时可用。", text);
        }

        public async Task ShowAboutAsync()
        {
            string text = AppInfo.TitleText + "\n" +
                          "给 L4D2 模型工程自动生成多级 LOD（Blender 减面 + $lod 写回 QC）。\n\n" +
                          "版本：" + AppInfo.FileVersionText + "（" + AppInfo.Bitness + "）\n" +
                          "运行时：.NET Framework " + Environment.Version + "\n" +
                          "设置文件：" + AppInfo.SettingsFile + "\n" +
                          "临时目录：" + AppInfo.TempRoot + "\n" +
                          "Blender：" + (string.IsNullOrEmpty(_blenderVersion) ? "未检测" : _blenderVersion) + "\n" +
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

        /// <summary>LOD 参数在运行前的额外校验（含 Blender 等）</summary>
        public void RaiseReady() { UpdateReadiness(); Raise("ShowStartButton"); Raise("ShowStopButton"); }

        // 让 SettingsViewModel 在保存时促使计划失效
        public void InvalidatePlans() { InvalidatePlan(); }

        // ─────────────────────────── 引擎观察者 ───────────────────────────

        private sealed class UiObserver : IBatchObserver
        {
            private readonly ShellViewModel _vm;
            public UiObserver(ShellViewModel vm) { _vm = vm; }
            public void Log(LogLevel level, string text) { _vm.PostLog(level, text); }
            public void Progress(int done, int total, TimeSpan elapsed) { _vm.PostProgress(done, total, elapsed); }
            public void Stage(BatchStage stage, string detail) { if (detail != null) _vm.PostLog(LogLevel.Stage, detail); }
        }
    }
}
