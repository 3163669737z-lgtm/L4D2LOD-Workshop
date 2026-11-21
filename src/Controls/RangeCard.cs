using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LodStudio.Engine;

namespace LodStudio.Controls
{
    /// <summary>RangeCard 提交事件参数。</summary>
    public sealed class RangeCardChangedEventArgs : EventArgs
    {
        /// <summary>改动后的方案（副本，可直接使用）</summary>
        public Engine.LodPlan Plan;
        /// <summary>被改动的级别下标（-1 = 整体变化，例如外部 SetPlan）</summary>
        public int LevelIndex;
        public RangeCardChangedEventArgs(Engine.LodPlan plan, int levelIndex) { Plan = plan; LevelIndex = levelIndex; }
    }

    /// <summary>阶梯图上被拖动的把手种类（拖动 / 命中 / 预览测试共用）。</summary>
    internal enum RangeHandleKind
    {
        /// <summary>比例把手（上下拖动 → 保留比例）</summary>
        Ratio,
        /// <summary>距离把手（左右拖动 → 切换距离）</summary>
        Distance
    }

    /// <summary>
    /// 阶梯图纯逻辑：优秀刻度步长、拖动取整、跨级严格约束（不依赖 WPF，可被 --selftest 无界面校验）。
    /// 约束与 <see cref="LodPlan.Validate"/> 保持一致：距离严格递增（1 HU 步进）、比例严格递减（0.1% 步进）。
    /// </summary>
    internal static class RangeMath
    {
        /// <summary>距离取整步长（HU）</summary>
        internal const double DistanceStep = 1.0;
        /// <summary>比例取整步长（0.1%）</summary>
        internal const double RatioStep = 0.001;
        /// <summary>合法距离下限（与 LodPlan.MinDistance 一致；拖动时按 1 HU 取整后实际下限为 1）</summary>
        internal const double MinDistance = 0.1;
        /// <summary>合法距离上限（与 LodPlan.MaxDistance 一致）</summary>
        internal const double MaxDistance = 100000.0;

        /// <summary>"好看"的尾数序列：1/2/2.5/5 → 得到 5/10/20/25/50/100/200… 的刻度步长。</summary>
        private static readonly double[] NiceMantissas = { 1.0, 2.0, 2.5, 5.0 };

