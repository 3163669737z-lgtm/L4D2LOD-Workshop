using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace LodStudio.Cli.UiAudit
{
    /// <summary>
    /// 视觉树小工具：按名字/类型查找元素、取"相对窗口内容"的包围盒、生成可读的矩形文本。
    /// 只读；不改任何界面状态。
    /// </summary>
    internal static class UiTree
    {
        /// <summary>在窗口的名字作用域里取 x:Name 元素（找不到返回 null）。</summary>
        public static FrameworkElement Named(FrameworkElement scope, string name)
        {
            if (scope == null || string.IsNullOrEmpty(name)) return null;
            try { return scope.FindName(name) as FrameworkElement; }
            catch { return null; }
        }

        /// <summary>视觉树里找第一个 T（深度优先，含自身）。</summary>
        public static T Find<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            var self = root as T;
            if (self != null) return self;
            int n = 0;
            try { n = VisualTreeHelper.GetChildrenCount(root); } catch { return null; }
            for (int i = 0; i < n; i++)
            {
                DependencyObject child;
                try { child = VisualTreeHelper.GetChild(root, i); } catch { continue; }
                T hit = Find<T>(child);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>视觉树里收集所有 T。</summary>
        public static void CollectAll<T>(DependencyObject root, List<T> into) where T : DependencyObject
        {
            if (root == null || into == null) return;
            var self = root as T;
            if (self != null) into.Add(self);
            int n = 0;
            try { n = VisualTreeHelper.GetChildrenCount(root); } catch { return; }
            for (int i = 0; i < n; i++)
            {
                DependencyObject child;
                try { child = VisualTreeHelper.GetChild(root, i); } catch { continue; }
                CollectAll<T>(child, into);
            }
        }

        /// <summary>向上找第一个 T（含自身）。</summary>
        public static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                var hit = d as T;
                if (hit != null) return hit;
                d = ParentOf(d);
            }
            return null;
        }

        /// <summary>视觉父级（跳过 ContentElement 等非 Visual 情况：退回逻辑父级）。</summary>
        public static DependencyObject ParentOf(DependencyObject d)
        {
            if (d == null) return null;
            try
            {
                DependencyObject p = VisualTreeHelper.GetParent(d);
                if (p != null) return p;
            }
            catch { }
            try
            {
                var fe = d as FrameworkElement;
                if (fe != null) return fe.Parent ?? LogicalTreeHelper.GetParent(fe);
            }
            catch { }
            return null;
        }

        /// <summary>元素相对 ancestor 的包围盒（布局槽，含 Margin 之外的实际占位）。失败返回 Rect.Empty。</summary>
        public static Rect Slot(FrameworkElement fe, Visual ancestor)
        {
            if (fe == null || ancestor == null) return Rect.Empty;
            try
            {
                GeneralTransform t = fe.TransformToAncestor(ancestor);
                if (t == null) return Rect.Empty;
                return t.TransformBounds(new Rect(0, 0, Math.Max(0, fe.ActualWidth), Math.Max(0, fe.ActualHeight)));
            }
            catch { return Rect.Empty; }
        }

        /// <summary>两个矩形的相交面积（不相交或空矩形返回 0）。</summary>
        public static double IntersectArea(Rect a, Rect b)
        {
            if (a.IsEmpty || b.IsEmpty) return 0;
            double w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
            double h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
            if (w <= 0 || h <= 0) return 0;
            return w * h;
        }

        /// <summary>可读的元素描述：类型#Name（模板子元素没有 Name 时带 DataContext 类型）。</summary>
        public static string Describe(DependencyObject d)
        {
            if (d == null) return "(null)";
            string type = d.GetType().Name;
            var fe = d as FrameworkElement;
            if (fe == null) return type;
            if (!string.IsNullOrEmpty(fe.Name)) return type + "#" + fe.Name;
            if (fe.DataContext != null && fe.DataContext.GetType() != typeof(object))
                return type + "<" + fe.DataContext.GetType().Name + ">";
            return type;
        }

        /// <summary>矩形的短文本：x,y WxH。</summary>
        public static string RectText(Rect r)
        {
            if (r.IsEmpty) return "(空)";
            return "(" + Fmt(r.Left) + "," + Fmt(r.Top) + " " + Fmt(r.Width) + "×" + Fmt(r.Height) + ")";
        }

        private static string Fmt(double v)
        {
            return Math.Round(v, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>路径文本：从 root 到元素的类型链（最长 6 级），用于定位问题元素。</summary>
        public static string PathOf(DependencyObject d, DependencyObject root)
        {
            var parts = new List<string>();
            DependencyObject cur = d;
            int guard = 0;
            while (cur != null && guard++ < 40)
            {
                parts.Add(Describe(cur));
                if (ReferenceEquals(cur, root)) break;
                cur = ParentOf(cur);
            }
            if (parts.Count > 6) parts = parts.GetRange(parts.Count - 6, 6);
            parts.Reverse();
            return string.Join(" › ", parts.ToArray());
        }
    }
}
