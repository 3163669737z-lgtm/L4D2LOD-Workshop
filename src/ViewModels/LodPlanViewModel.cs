using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using LodStudio.Engine;

namespace LodStudio.ViewModels
{
    /// <summary>一级 LOD 的编辑行。</summary>
    public sealed class LodLevelVm : VmBase
    {
        private readonly LodPlanViewModel _owner;
        private readonly int _index;
        private string _distanceText = "";
        private string _ratioText = "";
        private bool _distanceInvalid;
        private bool _ratioInvalid;
        private string _error = "";

        public LodLevelVm(LodPlanViewModel owner, int index) { _owner = owner; _index = index; }

        public int Number { get { return _index + 1; } }
        public string NumberText { get { return (_index + 1).ToString("00", CultureInfo.InvariantCulture); } }
        public double Ratio
        {
            get { return _owner.Plan.Levels[_index].Ratio; }
            set { if (Math.Abs(value - _owner.Plan.Levels[_index].Ratio) > 1e-6) _owner.SetRatio(_index, value); }
        }
        public double Distance { get { return _owner.Plan.Levels[_index].Distance; } }
        public double RatioPercent { get { return Math.Round(Ratio * 100.0, 1); } }

        /// <summary>比例条允许拖到的最大值：本级不能超过上一级（上一级 - 1 个百分点）。</summary>
        public double MaxRatio { get { return _owner.MaxRatioFor(_index); } }


        public string DistanceText
        {
            get { return _distanceText; }
            set
            {
                string v = value ?? "";
                if (v == _distanceText) return;
                _distanceText = v;
                Raise();
                _owner.OnDistanceEdited(_index, v);
            }
        }

        public string RatioText
        {
            get { return _ratioText; }
            set
            {
                string v = value ?? "";
                if (v == _ratioText) return;
                _ratioText = v;
                Raise();
                _owner.OnRatioEdited(_index, v);
            }
        }

        public bool DistanceInvalid { get { return _distanceInvalid; } set { Set(ref _distanceInvalid, value); } }
        public bool RatioInvalid { get { return _ratioInvalid; } set { Set(ref _ratioInvalid, value); } }
        public string Error { get { return _error; } set { Set(ref _error, value); } }
        public bool CanRemove => _owner.Count > LodPlan.MinLevels;

        /// <summary>从方案刷新显示（不触发写回）</summary>
        public void RefreshFromPlan()
        {
            _distanceText = _owner.Plan.Levels[_index].Distance.ToString("0.###", CultureInfo.InvariantCulture);
            _ratioText = Math.Round(_owner.Plan.Levels[_index].Ratio * 100.0, 1).ToString("0.#", CultureInfo.InvariantCulture);
            Raise("DistanceText"); Raise("RatioText"); Raise("Ratio"); Raise("Distance"); Raise("RatioPercent"); Raise("MaxRatio");
            Raise(nameof(CanRemove));
        }

        public void SetInvalid(bool distance, bool ratio, string error)
        {
            DistanceInvalid = distance;
            RatioInvalid = ratio;
            Error = error;
        }

        /// <summary>数值改了以后只刷新派生显示（比例条/百分比），不重写正在输入的文本。</summary>
        internal void RaiseDerived()
        {
            Raise("Ratio"); Raise("RatioPercent"); Raise("Distance"); Raise("MaxRatio");
        }
    }

    /// <summary>
    /// LOD 方案：预设选择 / 级别编辑 / 校验 / 写入方式 / 摘要。
    /// 所有修改最终落到 <see cref="Plan"/>；校验规则与引擎 LodPlan.Validate 一致。
    /// </summary>
    public sealed class LodPlanViewModel : VmBase
    {
        private LodPlan _plan = LodPlan.CreateDefault();
        private readonly ObservableCollection<LodLevelVm> _levels = new ObservableCollection<LodLevelVm>();
        private int _selectedPresetIndex = PresetLibrary.DefaultIndex;
        private int _restorePresetIndex = -1;
        private LodPlan _customPlan = LodPlan.CreateDefault();
        private string _validationError;
        private string _inputError;
        private int _writeMode;

