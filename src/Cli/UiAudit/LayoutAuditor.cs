using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LodStudio.Controls;

namespace LodStudio.Cli.UiAudit
{
    /// <summary>一组布局审计结果。</summary>
    internal sealed class LayoutAuditResult
    {
        public string Context = "";
        public int Overlaps;
        public int OutOfBounds;
        public readonly List<string> OverlapDetails = new List<string>();
        public readonly List<string> OutOfBoundsDetails = new List<string>();

        public bool Ok { get { return Overlaps == 0 && OutOfBounds == 0; } }

        public string Summary
        {
            get { return "重叠 " + Overlaps + " 处 / 越界 " + OutOfBounds + " 处"; }
        }
    }

    /// <summary>
    /// 布局审计：兄弟重叠 + 子元素越出父元素。
    ///
    /// 规则（与 docs/02_UI设计规范.md §8 一致）：
    ///  - 只看可见（Visibility=Visible 且尺寸 &gt;0）的 FrameworkElement；
    ///  - 同一父级下两个子元素矩形相交面积 &gt; 1px² → 记"重叠"；
    ///  - 子元素包围盒超出父元素包围盒 &gt; 1px → 记"越界"；
    ///  - 排除（整棵子树）：覆盖层与自绘装饰（EmptyState/NoMatchState/LogBody/
    ///    DropOverlay/DialogHost/ToastHost/Progress/TickStrip/CornerBrackets/Rule/SegmentedProgress）、
    ///    1px 分隔 Border、Canvas 子级、滚动区内容的越界（滚动区只查重叠不查越界）。
    ///
    /// 为什么还要"有效矩形"：WPF 里"标题左 + 摘要右"这类同一 Grid 单元格的对齐写法，
    /// StackPanel/TextBlock 的布局槽会撑满整行（矩形相交，但视觉上并不重叠）。
    /// 所以对 TextBlock 用 DesiredSize（墨迹宽度）并按对齐方向贴边，对<b>不绘制背景的 Panel/Border</b>
    /// 用子元素墨迹的并集——这样报出来的才是真正的视觉重叠。
    /// </summary>
    internal static class LayoutAuditor
    {
        private const double Tolerance = 1.0;
        private const int MaxDetailsPerKind = 8;

        public static LayoutAuditResult Audit(FrameworkElement root, string context)
        {
            var result = new LayoutAuditResult { Context = context ?? "" };
            if (root == null) return result;
            Walk(root, root, result, false);
            return result;
        }

