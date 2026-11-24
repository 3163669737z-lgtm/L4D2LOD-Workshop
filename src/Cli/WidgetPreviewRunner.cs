using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LodStudio.Controls;
using LodStudio.Engine;
using LodStudio.Services;

namespace LodStudio.Cli
{
    /// <summary>
    /// --widget-preview --out &lt;dir&gt;：把 RangeCard / Toast / Dialog 的状态矩阵渲染成 PNG，供设计核对与迭代。
    ///
    /// 做法：离屏 Window（Left/Top = -20000）+ RenderTargetBitmap（UiCapture），逐张截图；全部在 UI 线程上，
    /// 每次截图前泵消息（含动画时间），对话框用 async 打开后截图再关窗。退出码 0 = 全部产出。
    /// </summary>
    public static class WidgetPreviewRunner
    {
        private sealed class ToastSpec
        {
            public string Title;
            public string Sub;
            public ToastTone Tone;
            public ToastSpec(string title, string sub, ToastTone tone) { Title = title; Sub = sub; Tone = tone; }
        }

        private static readonly List<string> Log = new List<string>();
        private static readonly List<string> Files = new List<string>();
        private static string _outDir;
        private static int _failures;

        public static int Run(CommandLine cli)
        {
            _outDir = cli.OutDir;
            if (string.IsNullOrEmpty(_outDir)) _outDir = Path.Combine(AppInfo.TempRoot, "widget-preview");
            try { Directory.CreateDirectory(_outDir); } catch { }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                ThemeService.Initialize(ThemeKind.Light);
                bool widgetsLoaded = Application.Current != null && Application.Current.TryFindResource("Widget.Dialog.Panel") != null;
                Log.Add("out           : " + _outDir);
                Log.Add("Widgets.xaml  : " + (widgetsLoaded ? "已并入全局资源" : "未加载（对话框将回退到代码内联样式）"));
                Log.Add("动画开关      : ClientAreaAnimation=" + SafeAnimationFlag());
                Log.Add("");

                // ═══════════ RangeCard ═══════════
                ShotRange("range_4levels_light", ThemeKind.Light, DefaultPlan(), 420, 168,
                          "平衡预设 4 级（10/15/25/50 HU · 85/65/45/30%）");
                ShotRange("range_4levels_dark", ThemeKind.Dark, DefaultPlan(), 420, 168,
                          "暗色主题同一方案（令牌随主题切换）");
                ShotRange("range_maxlevels_light", ThemeKind.Light, MaxLevelPlan(), 440, 168,
                          LodPlan.MaxLevels + " 级上限");
                ShotRange("range_maxlevels_dark", ThemeKind.Dark, MaxLevelPlan(), 440, 168,
                          LodPlan.MaxLevels + " 级 · 暗色");
                ShotRange("range_extreme_light", ThemeKind.Light, ExtremePlan(), 420, 168,
                          "2 级 + 距离 100 / 100000 HU：刻度退化为大数，不崩不叠");
                ShotRange("range_1level_light", ThemeKind.Light, OneLevelPlan(), 420, 168,
                          "1 级最小方案");
                ShotRange("range_empty_light", ThemeKind.Light, new LodPlan(), 420, 168,
                          "空方案：坐标/基线仍在，给出「尚无 LOD 级别」");
                ShotRange("range_dragging_ratio_light", ThemeKind.Light, DefaultPlan(), 420, 168,
                          "合成比例拖动：浮层两行读数 + 水平辅助虚线 + 段顶 2px 信号线",
                          dragLevel: 1, dragKind: RangeHandleKind.Ratio, dragValue: 0.50);
                ShotRange("range_dragging_distance_light", ThemeKind.Light, DefaultPlan(), 440, 168,
                          "合成距离拖动：浮层 + 垂直辅助虚线",
                          dragLevel: 2, dragKind: RangeHandleKind.Distance, dragValue: 38);
                ShotRange("range_dragging_ratio_dark", ThemeKind.Dark, DefaultPlan(), 420, 168,
                          "拖动 · 暗色",
                          dragLevel: 1, dragKind: RangeHandleKind.Ratio, dragValue: 0.50);
                ShotRange("range_invalid_light", ThemeKind.Light, DefaultPlan(), 440, 168,
                          "外部校验错误：右上角错误标记 + 禁止开始拖动",
                          validationError: "保留比例必须一级比一级低（第 2 级 90% 不小于第 1 级 85%）。");
                ShotRangeRebound("range_rebound_flash_light", ThemeKind.Light, DefaultPlan(), 440, 168,
                                 "松手回弹：非法值回弹到拖动前并标红 400ms（此处为红闪瞬间）");
                ShotRangeThemeRoundTrip();

                // ═══════════ DialogHost ═══════════
                ShotDialogDanger("dialog_confirm_light", ThemeKind.Light);
                ShotDialogDanger("dialog_confirm_dark", ThemeKind.Dark);
                ShotDialogPrompt("dialog_prompt_light", ThemeKind.Light);
                ShotDialogPromptError("dialog_prompt_error_light", ThemeKind.Light);
                ShotDialogInfo("dialog_info_dark", ThemeKind.Dark);

                // ═══════════ ToastHost ═══════════
                ShotToast("toast_ok_light", ThemeKind.Light,
                          new ToastSpec("LOD 注入完成", "12 个 QC 已写回，3 个跳过", ToastTone.Ok));
                ShotToast("toast_single_light", ThemeKind.Light,
                          new ToastSpec("已复制 12 条日志", null, ToastTone.Ok));
                ShotToast("toast_long_text_light", ThemeKind.Light,
                          new ToastSpec("这是一个特别长的提示标题用来验证不会溢出也不会被裁切掉", "副行同样很长：路径 C:\\Users\\TPP\\Documents\\某工程\\SMD\\survivor_teenangst.dx90.vtx 已经处理完成", ToastTone.Info));
                ShotToast("toast_err_dark", ThemeKind.Dark,
                          new ToastSpec("减面进程启动失败", "内置减面组件缺失，请重新构建程序", ToastTone.Err));
                ShotToast("toast_stack_light", ThemeKind.Light,
                          new ToastSpec("扫描完成", "12 个 QC · 12 个模型", ToastTone.Ok),
                          new ToastSpec("部分 QC 有手写 $lod", "已按设置跳过，可在高级参数里改为覆盖", ToastTone.Warn),
                          new ToastSpec("运行失败", "nekomdl.exe 退出码 1，详见运行日志", ToastTone.Err));
                ShotToast("toast_info_dark", ThemeKind.Dark,
                          new ToastSpec("报告已生成", "已保存到 QC 目录旁", ToastTone.Info));

                // ═══════════ 行为断言（键盘 / 关闭 / 动画开关；--ui-audit 也会依赖这些语义） ═══════════
                RunBehaviourChecks();
            }
            catch (Exception ex)
            {
                _failures++;
                Log.Add("FATAL: " + ex);
                try { Console.WriteLine("WIDGET-PREVIEW FATAL: " + ex); } catch { }
            }
            finally
            {
                try { app.Shutdown(); } catch { }
            }