        public event EventHandler PlanChanged;
        public event EventHandler<int> EditRequested;      // 请求把焦点移到某行数字框

        public LodPlanViewModel()
        {
            RefreshLevels();
        }

        public LodPlan Plan { get { return _plan; } }
        public ObservableCollection<LodLevelVm> Levels { get { return _levels; } }
        public IReadOnlyList<string> PresetNames { get; } = PresetLibrary.Builtins()
            .Select(p => p.Name).Concat(new[] { PresetLibrary.CustomName, PresetLibrary.AutomaticName }).ToArray();
        public string ValidationError => _inputError ?? _validationError;
        public bool IsValid => IsAutomatic || string.IsNullOrEmpty(ValidationError);

        /// <summary>0 原 QC / 1 独立 _LOD.qc / 2 仅合集</summary>
        public int WriteMode
        {
            get { return _writeMode; }
            set
            {
                if (Set(ref _writeMode, Math.Max(0, Math.Min(2, value))))
                {
                    Raise("WriteModeHint");
                    Raise("IsCollectionOnly");
                }
            }
        }

        public bool IsCollectionOnly { get { return _writeMode == 2; } }

        public string WriteModeHint
        {
            get
            {
                switch (_writeMode)
                {
                    case 1: return "在 QC 旁生成 _LOD.qc，并在原 QC 中添加 $include。";
                    case 2: return "在工程根目录生成 _LOD合集.qc，供手动复制 $lod 块。";
                    default: return "将有效 $lod 写回 QC 或包含文件，重复生成会更新工具块。";
                }
            }
        }

        public int Count { get { return _levels.Count; } }
        public bool CanAddLevel { get { return !IsAutomatic && _levels.Count < LodPlan.MaxLevels && !IsLocked; } }
        public bool IsLocked { get; set; }
        public bool IsAutomatic => _selectedPresetIndex == PresetLibrary.AutomaticIndex;

        public string SpecText
        {
            get
            {
                if (IsAutomatic) return "扫描后估算级数、距离和保留比例";
                if (_plan.Levels.Count == 0) return "未设置";
                var dists = string.Join("/", _plan.Levels.Select(x => LodPlan.Fmt(x.Distance)).ToArray());
                return dists + " HU · " + _plan.Levels.Count + " 级 · " +
                       LodPlan.Pct(_plan.Levels.First().Ratio) + "% → " + LodPlan.Pct(_plan.Levels.Last().Ratio) + "%";
            }
        }

        public string PresetDisplay => PresetNames[_selectedPresetIndex];
        public bool ShowRestore => _restorePresetIndex >= 0 && !IsLocked;

        public int SelectedPresetIndex
        {
            get => _selectedPresetIndex;
            set
            {
                if (IsLocked || value < 0 || value >= PresetNames.Count || value == _selectedPresetIndex) return;
                _selectedPresetIndex = value;
                _restorePresetIndex = -1;
                if (!IsAutomatic)
                    _plan = value == PresetLibrary.CustomIndex
                        ? _customPlan.Clone()
                        : LodPlan.FromPreset(PresetLibrary.Builtins()[value]);
                RefreshLevels();
                Revalidate();
                RaiseAll();
                OnPlanChanged(-1);
            }
        }

        public LodPlan CloneCustomPlan() => _customPlan.Clone();

        public void LoadFrom(LodPlan plan, string presetName, LodPlan customPlan = null)
        {
            _plan = plan?.Clone() ?? LodPlan.CreateDefault();
            _selectedPresetIndex = PresetLibrary.SelectionFor(presetName, _plan);
            _restorePresetIndex = -1;
            if (customPlan != null && customPlan.Validate() == null)
                _customPlan = customPlan.Clone();
            if (_selectedPresetIndex == PresetLibrary.CustomIndex && _plan.Validate() == null)
                _customPlan = _plan.Clone();
            RefreshLevels();
            Revalidate();
            RaiseAll();
            OnPlanChanged(-1);
        }

