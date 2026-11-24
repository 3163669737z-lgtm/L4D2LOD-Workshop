using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LodStudio.Engine;

namespace LodStudio.Cli.UiAudit
{
    internal static class WorkflowAssertions
    {
        public static void Run(ShellAuditSession s, AuditReport report)
        {
            report.Section("7. 导入、计划与写入交互");
            s.ResetToRestState();
            s.SetSize(1120, 720);
            string project = Path.Combine(s.OutDir, "workflow", "带有长中文路径的工程测试副本", "models", "survivors");
            Directory.CreateDirectory(project);
            WriteModel(Path.Combine(project, "mesh.smd"));
            string first = WriteQc(project, "需要核对写入方式和档位的第一项");
            string second = WriteQc(project, "重新扫描后加入的第二项");
            string firstBefore = File.ReadAllText(first);
            string secondBefore = File.ReadAllText(second);

            var vm = s.Vm;
            bool initialQuality = vm.Settings.QualityFirst;
            try
            {
                Wait(s, () => vm.AddPathsAsync(new[] { first, first, Path.Combine(project, "missing.dmx") }));
                report.Check("导入有效文件并拦下重复与不存在的路径",
                    vm.Queue.Count == 1 && vm.Queue.Items[0].Scanned &&
                    vm.StatusSub.Contains("重复 1") && vm.StatusSub.Contains("无法识别 1"), vm.StatusSub);
                Wait(s, vm.PreviewPlanAsync);
                var original = vm.PlanPreview;
                report.Check("手动预览读取真实队列并显示原 QC 写入方式",
                    original?.Plan.QcPlans.Count == 1 && vm.CanStart && original.WriteText.Contains("$lod"),
                    original?.WriteText ?? "");

                vm.Settings.Animations = !vm.Settings.Animations;
                vm.OnSettingsChanged();
                vm.Queue.SearchText = "仅改变搜索条件";
                report.Check("外观与搜索变化保留当前预览", vm.CanStart && original?.IsStale == false, vm.StartHint);
                vm.Queue.SearchText = "";

                vm.LodPlan.Levels[0].DistanceText = "NaN";
                report.Check("无效数字输入停用生成与预览", !vm.CanStart && !vm.CanPreview && original?.IsStale == false, vm.StartHint);
                vm.LodPlan.NormalizeLevelText(0);
                vm.LodPlan.SetDistance(0, 12);
                vm.OnWriteModeChanged(1);
                Wait(s, vm.StartFromPlanAsync);
                report.Check("档位和写入方式变更后拦截旧计划",
                    original?.IsStale == true && ReferenceEquals(vm.PlanPreview, original) && !vm.CanStart &&
                    s.Named("StalePlanNotice").IsVisible && !s.Dialogs.IsOpen, vm.StatusTitle);

                Wait(s, () => vm.AddPathsAsync(new[] { second }));
                Wait(s, vm.PreviewPlanAsync);
                var refreshed = vm.PlanPreview;
                report.Check("重新扫描纳入新增 QC 与当前参数",
                    refreshed?.Plan.QcPlans.Count == 2 && vm.CanStart &&
                    refreshed.LevelsText.Contains("12 HU") && refreshed.WriteText.Contains("$include"), refreshed?.LevelsText ?? "");

                vm.Queue.Selected = new List<ViewModels.TaskItemVm> { vm.Queue.Items[1] };
                vm.MoveSelected(-1);
                report.Check("队列顺序变更使预览过期", refreshed?.IsStale == true && !vm.CanStart, vm.StatusTitle);
                vm.Settings.QualityFirst = !initialQuality;
                vm.OnSettingsChanged();
                Wait(s, vm.PreviewPlanAsync);
                var ready = vm.PlanPreview;
                report.Check("刷新后的预览采用最新队列顺序",
                    ready?.Plan.QcPlans.FirstOrDefault()?.QcPath == second && vm.CanStart, ready?.Headline ?? "");

                var start = (Button)s.Named("BtnStart");
                s.Win.Dispatcher.BeginInvoke(new Action(() =>
                {
                    start.Focus();
                    start.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, start));
                }));
                if (!UiPump.Until(() => s.Dialogs.IsOpen, 10000))
                    throw new TimeoutException("写入确认未出现 · " + vm.StatusTitle + " · " + vm.StartHint);
                var text = new List<TextBlock>();
                UiTree.CollectAll(s.Dialogs, text);
                string confirmation = string.Join("\n", text.Select(t => t.Text));
                bool current = confirmation.Contains("$include") && confirmation.Contains("12 HU") &&
                    confirmation.Contains("2 个 QC") && vm.IsBusy && !vm.CanEdit;
                s.RaiseKey(Keyboard.FocusedElement as UIElement ?? s.Dialogs, Key.F5);
                report.Check("实际写入确认采用更新后的计划与参数", current && s.Dialogs.IsOpen, vm.StatusTitle);

