using System;
using System.Windows;
using System.Windows.Media;

namespace LodStudio.Controls
{
    /// <summary>
    /// 现代精密刻度规：极细渐变基线 + 柔和刻度线（仅用于顶栏和空态视觉锚点）。
    /// </summary>
    public sealed class TickStrip : FrameworkElement
    {
        public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
            "Step", typeof(double), typeof(TickStrip),
            new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TickLengthProperty = DependencyProperty.Register(
            "TickLength", typeof(double), typeof(TickStrip),
            new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LongEveryProperty = DependencyProperty.Register(
            "LongEvery", typeof(int), typeof(TickStrip),
            new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LongLengthProperty = DependencyProperty.Register(
            "LongLength", typeof(double), typeof(TickStrip),
            new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TickBrushProperty = DependencyProperty.Register(
            "TickBrush", typeof(Brush), typeof(TickStrip),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Step { get { return (double)GetValue(StepProperty); } set { SetValue(StepProperty, value); } }
        public double TickLength { get { return (double)GetValue(TickLengthProperty); } set { SetValue(TickLengthProperty, value); } }
        public int LongEvery { get { return (int)GetValue(LongEveryProperty); } set { SetValue(LongEveryProperty, value); } }
        public double LongLength { get { return (double)GetValue(LongLengthProperty); } set { SetValue(LongLengthProperty, value); } }
        public Brush TickBrush { get { return (Brush)GetValue(TickBrushProperty); } set { SetValue(TickBrushProperty, value); } }

        public TickStrip()
        {
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
            Height = 7;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = Math.Max(2, ActualHeight);
            if (w < 4) return;
            Brush b = TickBrush ?? TryFindResource("Brush.Tick") as Brush ?? Brushes.Gray;
            dc.PushOpacity(0.55);
            var pen = new Pen(b, 1);
            double step = Math.Max(4, Step);
            int every = Math.Max(2, LongEvery);
            int idx = 0;
            for (double x = 0.5; x < w; x += step, idx++)
            {
                double len = (idx > 0 && idx % every == 0) ? Math.Min(h - 1, LongLength) : Math.Min(h - 1, TickLength);
                if (len <= 0) continue;
                dc.DrawLine(pen, new Point(x, h - 0.5), new Point(x, h - 0.5 - len));
            }
            dc.Pop();
        }
    }

    /// <summary>
    /// 现代玻璃卡片顶部流光高光边（取代生硬的直角方括号，保留类契约兼容 XAML）。
    /// </summary>
    public sealed class CornerBrackets : FrameworkElement
    {
        public static readonly DependencyProperty BracketLengthProperty = DependencyProperty.Register(
            "BracketLength", typeof(double), typeof(CornerBrackets),
            new FrameworkPropertyMetadata(9.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BracketBrushProperty = DependencyProperty.Register(
            "BracketBrush", typeof(Brush), typeof(CornerBrackets),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public double BracketLength { get { return (double)GetValue(BracketLengthProperty); } set { SetValue(BracketLengthProperty, value); } }
        public Brush BracketBrush { get { return (Brush)GetValue(BracketBrushProperty); } set { SetValue(BracketBrushProperty, value); } }

        public CornerBrackets()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 24 || h < 24) return;
            Brush highlight = TryFindResource("Brush.PrismHighlight") as Brush ??
                              TryFindResource("Brush.GlassHighlight") as Brush ?? BracketBrush ?? Brushes.Transparent;
            // 顶部中央柔和镜面高光带，营造通透磨砂玻璃折光感
            dc.DrawRoundedRectangle(highlight, null, new Rect(14, 0.5, Math.Max(0, w - 28), 1.5), 0.75, 0.75);
            double inset = Math.Max(4, Math.Min(w / 2 - 2, BracketLength));
            Brush warm = BracketBrush ?? TryFindResource("Brush.OrbitalAccent") as Brush ?? highlight;
            Brush cool = BracketBrush ?? TryFindResource("Brush.OrbitalSecondary") as Brush ?? highlight;
            dc.DrawEllipse(warm, null, new Point(inset, 3), 1.2, 1.2);
            dc.DrawEllipse(cool, null, new Point(w - inset, 3), 1.2, 1.2);
        }
    }

    /// <summary>现代柔光渐变分割线。</summary>
    public sealed class Rule : FrameworkElement
    {
        public static readonly DependencyProperty EndsProperty = DependencyProperty.Register(
            "Ends", typeof(bool), typeof(Rule),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty RuleBrushProperty = DependencyProperty.Register(
            "RuleBrush", typeof(Brush), typeof(Rule),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool Ends { get { return (bool)GetValue(EndsProperty); } set { SetValue(EndsProperty, value); } }
        public Brush RuleBrush { get { return (Brush)GetValue(RuleBrushProperty); } set { SetValue(RuleBrushProperty, value); } }

        public Rule()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
            Height = 1;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 1) return;
            Brush b = RuleBrush ?? TryFindResource("Brush.Line") as Brush ?? Brushes.Gray;
            var pen = new Pen(b, 1);
            double y = h / 2;
            dc.PushOpacity(0.75);
            if (Ends)
            {
                dc.DrawEllipse(b, null, new Point(2, y), 2, 2);
                dc.DrawEllipse(b, null, new Point(w - 2, y), 2, 2);
                dc.DrawLine(pen, new Point(8, y), new Point(w - 8, y));
            }
            else
            {
                dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            }
            dc.Pop();
        }
    }
}