        public void SetPlan(LodPlan plan, bool fromUser)
        {
            if (plan == null) return;
            if (!fromUser)
            {
                LoadFrom(plan, PresetDisplay);
                return;
            }
            _plan = plan.Clone();
            RefreshLevels();
            Revalidate();
            MarkModified();
            RaiseAll();
            OnPlanChanged(-1);
        }

        /// <summary>拖动中的静默同步（只更新行显示，不触发 PlanChanged）</summary>
        public void PreviewFromDrag(LodPlan plan)
        {
            if (plan == null || plan.Count != _levels.Count) return;
            _plan = plan.Clone();
            for (int i = 0; i < _levels.Count; i++) _levels[i].RefreshFromPlan();
            Raise("SpecText");
        }

        public void CommitFromDrag(LodPlan plan, int levelIndex)
        {
            if (plan == null) return;
            _plan = plan.Clone();
            RefreshLevels();
            Revalidate();
            MarkModified();
            RaiseAll();
            OnPlanChanged(levelIndex);
        }

        // ── 预设 ──

        public void RestorePreset()
        {
            if (ShowRestore) SelectedPresetIndex = _restorePresetIndex;
        }

        public void AddLevel()
        {
            if (IsAutomatic || IsLocked) return;
            if (!_plan.TryAddLevel()) return;
            EnforceAll();
            RefreshLevels();
            Revalidate();
            MarkModified();
            RaiseAll();
            OnPlanChanged(_plan.Count - 1);
            EditRequested?.Invoke(this, _plan.Count - 1);
        }

        public void RemoveLevel(int index)
        {
            if (IsAutomatic || IsLocked) return;
            if (_plan.Count <= LodPlan.MinLevels) return;
            if (index < 0 || index >= _plan.Count) return;
            _plan.Levels.RemoveAt(index);
            EnforceAll();
            RefreshLevels();
            Revalidate();
            MarkModified();
            RaiseAll();
            OnPlanChanged(-1);
        }

        // ── 编辑回调 ──

        internal void OnDistanceEdited(int index, string text)
        {
            if (IsAutomatic) return;
            if (index < 0 || index >= _plan.Levels.Count) return;
            string t = (text ?? "").Trim();
            if (t.Length == 0)
            {
                FlagInputError(index, true, "请输入切换距离");
                return;
            }
            double v;
            if (!double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ||
                double.IsNaN(v) || double.IsInfinity(v))
            {
                FlagInputError(index, true, "切换距离必须是有限数值");
                return;
            }
            bool correctedInput = _inputError != null;
            _inputError = null;
            v = Math.Max(1.0, Math.Min(10000.0, v));                 // 合法范围夹取（1..10000 HU）
            v = Math.Max(v, MinDistanceFor(index));                   // 必须比上一级大（至少 +1 HU）
            v = Math.Min(10000.0, v);
            if (Math.Abs(v - _plan.Levels[index].Distance) < 1e-9)
            {
                if (correctedInput) { Revalidate(); OnPlanChanged(index); }
                return;
            }
            _plan.Levels[index].Distance = v;
            CascadeAfter(index);                                      // 后续级别距离仍要保持递增
            Revalidate();
            MarkModified();
            RaiseDerivedAll();                                        // 不重写输入框文本（避免打字被抢焦点）
            Raise("SpecText");
            OnPlanChanged(index);
        }