        private static void Walk(DependencyObject parent, FrameworkElement root, LayoutAuditResult result, bool insideScroll)
        {
            // Canvas 子级自由定位（品牌标记等矢量组合），整棵不参与审计
            if (parent is Canvas) return;

            int count = 0;
            try { count = VisualTreeHelper.GetChildrenCount(parent); } catch { return; }

            var eligible = new List<FrameworkElement>();
            for (int i = 0; i < count; i++)
            {
                DependencyObject child;
                try { child = VisualTreeHelper.GetChild(parent, i); } catch { continue; }
                var fe = child as FrameworkElement;
                if (fe == null) continue;
                if (!IsAuditable(fe)) continue;
                eligible.Add(fe);
            }

            // ① 兄弟重叠
            for (int i = 0; i < eligible.Count; i++)
            {
                Rect a = Effective(eligible[i], root);
                if (a.IsEmpty) continue;
                for (int j = i + 1; j < eligible.Count; j++)
                {
                    // 非交互的装饰/后缀（例如数字框右侧的 "%" 单位）是刻意覆盖在输入框上的，不算布局重叠
                    if (!eligible[i].IsHitTestVisible || !eligible[j].IsHitTestVisible) continue;
                    Rect b = Effective(eligible[j], root);
                    if (b.IsEmpty) continue;
                    double area = UiTree.IntersectArea(a, b);
                    if (area <= 1.0) continue;
                    result.Overlaps++;
                    if (result.OverlapDetails.Count < MaxDetailsPerKind)
                    {
                        result.OverlapDetails.Add(
                            "[" + result.Context + "] 重叠 " + Math.Round(area, 1).ToString("0.#", CultureInfo.InvariantCulture) + "px² : " +
                            UiTree.Describe(eligible[i]) + " " + UiTree.RectText(a) + " ∩ " +
                            UiTree.Describe(eligible[j]) + " " + UiTree.RectText(b) +
                            "  父级=" + UiTree.Describe(parent));
                    }
                }
            }

            // ② 越界（滚动区内容不查：内容本来就可以比视口大）
            if (!insideScroll && !(parent is Canvas))
            {
                var parentFe = parent as FrameworkElement;
                Rect p = parentFe != null ? UiTree.Slot(parentFe, root) : Rect.Empty;
                if (!p.IsEmpty)
                {
                    for (int i = 0; i < eligible.Count; i++)
                    {
                        Rect c = UiTree.Slot(eligible[i], root);
                        if (c.IsEmpty) continue;
                        if (c.Left >= p.Left - Tolerance && c.Top >= p.Top - Tolerance &&
                            c.Right <= p.Right + Tolerance && c.Bottom <= p.Bottom + Tolerance) continue;
                        result.OutOfBounds++;
                        if (result.OutOfBoundsDetails.Count < MaxDetailsPerKind)
                        {
                            result.OutOfBoundsDetails.Add(
                                "[" + result.Context + "] 越界 : " + UiTree.Describe(eligible[i]) + " " + UiTree.RectText(c) +
                                " 超出父级 " + UiTree.Describe(parent) + " " + UiTree.RectText(p));
                        }
                    }
                }
            }

            // ③ 递归
            for (int i = 0; i < eligible.Count; i++)
            {
                FrameworkElement child = eligible[i];
                bool childInsideScroll = insideScroll || child is ScrollViewer || child is ScrollContentPresenter;
                Walk(child, root, result, childInsideScroll);
            }
        }

        /// <summary>可见、有尺寸、且不在排除名单里（排除名单命中时整棵子树跳过）。</summary>
        private static bool IsAuditable(FrameworkElement fe)
        {
            if (fe == null) return false;
            if (fe.Visibility != Visibility.Visible) return false;
            try { if (!fe.IsVisible) return false; } catch { return false; }
            if (fe.ActualWidth <= 0.5 || fe.ActualHeight <= 0.5) return false;
            return !IsExcluded(fe);
        }

        private static bool IsExcluded(FrameworkElement fe)
        {
            // 自绘装饰 / 覆盖层：不算"布局元素"
            if (fe is ToastHost) return true;
            if (fe is DialogHost) return true;
            if (fe is TickStrip) return true;
            if (fe is CornerBrackets) return true;
            if (fe is Rule) return true;
            if (fe is SegmentedProgress) return true;

            // 内置控件模板内部（TextBox/ComboBox/ScrollBar 的 PART_*/Ph/TextBoxView、AdornerLayer/AdornerDecorator）
            // 不是我们的布局，而且模板内部本来就有"输入宿主 + 占位符 + 装饰层"的覆盖关系，整棵跳过。
            if (fe is System.Windows.Documents.AdornerLayer) return true;
            if (fe is System.Windows.Documents.AdornerDecorator) return true;
            if (fe is ComboBox) return true;
            if (fe is ScrollBar) return true;
            if (IsInsideBuiltInTemplate(fe)) return true;

            string name = fe.Name ?? "";
            if (name == "DropOverlay") return true;
            if (name == "EmptyState") return true;
            if (name == "NoMatchState") return true;
            if (name == "LogBody") return true;
            if (name == "Progress") return true;

            // 1px 发丝分隔线（横/竖）不参与布局审计
            var border = fe as Border;
            if (border != null && (border.ActualWidth <= 1.5 || border.ActualHeight <= 1.5)) return true;

            return false;
        }