                s.RaiseKey(Keyboard.FocusedElement as UIElement ?? s.Dialogs, Key.Escape);
                bool returned = UiPump.Until(() => !s.Dialogs.IsOpen && vm.CanEdit, 2500);
                report.Check("取消写入后返回同一预览并恢复生成入口",
                    returned && vm.CanStart && ReferenceEquals(vm.PlanPreview, ready) &&
                    File.ReadAllText(first) == firstBefore && File.ReadAllText(second) == secondBefore &&
                    !Directory.EnumerateFiles(project, "*.lodbak").Any(), vm.StatusTitle);
                report.Check("取消确认后恢复主操作焦点", Keyboard.FocusedElement == start, s.Dialogs.DebugFocusText);

                vm.Settings.QualityFirst = initialQuality;
                vm.OnSettingsChanged();
                report.Check("有效高级设置变化使同模式预览过期", ready?.IsStale == true && !vm.CanStart, vm.StatusTitle);
                vm.LodPlan.SelectedPresetIndex = PresetLibrary.AutomaticIndex;
                Wait(s, vm.PreviewPlanAsync);
                report.Check("自动挡隐藏手动控件并显示无需处理的模型",
                    s.Named("LodTableHost").Visibility == Visibility.Collapsed &&
                    !s.Named("SegWriteMode").IsVisible && vm.PlanPreview?.Plan.Automatic == true && !vm.CanStart,
                    vm.PlanPreview?.Subline ?? "");

                s.RaiseKey((UIElement)s.Named("BtnClosePlan"), Key.Escape);
                UiPump.Frames(3);
                report.Check("Esc 从预览返回队列", !vm.IsPlanVisible && s.Named("QueuePane").IsVisible, vm.StatusTitle);
                vm.Queue.Clear();
                vm.Queue.Add(new TaskRef(TaskKind.Qc, Path.Combine(project, "已移走的工程.qc")), out _);
                Wait(s, vm.PreviewPlanAsync);
                var issue = (Button)s.Named("BtnShowIssues");
                report.Check("已丢失的工程在计划和日志中显示原因",
                    vm.PlanPreview?.ErrorCount == 1 && !vm.CanStart && issue.IsVisible, vm.PlanPreview?.Rows[0].Summary ?? "");
                issue.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, issue));
                report.Check("查看日志直接展开错误筛选", vm.IsLogExpanded && vm.Log.Filter == 1 && !vm.Log.IsEmpty, vm.Log.CountText);
            }
            finally
            {
                vm.Settings.QualityFirst = initialQuality;
                s.ResetToRestState();
            }
        }

        private static void Wait(ShellAuditSession s, Func<Task> action)
        {
            var task = s.Win.Dispatcher.InvokeAsync(action).Task.Unwrap();
            if (!UiPump.WaitTask(task, 20000)) throw new TimeoutException("等待界面操作超时");
            task.GetAwaiter().GetResult();
            UiPump.Frames(3);
        }

        private static string WriteQc(string dir, string name)
        {
            string path = Path.Combine(dir, name + ".qc");
            File.WriteAllText(path, "$modelname \"models/audit.mdl\"\n$body \"mesh\" \"mesh.smd\"\n", new UTF8Encoding(false));
            return path;
        }

        private static void WriteModel(string path)
        {
            var mesh = new SmdModel { SourcePath = path };
            mesh.Bones.Add(new SmdBone { Index = 0, Name = "root", Parent = -1 });
            for (int i = 0; i < 240; i++)
            {
                mesh.Triangles.Add(new SmdTriangle
                {
                    Material = "mesh",
                    V0 = new SmdVertex { X = i, Nz = 1 },
                    V1 = new SmdVertex { X = i + 1, Nz = 1 },
                    V2 = new SmdVertex { X = i, Y = 1, Nz = 1 }
                });
            }
            SmdCodec.Write(path, mesh);
        }
    }
}