        internal void OnRatioEdited(int index, string text)
        {
            if (IsAutomatic) return;
            if (index < 0 || index >= _plan.Levels.Count) return;
            string t = (text ?? "").Trim().Replace("%", "").Replace(',', '.');
            if (t.Length == 0)
            {
                FlagInputError(index, false, "请输入保留比例");
                return;
            }
            double v;
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ||
                double.IsNaN(v) || double.IsInfinity(v))
            {
                FlagInputError(index, false, "保留比例必须是有限数值");
                return;
            }
            bool correctedInput = _inputError != null;
            _inputError = null;
            double r = Math.Max(0.001, Math.Min(1.0, v / 100.0));     // 0.1% .. 100%
            if (index > 0) r = Math.Min(r, MaxRatioFor(index));       // 必须比上一级小（至少 -1 个百分点）
            r = Math.Max(0.001, r);
            if (Math.Abs(r - _plan.Levels[index].Ratio) < 1e-9)
            {
                if (correctedInput) { Revalidate(); OnPlanChanged(index); }
                return;
            }
            _plan.Levels[index].Ratio = r;
            CascadeAfter(index);                                      // 后续级别比例仍要保持递减
            Revalidate();
            MarkModified();
            RaiseDerivedAll();                                        // 比例条与百分比实时跟随
            Raise("SpecText");
            OnPlanChanged(index);
        }

        private void FlagInputError(int index, bool distance, string message)
        {
            _inputError = "第 " + (index + 1) + " 级 · " + message;
            _levels[index].SetInvalid(distance, !distance, _inputError);
            Raise(nameof(ValidationError));
            Raise(nameof(IsValid));
            OnPlanChanged(index);
        }

        /// <summary>失焦/回车时把文本规范成 plan 里的规范格式（例如 010 → 10），并兜底修正顺序。</summary>
        public void NormalizeLevelText(int index)
        {
            if (IsAutomatic) return;
            if (index < 0 || index >= _levels.Count) return;
            bool changed = EnforceAll();
            RefreshLevels();                 // 被自动强制改写的行在这里显示为强制后的值
            Revalidate();
            if (changed) MarkModified();
            Raise("SpecText");
            OnPlanChanged(index);
        }

        public void SetDistance(int index, double value)
        {
            if (IsAutomatic) return;
            if (index < 0 || index >= _plan.Levels.Count) return;
            double v = Math.Max(1.0, Math.Min(10000.0, Math.Round(value, 3)));
            v = Math.Min(10000.0, Math.Max(v, MinDistanceFor(index)));
            if (Math.Abs(v - _plan.Levels[index].Distance) < 1e-9) return;
            _plan.Levels[index].Distance = v;
            CascadeAfter(index);
            Revalidate(); MarkModified(); RefreshLevels(); Raise("SpecText");
            OnPlanChanged(index);
        }

        public void SetRatio(int index, double ratio)
        {
            if (IsAutomatic) return;
            if (index < 0 || index >= _plan.Levels.Count) return;
            double r = Math.Max(0.001, Math.Min(1.0, Math.Round(ratio, 4)));
            r = Math.Min(MaxRatioFor(index), r);
            if (Math.Abs(r - _plan.Levels[index].Ratio) < 1e-9) return;
            _plan.Levels[index].Ratio = r;
            CascadeAfter(index);
            Revalidate(); MarkModified(); RefreshLevels(); Raise("SpecText");
            OnPlanChanged(index);
        }

        /// <summary>本级距离下限：比上一级大 1 HU（第一级为 1）。</summary>
        internal double MinDistanceFor(int index)
        {
            return index <= 0 ? 1.0 : _plan.Levels[index - 1].Distance + 1.0;
        }

        /// <summary>本级比例上限：比上一级小 1 个百分点（第一级为 100%）。</summary>
        internal double MaxRatioFor(int index)
        {
            if (index <= 0) return 1.0;
            double prev = _plan.Levels[index - 1].Ratio;
            return Math.Max(0.001, prev - 0.01);
        }

