using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SandUp.Application;
using SandUp.Domain;

namespace SandUp.Desktop;

public partial class RecordsDashboard : UserControl
{
    private const double PlotHeight = 99;
    private const long MinuteMs = 60_000;
    private static readonly double[] AxisScales = [10, 20, 30, 60, 90, 120, 180, 240, 360, 480, 720, 1440];
    private readonly IHistoryService history;
    private readonly IReadOnlyList<ActivityDefinition> activities;
    private readonly Func<SessionRecord, Task> editActivities;
    private readonly Dictionary<DateOnly, RadioButton> columns = new();
    private IReadOnlyList<DaySummary> days = [];
    private DateOnly today = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly periodEnd;
    private DateOnly selectedDay;
    private int periodLength = 7;
    private int generation;
    private bool syncingSelection;

    public RecordsDashboard(IHistoryService history, IReadOnlyList<ActivityDefinition> activities,
        Func<SessionRecord, Task> editActivities, Func<Task> export, Func<Task> clear)
    {
        InitializeComponent();
        this.history = history;
        this.activities = activities;
        this.editActivities = editActivities;
        selectedDay = periodEnd = today;
        SelectedDate.DisplayDateEnd = today.ToDateTime(TimeOnly.MinValue);
        SyncSelection();
        SevenDays.Checked += async (_, _) => await SetPeriodAsync(7);
        ThirtyDays.Checked += async (_, _) => await SetPeriodAsync(30);
        SelectedDate.SelectedDateChanged += async (_, _) =>
        {
            if (syncingSelection) return;
            if (SelectedDate.SelectedDate is DateTime date) await SelectDayAsync(DateOnly.FromDateTime(date));
            else SyncSelection();
        };
        SelectedDate.DateValidationError += (_, args) =>
        {
            args.ThrowException = false;
            ShowFeedback("请选择今天或更早的日期。");
        };
        ActivityCards.SizeChanged += (_, args) => ActivityCards.Columns = args.NewSize.Width >= 420 ? 4 : 2;
        AddAction(ManagementActions, "刷新统计", RefreshAsync);
        AddAction(ManagementActions, "导出全部记录", export);
        AddAction(ManagementActions, "清空已结束记录", clear);
    }

    public Task RefreshAsync()
    {
        var currentDate = DateOnly.FromDateTime(DateTime.Today);
        if (periodEnd == today && selectedDay == today)
            selectedDay = periodEnd = currentDate;
        today = currentDate;
        SelectedDate.DisplayDateEnd = today.ToDateTime(TimeOnly.MinValue);
        SyncSelection();
        return LoadAsync(reloadTrend: true);
    }

    public void ShowFeedback(string message) => Feedback.Text = message;

    private Task SetPeriodAsync(int length)
    {
        if (periodLength == length) return Task.CompletedTask;
        periodLength = length;
        if (selectedDay < Period().From) selectedDay = periodEnd;
        SyncSelection();
        return LoadAsync(reloadTrend: true);
    }

    private Task SelectDayAsync(DateOnly day)
    {
        if (day == selectedDay) return Task.CompletedTask;
        selectedDay = day;
        var range = Period();
        bool outsidePeriod = day < range.From || day > range.To;
        if (outsidePeriod) periodEnd = day;
        SyncSelection();
        return LoadAsync(reloadTrend: outsidePeriod || days.Count == 0);
    }

    private DateRange Period() => new(DateOnly.FromDayNumber(Math.Max(0, periodEnd.DayNumber - periodLength + 1)), periodEnd);

    private void SyncSelection()
    {
        syncingSelection = true;
        try
        {
            SelectedDate.SelectedDate = selectedDay.ToDateTime(TimeOnly.MinValue);
            if (columns.TryGetValue(selectedDay, out var column)) column.IsChecked = true;
        }
        finally { syncingSelection = false; }
    }