            Log.Add("");
            for (int i = 0; i < Files.Count; i++) Log.Add("  [ok] " + Files[i]);
            Log.Add("");
            Log.Add("WIDGET-PREVIEW: " + Files.Count + " images, " + _failures + " failure(s)");
            try
            {
                File.WriteAllText(Path.Combine(_outDir, "widget-preview.txt"), string.Join(Environment.NewLine, Log.ToArray()) + Environment.NewLine,
                                  new UTF8Encoding(false));
            }
            catch { }
            for (int i = 0; i < Log.Count; i++) { try { Console.WriteLine(Log[i]); } catch { } }
            return _failures == 0 ? 0 : 2;
        }

        // ══════════════════════════ RangeCard ══════════════════════════

        private static void ShotRange(string name, ThemeKind theme, LodPlan plan, double winW, double winH, string note,
                                      string validationError = null, int dragLevel = -1,
                                      RangeHandleKind dragKind = RangeHandleKind.Ratio, double dragValue = 0)
        {
            ThemeService.Apply(theme);
            var card = new RangeCard { Margin = new Thickness(10) };
            card.SetPlan(plan);
            if (!string.IsNullOrEmpty(validationError)) card.SetValidationError(validationError);
            var frame = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(0) };
            frame.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
            frame.Child = card;
            var host = new Grid { Background = Brushes.Transparent };
            host.SetResourceReference(Panel.BackgroundProperty, "Brush.Canvas");
            host.Children.Add(frame);
            Window win = NewWindow(host, winW, winH);
            try
            {
                Pump(70);
                if (dragLevel >= 0)
                {
                    card.DebugBeginDrag(dragLevel, dragKind, dragValue);
                    Pump(60);
                }
                CaptureAndLog(win, name, note);
            }
            finally
            {
                try { win.Close(); } catch { }
                Pump(30);
            }
        }

        private static void ShotRangeRebound(string name, ThemeKind theme, LodPlan plan, double winW, double winH, string note)
        {
            ThemeService.Apply(theme);
            var card = new RangeCard { Margin = new Thickness(10) };
            card.SetPlan(plan);
            var frame = new Border { BorderThickness = new Thickness(1) };
            frame.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
            frame.Child = card;
            var host = new Grid();
            host.SetResourceReference(Panel.BackgroundProperty, "Brush.Canvas");
            host.Children.Add(frame);
            Window win = NewWindow(host, winW, winH);
            try
            {
                Pump(70);
                card.DebugBeginDrag(1, RangeHandleKind.Ratio, 0.0);   // 拖到 0% → 非法（必须严格低于上一级但 >0）
                Pump(50);
                card.DebugEndDrag(false);                             // 回弹 + 400ms 红闪
                Pump(60);                                             // 红闪窗口内截图
                CaptureAndLog(win, name, note);
            }
            finally
            {
                try { win.Close(); } catch { }
                Pump(30);
            }
        }

        // ══════════════════════════ 主题往返（证明切换主题后自绘控件重绘正确） ══════════════════════════

        private static void ShotRangeThemeRoundTrip()
        {
            ThemeService.Apply(ThemeKind.Light);
            var card = new RangeCard { Margin = new Thickness(10) };
            card.SetPlan(DefaultPlan());
            var frame = new Border { BorderThickness = new Thickness(1) };
            frame.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
            frame.Child = card;
            var host = new Grid();
            host.SetResourceReference(Panel.BackgroundProperty, "Brush.Canvas");
            host.Children.Add(frame);
            Window win = NewWindow(host, 420, 168);
            try
            {
                Pump(70);
                string lightPath = Path.Combine(_outDir, "range_theme_roundtrip_light.png");
                UiCapture.CaptureWindow(win, lightPath);

                ThemeService.Apply(ThemeKind.Dark);      // 不重建控件，只换令牌字典
                Pump(80);
                string darkPath = Path.Combine(_outDir, "range_theme_roundtrip_dark.png");
                UiCapture.CaptureWindow(win, darkPath);

                ThemeService.Apply(ThemeKind.Light);
                Pump(80);
                string backPath = Path.Combine(_outDir, "range_theme_roundtrip_back.png");
                UiCapture.CaptureWindow(win, backPath);

                var toDark = UiCapture.Compare(lightPath, darkPath);
                var roundTrip = UiCapture.Compare(lightPath, backPath);
                Log.Add("    theme round-trip: 浅→暗 差异 " + toDark.Percent.ToString("0.0") + "%（应 >20%）；浅→暗→浅 差异 " +
                        roundTrip.Different + " 像素（应为 0）");
                if (toDark.Percent < 20.0) { _failures++; Log.Add("  [FAIL] 主题切换没生效（差异过小）"); }
                if (roundTrip.Different != 0) { _failures++; Log.Add("  [FAIL] 主题往返不是 0 差异（有残留）"); }
                Files.Add("range_theme_roundtrip_light/dark/back.png  (420×168)  主题切换重绘 + 往返 0 差异验证");
            }
            finally
            {
                try { win.Close(); } catch { }
                Pump(30);
            }
        }

        // ══════════════════════════ DialogHost ══════════════════════════

        private static void ShotDialogDanger(string name, ThemeKind theme)
        {
            var options = new DialogOptions
            {
                Title = "停止本次生成？",
                Message = "已经生成的 LOD 文件会保留，QC 不再继续写入。未完成的模型可以稍后重新运行。",
                Detail = "正在处理：\n  角色\\survivor_teenangst.qc\n  道具\\crate_large.qc\n已完成 4 / 12 个 QC · 3 个减面进程将被结束",
                OkText = "停止生成",
                CancelText = "继续生成",
                Danger = true
            };
            ShotDialog(name, theme, delegate (DialogHost host) { return host.ConfirmAsync(options); }, options,
                       "危险确认：主按钮红色、默认焦点在取消（Esc 取消，Enter 不触发确认）", null);
        }

        private static void ShotDialogPrompt(string name, ThemeKind theme)
        {
            var options = new DialogOptions
            {
                Title = "输入名称",
                Message = "名称最多 24 个字。",
                InputLabel = "名称",
                InputInitial = "角色·8级 轻量",
                OkText = "保存",
                CancelText = "取消",
                Validate = delegate (string v)
                {
                    if (string.IsNullOrWhiteSpace(v)) return "名称不能为空。";
                    if (v.Trim().Length > 24) return "名称最多 24 个字。";
                    return null;
                }
            };
            ShotDialog(name, theme, delegate (DialogHost host) { return host.PromptAsync(options); }, options,
                       "文本输入：Input.Text 输入框 + 行内校验（此处预填合法名）", null);
        }

        private static void ShotDialogPromptError(string name, ThemeKind theme)
        {
            var options = new DialogOptions
            {
                Title = "输入名称",
                Message = "名称最多 24 个字。",
                InputLabel = "名称",
                InputInitial = string.Empty,
                OkText = "保存",
                CancelText = "取消",
                Validate = delegate (string v)
                {
                    if (string.IsNullOrWhiteSpace(v)) return "名称不能为空。";
                    if (v.Trim().Length > 24) return "名称最多 24 个字。";
                    return null;
                }
            };
            ShotDialog(name, theme, delegate (DialogHost host) { return host.PromptAsync(options); }, options,
                       "文本输入 + 校验失败：行内红字、不关闭、焦点回到输入框",
                       delegate (DialogHost host) { host.DebugAcceptPrimary(); });
        }

        private static void ShotDialogInfo(string name, ThemeKind theme)
        {
            var options = new DialogOptions
            {
                Title = "关于远景工坊",
                Message = "远景工坊 · L4D2 自动 LOD 工具 v" + AppInfo.Version + "\n为模型工程自动生成多级 LOD，并把 $lod 幂等写回 QC。",
                Detail = "运行环境：Windows 10/11 · .NET Framework 4.8\n减面后端：内置 meshoptimizer\n配置文件：%APPDATA%\\L4D2LOD远景工坊\\settings.ini",
                OkText = "知道了"
            };
            ShotDialog(name, theme, delegate (DialogHost host) { return host.ShowInfoAsync(options); }, options,
                       "信息对话框：单个「知道了」按钮、详情区等宽可滚动", null);
        }

        private static void ShotDialog(string name, ThemeKind theme, Func<DialogHost, Task> open, DialogOptions options,
                                       string note, Action<DialogHost> afterOpen)
        {
            ThemeService.Apply(theme);

            var root = new Grid();
            root.SetResourceReference(Panel.BackgroundProperty, "Brush.Canvas");

            // 底层假内容：证明遮罩盖住内容、且标题类信息在遮罩下仍可辨认
            var basePanel = new Border { Margin = new Thickness(24), BorderThickness = new Thickness(1) };
            basePanel.SetResourceReference(Border.BackgroundProperty, "Brush.Panel");
            basePanel.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
            var baseText = new TextBlock
            {
                Text = "主窗口内容层（作业队列 / LOD 方案 / 日志 / HUD）",
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap
            };
            baseText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.InkSoft");
            baseText.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Cjk");
            baseText.FontSize = 12.5;
            basePanel.Child = baseText;
            root.Children.Add(basePanel);

            var dialogs = new DialogHost();
            root.Children.Add(dialogs);

            Window win = NewWindow(root, 660, 440);
            try
            {
                Pump(80);
                Task task = open(dialogs);
                Pump(420);                        // 遮罩 120ms + 面板 160ms 动画 + 焦点落到默认按钮
                if (afterOpen != null)
                {
                    afterOpen(dialogs);
                    Pump(90);
                }
                Log.Add("    " + name + ": focus=" + dialogs.DebugFocusText +
                        "  isOpen=" + dialogs.IsOpen + "  taskDone=" + task.IsCompleted);
                CaptureAndLog(win, name, note);
            }
            finally
            {
                try { win.Close(); } catch { }
                Pump(40);
            }
        }

        // ══════════════════════════ ToastHost ══════════════════════════

        private static void ShotToast(string name, ThemeKind theme, params ToastSpec[] toasts)
        {
            ThemeService.Apply(theme);
            var root = new Grid();
            root.SetResourceReference(Panel.BackgroundProperty, "Brush.Canvas");
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });

            var toasts0 = new ToastHost { Margin = new Thickness(0, 0, 12, 10), Height = 200 };
            Grid.SetRow(toasts0, 0);
            root.Children.Add(toasts0);

            // 假 HUD：证明 Toast 落在 HUD 上方、不遮挡正文
            var hud = new Border { Margin = new Thickness(16, 0, 16, 12), BorderThickness = new Thickness(1) };
            hud.SetResourceReference(Border.BackgroundProperty, "Brush.Panel");
            hud.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
            var hudText = new TextBlock { Text = "HUD 68 · 正在生成 3 / 8 · 剩余约 2 分钟", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            hudText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.InkSoft");
            hudText.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Cjk");
            hudText.FontSize = 12.5;
            hud.Child = hudText;
            Grid.SetRow(hud, 1);
            root.Children.Add(hud);

            Window win = NewWindow(root, 460, 320);
            try
            {
                Pump(60);
                for (int i = 0; i < toasts.Length; i++)
                {
                    toasts0.Show(toasts[i].Title, toasts[i].Sub, toasts[i].Tone, 60000);
                    if (i < toasts.Length - 1) Pump(40);       // 连续 Show 触发重排，最后一帧留给动画
                }
                Pump(260);                                     // 入场动画 160ms + 堆叠重排
                string note = toasts.Length > 1
                    ? "连续 " + toasts.Length + " 条：新的在最下面、旧的上移，最多 3 条"
                    : "单条 Toast：" + ToneName(toasts[0].Tone) + (toasts[0].Sub == null ? "（仅标题，44 高）" : "（标题+副行，58 高）");
                CaptureAndLog(win, name, note);
            }
            finally
            {
                try { win.Close(); } catch { }
                Pump(30);
            }
        }

        // ══════════════════════════ 行为断言：键盘 / 关闭 / 动画开关 ══════════════════════════

        /// <summary>
        /// 用真实的 PreviewKeyDown / ForceCloseForTest 路径驱动对话框，断言主控 --ui-audit 依赖的语义：
        /// Esc=取消、危险确认 Enter 不确认、IsOpen 生命周期、ForceCloseForTest 完成 Task、Toast 动画开关。
        /// </summary>
        private static void RunBehaviourChecks()
        {
            Log.Add("");
            Log.Add("── 行为断言（键盘 / 关闭 / 动画开关） ──");
            Window win = null;
            try
            {
                var root = new Grid();
                root.SetResourceReference(Panel.BackgroundProperty, "Brush.Canvas");
                var dialogs = new DialogHost();
                var toasts = new ToastHost { Height = 160, Width = 360, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 12, 12) };
                root.Children.Add(dialogs);
                root.Children.Add(toasts);
                win = NewWindow(root, 520, 380);
                Pump(90);

                DialogHost.DebugSetAnimations(false);        // 断言不受动画时长影响

                // 1) 打开期间 IsOpen=true、危险确认默认焦点在"取消"
                Task<bool> t1 = dialogs.ConfirmAsync(DangerOptions());
                Pump(70);
                Check("对话框打开后 IsOpen=true", dialogs.IsOpen, "IsOpen=" + dialogs.IsOpen);
                string focus = dialogs.DebugFocusText ?? "(none)";
                Check("危险确认默认焦点在取消（继续生成）", focus.IndexOf("继续生成", StringComparison.Ordinal) >= 0, "focus=" + focus);

                // 2) Esc → 取消关闭，Task 以 false 完成
                dialogs.DebugKey(Key.Escape);
                Pump(90);
                Check("Esc 关闭并完成 Task(false)", !dialogs.IsOpen && t1.IsCompleted && !t1.Result,
                      "IsOpen=" + dialogs.IsOpen + " done=" + t1.IsCompleted + " result=" + (t1.IsCompleted ? t1.Result.ToString() : "-"));

                // 3) 危险确认 Enter 不触发确认
                Task<bool> t2 = dialogs.ConfirmAsync(DangerOptions());
                Pump(70);
                dialogs.DebugKey(Key.Enter);
                Pump(90);
                Check("危险确认 Enter 不确认（防误删）", dialogs.IsOpen && !t2.IsCompleted,
                      "IsOpen=" + dialogs.IsOpen + " done=" + t2.IsCompleted);

                // 4) ForceCloseForTest：同步关闭、移除遮罩/面板、完成挂起 Task
                dialogs.ForceCloseForTest();
                Pump(70);
                Check("ForceCloseForTest 关闭并完成 Task(false)",
                      !dialogs.IsOpen && t2.IsCompleted && !t2.Result && dialogs.Content == null && dialogs.Visibility != Visibility.Visible,
                      "IsOpen=" + dialogs.IsOpen + " done=" + t2.IsCompleted + " content=" + (dialogs.Content == null ? "null" : "存在") +
                      " vis=" + dialogs.Visibility);

                // 5) 普通确认 Enter → true
                var normal = new DialogOptions { Title = "确认继续？", Message = "普通确认，Enter 应触发主按钮。", OkText = "确定", CancelText = "取消" };
                Task<bool> t3 = dialogs.ConfirmAsync(normal);
                Pump(70);
                dialogs.DebugKey(Key.Enter);
                Pump(90);
                Check("普通确认 Enter 触发确定（返回 true）", !dialogs.IsOpen && t3.IsCompleted && t3.Result,
                      "IsOpen=" + dialogs.IsOpen + " done=" + t3.IsCompleted + " result=" + (t3.IsCompleted ? t3.Result.ToString() : "-"));

                // 6) Prompt 取消返回 null；Esc 同样生效
                var prompt = new DialogOptions
                {
                    Title = "重命名",
                    InputLabel = "名称",
                    InputInitial = "预设 A",
                    Validate = delegate (string v) { return string.IsNullOrWhiteSpace(v) ? "不能为空" : null; }
                };
                Task<string> t4 = dialogs.PromptAsync(prompt);
                Pump(70);
                dialogs.DebugKey(Key.Escape);
                Pump(90);
                Check("Prompt Esc 取消返回 null", !dialogs.IsOpen && t4.IsCompleted && t4.Result == null,
                      "IsOpen=" + dialogs.IsOpen + " done=" + t4.IsCompleted);

                // 7) Toast：动画关闭时立即全不透明显示、到点立即移除
                ToastHost.DebugSetAnimations(false);
                toasts.Show("立即显示", "系统关闭动画时不淡入", ToastTone.Ok, 90);
                Check("动画关闭：Toast 立即全不透明", Math.Abs(toasts.DebugFirstAlpha - 1.0) < 1e-6,
                      "alpha=" + toasts.DebugFirstAlpha.ToString("0.###"));
                Pump(220);
                Check("动画关闭：Toast 到点立即移除", toasts.DebugItemCount == 0, "count=" + toasts.DebugItemCount);

                // 8) Toast：动画开启时从透明淡入，160ms 后达到不透明
                ToastHost.DebugSetAnimations(true);
                toasts.Show("淡入中", null, ToastTone.Info, 100000);
                Check("动画开启：Toast 从透明开始", toasts.DebugFirstAlpha <= 0.05,
                      "alpha=" + toasts.DebugFirstAlpha.ToString("0.###"));
                Pump(300);
                Check("动画开启：Toast 在 160ms 内达到不透明", toasts.DebugFirstAlpha >= 0.99,
                      "alpha=" + toasts.DebugFirstAlpha.ToString("0.###"));

                // 9) Rx：Host 不抢鼠标/焦点（回归断言，防止后续误改）
                Check("ToastHost 不抢鼠标（IsHitTestVisible=false）", !toasts.IsHitTestVisible, "IsHitTestVisible=" + toasts.IsHitTestVisible);
                Check("ToastHost 不抢焦点（Focusable=false）", !toasts.Focusable, "Focusable=" + toasts.Focusable);
            }
            catch (Exception ex)
            {
                _failures++;
                Log.Add("  [FAIL] 行为断言异常：" + ex.Message);
            }
            finally
            {
                DialogHost.DebugSetAnimations(null);
                ToastHost.DebugSetAnimations(null);
                if (win != null) { try { win.Close(); } catch { } Pump(40); }
            }
        }

        private static DialogOptions DangerOptions()
        {
            return new DialogOptions
            {
                Title = "停止本次生成？",
                Message = "已经生成的 LOD 文件会保留，QC 不再继续写入。",
                OkText = "停止生成",
                CancelText = "继续生成",
                Danger = true
            };
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) Log.Add("  [PASS] " + name + "   " + detail);
            else { _failures++; Log.Add("  [FAIL] " + name + "   " + detail); }
        }

        // ══════════════════════════ 基础设施 ══════════════════════════

        private static Window NewWindow(UIElement content, double width, double height)
        {
            var win = new Window
            {
                Content = content,
                Width = width,
                Height = height,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                Background = Brushes.Transparent,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            win.Show();
            try { win.Activate(); } catch { }
            win.UpdateLayout();
            UiCapture.DoEvents(3);
            Pump(60);
            return win;
        }

        private static void CaptureAndLog(Window win, string name, string note)
        {
            string path = Path.Combine(_outDir, name + ".png");
            UiCapture.CaptureWindow(win, path);
            bool ok = false;
            long size = 0;
            try
            {
                var fi = new FileInfo(path);
                ok = fi.Exists && fi.Length > 1024;
                size = fi.Exists ? fi.Length : 0;
            }
            catch { }
            if (!ok) _failures++;
            if (ok) Files.Add(name + ".png  (" + Math.Round(win.ActualWidth) + "×" + Math.Round(win.ActualHeight) + ", " + size + " B)  " + note);
            else Log.Add("  [FAIL] " + name + ".png  " + note);
        }

        private static void Pump(int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                UiCapture.DoEvents(1);
                Thread.Sleep(8);
            }
            UiCapture.DoEvents(2);
        }

        private static string SafeAnimationFlag()
        {
            try { return SystemParameters.ClientAreaAnimation ? "on" : "off"; }
            catch { return "unknown"; }
        }

        private static string ToneName(ToastTone tone)
        {
            switch (tone)
            {
                case ToastTone.Ok: return "Ok";
                case ToastTone.Warn: return "Warn";
                case ToastTone.Err: return "Err";
                default: return "Info";
            }
        }

        // ══════════════════════════ 方案 ══════════════════════════

        private static LodPlan DefaultPlan()
        {
            return LodPlan.CreateDefault();
        }

        internal static LodPlan MaxLevelPlan()
        {
            var plan = new LodPlan();
            for (int i = 0; i < LodPlan.MaxLevels; i++)
                plan.Levels.Add(new LodLevel(10 + i * 10, (95 - i * 10) / 100.0));
            return plan;
        }

        private static LodPlan OneLevelPlan()
        {
            var plan = new LodPlan();
            plan.Levels.Add(new LodLevel(40, 0.55));
            return plan;
        }

        private static LodPlan ExtremePlan()
        {
            var plan = new LodPlan();
            plan.Levels.Add(new LodLevel(100, 0.85));
            plan.Levels.Add(new LodLevel(100000, 0.30));
            return plan;
        }
    }
}
