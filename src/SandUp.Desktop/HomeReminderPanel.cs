using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SandUp.Application;
using SandUp.Domain;

namespace SandUp.Desktop;

internal sealed class HomeReminderPanel : Border
{
    private static readonly Brush Ink = UiTheme.Text, Muted = UiTheme.Muted, TodayBrush = UiTheme.Green, HistoryBrush = UiTheme.Blue, Track = UiTheme.Line;
    private const double PartLabelWidth = 110;
    private readonly Grid table = new() { MaxWidth = 554 };
    private readonly ColumnDefinition figureColumn = new() { Width = new GridLength(84) };
    private readonly BodyCompanionFigure figure;
    private readonly TextBlock historyHeader = new() { Text = "历史" };
    private readonly TextBlock status = new() { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<BodyPart, MetricRow> metrics = [];
    private Popup? guidePopup;
    private readonly DispatcherTimer guideTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Border? guideTrigger;
    private Window? guideWindow;

    public HomeReminderPanel()
    {
        Name = "HomeDurability";
        Margin = new Thickness(0, 8, 0, 0); Padding = new Thickness(12, 8, 12, 8);
        BorderBrush = UiTheme.Line; BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12); Background = UiTheme.Surface;
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "耐久度", Foreground = Ink, FontSize = 20, FontWeight = FontWeights.SemiBold });
        status.Margin = new Thickness(0, 5, 0, 0); content.Children.Add(status);
        guideTimer.Tick += (_, _) =>
        {
            guideTimer.Stop();
            if (guideTrigger?.IsMouseOver != true && guidePopup?.Child?.IsMouseOver != true && guideTrigger?.IsKeyboardFocusWithin != true)
                CloseGuide();
        };
        Loaded += (_, _) => { guideWindow = Window.GetWindow(this); if (guideWindow is not null) { guideWindow.PreviewKeyDown += DismissGuideOnEscape; guideWindow.Deactivated += DismissGuideOnDeactivate; } };
        Unloaded += (_, _) => { CloseGuide(); if (guideWindow is not null) { guideWindow.PreviewKeyDown -= DismissGuideOnEscape; guideWindow.Deactivated -= DismissGuideOnDeactivate; } guideWindow = null; };
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseGuide(); };
        BuildTable();
        figure = new BodyCompanionFigure(table.RowDefinitions);
        Grid.SetRowSpan(figure, table.RowDefinitions.Count);
        table.Children.Insert(0, figure);
        table.SizeChanged += (_, e) => figureColumn.Width = new GridLength(Math.Clamp(e.NewSize.Width - PartLabelWidth - 248, 84, 196));
        content.Children.Add(table); Child = content;
        ShowLoading();
    }

    private void BuildTable()
    {
        table.Margin = new Thickness(0, 4, 0, 0);
        table.ColumnDefinitions.Add(figureColumn);
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PartLabelWidth) });
        table.ColumnDefinitions.Add(new ColumnDefinition()); table.ColumnDefinitions.Add(new ColumnDefinition());
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddHeader(new TextBlock { Text = "今日" }, 2); AddHeader(historyHeader, 3);
        int rowIndex = 1;
        foreach (var part in BodyPartDisplay.Order)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var identity = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            identity.Children.Add(new BodyMetricIcon(part) { Width = 24, Height = 24 });
            identity.Children.Add(new TextBlock { Text = BodyPartDisplay.Name(part), Foreground = Ink, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
            var trigger = CreateGuideTrigger(part, identity);
            Grid.SetRow(trigger, rowIndex); Grid.SetColumn(trigger, 1); table.Children.Add(trigger);
            var today = new MetricCell(BodyPartDisplay.Name(part) + "今日耐久度", TodayBrush);
            var past = new MetricCell(BodyPartDisplay.Name(part) + "历史耐久度", HistoryBrush);
            Grid.SetRow(today.Container, rowIndex); Grid.SetColumn(today.Container, 2); table.Children.Add(today.Container);
            Grid.SetRow(past.Container, rowIndex); Grid.SetColumn(past.Container, 3); table.Children.Add(past.Container);
            metrics.Add(part, new(today, past)); rowIndex++;
        }
    }

    private Border CreateGuideTrigger(BodyPart part, UIElement identity)
    {
        var trigger = new Border { Child = identity, Background = Brushes.Transparent, Focusable = true };
        AutomationProperties.SetName(trigger, BodyPartDisplay.Name(part) + "休息建议");
        var entry = BodyPartGuide.For(part);
        AutomationProperties.SetHelpText(trigger, $"{entry.Activity} {entry.Risk}");
        KeyboardNavigation.SetIsTabStop(trigger, true);
        trigger.MouseEnter += (_, _) => OpenGuide(trigger, part);
        trigger.MouseLeave += (_, _) => ScheduleGuideClose();
        trigger.GotKeyboardFocus += (_, _) => { trigger.Background = UiTheme.Hover; OpenGuide(trigger, part); };
        trigger.LostKeyboardFocus += (_, _) => { trigger.Background = Brushes.Transparent; ScheduleGuideClose(); };
        return trigger;
    }

    private void OpenGuide(Border trigger, BodyPart part)
    {
        guideTimer.Stop();
        if (guidePopup?.IsOpen == true && guideTrigger == trigger) return;
        CloseGuide();
        guideTrigger = trigger;
        figure.Highlight(part);
        var guide = BodyPartGuide.For(part);
        var content = new StackPanel();
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        heading.Children.Add(new BodyMetricIcon(part) { Width = 36, Height = 36 });
        heading.Children.Add(new TextBlock { Text = BodyPartDisplay.Name(part), FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        content.Children.Add(heading);
        static TextBlock Label(string title) => new() { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Ink, Margin = new Thickness(0, 0, 0, 3) };
        static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ink, FontSize = 13, LineHeight = 20 };
        var action = new StackPanel();
        action.Children.Add(Label("休息方式"));
        action.Children.Add(Paragraph(guide.Activity));
        content.Children.Add(new Border { Child = action, Background = UiTheme.Rest, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 0, 0, 10) });
        content.Children.Add(Label("久坐伏案"));
        content.Children.Add(Paragraph(guide.Risk));
        content.Children.Add(new TextBlock { Text = "依据", Foreground = Muted, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 9, 0, 0) });
        content.Children.Add(new TextBlock { Text = guide.Evidence, Foreground = Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11, LineHeight = 16 });
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(120, SystemParameters.WorkArea.Height - 60) };
        var card = new Border { Child = scroll, Width = 300, MaxWidth = Math.Max(180, SystemParameters.WorkArea.Width - 32), Padding = new Thickness(14), Background = UiTheme.Raised, BorderBrush = UiTheme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10) };
        TextElement.SetFontFamily(card, UiTypography.Family);
        card.MouseEnter += (_, _) => { if (guidePopup?.Child == card) guideTimer.Stop(); };
        card.MouseLeave += (_, _) => { if (guidePopup?.Child == card) ScheduleGuideClose(); };
        // WPF may keep the old PopupRoot attached until its native window closes.
        guidePopup = new Popup { Placement = PlacementMode.Right, AllowsTransparency = true, StaysOpen = true,
            Child = card, PlacementTarget = trigger };
        Tag = guidePopup;
        guidePopup.IsOpen = true;
    }

    private void ScheduleGuideClose() { guideTimer.Stop(); guideTimer.Start(); }
    private void CloseGuide()
    {
        guideTimer.Stop();
        var closing = guidePopup;
        guidePopup = null;
        guideTrigger = null;
        figure.Highlight(null);
        Tag = null;
        if (closing is not null) closing.IsOpen = false;
    }
    private void DismissGuideOnEscape(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) CloseGuide(); }
    private void DismissGuideOnDeactivate(object? sender, EventArgs e) => CloseGuide();

    private void AddHeader(TextBlock header, int column)
    {
        header.Foreground = Ink;
        header.FontSize = 12;
        header.FontWeight = FontWeights.SemiBold;
        header.HorizontalAlignment = HorizontalAlignment.Left;
        header.Margin = new Thickness(4, 0, 0, 2);
        Grid.SetColumn(header, column); table.Children.Add(header);
    }

    public void ShowLoading() => ShowMessage("正在读取…");
    public void ShowError() => ShowMessage("耐久度暂不可用。");
    private void ShowMessage(string message) { CloseGuide(); status.Text = message; status.Visibility = Visibility.Visible; table.Visibility = Visibility.Collapsed; }

    public void ShowDays(DateOnly date, IReadOnlyList<DaySummary> days)
    {
        var current = days.FirstOrDefault(day => day.LocalDate == date);
        var levels = current?.Reminder.Levels ?? ReminderLevels.Full;
        var history = current?.Reminder.History ?? ReminderLevels.Full;
        var historyDays = current?.Reminder.HistoryDays ?? 0;
        foreach (var part in BodyPartDisplay.Order)
        {
            metrics[part].Today.Set(levels.For(part));
            metrics[part].History.Set(historyDays == 0 ? null : history.For(part));
        }
        status.Visibility = Visibility.Collapsed; table.Visibility = Visibility.Visible;
        historyHeader.ToolTip = historyDays == 0 ? "此前暂无记录。" : $"此前 {historyDays} 个有记录日的实际结余逐日加权计算。";
    }

    private sealed record MetricRow(MetricCell Today, MetricCell History);
    private sealed class MetricCell
    {
        public Grid Container { get; } = new() { Margin = new Thickness(4, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center };
        private readonly ProgressBar bar;
        private readonly TextBlock value = new() { Foreground = Ink, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        public MetricCell(string name, Brush foreground)
        {
            Container.ColumnDefinitions.Add(new ColumnDefinition());
            Container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6, Background = Track, Foreground = foreground,
                BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(value, 1);
            AutomationProperties.SetName(bar, name); Container.Children.Add(bar); Container.Children.Add(value);
        }
        public void Set(double? amount)
        {
            bar.Value = amount is double n ? Math.Clamp(n, 0, 100) : 0;
            value.Text = amount is double number ? $"{Math.Clamp(number, 0, 100):0.#}" : "—";
            AutomationProperties.SetHelpText(bar, amount is null ? "无历史记录" : value.Text);
        }
    }
}