        /// <summary>从 index 之后的级别开始，强制保持"距离递增 / 比例递减"。</summary>
        private bool CascadeAfter(int index)
        {
            bool changed = false;
            int n = _plan.Levels.Count;
            for (int j = Math.Max(1, index + 1); j < n; j++)
            {
                bool rowChanged = false;
                if (_plan.Levels[j].Distance <= _plan.Levels[j - 1].Distance)
                {
                    _plan.Levels[j].Distance = Math.Min(10000.0, _plan.Levels[j - 1].Distance + 1.0);
                    rowChanged = true;
                }
                double maxR = Math.Max(0.001, _plan.Levels[j - 1].Ratio - 0.01);
                if (_plan.Levels[j].Ratio >= _plan.Levels[j - 1].Ratio || _plan.Levels[j].Ratio > maxR)
                {
                    _plan.Levels[j].Ratio = maxR;
                    rowChanged = true;
                }
                if (rowChanged)
                {
                    changed = true;
                    if (j < _levels.Count) _levels[j].RefreshFromPlan();
                }
            }
            return changed;
        }

        private bool EnforceAll() { return CascadeAfter(0); }

        private void RaiseDerivedAll()
        {
            for (int i = 0; i < _levels.Count; i++) _levels[i].RaiseDerived();
        }

        private void RefreshLevels()
        {
            _inputError = null;
            while (_levels.Count < _plan.Levels.Count)
                _levels.Add(new LodLevelVm(this, _levels.Count));
            while (_levels.Count > _plan.Levels.Count)
                _levels.RemoveAt(_levels.Count - 1);
            for (int i = 0; i < _levels.Count; i++)
            {
                _levels[i].RefreshFromPlan();
                _levels[i].SetInvalid(false, false, "");
            }
            Raise("Count"); Raise("CanAddLevel"); Raise("SpecText");
        }

        /// <summary>重新校验并把错误定位到具体行/字段。</summary>
        public void Revalidate()
        {
            string err = _plan.Validate();
            _validationError = err;
            for (int i = 0; i < _levels.Count; i++) _levels[i].SetInvalid(false, false, "");

            if (!string.IsNullOrEmpty(err))
            {
                // 把错误定位到行：优先解析 "第 N 级"
                int level = ParseLevelNumber(err);
                for (int i = 0; i < _levels.Count; i++)
                {
                    if (i == level)
                    {
                        bool dist = err.IndexOf("距离", StringComparison.Ordinal) >= 0;
                        bool ratio = err.IndexOf("比例", StringComparison.Ordinal) >= 0;
                        _levels[i].SetInvalid(dist || (!dist && !ratio), ratio, err);
                        break;
                    }
                }
            }
            Raise("ValidationError"); Raise("IsValid");
        }

        private static int ParseLevelNumber(string err)
        {
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(err, "第\\s*(\\d+)\\s*级");
                if (m.Success)
                {
                    int n;
                    if (int.TryParse(m.Groups[1].Value, out n)) return n - 1;
                }
            }
            catch { }
            return -1;
        }

        private void MarkModified()
        {
            if (_selectedPresetIndex != PresetLibrary.CustomIndex)
            {
                _restorePresetIndex = _selectedPresetIndex;
                _selectedPresetIndex = PresetLibrary.CustomIndex;
                Raise("SelectedPresetIndex");
                Raise("PresetDisplay");
                Raise("ShowRestore");
            }
            if (_plan.Validate() == null) _customPlan = _plan.Clone();
        }

        private void OnPlanChanged(int level)
        {
            var h = PlanChanged;
            if (h != null) h(this, EventArgs.Empty);
            Raise("SpecText");
            Raise("CanAddLevel");
        }

        /// <summary>运行前校验：参数合法且至少一级；返回 null = 通过，否则中文原因。</summary>
        public string ValidateForRun()
        {
            if (IsAutomatic) return null;
            Revalidate();
            if (!IsValid) return ValidationError;
            if (_plan.Levels.Count < LodPlan.MinLevels) return "至少要设置 " + LodPlan.MinLevels + " 级 LOD。";
            return null;
        }
        /// <summary>给 RangeCard 用的方案副本</summary>
        public LodPlan ClonePlan() { return _plan.Clone(); }
    }
}
