using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LodStudio.Controls;
using LodStudio.Services;

namespace LodStudio.Cli
{
    /// <summary>
    /// --icon-sheet --out &lt;dir&gt;：把全部图标渲染成 PNG 总表（浅底/暗底、24/16/12 三档），
    /// 并输出数据校验结果。用于人工核对图标形状与名称是否对应，也用于设计迭代。
    /// </summary>
    public static class IconSheetRunner
    {
        public static int Run(CommandLine cli)
        {
            string outDir = cli.OutDir;
            if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(AppInfo.TempRoot, "icon-sheet");
            try { Directory.CreateDirectory(outDir); } catch { }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ThemeService.Initialize(ThemeKind.Light);
            var errors = IconAudit.Validate();
            foreach (var e in errors) Console.WriteLine("ICON ERROR: " + e);

            try
            {
                var win = BuildSheetWindow(ThemeKind.Light);
                win.Show();
                win.UpdateLayout();
                UiCapture.DoEvents(4);
                UiCapture.CaptureWindow(win, Path.Combine(outDir, "icons_light.png"));
                win.Content = null;
                win.Close();

                var win2 = BuildSheetWindow(ThemeKind.Dark);
                ThemeService.Apply(ThemeKind.Dark);
                win2.Show();
                win2.UpdateLayout();
                UiCapture.DoEvents(4);
                UiCapture.CaptureWindow(win2, Path.Combine(outDir, "icons_dark.png"));
                win2.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine("ICON-SHEET FATAL: " + ex);
                return 2;
            }
            finally
            {
                try { app.Shutdown(); } catch { }
            }

            Console.WriteLine("ICON-SHEET: " + IconPaths.All.Count() + " icons, " + errors.Count + " errors -> " + outDir);
            return errors.Count == 0 ? 0 : 1;
        }

        private static Window BuildSheetWindow(ThemeKind kind)
        {
            var root = new StackPanel { Margin = new Thickness(20) };
            var title = new TextBlock
            {
                Text = "图标总表 · " + (kind == ThemeKind.Dark ? "夜测" : "日测"),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Ink");
            root.Children.Add(title);

            var wrap = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Width = 980 };
            foreach (var kv in IconPaths.All)
            {
                var cell = new StackPanel { Width = 118, Margin = new Thickness(0, 0, 10, 12) };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(MakeIcon(kv.Key, 24));
                row.Children.Add(new Border { Width = 10 });
                row.Children.Add(MakeIcon(kv.Key, 16));
                row.Children.Add(new Border { Width = 10 });
                row.Children.Add(MakeIcon(kv.Key, 12));
                cell.Children.Add(row);
                var label = new TextBlock
                {
                    Text = kv.Key.ToString(),
                    FontFamily = new FontFamily("Bahnschrift"),
                    FontSize = 10.5,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.InkFaint");
                cell.Children.Add(label);
                wrap.Children.Add(cell);
            }
            root.Children.Add(wrap);

            var grid = new Grid();
            grid.SetResourceReference(Panel.BackgroundProperty, "Brush.Canvas");
            grid.Children.Add(root);
            return new Window
            {
                Content = grid,
                Width = 1040,
                Height = 1180,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize
            };
        }

        private static Icon MakeIcon(IconKind kind, double size)
        {
            var icon = new Icon { Kind = kind, Size = size, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(Icon.IconBrushProperty, "Brush.Ink");
            return icon;
        }
    }
}