        /// <summary>是否位于 TextBox / ComboBox / ScrollBar 的模板内部（向上找祖先）。</summary>
        private static bool IsInsideBuiltInTemplate(FrameworkElement fe)
        {
            DependencyObject cur = UiTree.ParentOf(fe);
            int guard = 0;
            while (cur != null && guard++ < 24)
            {
                if (cur is TextBox) return true;
                if (cur is ComboBox) return true;
                if (cur is ScrollBar) return true;
                if (cur is System.Windows.Documents.AdornerLayer) return true;
                cur = UiTree.ParentOf(cur);
            }
            return false;
        }

        /// <summary>视觉有效矩形（用于重叠判定）。</summary>
        private static Rect Effective(FrameworkElement fe, FrameworkElement root)
        {
            Rect slot = UiTree.Slot(fe, root);
            if (slot.IsEmpty) return Rect.Empty;

            // 文本：用 DesiredSize 表示墨迹，按对齐方向贴边（避免"左标题 + 右摘要"误报）
            var tb = fe as TextBlock;
            if (tb != null && tb.TextWrapping == TextWrapping.NoWrap)
            {
                return TrimToInk(tb, slot);
            }

            // 不绘制背景的 Panel：用子元素墨迹的并集
            if (fe is Panel)
            {
                Rect? union = ChildrenInk(fe, root);
                return union.HasValue ? union.Value : new Rect(slot.X, slot.Y, 0, 0);
            }

            // 不绘制背景/边框的 Border：同样用子元素并集（例如计划行的透明底 Border）
            var border = fe as Border;
            if (border != null && !PaintsBackground(border.Background) && border.BorderThickness == new Thickness(0))
            {
                Rect? union = ChildrenInk(border, root);
                return union.HasValue ? union.Value : new Rect(slot.X, slot.Y, 0, 0);
            }

            return slot;
        }

        private static Rect TrimToInk(TextBlock tb, Rect slot)
        {
            if (string.IsNullOrEmpty(tb.Text)) return new Rect(slot.X, slot.Y, 0, 0);
            double inkW = tb.DesiredSize.Width;
            double inkH = tb.DesiredSize.Height;
            if (inkW <= 0 || inkW > slot.Width) inkW = slot.Width;
            if (inkH <= 0 || inkH > slot.Height) inkH = slot.Height;

            double x = slot.X;
            switch (tb.HorizontalAlignment)
            {
                case HorizontalAlignment.Right: x = slot.Right - inkW; break;
                case HorizontalAlignment.Center: x = slot.X + (slot.Width - inkW) / 2.0; break;
            }
            double y = slot.Y;
            switch (tb.VerticalAlignment)
            {
                case VerticalAlignment.Bottom: y = slot.Bottom - inkH; break;
                case VerticalAlignment.Center: y = slot.Y + (slot.Height - inkH) / 2.0; break;
            }
            return new Rect(x, y, Math.Max(0, inkW), Math.Max(0, inkH));
        }

        /// <summary>子元素（可审计的）墨迹矩形的并集；没有子元素时返回 null。</summary>
        private static Rect? ChildrenInk(FrameworkElement parent, FrameworkElement root)
        {
            int count = 0;
            try { count = VisualTreeHelper.GetChildrenCount(parent); } catch { return null; }
            Rect? union = null;
            for (int i = 0; i < count; i++)
            {
                DependencyObject child;
                try { child = VisualTreeHelper.GetChild(parent, i); } catch { continue; }
                var fe = child as FrameworkElement;
                if (fe == null || !IsAuditable(fe)) continue;
                Rect r = Effective(fe, root);
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;
                union = union.HasValue ? Rect.Union(union.Value, r) : r;
            }
            return union;
        }

        private static bool PaintsBackground(Brush brush)
        {
            if (brush == null) return false;
            var solid = brush as SolidColorBrush;
            if (solid != null && solid.Color.A == 0) return false;
            return true;
        }
    }
}
