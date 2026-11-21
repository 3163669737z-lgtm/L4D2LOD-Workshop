using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LodStudio.Controls
{
    public sealed class SegOption
    {
        public string Text;
        public int Count = -1;
        public bool Enabled = true;
        public SegOption() { }
        public SegOption(string text, int count = -1) { Text = text; Count = count; }
        public string Display { get { return Count >= 0 ? Text + " " + Count : Text; } }
    }

    public enum SegVariant
    {
        /// <summary>页签：纯文字 + 底部信号色下划线（选中）</summary>
        Tabs,
        /// <summary>分段按钮：1px 外框，选中段填充</summary>
        Boxed
    }

    /// <summary>
    /// 自绘分段控件（页签 / 分段按钮）。
    /// - 键盘：Tab 聚焦，←→ 切换，Home/End 到首尾，Enter/Space 确认；
    /// - 数量小字（Count>=0 时显示）；
    /// - 选中态用信号色下划线（Tabs）或填充（Boxed），悬停用 InkFaint。
    /// </summary>
    public sealed class Segmented : FrameworkElement
    {
        private static readonly Typeface Cjk = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Typeface CjkBold = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        private readonly List<Rect> _rects = new List<Rect>();
        private int _hover = -1;
        private int _selected;

        public IList<SegOption> Items { get; private set; } = new List<SegOption>();
        public SegVariant Variant { get; set; } = SegVariant.Tabs;
        public double FontSize { get; set; } = 12.5;
        public double CountFontSize { get; set; } = 10.5;

    /// <summary>计数数字的固定槽宽：0→10→100 变化时页签宽度不变，避免整行控件轻微位移。</summary>
    public double CountSlotWidth { get; set; } = 26;
        public double ItemPadding { get; set; } = 14;
        /// <summary>true = 各段等分控件宽度（铺满），false = 按文字自然宽度排列</summary>
        public bool EqualWidths { get; set; }

        public event EventHandler SelectedIndexChanged;

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                if (Items == null || Items.Count == 0) { _selected = 0; return; }
                int v = Math.Max(0, Math.Min(Items.Count - 1, value));
                if (v == _selected) return;
                _selected = v;
                InvalidateVisual();
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public SegOption SelectedOption
        {
            get
            {
                if (Items == null || Items.Count == 0) return null;
                return Items[Math.Max(0, Math.Min(Items.Count - 1, _selected))];
            }
        }

        public Segmented()
        {
            Focusable = true;
            SnapsToDevicePixels = true;
            FocusVisualStyle = null;          // 不用系统默认虚线焦点框（自绘中性实线）
            Cursor = Cursors.Hand;
            IsKeyboardFocusWithinChanged += delegate { InvalidateVisual(); };
            MouseEnter += delegate { if (IsKeyboardFocused) InvalidateVisual(); };
            MouseLeave += delegate { if (IsKeyboardFocused) InvalidateVisual(); };
        }

        public void SetItems(IList<SegOption> items, int selected)
        {
            Items = items ?? new List<SegOption>();
            _selected = Math.Max(0, Math.Min(Math.Max(0, Items.Count - 1), selected));
            _hover = -1;
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = 0, h = 0;
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (int i = 0; i < Items.Count; i++)
            {
                var opt = Items[i];
                var ft = MakeText(opt, Cjk, dpi);
                var ftc = opt.Count >= 0 ? MakeCount(opt, dpi) : null;
                double iw = ft.Width + (ftc != null ? 5 + CountSlotWidth : 0) + ItemPadding * 2;
                w += iw;
                h = Math.Max(h, ft.Height + (Variant == SegVariant.Boxed ? 12 : 6));
            }
            if (Variant == SegVariant.Boxed) { w += 2; h += 2; }
            if (EqualWidths && !double.IsInfinity(availableSize.Width) && availableSize.Width > w)
                w = availableSize.Width;                       // 铺满可用宽度，各段等分
            if (EqualWidths && !double.IsInfinity(availableSize.Height) && availableSize.Height > h)
                h = availableSize.Height;
            return new Size(w, h);
        }

        private double[] BuildWidths(double total)
        {
            int n = Math.Max(1, Items.Count);
            double[] widths = new double[n];
            if (!EqualWidths)
            {
                var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                double x = 0;
                for (int i = 0; i < Items.Count; i++)
                {
                    var opt = Items[i];
                    var ft = MakeText(opt, Cjk, dpi);
                    var ftc = opt.Count >= 0 ? MakeCount(opt, dpi) : null;
                    widths[i] = ft.Width + (ftc != null ? 5 + CountSlotWidth : 0) + ItemPadding * 2;
                    x += widths[i];
                }
                return widths;
            }
            double usable = total - (Variant == SegVariant.Boxed ? 2 : 0);
            double each = usable / n;
            for (int i = 0; i < n; i++) widths[i] = each;
            return widths;
        }

        protected override void OnRender(DrawingContext dc)
        {
            _rects.Clear();
            var ink = Res("Brush.Ink");
            var inkFaint = Res("Brush.InkFaint");
            var inkSoft = Res("Brush.InkSoft");
            var signal = Res("Brush.Signal");
            var line = Res("Brush.Line");
            var lineStrong = Res("Brush.LineStrong");
            var sunken = Res("Brush.Sunken");
            var hoverFill = Res("Brush.RowHover");
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            double x = 0, h = ActualHeight;
            if (Variant == SegVariant.Boxed) x = 1;

            double[] widths = BuildWidths(ActualWidth);
            var underlinePoints = new List<Point>();
            for (int i = 0; i < Items.Count; i++)
            {
                var opt = Items[i];
                var ft = MakeText(opt, i == _selected ? CjkBold : Cjk, dpi);
                FormattedText ftc = opt.Count >= 0 ? MakeCount(opt, dpi) : null;
                double iw = widths[i];
                var r = new Rect(x, 0, iw, h);
                _rects.Add(r);

                if (Variant == SegVariant.Tabs)
                {
                    if (i == _hover && opt.Enabled) dc.DrawRectangle(hoverFill, null, new Rect(r.X, 2, r.Width, h - 4));
                    double ty = (h - ft.Height) / 2 - 1;
                    double tx = r.X + ItemPadding;
                    dc.DrawText(ft, new Point(tx, ty));
                    if (ftc != null) dc.DrawText(ftc, new Point(tx + ft.Width + 5 + (CountSlotWidth - ftc.Width) / 2, ty + ft.Baseline - ftc.Baseline));
                    if (i == _selected)
                    {
                        underlinePoints.Add(new Point(r.X + ItemPadding - 4, h - 1.5));
                        underlinePoints.Add(new Point(r.Right - ItemPadding + 4, h - 1.5));
                    }
                }
                else
                {
                    bool sel = i == _selected;
                    if (i > 0) dc.DrawLine(new Pen(line, 1), new Point(r.X, 1), new Point(r.X, h - 1));
                    // 填充左右各让 1px：左边不盖分隔线/外框，右边不盖右侧外框（最后一段最容易越界）
                    double fillLeft = r.X + 1;
                    double fillWidth = Math.Max(0, r.Width - (i > 0 ? 2 : 3));
                    if (sel) dc.DrawRectangle(sunken, null, new Rect(fillLeft, 1, fillWidth, h - 2));
                    else if (i == _hover) dc.DrawRectangle(hoverFill, null, new Rect(fillLeft, 1, fillWidth, h - 2));
                    double ty = (h - ft.Height) / 2;
                    double tx = r.X + (r.Width - ft.Width - (ftc != null ? 5 + CountSlotWidth : 0)) / 2;
                    dc.DrawText(ft, new Point(tx, ty));
                    if (ftc != null) dc.DrawText(ftc, new Point(tx + ft.Width + 5 + (CountSlotWidth - ftc.Width) / 2, ty + ft.Baseline - ftc.Baseline));
                }
                x += iw;
            }

            if (Variant == SegVariant.Tabs && underlinePoints.Count == 2)
            {
                var pen = new Pen(signal, 2);
                dc.DrawLine(pen, underlinePoints[0], underlinePoints[1]);
            }

            // 外框最后画：避免最后一段的 hover/选中填充盖住右侧外框线
            if (Variant == SegVariant.Boxed)
            {
                dc.DrawRectangle(null, new Pen(lineStrong, 1),
                    new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, h - 1)));
            }
        }

        private Brush Res(string key)
        {
            var b = TryFindResource(key) as Brush;
            return b ?? Brushes.Black;
        }

        private FormattedText MakeText(SegOption opt, Typeface face, double dpi)
        {
            return new FormattedText(opt.Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face,
                FontSize, Res("Brush.Ink"), dpi);
        }

        private FormattedText MakeCount(SegOption opt, double dpi)
        {
            var tf = new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            return new FormattedText(opt.Count.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, tf, CountFontSize, Res("Brush.InkFaint"), dpi);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = HitTest(e.GetPosition(this));
            if (idx != _hover)
            {
                _hover = idx;
                Cursor = idx >= 0 && (idx >= Items.Count || Items[idx].Enabled) ? Cursors.Hand : Cursors.Arrow;
                InvalidateVisual();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (_hover != -1) { _hover = -1; InvalidateVisual(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            int idx = HitTest(e.GetPosition(this));
            if (idx >= 0 && idx < Items.Count && Items[idx].Enabled) SelectedIndex = idx;
            e.Handled = true;
            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int n = Items.Count;
            if (n == 0) { base.OnKeyDown(e); return; }
            if (e.Key == Key.Left)
            {
                int i = _selected - 1;
                while (i >= 0 && !Items[i].Enabled) i--;
                if (i >= 0) SelectedIndex = i;
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                int i = _selected + 1;
                while (i < n && !Items[i].Enabled) i++;
                if (i < n) SelectedIndex = i;
                e.Handled = true;
            }
            else if (e.Key == Key.Home) { SelectedIndex = 0; e.Handled = true; }
            else if (e.Key == Key.End) { SelectedIndex = n - 1; e.Handled = true; }
            base.OnKeyDown(e);
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < _rects.Count; i++)
                if (_rects[i].Contains(p)) return i;
            return -1;
        }
    }
}