    private async Task LoadAsync(bool reloadTrend)
    {
        var request = ++generation;
        var day = selectedDay;
        var range = Period();
        OverviewDate.Text = $"{(day == today ? "今日概览" : "单日概览")} · {day:yyyy/MM/dd}";
        WorkTotal.Text = RestTotal.Text = CompletedTotal.Text = WorkAverage.Text = RestAverage.Text = "—";
        WorkAverageGroup.ToolTip = RestAverageGroup.ToolTip = null;
        ShowFeedback("");
        SessionRows.Children.Clear();
        DayDurations.Text = DayOutcomes.Text = DayAverages.Text = DayReminder.Text = "";
        ActivityPanel.Visibility = Visibility.Collapsed;
        DetailMessage.Text = "正在读取轮次…";
        if (reloadTrend)
        {
            days = [];
            columns.Clear();
            ChartDays.Children.Clear();
            ChartLabels.Children.Clear();
            ChartLabels.ColumnDefinitions.Clear();
            SetChartMessage("正在读取统计…");
            TrendPeriod.Text = $"{range.From:M/d} — {range.To:M/d}";
            AxisMax.Text = AxisMid.Text = "";

            try
            {
                var result = await history.GetDaysAsync(range);
                if (request != generation) return;
                if (today != DateOnly.FromDateTime(DateTime.Today)) { await RefreshAsync(); return; }
                days = result;
                RenderChart();
            }
            catch
            {
                if (request != generation) return;
                SetChartMessage("统计暂时无法读取。");
            }
        }

        var summary = days.FirstOrDefault(item => item.LocalDate == day);
        if (summary is not null)
        {
            RenderDayOverview(summary);
            DayDurations.Text = $"工作 {Duration(summary.WorkMs)}   ·   休息 {Duration(summary.RestMs)}";
            DayOutcomes.Text = $"完成 {summary.CompletedCount}    跳过休息 {summary.SkippedCount}    提前结束 {summary.EndedEarlyCount}    中断 {summary.InterruptedCount}";
            DayAverages.Text = $"每轮平均工作 {AverageDuration(summary.WorkRounds)}   ·   平均休息 {AverageDuration(summary.RestRounds)}";
            DayReminder.Text = summary.Reminder.HasRecords
                ? "当日耐久度  ·  " + string.Join("    ", BodyPartDisplay.Order.Select(part =>
                    $"{BodyPartDisplay.Name(part)} {summary.Reminder.Levels.For(part):0.#}"))
                : "当日耐久度  ·  当日暂无记录";
            RenderActivityCounts(summary.ActivityCounts);
        }
        await LoadSessionsAsync(day, null, request);
    }

    private void RenderDayOverview(DaySummary day)
    {
        WorkTotal.Text = Duration(day.WorkMs);
        RestTotal.Text = Duration(day.RestMs);
        CompletedTotal.Text = $"{day.CompletedCount} 轮";
        WorkAverage.Text = AverageDuration(day.WorkRounds);
        RestAverage.Text = AverageDuration(day.RestRounds);
        WorkAverageGroup.ToolTip = $"{day.WorkRounds.RoundCount} 轮有工作计时。按已结束轮次的实际工作时间计算，包含延后收尾。";
        RestAverageGroup.ToolTip = $"{day.RestRounds.RoundCount} 轮有休息计时。按已结束轮次的实际休息时间计算，跳过休息的轮次不计入。";
    }