        internal static double Clamp(double v, double lo, double hi)
        {
            if (double.IsNaN(v)) return lo;
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        /// <summary>
        /// 横轴域 = max(方案里最大距离, 下限) × 1.25（规格 0..max(距离)×1.25）。空方案兜底 1.25，避免 0 宽域。
        /// </summary>
        internal static double DomainFor(double maxDistance, double floor)
        {
            double m = maxDistance;
            if (double.IsNaN(m) || double.IsInfinity(m) || m < 0) m = 0;
            double f = double.IsNaN(floor) || floor < 0 ? 0 : floor;
            double d = Math.Max(m, f) * 1.25;
            return d < 1e-6 ? 1.25 : d;
        }

        /// <summary>
        /// 选择刻度的"好看步长"：在 1/2/2.5/5 × 10^n 里取第一个 ≥ domain/targetTicks 的值，最小 1 HU。
        /// 例：domain 62.5、目标 7 格 → 10；domain 125000、目标 7 格 → 20000。
        /// </summary>
        internal static double NiceStep(double domain, int targetTicks)
        {
            if (double.IsNaN(domain) || double.IsInfinity(domain) || domain <= 0) return 1.0;
            int target = targetTicks < 2 ? 2 : targetTicks;
            double raw = domain / target;
            if (raw <= 0) return 1.0;
            double exp = Math.Floor(Math.Log10(raw));
            double pow = Math.Pow(10.0, exp);
            double mantissa = raw / pow;
            double best = 10.0;
            for (int i = 0; i < NiceMantissas.Length; i++)
            {
                if (NiceMantissas[i] >= mantissa - 1e-9) { best = NiceMantissas[i]; break; }
            }
            double step = best * pow;
            if (step < 1.0) step = 1.0;   // 1 HU 以下没有量测意义
            return step;
        }

        /// <summary>距离取整：1 HU 步进 + 夹在 LodPlan 合法范围内。</summary>
        internal static double SnapDistance(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return DistanceStep;
            double s = Math.Round(v / DistanceStep, MidpointRounding.AwayFromZero) * DistanceStep;
            return Clamp(s, DistanceStep, MaxDistance);
        }

        /// <summary>比例取整：0.1% 步进 + 夹在 LodPlan 合法范围内。</summary>
        internal static double SnapRatio(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return LodPlan.MinRatio;
            double s = Math.Round(v / RatioStep, MidpointRounding.AwayFromZero) * RatioStep;
            return Clamp(s, LodPlan.MinRatio, LodPlan.MaxRatio);
        }

        /// <summary>
        /// 把"原始比例"夹进该级的合法区间（严格小于上一级、严格大于下一级，步长 0.1%）。
        /// 返回 false = 该级两侧邻级太近，不存在合法值（保留原值，不移动）。
        /// </summary>
        internal static bool TryConstrainRatio(LodPlan plan, int level, double raw, out double value)
        {
            value = 0;
            if (plan == null || plan.Levels == null || level < 0 || level >= plan.Levels.Count) return false;
            double hi = LodPlan.MaxRatio;
            double lo = LodPlan.MinRatio;
            if (level > 0) hi = Math.Min(hi, plan.Levels[level - 1].Ratio - RatioStep);
            if (level < plan.Levels.Count - 1) lo = Math.Max(lo, plan.Levels[level + 1].Ratio + RatioStep);
            hi = Math.Round(hi / RatioStep) * RatioStep;
            lo = Math.Round(lo / RatioStep) * RatioStep;
            if (lo > hi + 1e-12) return false;                       // 邻级挤在一起：不动
            double s = SnapRatio(raw);
            s = Math.Round(s / RatioStep) * RatioStep;
            value = Clamp(s, lo, hi);
            return true;
        }

        /// <summary>
        /// 把"原始距离"夹进该级的合法区间（严格大于上一级、严格小于下一级，步长 1 HU）。
        /// 返回 false = 邻级之间已没有 1 HU 空位（保留原值，不移动）。
        /// </summary>
        internal static bool TryConstrainDistance(LodPlan plan, int level, double raw, out double value)
        {
            value = 0;
            if (plan == null || plan.Levels == null || level < 0 || level >= plan.Levels.Count) return false;
            double lo = DistanceStep;
            double hi = MaxDistance;
            if (level > 0) lo = Math.Max(lo, plan.Levels[level - 1].Distance + DistanceStep);
            if (level < plan.Levels.Count - 1) hi = Math.Min(hi, plan.Levels[level + 1].Distance - DistanceStep);
            lo = Math.Ceiling(lo / DistanceStep - 1e-9) * DistanceStep;
            hi = Math.Floor(hi / DistanceStep + 1e-9) * DistanceStep;
            if (lo > hi + 1e-9) return false;
            double s = SnapDistance(raw);
            value = Clamp(s, lo, hi);
            return true;
        }

        /// <summary>图内百分比文本（固定 1 位小数，量具读数的一致格式）：0.653 → "65.3%"。</summary>
        internal static string FormatPct(double ratio)
        {
            double pct = Clamp(ratio, 0, 1) * 100.0;
            return pct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>图内距离文本：整数不带小数点，非整数最多 3 位：15 → "15"，10.5 → "10.5"。</summary>
        internal static string FormatDistance(double distance)
        {
            if (Math.Abs(distance - Math.Round(distance)) < 1e-9)
                return ((long)Math.Round(distance)).ToString(CultureInfo.InvariantCulture);
            return distance.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>刻度数字：步长 ≥1 时取整；小于 1 时按步长保留小数位。</summary>
        internal static string FormatTick(double value, double step)
        {
            if (step >= 1.0 - 1e-9) return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
            int digits = step >= 0.1 - 1e-9 ? 1 : step >= 0.01 - 1e-9 ? 2 : 3;
            return value.ToString("0." + new string('0', digits), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 距离—细节阶梯图：本程序的视觉核心（"测距仪"语言），可拖拽改参数。
    ///
    /// 契约（实现者不要改签名）：
    ///  - SetPlan(plan)：设置方案（内部克隆，不改调用方对象）；
    ///  - Changed：松手提交后触发一次（参数为改动后的完整方案副本 + 级别下标）；
    ///  - DragPreview：拖动过程中触发（同一时刻可能高频），供界面同步行数值；
    ///  - EditRequested：双击把手，参数为级别下标（界面把焦点移到该行数字框）；
    ///  - SetValidationError：外部校验错误文本（非空时图表右上角标红并禁止开始拖动，仍可 hover/双击）。
    ///
    /// 坐标：横轴 = 切换距离（HU，域 0..max(距离)×1.25）；纵轴 = 保留比例（0..100%）。
    /// 视觉规格见 docs/00 §5：只有"当前 hover/拖动/回弹闪红"的级别使用信号色/错误色，其余全部中性令牌。
    /// 所有颜色在 OnRender 里用 TryFindResource 现取（跨主题不缓存 Brush），画笔按资源实例失效重建。
    /// </summary>
    public sealed class RangeCard : FrameworkElement
    {
        // ── 几何常量（规格：内边距 left 26 / right 10 / top 20 / bottom 20） ──
        private const double PadLeft = 26.0;
        private const double PadRight = 10.0;
        private const double PadTop = 20.0;
        private const double PadBottom = 20.0;
        private const double RatioHandleIdle = 7.0;      // 比例把手 7×7
        private const double RatioHandleHover = 9.0;     // hover/拖动 9×9
        private const double DistanceHandleIdle = 6.0;   // 距离把手 6×6
        private const double DistanceHandleHover = 8.0;
        private const double HandleHitSlop = 4.0;        // 命中区外扩 4px
        private const double FlashMs = 400.0;            // 回弹后把手/浮层标红时长
        private const double DomainFloor = 10.0;         // 还没渲染过时的兜底域下限（正常路径用 DomainFor）

        private static readonly Typeface FaceLatin = new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Typeface FaceLatinSemi = new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private static readonly Typeface FaceCjk = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // ── 方案状态 ──
        private LodPlan _plan = new LodPlan();   // 已提交方案（外部输入 + 上次提交）
        private LodPlan _work = new LodPlan();   // 工作副本（拖动中即时反映，CurrentPlan 返回它）
        private string _validationError;
        private double _domainFloor;             // 域下限来源：SetPlan 时的最大距离（拖动缩小距离时域不抖）

        // ── hover ──
        private bool _hasHover;
        private int _hoverLevel = -1;
        private RangeHandleKind _hoverKind;

        // ── 拖动状态机：Idle → Dragging → (提交 / 回弹→Flash 400ms) → Idle ──
        private bool _dragging;
        private int _dragLevel;
        private RangeHandleKind _dragKind;
        private double _dragOrigRatio, _dragOrigDistance;   // 拖动前值（回弹用）
        private double _dragCurRatio, _dragCurDistance;     // 当前值（提交/浮层用）
        private double _dragDomain;                         // 拖动中冻结的横轴域（避免拖动改变域造成反馈抖动）
        private double _dragPlotLeft, _dragPlotTop, _dragPlotW, _dragPlotH;
        private bool _finishing;                            // 防止 LostMouseCapture 重入
        private bool _flash;                                // 回弹后的 400ms 红闪
        private int _flashLevel;
        private RangeHandleKind _flashKind;
        private DispatcherTimer _flashTimer;

        // ── 渲染缓存（主题实例变化时整体失效） ──
        private readonly Dictionary<string, Pen> _penCache = new Dictionary<string, Pen>(StringComparer.Ordinal);
        private readonly Dictionary<int, CachedText> _ratioTextCache = new Dictionary<int, CachedText>();
        private Brush _themeStamp;
        private string _tickCacheKey;
        private readonly List<CachedText> _tickCache = new List<CachedText>();
        private double _dpi = 1.0;

        // ── 布局缓存（OnRender 计算，命中测试/浮层共用），只 Clear/Add 不换实例 ──
        private readonly List<LevelBox> _boxes = new List<LevelBox>();
        private readonly List<Point> _ratioHandles = new List<Point>();
        private readonly List<Point> _distanceHandles = new List<Point>();
        private double _plotLeft, _plotTop, _plotRight, _plotBottom, _domain;
        private Rect _errorTagRect = Rect.Empty;   // 右上角校验错误标记占位（百分比标签让位用）

        private struct LevelBox
        {
            public double X0, X1, YTop, YBottom;
        }

        private sealed class CachedText
        {
            public double Value;
            public Brush Brush;
            public FormattedText Text;
        }

        public event EventHandler<RangeCardChangedEventArgs> Changed;
        public event EventHandler<RangeCardChangedEventArgs> DragPreview;
        public event EventHandler<int> EditRequested;

        /// <summary>当前工作方案（拖动中包含未提交的预览值）。</summary>
        public Engine.LodPlan CurrentPlan { get { return _work; } }

        /// <summary>是否正在拖动把手（视图用来避免拖动时被外部 SetPlan 打断；回弹红闪期间为 false）。</summary>
        public bool IsDragging { get { return _dragging; } }

        /// <summary>外部设置的校验错误文本（非空时右上角显示并禁止开始拖动）。</summary>
        public string ValidationError { get { return _validationError; } }

        public RangeCard()
        {
            FocusVisualStyle = null;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            MinHeight = 96;
            Height = 120;
            Focusable = false;                 // 键盘由级别表里的数字框负责；图内不接收焦点
            Cursor = Cursors.Arrow;
            IsHitTestVisible = true;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // ══════════════════════════ 公开 API ══════════════════════════

        /// <summary>设置方案：内部克隆，不改调用方对象；拖动中调用会先放弃当前拖动（不提交）。</summary>
        public void SetPlan(Engine.LodPlan plan)
        {
            AbortDrag();
            StopFlash();
            _plan = plan != null && plan.Levels != null ? plan.Clone() : new LodPlan();
            _work = _plan.Clone();
            _domainFloor = MaxDistance(_plan);
            _hasHover = false;
            _hoverLevel = -1;
            InvalidateMeasure();
            InvalidateVisual();
        }

        /// <summary>设置外部校验错误（null/空 = 清除）。非空时禁止开始新的拖动，但仍可 hover 与双击进数字框。</summary>
        public void SetValidationError(string message)
        {
            _validationError = string.IsNullOrEmpty(message) ? null : message;
            InvalidateVisual();
        }

        // ══════════════════════════ 主题 / 生命周期 ══════════════════════════

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Services.ThemeService.ThemeChanged -= OnThemeChanged;
            Services.ThemeService.ThemeChanged += OnThemeChanged;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Services.ThemeService.ThemeChanged -= OnThemeChanged;
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            _penCache.Clear();
            _ratioTextCache.Clear();
            _tickCacheKey = null;
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsNaN(availableSize.Width) || double.IsInfinity(availableSize.Width)
                ? 340.0
                : Math.Min(availableSize.Width, 420.0);
            double h = double.IsNaN(Height) ? 120.0 : Height;
            return new Size(Math.Max(40.0, w), h);
        }

        // ══════════════════════════ 绘制 ══════════════════════════

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 30) return;

            Brush panelBrush = Res("Brush.Panel", Brushes.White);
            Brush chartBand = Res("Brush.ChartBand", Brushes.LightGray);
            Brush chartBandActive = Res("Brush.ChartBandActive", Brushes.LightGray);
            Brush chartGrid = Res("Brush.ChartGrid", Brushes.Gray);
            Brush tickBrush = Res("Brush.Tick", Brushes.Gray);
            Brush inkSoft = Res("Brush.InkSoft", Brushes.DimGray);
            Brush inkFaint = Res("Brush.InkFaint", Brushes.Gray);
            Brush signal = Res("Brush.Signal", Brushes.OrangeRed);
            Brush err = Res("Brush.Err", Brushes.Red);
            Brush lineStrong = Res("Brush.LineStrong", Brushes.Gray);
            Brush lineBrush = Res("Brush.Line", Brushes.Gray);
            Brush hardShadow = Res("Brush.HardShadow", Brushes.Transparent);

            // 主题签名：SolidColorBrush 资源实例变了 → 画笔/文本缓存整体失效（切换主题后重绘正确）
            if (!ReferenceEquals(signal, _themeStamp))
            {
                _themeStamp = signal;
                _penCache.Clear();
                _ratioTextCache.Clear();
                _tickCacheKey = null;
            }
            _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            dc.DrawRectangle(panelBrush, null, new Rect(0, 0, w, h));

            double left = PadLeft;
            double right = Math.Max(left + 24, w - PadRight);
            double top = PadTop;
            double bottom = Math.Max(top + 24, h - PadBottom);
            double plotW = right - left;
            double plotH = bottom - top;

            // 横轴域：0..max(距离)×1.25。拖动中冻结，避免"边拖边缩放"的反馈抖动。
            // 空方案给一个"好看的量程"（50 HU 档），否则刻度会退化成 0..1 的怪读数。
            double domain = _work.Levels != null && _work.Levels.Count == 0
                ? RangeMath.DomainFor(0, 50)
                : RangeMath.DomainFor(MaxDistance(_work), _domainFloor);
            if (_dragging && _dragDomain > 0) domain = _dragDomain;

            _plotLeft = left;
            _plotTop = top;
            _plotRight = right;
            _plotBottom = bottom;
            _domain = domain;
            BuildLayout(domain);

            // ① 0/50/100% 三条水平发丝线 + 左侧微标
            Pen gridPen = PenOf("Brush.ChartGrid", 1.0, false, chartGrid);
            for (int i = 0; i <= 2; i++)
            {
                double ratio = i * 0.5;
                double y = SnapPx(top + plotH * (1.0 - ratio)) + 0.5;
                dc.DrawLine(gridPen, new Point(left, y), new Point(right, y));
                string label = (i * 50).ToString(CultureInfo.InvariantCulture);
                FormattedText ft = Text(label, FaceLatin, 10.0, inkFaint);
                double ly = y - ft.Height / 2.0;
                if (ly < 0) ly = 0;
                // 右对齐留 7px 余量：左上角的 100% 微标不能贴到比例把手（把手半宽 3.5 + 3）
                dc.DrawText(ft, new Point(Math.Max(0, left - 7 - ft.Width), ly));
            }

            // ② 每个级别距离处的常驻竖虚线（1px，Dash 3,3）
            Pen levelDash = PenOf("Brush.ChartGrid|dash", 1.0, true, chartGrid);
            for (int i = 0; i < _distanceHandles.Count; i++)
            {
                double x = SnapPx(_distanceHandles[i].X) + 0.5;
                if (x < left || x > right) continue;
                dc.DrawLine(levelDash, new Point(x, top), new Point(x, bottom));
            }

            // ③ 阶梯段（填充 ChartBand）+ 段顶 1px 发丝线（当前级别 = ChartBandActive + 2px 信号线）
            Pen bandTopPen = PenOf("Brush.ChartGrid", 1.0, false, chartGrid);
            for (int i = 0; i < _boxes.Count; i++)
            {
                LevelBox box = _boxes[i];
                double x0 = SnapPx(box.X0), x1 = SnapPx(box.X1);
                if (x1 - x0 < 0.5) continue;
                double yA = SnapPx(Math.Min(box.YTop, box.YBottom));
                double yB = SnapPx(Math.Max(box.YTop, box.YBottom));
                bool active = IsLevelActive(i);
                dc.DrawRectangle(active ? chartBandActive : chartBand, null, new Rect(x0, yA, x1 - x0, Math.Max(1.0, yB - yA)));
                if (active)
                {
                    // 2px 信号色线（用填充矩形保证像素对齐清晰，避免 2px 描边半像素发虚）
                    dc.DrawRectangle(signal, null, new Rect(x0, SnapPx(box.YTop) - 1.0, x1 - x0, 2.0));
                }
                else
                {
                    dc.DrawLine(bandTopPen, new Point(x0, SnapPx(box.YTop) + 0.5), new Point(x1, SnapPx(box.YTop) + 0.5));
                }
            }

            // ④ 百分比标签（每段上方左侧；放不下/会与前一个标签相压就跳过）
            MeasureValidationTag(w, err);
            DrawRatioLabels(dc, inkSoft, w);

            // ⑤ 底部距离刻度（短线 + 微标数字，Bahnschrift 10.5）
            DrawDistanceTicks(dc, domain, plotW, tickBrush, inkFaint, lineBrush);

            // ⑥ 空方案提示
            if (_boxes.Count == 0)
            {
                FormattedText empty = Text("尚无 LOD 级别", FaceCjk, 11.5, inkFaint);
                double ex = left + (plotW - empty.Width) / 2.0;
                double ey = top + (plotH - empty.Height) / 2.0;
                dc.DrawRectangle(panelBrush, null, new Rect(ex - 8, ey - 4, empty.Width + 16, empty.Height + 8));
                dc.DrawText(empty, new Point(ex, ey));
            }

            // ⑦ 把手（默认 InkSoft/InkFaint，hover/拖动 = 信号色，回弹闪红 = Err）
            DrawHandles(dc, inkSoft, inkFaint, signal, err);

            // ⑧ 拖动浮层 + 辅助虚线（回弹红闪时同样显示 400ms）
            if (_dragging || _flash) DrawDragOverlay(dc, w, h, panelBrush, lineStrong, hardShadow, inkFaint, signal, err);

            // ⑨ 外部校验错误标记（右上角，过长省略；带 Panel 底，避免与百分比标签叠字）
            DrawValidationTag(dc, panelBrush, err);
        }

        /// <summary>计算本帧布局：每个级别的台阶矩形、比例把手、距离把手位置。</summary>
        private void BuildLayout(double domain)
        {
            _boxes.Clear();
            _ratioHandles.Clear();
            _distanceHandles.Clear();
            double plotW = Math.Max(1.0, _plotRight - _plotLeft);
            double plotH = Math.Max(1.0, _plotBottom - _plotTop);
            double prevRatio = 1.0;
            double prevX = _plotLeft;
            int count = _work.Levels == null ? 0 : _work.Levels.Count;
            for (int i = 0; i < count; i++)
            {
                LodLevel lv = _work.Levels[i];
                double ratio = RangeMath.Clamp(lv.Ratio, 0.0, 1.0);
                double distance = RangeMath.Clamp(lv.Distance, 0.0, domain);
                double x1 = _plotLeft + plotW * (distance / domain);
                double yTop = _plotTop + plotH * (1.0 - ratio);
                double yBottom = _plotTop + plotH * (1.0 - prevRatio);
                _boxes.Add(new LevelBox { X0 = prevX, X1 = x1, YTop = yTop, YBottom = yBottom });
                _ratioHandles.Add(new Point(prevX, yTop));      // 比例把手：本级台阶左端、本级比例高度
                _distanceHandles.Add(new Point(x1, _plotBottom)); // 距离把手：基线 y、本级距离 x
                prevX = x1;
                prevRatio = ratio;
            }
        }

        private void DrawRatioLabels(DrawingContext dc, Brush inkSoft, double w)
        {
            List<Rect> placed = null;
            for (int i = 0; i < _boxes.Count; i++)
            {
                LevelBox box = _boxes[i];
                double segW = box.X1 - box.X0;
                if (segW < 12) continue;
                if (_work.Levels == null || i >= _work.Levels.Count) continue;
                CachedText cached = RatioText(i, _work.Levels[i].Ratio, inkSoft);
                FormattedText ft = cached.Text;
                // 优先贴在台阶左上方的空白区（不要求段宽放下：竖直方向错开 + placed 碰撞检查兜住相邻标签）
                double x = SnapPx(box.X0) + 3.0;
                double y = SnapPx(box.YTop) - ft.Height - 2.0;
                if (y < 1)
                {
                    // 顶到图外（接近 100% 的级）：画在色带内左上
                    if ((box.YBottom - box.YTop) >= ft.Height + 8.0) y = SnapPx(box.YTop) + 3.0;
                    else continue;
                }
                var rect = new Rect(x - 1, y - 1, ft.Width + 2, ft.Height + 2);
                if (rect.Top < 1 || rect.Right > w - PadRight + 2) continue;
                if (!_errorTagRect.IsEmpty && rect.IntersectsWith(_errorTagRect)) continue;   // 让位给右上角错误标记
                if (placed != null)
                {
                    bool hit = false;
                    for (int k = 0; k < placed.Count; k++)
                    {
                        if (placed[k].IntersectsWith(rect)) { hit = true; break; }
                    }
                    if (hit) continue;                                   // 与已放标签相压 → 跳过
                }
                if (placed == null) placed = new List<Rect>();
                placed.Add(rect);
                dc.DrawText(ft, new Point(x, y));
            }
        }

        private void DrawDistanceTicks(DrawingContext dc, double domain, double plotW, Brush tickBrush, Brush inkFaint, Brush lineBrush)
        {
            int target = Math.Max(4, (int)(plotW / 46.0));
            double step = RangeMath.NiceStep(domain, target);
            string key = domain.ToString("R", CultureInfo.InvariantCulture) + "|" + plotW.ToString("R", CultureInfo.InvariantCulture) +
                         "|" + (inkFaint == null ? 0 : inkFaint.GetHashCode()) + "|" + (tickBrush == null ? 0 : tickBrush.GetHashCode());
            if (_tickCacheKey != key)
            {
                _tickCacheKey = key;
                _tickCache.Clear();
                int guard = 0;
                for (int k = 0; k * step <= domain + 1e-9 && guard < 200; k++, guard++)
                {
                    double value = k * step;
                    string label = RangeMath.FormatTick(value, step);
                    FormattedText ft = Text(label, FaceLatin, 10.5, inkFaint);
                    _tickCache.Add(new CachedText { Value = value, Text = ft });
                }
            }
            Pen tickPen = PenOf("Brush.Tick", 1.0, false, tickBrush);
            Pen basePen = PenOf("Brush.Line", 1.0, false, lineBrush);
            double lastRight = double.NegativeInfinity;
            for (int i = 0; i < _tickCache.Count; i++)
            {
                CachedText t = _tickCache[i];
                double x = _plotLeft + plotW * (t.Value / domain);
                double xr = SnapPx(x) + 0.5;
                if (xr < _plotLeft - 1 || xr > _plotRight + 1) continue;
                dc.DrawLine(tickPen, new Point(xr, _plotBottom + 0.5), new Point(xr, _plotBottom + 3.5));
                FormattedText ft = t.Text;
                // 0 这个读数由左侧纵轴微标负责，横轴不再重复（否则左下角会出现两个 "0" 挤在一起）
                if (t.Value > 1e-9)
                {
                    double lx = x - ft.Width / 2.0;
                    if (lx < 1 || lx + ft.Width > ActualWidth - PadRight + 2) continue;
                    if (lx < lastRight + 6) continue;                    // 放不下就跳过，绝不压字
                    lastRight = lx + ft.Width;
                    dc.DrawText(ft, new Point(lx, _plotBottom + 4.0));
                }
            }
            // 基线：0% 发丝线的承托（中性 Line，不做装饰性加粗）
            dc.DrawLine(basePen, new Point(_plotLeft, SnapPx(_plotBottom) + 0.5), new Point(_plotRight, SnapPx(_plotBottom) + 0.5));
        }

        private void DrawHandles(DrawingContext dc, Brush inkSoft, Brush inkFaint, Brush signal, Brush err)
        {
            for (int i = 0; i < _boxes.Count; i++)
            {
                // 比例把手：7×7（hover 9×9）
                bool hoverRatio = _hasHover && !_dragging && _hoverLevel == i && _hoverKind == RangeHandleKind.Ratio;
                bool dragRatio = _dragging && _dragLevel == i && _dragKind == RangeHandleKind.Ratio;
                bool flashRatio = _flash && _flashLevel == i && _flashKind == RangeHandleKind.Ratio;
                double size = (hoverRatio || dragRatio) ? RatioHandleHover : RatioHandleIdle;
                Brush fill = flashRatio ? err : (dragRatio || hoverRatio) ? signal : inkSoft;
                Point c = _ratioHandles[i];
                dc.DrawRectangle(fill, null, new Rect(SnapPx(c.X - size / 2.0), SnapPx(c.Y - size / 2.0), size, size));

                // 距离把手：6×6（hover 8×8），画在基线上
                bool hoverDist = _hasHover && !_dragging && _hoverLevel == i && _hoverKind == RangeHandleKind.Distance;
                bool dragDist = _dragging && _dragLevel == i && _dragKind == RangeHandleKind.Distance;
                bool flashDist = _flash && _flashLevel == i && _flashKind == RangeHandleKind.Distance;
                double dsize = (hoverDist || dragDist) ? DistanceHandleHover : DistanceHandleIdle;
                Brush dfill = flashDist ? err : (dragDist || hoverDist) ? signal : inkFaint;
                Point dc0 = _distanceHandles[i];
                dc.DrawRectangle(dfill, null, new Rect(SnapPx(dc0.X - dsize / 2.0), SnapPx(dc0.Y - dsize / 2.0), dsize, dsize));
            }
        }

        private void DrawDragOverlay(DrawingContext dc, double w, double h, Brush panelBrush, Brush lineStrong,
                                     Brush hardShadow, Brush inkFaint, Brush signal, Brush err)
        {
            int level = _dragging ? _dragLevel : _flashLevel;
            RangeHandleKind kind = _dragging ? _dragKind : _flashKind;
            if (level < 0 || level >= _boxes.Count) return;
            bool bad = _flash && !_dragging;
            Brush accent = bad ? err : signal;

            double plotW = Math.Max(1.0, _plotRight - _plotLeft);
            double plotH = Math.Max(1.0, _plotBottom - _plotTop);
            double ratio = RangeMath.Clamp(_work.Levels[level].Ratio, 0.0, 1.0);
            double distance = RangeMath.Clamp(_work.Levels[level].Distance, 0.0, _domain > 0 ? _domain : 1.0);
            double hx = _plotLeft + plotW * (distance / Math.Max(1e-6, _domain));
            double hy = _plotTop + plotH * (1.0 - ratio);

            // 辅助虚线：比例拖动 = 水平线；距离拖动 = 竖线
            Pen guide = PenOf(bad ? "Brush.Err|dash" : "Brush.Signal|dash", 1.0, true, accent);
            if (kind == RangeHandleKind.Ratio)
            {
                double y = SnapPx(hy) + 0.5;
                dc.DrawLine(guide, new Point(_plotLeft, y), new Point(_plotRight, y));
            }
            else
            {
                double x = SnapPx(hx) + 0.5;
                dc.DrawLine(guide, new Point(x, _plotTop), new Point(x, _plotBottom));
            }

            // 浮层：两行读数（本级的距离 / 比例），Panel 底 + 1px 边 + 右下 3px 硬偏移（不用模糊阴影）
            // 回弹红闪时整块读数都用 Err 色（"把手/浮层标红"）
            FormattedText t1 = Text(RangeMath.FormatDistance(_work.Levels[level].Distance) + " HU", FaceLatinSemi, 11.5,
                                    bad ? err : (kind == RangeHandleKind.Distance ? accent : inkFaint));
            FormattedText t2 = Text(RangeMath.FormatPct(_work.Levels[level].Ratio), FaceLatinSemi, 11.5,
                                    bad ? err : (kind == RangeHandleKind.Ratio ? accent : inkFaint));
            double boxW = Math.Max(t1.Width, t2.Width) + 16;
            double boxH = t1.Height + t2.Height + 9;
            double bx, by;
            if (kind == RangeHandleKind.Ratio)
            {
                bx = hx + 12;
                by = hy - boxH - 6;
                if (bx + boxW > w - 3) bx = hx - boxW - 12;
            }
            else
            {
                bx = hx - boxW / 2.0;
                by = _plotBottom - boxH - 14;
            }
            bx = RangeMath.Clamp(bx, 3, Math.Max(3, w - boxW - 5));
            by = RangeMath.Clamp(by, 3, Math.Max(3, h - boxH - 5));

            dc.DrawRectangle(hardShadow, null, new Rect(bx + 3, by + 3, boxW, boxH));
            dc.DrawRectangle(panelBrush, null, new Rect(bx, by, boxW, boxH));
            dc.DrawRectangle(null, PenOf(bad ? "Brush.Err" : "Brush.LineStrong", 1.0, false, bad ? err : lineStrong),
                             new Rect(bx + 0.5, by + 0.5, Math.Max(0, boxW - 1), Math.Max(0, boxH - 1)));
            dc.DrawText(t1, new Point(bx + 8, by + 4));
            dc.DrawText(t2, new Point(bx + 8, by + 4 + t1.Height + 1));
        }

        /// <summary>先量出右上角错误标记的占位矩形，让百分比标签避开它（避免叠字）。</summary>
        private void MeasureValidationTag(double w, Brush err)
        {
            _errorTagRect = Rect.Empty;
            if (string.IsNullOrEmpty(_validationError)) return;
            double maxW = Math.Max(60, w * 0.62);
            FormattedText ft = FitSingleLine(_validationError.Trim(), FaceCjk, 10.5, err, maxW);
            double x = w - PadRight - ft.Width;
            // 高度压在 100% 发丝线之上（top padding 20 里），不压线
            _errorTagRect = new Rect(x - 4.5, 1.0, ft.Width + 9, ft.Height + 3);
        }

        private void DrawValidationTag(DrawingContext dc, Brush panelBrush, Brush err)
        {
            if (_errorTagRect.IsEmpty) return;
            FormattedText ft = FitSingleLine(_validationError.Trim(), FaceCjk, 10.5, err, Math.Max(60, ActualWidth * 0.62));
            Rect r = _errorTagRect;
            dc.DrawRectangle(panelBrush, null, new Rect(r.X + 0.5, r.Y + 1, r.Width - 1, r.Height - 2));
            dc.DrawRectangle(null, PenOf("Brush.Err", 1.0, false, err), r);
            dc.DrawText(ft, new Point(r.X + 4, r.Y + 2.5));
        }

        // ══════════════════════════ 命中测试 / 光标 ══════════════════════════

        private bool HitTestHandle(Point p, out int level, out RangeHandleKind kind)
        {
            level = -1;
            kind = RangeHandleKind.Ratio;
            double best = double.MaxValue;
            for (int i = 0; i < _ratioHandles.Count; i++)
            {
                double r = RatioHandleIdle / 2.0 + HandleHitSlop;
                double d = Dist2(p, _ratioHandles[i]);
                if (d <= r * r && d < best) { best = d; level = i; kind = RangeHandleKind.Ratio; }
            }
            for (int i = 0; i < _distanceHandles.Count; i++)
            {
                double r = DistanceHandleIdle / 2.0 + HandleHitSlop;
                double d = Dist2(p, _distanceHandles[i]);
                if (d <= r * r && d < best) { best = d; level = i; kind = RangeHandleKind.Distance; }
            }
            return level >= 0;
        }

        private static double Dist2(Point a, Point b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        private void UpdateCursor()
        {
            if (_dragging)
            {
                Cursor = _dragKind == RangeHandleKind.Ratio ? Cursors.SizeNS : Cursors.SizeWE;
                return;
            }
            if (_hasHover && _hoverLevel >= 0)
            {
                Cursor = _hoverKind == RangeHandleKind.Ratio ? Cursors.SizeNS : Cursors.SizeWE;
                return;
            }
            Cursor = Cursors.Arrow;
        }

        private void UpdateHoverFromMouse()
        {
            Point p;
            try { p = Mouse.GetPosition(this); }
            catch { p = new Point(double.NaN, double.NaN); }
            if (double.IsNaN(p.X) || double.IsNaN(p.Y) || !IsMouseOver)
            {
                _hasHover = false;
                _hoverLevel = -1;
                UpdateCursor();
                return;
            }
            int level;
            RangeHandleKind kind;
            bool hit = HitTestHandle(p, out level, out kind);
            _hasHover = hit;
            _hoverLevel = hit ? level : -1;
            if (hit) _hoverKind = kind;
            UpdateCursor();
        }

        // ══════════════════════════ 鼠标交互 ══════════════════════════

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                UpdateDrag(e.GetPosition(this));
                e.Handled = true;
                return;
            }
            Point p = e.GetPosition(this);
            int level;
            RangeHandleKind kind;
            bool hit = HitTestHandle(p, out level, out kind);
            if (hit != _hasHover || (hit && (level != _hoverLevel || kind != _hoverKind)))
            {
                _hasHover = hit;
                _hoverLevel = hit ? level : -1;
                if (hit) _hoverKind = kind;
                UpdateCursor();
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_dragging) return;
            if (_hasHover)
            {
                _hasHover = false;
                _hoverLevel = -1;
                Cursor = Cursors.Arrow;
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Point p = e.GetPosition(this);
            int level;
            RangeHandleKind kind;
            if (!HitTestHandle(p, out level, out kind)) return;
            e.Handled = true;

            // 双击把手 = 让界面把焦点移到该行数字框（即使有校验错误也允许，用户正是要去改它）
            if (e.ClickCount >= 2)
            {
                var edit = EditRequested;
                if (edit != null) edit(this, level);
                return;
            }
            if (!string.IsNullOrEmpty(_validationError)) return;      // 校验错误时禁止开始新的拖动
            if (level >= _work.Levels.Count) return;
            BeginDrag(level, kind);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_dragging) return;
            e.Handled = true;
            FinishDrag(true);
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (_finishing) return;
            if (_dragging) FinishDrag(true);                          // 丢捕获也要收尾（按松手处理）
        }

        private void BeginDrag(int level, RangeHandleKind kind)
        {
            StopFlash();
            _dragging = true;
            _dragLevel = level;
            _dragKind = kind;
            _dragOrigRatio = _work.Levels[level].Ratio;
            _dragOrigDistance = _work.Levels[level].Distance;
            _dragCurRatio = _dragOrigRatio;
            _dragCurDistance = _dragOrigDistance;
            _dragDomain = _domain > 0 ? _domain : DomainFloor * 1.25;
            _dragPlotLeft = _plotLeft;
            _dragPlotTop = _plotTop;
            _dragPlotW = Math.Max(1.0, _plotRight - _plotLeft);
            _dragPlotH = Math.Max(1.0, _plotBottom - _plotTop);
            _hasHover = true;
            _hoverLevel = level;
            _hoverKind = kind;
            try { Mouse.Capture(this, CaptureMode.Element); } catch { }
            UpdateCursor();
            InvalidateVisual();
        }

        private void UpdateDrag(Point p)
        {
            if (!_dragging) return;
            bool changed = false;
            if (_dragKind == RangeHandleKind.Ratio)
            {
                double raw = 1.0 - (p.Y - _dragPlotTop) / _dragPlotH;   // 向上 = 比例变大
                double v;
                if (RangeMath.TryConstrainRatio(_work, _dragLevel, raw, out v) && Math.Abs(v - _dragCurRatio) > 1e-9)
                {
                    _work.Levels[_dragLevel].Ratio = v;
                    _dragCurRatio = v;
                    changed = true;
                }
            }
            else
            {
                double raw = (p.X - _dragPlotLeft) / _dragPlotW * _dragDomain;
                double v;
                if (RangeMath.TryConstrainDistance(_work, _dragLevel, raw, out v) && Math.Abs(v - _dragCurDistance) > 1e-9)
                {
                    _work.Levels[_dragLevel].Distance = v;
                    _dragCurDistance = v;
                    changed = true;
                }
            }
            if (!changed) return;
            InvalidateVisual();                                       // 拖动中只重绘，不重建任何对象/子元素
            var preview = DragPreview;
            if (preview != null) preview(this, new RangeCardChangedEventArgs(_work.Clone(), _dragLevel));
        }

        /// <summary>收尾：commit=true 表示按松手处理（合法则提交 Changed）；false 表示放弃。</summary>
        private void FinishDrag(bool commit)
        {
            if (!_dragging) return;
            int level = _dragLevel;
            RangeHandleKind kind = _dragKind;
            bool changed = Math.Abs(_dragCurRatio - _dragOrigRatio) > 1e-9 ||
                           Math.Abs(_dragCurDistance - _dragOrigDistance) > 1e-9;
            _dragging = false;
            ReleaseCapture();

            if (!changed)
            {
                UpdateHoverFromMouse();
                InvalidateVisual();
                return;
            }

            bool legal = commit && string.IsNullOrEmpty(_validationError) && _work.Validate() == null;
            if (legal)
            {
                _plan = _work.Clone();
                _domainFloor = MaxDistance(_plan);
                var handler = Changed;
                if (handler != null) handler(this, new RangeCardChangedEventArgs(_plan.Clone(), level));
            }
            else
            {
                // 回弹：恢复拖动前的值，不触发 Changed，把手/浮层标红 400ms
                if (level < _work.Levels.Count)
                {
                    _work.Levels[level].Ratio = _dragOrigRatio;
                    _work.Levels[level].Distance = _dragOrigDistance;
                }
                StartFlash(level, kind);
            }
            UpdateHoverFromMouse();
            InvalidateVisual();
        }

        private void AbortDrag()
        {
            if (!_dragging) return;
            int level = _dragLevel;
            _dragging = false;
            ReleaseCapture();
            if (level >= 0 && _work.Levels != null && level < _work.Levels.Count)
            {
                _work.Levels[level].Ratio = _dragOrigRatio;
                _work.Levels[level].Distance = _dragOrigDistance;
            }
        }

        private void ReleaseCapture()
        {
            _finishing = true;
            try
            {
                if (Mouse.Captured == this) Mouse.Capture(null);
            }
            catch { }
            finally { _finishing = false; }
        }

        private void StartFlash(int level, RangeHandleKind kind)
        {
            _flash = true;
            _flashLevel = level;
            _flashKind = kind;
            if (_flashTimer == null)
            {
                _flashTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(FlashMs) };
                _flashTimer.Tick += OnFlashTick;
            }
            _flashTimer.Stop();
            _flashTimer.Start();
        }

        private void OnFlashTick(object sender, EventArgs e)
        {
            if (_flashTimer != null) _flashTimer.Stop();
            _flash = false;
            InvalidateVisual();
        }

        private void StopFlash()
        {
            if (_flashTimer != null) _flashTimer.Stop();
            _flash = false;
        }

        // ══════════════════════════ 预览/自测入口（internal） ══════════════════════════

        /// <summary>
        /// 测试与截图专用：不经过鼠标事件直接进入拖动状态（保证 --widget-preview 能稳定截到浮层+辅助线）。
        /// 走与真实拖动完全相同的约束/浮层/回弹渲染路径。
        /// </summary>
        internal void DebugBeginDrag(int level, RangeHandleKind kind, double value)
        {
            if (_work.Levels == null || level < 0 || level >= _work.Levels.Count) return;
            if (_domain <= 0) { _domain = RangeMath.DomainFor(MaxDistance(_work), _work.Levels.Count == 0 ? 50 : 0); }
            StopFlash();
            _dragging = true;
            _dragLevel = level;
            _dragKind = kind;
            _dragOrigRatio = _work.Levels[level].Ratio;
            _dragOrigDistance = _work.Levels[level].Distance;
            _dragCurRatio = _dragOrigRatio;
            _dragCurDistance = _dragOrigDistance;
            _dragDomain = _domain;
            _dragPlotLeft = _plotLeft > 0 ? _plotLeft : PadLeft;
            _dragPlotTop = _plotTop > 0 ? _plotTop : PadTop;
            double right = _plotRight > _dragPlotLeft ? _plotRight : Math.Max(_dragPlotLeft + 10, ActualWidth - PadRight);
            double bottom = _plotBottom > _dragPlotTop ? _plotBottom : Math.Max(_dragPlotTop + 10, ActualHeight - PadBottom);
            _dragPlotW = Math.Max(1.0, right - _dragPlotLeft);
            _dragPlotH = Math.Max(1.0, bottom - _dragPlotTop);
            if (kind == RangeHandleKind.Ratio)
            {
                double v;
                if (RangeMath.TryConstrainRatio(_work, level, value, out v))
                {
                    _work.Levels[level].Ratio = v;
                    _dragCurRatio = v;
                }
            }
            else
            {
                double v;
                if (RangeMath.TryConstrainDistance(_work, level, value, out v))
                {
                    _work.Levels[level].Distance = v;
                    _dragCurDistance = v;
                }
            }
            _hasHover = true;
            _hoverLevel = level;
            _hoverKind = kind;
            UpdateCursor();
            InvalidateVisual();
        }

        /// <summary>测试与截图专用：结束内部拖动（commit=false 时走回弹+红闪）。</summary>
        internal void DebugEndDrag(bool commit)
        {
            FinishDrag(commit);
        }

        // ══════════════════════════ 绘图工具 ══════════════════════════

        private Brush Res(string key, Brush fallback)
        {
            Brush b = TryFindResource(key) as Brush;
            return b ?? fallback;
        }

        /// <summary>画笔缓存：按（资源键 + 线宽 + 虚线）缓存，主题资源实例变化时整体清空。</summary>
        private Pen PenOf(string cacheKey, double thickness, bool dashed, Brush brush)
        {
            Pen pen;
            if (_penCache.TryGetValue(cacheKey, out pen)) return pen;
            pen = new Pen(brush ?? Brushes.Gray, thickness);
            if (dashed) pen.DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0);
            if (pen.CanFreeze) pen.Freeze();
            _penCache[cacheKey] = pen;
            return pen;
        }

        private FormattedText Text(string text, Typeface face, double size, Brush brush)
        {
            return new FormattedText(text ?? string.Empty, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                     face, size, brush ?? Brushes.Gray, _dpi > 0 ? _dpi : 1.0);
        }

        private CachedText RatioText(int level, double ratio, Brush brush)
        {
            CachedText cached;
            if (_ratioTextCache.TryGetValue(level, out cached) &&
                Math.Abs(cached.Value - ratio) < 1e-12 && ReferenceEquals(cached.Brush, brush))
                return cached;
            cached = new CachedText { Value = ratio, Brush = brush, Text = Text(RangeMath.FormatPct(ratio), FaceLatin, 10.5, brush) };
            _ratioTextCache[level] = cached;
            return cached;
        }

        /// <summary>单行截断（超宽时用 "…" 结尾），保证任何文案都不溢出画布。</summary>
        private FormattedText FitSingleLine(string text, Typeface face, double size, Brush brush, double maxWidth)
        {
            FormattedText ft = Text(text, face, size, brush);
            if (ft.Width <= maxWidth || string.IsNullOrEmpty(text)) return ft;
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                FormattedText cand = Text(text.Substring(0, mid) + "…", face, size, brush);
                if (cand.Width <= maxWidth) lo = mid; else hi = mid - 1;
            }
            return Text(text.Substring(0, lo) + "…", face, size, brush);
        }

        private static double SnapPx(double v)
        {
            return Math.Round(v);
        }

        private static double MaxDistance(LodPlan plan)
        {
            if (plan == null || plan.Levels == null) return 0;
            double max = 0;
            for (int i = 0; i < plan.Levels.Count; i++)
            {
                double d = plan.Levels[i].Distance;
                if (!double.IsNaN(d) && !double.IsInfinity(d) && d > max) max = d;
            }
            return max;
        }

        private bool IsLevelActive(int index)
        {
            if (_dragging && _dragLevel == index) return true;
            if (_flash && _flashLevel == index) return true;
            return _hasHover && _hoverLevel == index;
        }
    }
}
