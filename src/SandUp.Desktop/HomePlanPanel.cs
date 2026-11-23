using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SandUp.Application;
using SandUp.Domain;

namespace SandUp.Desktop;

internal sealed class HomePlanPanel : Border
{
    private const double MinBlockWidth = 9;
    private readonly TextBlock work = new() { Name = "HomeWorkTotal" };
    private readonly TextBlock rest = new() { Name = "HomeRestTotal" };
    private readonly UniformGrid blocks = new() { Name = "PlanProgressBlocks", Rows = 1 };
    private readonly ScrollViewer track;
    private PlanProgress? displayed;

    public HomePlanPanel()
    {
        Background = UiTheme.Surface;
        BorderBrush = UiTheme.Line;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(9);
        Padding = new Thickness(11, 5, 11, 6);
        Margin = new Thickness(0, 3, 0, 0);
        var totals = new Grid { ToolTip = "按当天已保存的计时累计，每 5 秒刷新。" };
        totals.ColumnDefinitions.Add(new ColumnDefinition());
        totals.ColumnDefinitions.Add(new ColumnDefinition());
        AddTotal(totals, "今日工作", work, UiTheme.Blue, 0);
        AddTotal(totals, "今日休息", rest, UiTheme.Green, 1);

        track = new ScrollViewer
        {
            Content = blocks, Focusable = true, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden
        };
        track.ScrollChanged += (_, args) => { if (args.ViewportWidthChange != 0) FitStrip(); };
        track.PreviewMouseWheel += (_, args) =>
        {
            if (track.ScrollableWidth <= 0) return;
            track.ScrollToHorizontalOffset(track.HorizontalOffset - args.Delta / 2d);
            args.Handled = true;
        };
        var progress = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        var label = new TextBlock { Text = "计划进度", FontSize = 11, Foreground = UiTheme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
        DockPanel.SetDock(label, Dock.Left);
        progress.Children.Add(label);
        progress.Children.Add(track);
        Child = new StackPanel { Children = { totals, progress } };
        ShowTotalsLoading();
    }

    private static void AddTotal(Grid totals, string label, TextBlock value, Brush accent, int column)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border { Background = accent, Width = 6, Height = 6, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        row.Children.Add(new TextBlock { Text = label, Foreground = UiTheme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        value.Foreground = accent;
        value.FontSize = 15;
        value.FontWeight = FontWeights.SemiBold;
        value.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(value);
        Grid.SetColumn(row, column);
        totals.Children.Add(row);
    }

    public void ShowTotals(long workMs, long restMs)
    {
        work.Text = Duration(workMs, precise: false);
        rest.Text = Duration(restMs, precise: false);
        work.ToolTip = rest.ToolTip = null;
        AutomationProperties.SetHelpText(work, "");
        AutomationProperties.SetHelpText(rest, "");
    }

    public void ShowTotalsLoading() => ShowTotalsStatus("正在读取今日统计…");
    public void ShowTotalsError() => ShowTotalsStatus("今日统计暂不可用，稍后自动重试。");

    private void ShowTotalsStatus(string message)
    {
        work.Text = rest.Text = "—";
        work.ToolTip = rest.ToolTip = message;
        AutomationProperties.SetHelpText(work, message);
        AutomationProperties.SetHelpText(rest, message);
    }

    public void ShowPlan(PlanProgress? plan)
    {
        if (ReferenceEquals(displayed, plan)) return;
        var sameShape = displayed is not null && plan is not null && plan.Modules.SequenceEqual(displayed.Modules);
        displayed = plan;
        if (plan is null)
        {
            blocks.Children.Clear();
            return;
        }
        if (!sameShape)
        {
            blocks.Children.Clear();
            foreach (var module in plan.Modules)
                blocks.Children.Add(new Border { Height = 10, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 2, 3, 2) });
            blocks.Columns = Math.Max(1, blocks.Children.Count);
            FitStrip();
        }

        for (int i = 0; i < plan.Modules.Count; i++)
        {
            var module = plan.Modules[i];
            var tile = (Border)blocks.Children[i];
            bool completed = i < plan.CompletedCount;
            bool current = plan.HasCurrent && i == plan.CompletedCount;
            bool working = module.Kind == SliceKind.Work;
            var color = working ? UiTheme.Blue : UiTheme.Green;
            tile.Background = completed ? color : working ? UiTheme.Work : UiTheme.Rest;
            tile.BorderBrush = current ? color : Brushes.Transparent;
            var description = $"{(working ? "工作" : "休息")} · {Duration(module.DurationMs)} · {(completed ? "已完成" : current ? "当前模块" : "待完成")}";
            tile.ToolTip = description;
            AutomationProperties.SetName(tile, description);
        }
        AutomationProperties.SetName(track, $"计划进度，显示 {plan.Modules.Count} 个模块，已完成 {plan.CompletedCount} 个。");
        track.ToolTip = plan.HasEarlierModules || plan.HasLaterModules ? "长计划展示当前模块附近的进度。" : null;
        var cursor = Math.Min(plan.CompletedCount, plan.Modules.Count - 1);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!ReferenceEquals(displayed, plan) || cursor < 0) return;
            if (cursor == 0) track.ScrollToLeftEnd();
            else if (track.ScrollableWidth > 0)
            {
                var cellWidth = blocks.Width / plan.Modules.Count;
                track.ScrollToHorizontalOffset(Math.Max(0, (cursor + 0.5) * cellWidth - track.ViewportWidth / 2));
            }
        });
    }

    private void FitStrip() => blocks.Width = Math.Max(blocks.Children.Count * MinBlockWidth, track.ViewportWidth);

    private static string Duration(long milliseconds, bool precise = true)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        var parts = new List<string>(3);
        if (time.TotalHours >= 1) parts.Add($"{(long)time.TotalHours} 小时");
        if (time.Minutes > 0) parts.Add($"{time.Minutes} 分");
        if (time.Seconds > 0 && (precise || parts.Count == 0)) parts.Add($"{time.Seconds} 秒");
        return parts.Count == 0 ? "0 分" : string.Join(" ", parts);
    }
}