    private void RenderChart()
    {
        var maximum = days.Max(day => (day.WorkMs + day.RestMs) / (double)MinuteMs);
        var ceiling = AxisScales.FirstOrDefault(scale => scale >= maximum, Math.Ceiling(maximum / 60) * 60);
        AxisMax.Text = $"{ceiling:0} 分";
        AxisMid.Text = $"{ceiling / 2:0} 分";
        SetChartMessage(maximum == 0 ? "这段时间还没有工作或休息计时。" : "");

        bool dense = days.Count > 7;
        for (int index = 0; index < days.Count; index++) ChartLabels.ColumnDefinitions.Add(new ColumnDefinition());
        for (int index = 0; index < days.Count; index++)
        {
            var day = days[index];
            var header = $"{day.LocalDate:M月d日} · 工作 {Duration(day.WorkMs)} · 休息 {Duration(day.RestMs)}";
            var column = new RadioButton
            {
                Style = (Style)FindResource("ChartDay"),
                Content = ChartColumn(day, ceiling, dense),
                IsChecked = day.LocalDate == selectedDay,
                ToolTip = header
            };
            AutomationProperties.SetName(column, header);
            columns.Add(day.LocalDate, column);
            ChartDays.Children.Add(column);
            column.Checked += async (_, _) =>
            {
                if (!syncingSelection) await SelectDayAsync(day.LocalDate);
            };
            if (!dense || index == days.Count - 1 || (index % 5 == 0 && index < days.Count - 3))
                AddDateLabel(day.LocalDate, index, dense);
        }
    }

    private void RenderActivityCounts(IReadOnlyDictionary<string, int> counts)
    {
        ActivityCards.Children.Clear();
        foreach (var activity in activities)
        {
            var item = new DockPanel();
            var count = new TextBlock
            {
                Text = $"{counts.GetValueOrDefault(activity.ActivityId)} 次",
                Foreground = UiTheme.Text,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(count, Dock.Right);
            item.Children.Add(count);
            item.Children.Add(new TextBlock
            {
                Text = activity.DisplayName, Foreground = UiTheme.Muted,
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            });
            ActivityCards.Children.Add(new Border
            {
                Child = item, Background = UiTheme.Rest,
                CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 6, 5),
                Padding = new Thickness(8, 6, 8, 6)
            });
        }
        ActivityPanel.Visibility = Visibility.Visible;
    }

