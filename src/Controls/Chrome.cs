using System;
using System.Windows;
using System.Windows.Media;

namespace LodStudio.Controls
{
    /// <summary>
    /// 刻度规：底部一条 1px 基线 + 向上短线（每 Step 一条，每 LongEvery 条加长）。
    /// 用途：顶栏底边、LOD 阶梯图基线 —— 全程序只在这两处出现，作为"量具"语言，不做装饰性滥用。
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
            Brush b = TickBrush ?? TryFindResource("Brush.Tick") as Brush ?? Brushes.Gray;
            var pen = new Pen(b, 1);
            double step = Math.Max(3, Step);
            int every = Math.Max(2, LongEvery);
            dc.DrawLine(pen, new Point(0, h - 0.5), new Point(w, h - 0.5));
            int idx = 0;
            for (double x = 0.5; x < w; x += step, idx++)
            {
                double len = (idx > 0 && idx % every == 0) ? Math.Min(h - 1, LongLength) : Math.Min(h - 1, TickLength);
                if (len <= 0) continue;
                dc.DrawLine(pen, new Point(x, h - 0.5), new Point(x, h - 0.5 - len));
            }
        }
    }

    /// <summary>
    /// 四角方括号角标：只给"当前主作业区"用（本程序里只挂在作业队列面板上）。
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
            if (w < 4 || h < 4) return;
            Brush b = BracketBrush ?? TryFindResource("Brush.LineStrong") as Brush ?? Brushes.Gray;
            var pen = new Pen(b, 1);
            double l = Math.Min(BracketLength, Math.Min(w, h) / 2 - 1);
            if (l <= 1) return;
            // 左上
            dc.DrawLine(pen, new Point(0.5, 0.5), new Point(l, 0.5));
            dc.DrawLine(pen, new Point(0.5, 0.5), new Point(0.5, l));
            // 右上
            dc.DrawLine(pen, new Point(w - l, 0.5), new Point(w - 0.5, 0.5));
            dc.DrawLine(pen, new Point(w - 0.5, 0.5), new Point(w - 0.5, l));
            // 左下
            dc.DrawLine(pen, new Point(0.5, h - 0.5), new Point(l, h - 0.5));
            dc.DrawLine(pen, new Point(0.5, h - l), new Point(0.5, h - 0.5));
            // 右下
            dc.DrawLine(pen, new Point(w - l, h - 0.5), new Point(w - 0.5, h - 0.5));
            dc.DrawLine(pen, new Point(w - 0.5, h - l), new Point(w - 0.5, h - 0.5));
        }
    }

    /// <summary>水平细规：两端带小方块的 1px 线（分节用；本程序用在面板头下方与设置分组）。</summary>
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
            if (Ends)
            {
                dc.DrawRectangle(b, null, new Rect(0, y - 2, 4, 4));
                dc.DrawRectangle(b, null, new Rect(w - 4, y - 2, 4, 4));
                dc.DrawLine(pen, new Point(8, y), new Point(w - 8, y));
            }
            else
            {
                dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            }
        }
    }
}