    private Grid ChartColumn(DaySummary day, double ceiling, bool dense)
    {
        var cell = new Grid { Height = 102, VerticalAlignment = VerticalAlignment.Top };
        var bars = new StackPanel { Width = dense ? 6 : 19, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
        void AddBar(long millis, Brush color, bool isTop)
        {
            if (millis <= 0) return;
            bars.Children.Add(new Border
            {
                Height = PlotHeight * millis / (ceiling * MinuteMs),
                Background = color,
                CornerRadius = isTop ? new CornerRadius(3, 3, 0, 0) : new CornerRadius(0)
            });
        }
        AddBar(day.RestMs, UiTheme.Green, true);
        AddBar(day.WorkMs, UiTheme.Blue, day.RestMs == 0);
        if (bars.Children.Count == 0)
            bars.Children.Add(new Border { Height = 2, Background = UiTheme.Line, CornerRadius = new CornerRadius(1) });
        cell.Children.Add(bars);
        return cell;
    }

    private void AddDateLabel(DateOnly day, int index, bool dense)
    {
        bool first = dense && index == 0;
        bool last = dense && index == days.Count - 1;
        var label = new TextBlock
        {
            Text = day.ToString(!dense || first || last || day.Day == 1 ? "M/d" : "%d", CultureInfo.InvariantCulture),
            FontSize = 10,
            Foreground = UiTheme.Muted,
            HorizontalAlignment = first ? HorizontalAlignment.Left : last ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, !dense ? index : first ? 0 : last ? index - 2 : index - 1);
        Grid.SetColumnSpan(label, dense ? 3 : 1);
        ChartLabels.Children.Add(label);
    }

    private async Task<bool> LoadSessionsAsync(DateOnly day, string? cursor, int request)
    {
        try
        {
            var result = await history.GetSessionsAsync(new(new(day, day), cursor, 100));
            if (request != generation) return false;
            if (today != DateOnly.FromDateTime(DateTime.Today)) { await RefreshAsync(); return false; }
            DetailMessage.Text = "当天开始的轮次";
            if (cursor is null && result.Items.Count == 0)
                DetailMessage.Text = days.Any(item => item.LocalDate == day && (item.WorkMs > 0 || item.RestMs > 0))
                    ? "这一天没有开始的轮次。跨午夜延续的计时仍计入上方时长。"
                    : "这一天还没有轮次记录。";
            foreach (var session in result.Items) SessionRows.Children.Add(CreateSessionRow(session));
            if (result.NextCursor is string next)
            {
                var more = IconButtons.Create("加载更多");
                more.Click += async (_, _) =>
                {
                    more.IsEnabled = false;
                    if (await LoadSessionsAsync(day, next, request)) SessionRows.Children.Remove(more);
                    else more.IsEnabled = true;
                };
                SessionRows.Children.Add(more);
            }
            return true;
        }
        catch
        {
            if (request == generation)
            {
                DetailMessage.Text = "轮次读取失败，请重试。";
            }
            return false;
        }
    }

    private Border CreateSessionRow(SessionRecord session)
    {
        var layout = new DockPanel();
        if (session.Outcome != SessionOutcome.Active)
        {
            var action = AddAction(layout, "补填或修改活动", () => editActivities(session));
            action.Width = action.Height = 36;
            action.Padding = new Thickness(5);
            action.Margin = new Thickness(6, 0, 0, 0);
            action.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(action, Dock.Right);
        }
        var outcome = session.Outcome switch
        {
            SessionOutcome.Active => "进行中",
            SessionOutcome.Completed => "计时完成",
            SessionOutcome.Skipped => "跳过休息",
            SessionOutcome.EndedEarly => "提前结束",
            _ => "中断"
        };
        var labels = new StackPanel();
        labels.Children.Add(new TextBlock
        {
            Text = $"{session.StartedAtUtc.ToLocalTime():HH:mm}  ·  {outcome}",
            FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = UiTheme.Text
        });
        var activity = session.CheckInStatus == CheckInStatus.Reported ? "已自报活动" : "活动未记录";
        labels.Children.Add(new TextBlock
        {
            Text = $"工作 {Duration(session.WorkMs)}  ·  休息 {Duration(session.RestMs)}  ·  {activity}",
            TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Foreground = UiTheme.Muted, Margin = new Thickness(0, 3, 0, 0)
        });
        layout.Children.Add(labels);
        return new Border
        {
            Child = layout, CornerRadius = new CornerRadius(9), Background = UiTheme.Raised,
            Padding = new Thickness(10, 8, 8, 8), Margin = new Thickness(0, 7, 0, 0)
        };
    }

    private Button AddAction(Panel panel, string label, Func<Task> action)
    {
        var button = IconButtons.Create(label);
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch { ShowFeedback("操作未完成，请重试。"); }
        };
        panel.Children.Add(button);
        return button;
    }

    private void SetChartMessage(string message)
    {
        ChartMessage.Text = message;
        ChartMessage.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Duration(long milliseconds)
    {
        var minutes = milliseconds / MinuteMs;
        if (minutes == 0) return milliseconds > 0 ? "不足 1 分" : "0 分";
        if (minutes < 60) return $"{minutes} 分";
        var hours = minutes / 60;
        return minutes % 60 == 0 ? $"{hours} 小时" : $"{hours} 小时 {minutes % 60} 分";
    }

    private static string AverageDuration(RoundDurationTotals totals)
    {
        if (totals.RoundCount == 0) return "—";
        var seconds = (long)Math.Round(totals.DurationMs / (totals.RoundCount * 1000d), MidpointRounding.AwayFromZero);
        if (seconds == 0) return "不足 1 秒";
        if (seconds < 60) return $"{seconds} 秒";
        var minutes = Duration(seconds * 1000);
        return seconds % 60 == 0 ? minutes : $"{minutes} {seconds % 60} 秒";
    }
}
