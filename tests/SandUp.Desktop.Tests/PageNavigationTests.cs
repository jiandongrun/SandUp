using SandUp.Application;
using SandUp.Desktop;
using SandUp.Domain;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Automation;

namespace SandUp.Desktop.Tests;

[Collection("Desktop windows")]
public sealed class PageNavigationTests
{
    [Fact]
    public async Task StartupSettingRegistersAndRemovesOnlyItsOwnValue()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                var suffix = Guid.NewGuid().ToString("N");
                var registryPath = $@"Software\SandUp.StartupTests.{suffix}";
                var executable = Path.Combine(Path.GetTempPath(), $"SandUp Test {suffix}.exe");
                MainWindow? window = null;
                try
                {
                    File.WriteAllText(executable, "");
                    using (var key = Registry.CurrentUser.CreateSubKey(registryPath))
                        key.SetValue("AnotherApp", "unrelated");

                    var registration = new StartupRegistration(executable, registryPath);
                    var app = new AppStub();
                    window = new MainWindow(app, new HistoryStub(), new ExportStub());
                    window.AddStartupSetting(registration);
                    window.Show();
                    Assert.False(registration.IsRegistered());
                    Walk((DependencyObject)window.Content).OfType<Button>()
                        .Single(button => button.IsVisible && AutomationProperties.GetName(button) == "设置")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var toggle = Walk((DependencyObject)window.Content).OfType<CheckBox>()
                        .Single(option => option.IsVisible && Equals(option.Content, "开机自启动"));
                    Assert.False(toggle.IsChecked);
                    toggle.IsChecked = true;
                    Assert.False(registration.IsRegistered());
                    toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    using (var key = Registry.CurrentUser.OpenSubKey(registryPath))
                    {
                        Assert.Equal($"\"{executable}\" --startup", key!.GetValue("SandUp"));
                        Assert.Equal(RegistryValueKind.String, key.GetValueKind("SandUp"));
                    }
                    toggle.IsChecked = false;
                    toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(registration.IsRegistered());
                    using (var key = Registry.CurrentUser.OpenSubKey(registryPath))
                        Assert.Equal("unrelated", key!.GetValue("AnotherApp"));
                    Assert.Empty(app.Commands);
                    finished.TrySetResult();
                }
                catch (Exception ex) { finished.TrySetException(ex); }
                finally
                {
                    if (window is not null) { window.AllowClose = true; window.Close(); }
                    Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
                    File.Delete(executable);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task MidnightDiscardsLateOldDayAndLoadsNewDay()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                MainWindow? window = null;
                try
                {
                    var now = new DateTime(2026, 11, 19, 23, 59, 59);
                    var oldDate = DateOnly.FromDateTime(now);
                    var history = new HistoryStub();
                    var delayed = new TaskCompletionSource<DaySummary>(TaskCreationOptions.RunContinuationsAsynchronously);
                    bool block = false;
                    history.DayOverride = date =>
                    {
                        var levels = date == oldDate ? new ReminderLevels(20, 20, 20, 20, 20, 20) : ReminderLevels.Full;
                        var past = date == oldDate ? new ReminderLevels(60, 60, 60, 60, 60, 60) : new ReminderLevels(52, 52, 52, 52, 52, 52);
                        var day = history.MakeDay(date) with { Reminder = new(levels, true) { History = past, HistoryDays = 1 } };
                        return block && date == oldDate ? delayed.Task : Task.FromResult(day);
                    };
                    window = new MainWindow(new AppStub(), history, new ExportStub(), localClock: () => now);
                    window.Show();
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var bars = Walk((DependencyObject)window.Content).OfType<ProgressBar>().ToArray();
                    Assert.Equal(20, bars[0].Value);
                    Assert.Equal(60, bars[1].Value);
                    block = true;
                    var refresh = (Task)typeof(MainWindow).GetMethod("RefreshHomeSummaryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null)!;
                    now = now.AddSeconds(1);
                    var check = (Task)typeof(MainWindow).GetMethod("CheckDateAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null)!;
                    await check;
                    Assert.False(bars[0].IsVisible);
                    delayed.SetResult(history.MakeDay(oldDate) with { Reminder = new(new(20, 20, 20, 20, 20, 20), true) { History = new(60, 60, 60, 60, 60, 60), HistoryDays = 1 } });
                    await refresh;
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert.All(bars.Where((_, i) => i % 2 == 0), b => Assert.Equal(100, b.Value));
                    Assert.All(bars.Where((_, i) => i % 2 == 1), b => Assert.Equal(52, b.Value));
                    finished.SetResult();
                }
                catch (Exception e) { finished.SetException(e); }
                finally { if (window is not null) { window.AllowClose = true; window.Close(); } dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task NavigationSeparatesControlsAndPreservesSettingsInput()
    {
        var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async()=>
            {
                MainWindow? window=null;
                try
                {
                    var app=new AppStub();var history=new HistoryStub();
                    window=new MainWindow(app,history,new ExportStub());window.Show();
                    await dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    var root=(DependencyObject)window.Content;
                    T[] Visible<T>() where T:DependencyObject=>Walk(root).OfType<T>().ToArray();
                    async Task Click(string label)
                    {
                        Visible<Button>().Single(b=>AutomationProperties.GetName(b)==label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    }
                    Assert.Equal(new[]{"设置","记录","开始工作"},Visible<Button>().Select(AutomationProperties.GetName));
                    Assert.Equal("SandUp · 沙立", window.Title);
                    Assert.Contains(Visible<TextBlock>(), text => text.Text == "SandUp · 沙立");
                    Assert.All(Visible<Button>(),b=>{Assert.IsNotType<string>(b.Content);Assert.Equal(AutomationProperties.GetName(b),b.ToolTip);});
                    AssertAppFont(Visible<TextBlock>().Single(t => t.Text == "准备好后，开始一轮").FontFamily, FontWeights.Normal);
                    AssertAppFont(window.FontFamily, FontWeights.Bold);
                    Assert.Same(UiTheme.Canvas, window.Background);
                    Assert.Empty(Visible<TextBox>());
                    Assert.Equal("1 小时 20 分", Visible<TextBlock>().Single(t => t.Name == "HomeWorkTotal").Text);
                    Assert.Equal("15 分", Visible<TextBlock>().Single(t => t.Name == "HomeRestTotal").Text);
                    Assert.Equal(640, window.Width);
                    Assert.Equal(580, window.Height);
                    Assert.DoesNotContain(Visible<TextBlock>(), t => t.Text.Contains("上一轮"));
                    Assert.DoesNotContain(Visible<TextBlock>(), t => t.Text.Contains("循环 · 第"));
                    var initialPlan = app.ReadSnapshot().PlanProgress!;
                    var homeBlocks = Visible<UniformGrid>().Single(g => g.Name == "PlanProgressBlocks");
                    var planTop = homeBlocks.TranslatePoint(new Point(), window).Y;
                    app.SetPersistence(PersistenceStatus.Saving);
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert.DoesNotContain(Visible<TextBlock>(), t => t.Text.Contains("正在保存计时记录"));
                    Assert.Equal(planTop, homeBlocks.TranslatePoint(new Point(), window).Y);
                    app.SetPersistence(PersistenceStatus.PendingRetry, "暂时无法写入");
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Contains(Visible<TextBlock>(), t => t.Text.Contains("暂时无法写入"));
                    Assert.Contains(Visible<Button>(), b => AutomationProperties.GetName(b) == "重试保存");
                    app.SetPersistence(PersistenceStatus.Saved);
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Equal(planTop, homeBlocks.TranslatePoint(new Point(), window).Y);
                    Assert.Collection(homeBlocks.Children.OfType<Border>(),
                        tile => Assert.Same(UiTheme.Work, tile.Background), tile => Assert.Same(UiTheme.Rest, tile.Background));
                    var sample = new PlanProgress([
                        new(SliceKind.Work, 1_200_000), new(SliceKind.Rest, 20_000), new(SliceKind.Work, 1_200_000), new(SliceKind.Rest, 600_000)
                    ], 2, true);
                    app.SetProgress(sample);
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Collection(homeBlocks.Children.OfType<Border>(), tile => Assert.Same(UiTheme.Blue, tile.Background),
                        tile => Assert.Same(UiTheme.Green, tile.Background), tile => Assert.Same(UiTheme.Blue, tile.BorderBrush),
                        tile => Assert.Same(UiTheme.Rest, tile.Background));
                    SavePreview(window, "home-plan-progress.png");
                    app.SetProgress(initialPlan);
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Equal(64, Visible<TextBlock>().Single(t => t.Text == "25:00").FontSize);
                    Assert.Equal(20, Visible<TextBlock>().Single(t => t.Text == "准备好后，开始一轮").FontSize);
                    var parts = new[] { "眼睛", "肩颈", "腰背", "腹部", "臀腿", "肛门·前列腺" };
                    var bars = Visible<ProgressBar>();
                    Assert.Equal(12, bars.Length);
                    Assert.Equal(new double[] { 75, 50, 65, 70, 25, 80 }, bars.Where((_, index) => index % 2 == 0).Select(b => b.Value));
                    Assert.Equal(new double[] { 60, 61, 62, 63, 64, 65 }, bars.Where((_, index) => index % 2 == 1).Select(b => b.Value));
                    Assert.All(bars, b => { Assert.Equal(0, b.Minimum); Assert.Equal(100, b.Maximum); });
                    Assert.Equal(parts.SelectMany(p => new[] { p + "今日耐久度", p + "历史耐久度" }), bars.Select(AutomationProperties.GetName));
                    Assert.Contains(Visible<TextBlock>(), t => t.Text == "历史" && t.ToolTip is string tip && tip.Contains("3 个有记录日的实际结余逐日加权计算"));
                    var durability = Visible<Border>().Single(b => b.Name == "HomeDurability");
                    var durabilityLabels = Walk(durability).OfType<TextBlock>().Select(text => text.Text).ToArray();
                    Assert.Contains("耐久度", durabilityLabels);
                    Assert.DoesNotContain("身体耐久度", durabilityLabels);
                    Assert.DoesNotContain(durabilityLabels, label => label.Contains("仅用于休息提醒"));
                    var triggers = Visible<Border>().Where(b => parts.Any(part => AutomationProperties.GetName(b) == part + "休息建议")).ToArray();
                    Assert.Equal(6, triggers.Length);
                    var diagram = Visible<FrameworkElement>().Single(element => AutomationProperties.GetName(element) == "身体部位示意");
                    const System.Reflection.BindingFlags diagramFields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var eyes = Assert.IsType<ScaleTransform>(diagram.GetType().GetField("eyeBlink", diagramFields)!.GetValue(diagram));
                    var blinkTimer = Assert.IsType<DispatcherTimer>(diagram.GetType().GetField("blinkTimer", diagramFields)!.GetValue(diagram));
                    var leftWave = Assert.IsType<RotateTransform>(diagram.GetType().GetField("leftWave", diagramFields)!.GetValue(diagram));
                    var rightWave = Assert.IsType<RotateTransform>(diagram.GetType().GetField("rightWave", diagramFields)!.GetValue(diagram));
                    var figurePlacement = diagram.GetType().GetMethod("FigurePlacement", diagramFields)!;
                    var updateHover = diagram.GetType().GetMethod("UpdateHover", diagramFields)!;
                    Point FigurePoint(double x, double y)
                    {
                        var (scale, left, top) = ((double, double, double))figurePlacement.Invoke(diagram, null)!;
                        return new(left + x * scale, top + y * scale);
                    }
                    void AssertCompanionHitRegions()
                    {
                        Assert.Same(diagram, diagram.InputHitTest(FigurePoint(45, 27)));
                        Assert.Same(diagram, diagram.InputHitTest(FigurePoint(6, 90)));
                        Assert.Same(diagram, diagram.InputHitTest(FigurePoint(84, 90)));
                        Assert.Null(diagram.InputHitTest(FigurePoint(45, 90)));
                    }
                    void HoverCompanion(double x, double y) => updateHover.Invoke(diagram, [FigurePoint(x, y)]);
                    Assert.Equal(SystemParameters.ClientAreaAnimation, blinkTimer.IsEnabled);
                    Assert.True(diagram.TranslatePoint(new Point(diagram.ActualWidth, 0), durability).X <= triggers[0].TranslatePoint(new Point(), durability).X);
                    Assert.All(bars, b => Assert.InRange(b.ActualWidth, 1, 95));
                    var homeScroll = (ScrollViewer)window.Content;
                    Assert.Equal(0, homeScroll.ScrollableHeight);
                    var regularHeight = window.Height;
                    window.Width = window.MinWidth; window.Height = window.MinHeight;
                    window.UpdateLayout();
                    SavePreview(window, "home-compact-narrow.png");
                    Assert.Equal(0, homeScroll.ScrollableHeight);
                    Assert.Equal(0, homeScroll.ScrollableWidth);
                    window.Width = 650; window.Height = regularHeight;
                    window.UpdateLayout();
                    AssertCompanionHitRegions();
                    SaveElementPreview(durability, "home-durability.png");
                    Popup OpenCard() => Assert.IsType<Popup>(durability.Tag);
                    string[] risks = ["干眼", "肩颈痛", "腰痛", "2型糖尿病", "臀部", "痔"];
                    string[] actions = ["眨眼", "转头", "起身", "走动", "走动", "走动"];
                    void MouseEvent(UIElement element, RoutedEvent routedEvent) => element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = routedEvent });
                    for (int i = 0; i < triggers.Length; i++)
                    {
                        var trigger = triggers[i];
                        Assert.True(trigger.Focusable);
                        Assert.Equal(parts[i] + "休息建议", AutomationProperties.GetName(trigger));
                        MouseEvent(trigger, Mouse.MouseEnterEvent);
                        var card = OpenCard();
                        Assert.True(card.IsOpen);
                        Assert.Same(trigger, card.PlacementTarget);
                        AssertAppFont(Walk(card.Child).OfType<TextBlock>().First().FontFamily, FontWeights.Normal);
                        var texts = Walk(card.Child).OfType<TextBlock>().Select(t => t.Text).ToArray();
                        Assert.Collection(texts,
                            text => Assert.Equal(parts[i], text),
                            text => Assert.Equal("休息方式", text),
                            text => Assert.Contains(actions[i], text),
                            text => Assert.Equal("久坐伏案", text),
                            text => Assert.Contains(risks[i], text),
                            text => Assert.Equal("依据", text),
                            text => Assert.False(string.IsNullOrWhiteSpace(text)));
                        if (i == 4) SaveElementPreview((FrameworkElement)card.Child, "home-guide-legs.png");
                        MouseEvent(trigger, Mouse.MouseLeaveEvent);
                        MouseEvent((UIElement)card.Child, Mouse.MouseEnterEvent);
                        await Task.Delay(300);
                        Assert.True(card.IsOpen);
                        MouseEvent((UIElement)card.Child, Mouse.MouseLeaveEvent);
                    }
                    triggers[0].Focus();
                    var firstCard = OpenCard();
                    Assert.True(firstCard.IsOpen);
                    MouseEvent(triggers[1], Mouse.MouseEnterEvent);
                    Assert.False(firstCard.IsOpen);
                    var popup = OpenCard();
                    Assert.Same(triggers[1], popup.PlacementTarget);
                    Assert.Contains(Walk(popup.Child).OfType<TextBlock>(), text => text.Text == "肩颈");
                    var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(triggers[0])!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    triggers[0].RaiseEvent(escape);
                    Assert.False(popup.IsOpen); Assert.False(escape.Handled);
                    triggers[1].Focus();
                    popup = OpenCard();
                    Assert.True(popup.IsOpen);
                    Visible<Button>().Single(b => AutomationProperties.GetName(b) == "设置").Focus();
                    await Task.Delay(300);
                    Assert.False(popup.IsOpen);
                    Assert.Equal(new double[] { 75, 50, 65, 70, 25, 80 }, bars.Where((_, index) => index % 2 == 0).Select(b => b.Value));
                    window.Width = 440; window.UpdateLayout();
                    Assert.All(bars, b => Assert.True(b.ActualWidth > 0));
                    AssertCompanionHitRegions();
                    SaveElementPreview(durability, "home-durability-narrow.png");
                    window.Width = 650;
                    var showDays = durability.GetType().GetMethod("ShowDays")!;
                    showDays.Invoke(durability, [DateOnly.FromDateTime(DateTime.Today), Array.Empty<DaySummary>()]);
                    Assert.Equal(bars, Visible<ProgressBar>());
                    Assert.All(bars.Where((_, index) => index % 2 == 1), b => { Assert.Equal(0, b.Value); Assert.Equal("无历史记录", AutomationProperties.GetHelpText(b)); });
                    Assert.Equal(6, Visible<TextBlock>().Count(t => t.Text == "—"));
                    showDays.Invoke(durability, [DateOnly.FromDateTime(DateTime.Today), new[] { await history.GetDayAsync(DateOnly.FromDateTime(DateTime.Today)) }]);
                    Assert.Empty(history.Ranges);
                    Assert.Empty(Visible<DatePicker>());
                    Assert.DoesNotContain(Visible<TextBlock>(), t => t.Text.Contains("回来接着做什么"));
                    ((ScrollViewer)window.Content).ScrollToTop();
                    SavePreview(window,"home-page.png");
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        await WaitForMotion(() => eyes.ScaleY < .25, TimeSpan.FromSeconds(7));
                        SaveElementPreview(durability, "companion-eyes-closed.png");
                        await WaitForMotion(() => eyes.ScaleY == 1, TimeSpan.FromSeconds(1));
                        SaveElementPreview(durability, "companion-eyes-open.png");

                        HoverCompanion(45, 27);
                        await WaitForMotion(() => eyes.ScaleY < .5, TimeSpan.FromSeconds(1));
                        HoverCompanion(6, 90);
                        await WaitForMotion(() => leftWave.Angle > 74, TimeSpan.FromSeconds(1));
                        Assert.Equal(0, rightWave.Angle);
                        SaveElementPreview(durability, "companion-left-wave.png");
                        await WaitForMotion(() => leftWave.Angle == 0, TimeSpan.FromSeconds(2));
                        HoverCompanion(84, 90);
                        await WaitForMotion(() => rightWave.Angle > 74, TimeSpan.FromSeconds(1));
                        Assert.Equal(0, leftWave.Angle);
                        SaveElementPreview(durability, "companion-right-wave.png");
                    }
                    else
                    {
                        HoverCompanion(45, 27);
                        HoverCompanion(6, 90);
                        Assert.False(eyes.HasAnimatedProperties);
                        Assert.False(leftWave.HasAnimatedProperties);
                    }
                    static async Task WaitForMotion(Func<bool> expected, TimeSpan timeout)
                    {
                        var elapsed = Stopwatch.StartNew();
                        while (!expected() && elapsed.Elapsed < timeout) await Task.Delay(12);
                        Assert.True(expected(), "The companion did not reach the expected animation pose.");
                    }
                    window.Activate();
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    MouseEvent(triggers[0], Mouse.MouseEnterEvent);
                    popup = OpenCard();
                    Assert.True(popup.IsOpen);
                    await Click("设置");
                    Assert.False(popup.IsOpen);
                    Assert.False(blinkTimer.IsEnabled);
                    Assert.False(eyes.HasAnimatedProperties);
                    Assert.False(leftWave.HasAnimatedProperties);
                    Assert.False(rightWave.HasAnimatedProperties);
                    Assert.Empty(Visible<DatePicker>());
                    Assert.Equal(8,Visible<TextBox>().Length);
                    Assert.DoesNotContain(Visible<TextBlock>(), text => text.Text is "循环计划" or "默认时长");
                    Assert.DoesNotContain(Visible<CheckBox>(), box => Equals(box.Content, "使用多层循环"));
                    Assert.All(parts, part => Assert.Contains(Visible<TextBox>(), box => AutomationProperties.GetName(box) == part + "每小时扣分"));
                    Assert.Contains(Visible<Button>(), button => AutomationProperties.GetName(button) == "恢复默认扣分");
                    var work = Visible<TextBox>().Single(box => AutomationProperties.GetName(box) == "工作时长");
                    AssertAppFont(work.FontFamily, FontWeights.Normal);
                    var rest = Visible<TextBox>().Single(box => AutomationProperties.GetName(box) == "休息时长");
                    Assert.Equal("25", work.Text); Assert.Equal("5", rest.Text);
                    var activity = Visible<ComboBox>().Single(box => AutomationProperties.GetName(box) == "休息方式");
                    activity.ApplyTemplate();
                    var activityPopup = Assert.IsType<Popup>(activity.Template.FindName("PART_Popup", activity));
                    activity.IsDropDownOpen = true;
                    Assert.True(activityPopup.IsOpen);
                    activity.IsDropDownOpen = false;
                    activity.SelectedValue = "look_away";
                    Assert.Contains(Visible<TextBlock>(), text => text.Text.Contains("休息 5 分钟 · 远眺"));
                    activity.SelectedValue = "walk";
                    SavePreview(window,"settings-default-modules.png");
                    work.Text="35";
                    var eyeRate = Visible<TextBox>().Single(box => AutomationProperties.GetName(box) == "眼睛每小时扣分");
                    eyeRate.Text = "41";
                    await Click("保存设置");
                    Assert.Empty(app.Commands.OfType<UpdateSettings>());
                    Assert.Contains(Visible<TextBlock>(), t => t.Text.Contains("眼睛每小时扣分须为"));
                    await Click("恢复默认扣分");
                    Assert.Equal("12", eyeRate.Text);
                    Assert.Equal("35", work.Text);
                    eyeRate.Text = "17.5";
                    SavePreview(window,"settings-page.png");
                    await ChooseLeave(window, "保存并离开", () => Click("记录"));
                    Assert.Single(Visible<DatePicker>());Assert.Empty(Visible<TextBox>());
                    Assert.Contains(Visible<Button>(),b=>AutomationProperties.GetName(b)=="导出全部记录");
                    Assert.Contains(Visible<Button>(),b=>AutomationProperties.GetName(b)=="清空已结束记录");
                    Assert.Contains(Visible<Button>(),b=>AutomationProperties.GetName(b)=="补填或修改活动");
                    Assert.Single(history.Ranges);
                    Assert.Contains(Visible<TextBlock>(),t=>t.Text=="工作 1 小时 20 分   ·   休息 15 分");
                    string Metric(string name) => Visible<TextBlock>().Single(text => text.Name == name).Text;
                    string ActivityCount(string activity) => Visible<DockPanel>().Where(panel => panel.Children.OfType<TextBlock>().Any(text => text.Text == activity))
                        .Single().Children.OfType<TextBlock>().First().Text;
                    var today = DateOnly.FromDateTime(DateTime.Today);
                    Assert.Equal($"今日概览 · {today:yyyy/MM/dd}", Metric("OverviewDate"));
                    Assert.Equal("1 小时 20 分", Metric("WorkTotal"));
                    Assert.Equal("15 分", Metric("RestTotal"));
                    Assert.Equal("3 轮", Metric("CompletedTotal"));
                    Assert.Equal("18 分 45 秒", Metric("WorkAverage"));
                    Assert.Equal("5 分", Metric("RestAverage"));
                    Assert.Contains("眼睛 75", Metric("DayReminder"));
                    Assert.Equal("每轮平均工作 18 分 45 秒   ·   平均休息 5 分", Metric("DayAverages"));
                    Assert.Equal("2 次", ActivityCount("起身走动"));
                    Assert.Equal("1 次", ActivityCount("看向远处"));
                    Assert.Equal("1 次", ActivityCount("放松肩手"));
                    Assert.Equal("0 次", ActivityCount("其他活动"));
                    RadioButton[] DayColumns() => Visible<RadioButton>().Where(b=>b.ToolTip is string tip && tip.Contains("· 工作")).ToArray();
                    Assert.Equal(7,DayColumns().Length);
                    SavePreview(window,"records-page.png");
                    var datePicker = Visible<DatePicker>().Single();
                    datePicker.IsDropDownOpen = true;
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var datePopup = Assert.IsType<Popup>(datePicker.Template.FindName("PART_Popup", datePicker));
                    Assert.True(datePopup.IsOpen);
                    var calendar = Assert.IsType<Calendar>(datePopup.Child);
                    SaveElementPreview(calendar, "records-calendar.png");
                    var calendarItem = Assert.IsType<CalendarItem>(calendar.Template.FindName("PART_CalendarItem", calendar));
                    var monthView = Assert.IsType<Grid>(calendarItem.Template.FindName("PART_MonthView", calendarItem));
                    Assert.Equal(42, monthView.Children.OfType<CalendarDayButton>().Count());
                    var header = Assert.IsType<Button>(calendarItem.Template.FindName("PART_HeaderButton", calendarItem));
                    header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(CalendarMode.Year, calendar.DisplayMode);
                    calendar.DisplayMode = CalendarMode.Month;
                    datePicker.IsDropDownOpen = false;
                    DayColumns()[5].IsChecked=true;
                    await dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    Assert.Equal(today.AddDays(-1),history.Queries.Last().Range.From);
                    Assert.Single(history.Ranges);
                    Assert.Equal($"单日概览 · {today.AddDays(-1):yyyy/MM/dd}", Metric("OverviewDate"));
                    Assert.Equal("1 小时 12 分", Metric("WorkTotal"));
                    Assert.Equal("12 分", Metric("RestTotal"));
                    Assert.Contains("眼睛 60", Metric("DayReminder"));
                    Assert.Equal("2 轮", Metric("CompletedTotal"));
                    Assert.Equal("36 分", Metric("WorkAverage"));
                    Assert.Equal("6 分", Metric("RestAverage"));
                    Assert.Equal("每轮平均工作 36 分   ·   平均休息 6 分", Metric("DayAverages"));
                    Assert.Equal("1 次", ActivityCount("其他活动"));
                    Assert.Equal("0 次", ActivityCount("看向远处"));
                    Visible<RadioButton>().Single(b=>AutomationProperties.GetName(b)=="最近 30 天").IsChecked=true;
                    await dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    Assert.Equal(30,DayColumns().Length);
                    Assert.Equal(today.AddDays(-29),history.Ranges.Last().From);
                    Assert.Equal(today.AddDays(-1),history.Queries.Last().Range.From);
                    Assert.Equal("1 小时 12 分", Metric("WorkTotal"));
                    Assert.Equal("36 分", Metric("WorkAverage"));
                    Assert.Equal($"{today.AddDays(-29):M/d} — {today:M/d}", Metric("TrendPeriod"));
                    window.Width=440;SavePreview(window,"records-30-days-narrow.png");window.Width=650;
                    Visible<DatePicker>().Single().SelectedDate=today.AddDays(-60).ToDateTime(TimeOnly.MinValue);
                    await dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    Assert.Equal(today.AddDays(-60),history.Ranges.Last().To);
                    Assert.Equal(today.AddDays(-60),history.Queries.Last().Range.From);
                    Assert.Contains(Visible<TextBlock>(),t=>t.Text=="这段时间还没有工作或休息计时。");
                    Assert.Equal($"单日概览 · {today.AddDays(-60):yyyy/MM/dd}", Metric("OverviewDate"));
                    Assert.Equal("0 分", Metric("WorkTotal"));
                    Assert.Equal("0 分", Metric("RestTotal"));
                    Assert.Equal("0 轮", Metric("CompletedTotal"));
                    Assert.Equal("—", Metric("WorkAverage"));
                    Assert.Equal("—", Metric("RestAverage"));
                    Assert.Equal("0 次", ActivityCount("起身走动"));
                    Assert.Equal("0 次", ActivityCount("其他活动"));
                    SavePreview(window,"records-empty.png");
                    await Click("设置");Assert.Equal("35",work.Text);
                    Assert.True(CyclePlan.Compose([new CycleWork(2100), new CycleRest(300, "walk")], true).SameAs(Assert.Single(app.Commands.OfType<UpdateSettings>()).CyclePlan));
                    Assert.Equal(17.5, Assert.Single(app.Commands.OfType<UpdateSettings>()).Rates!.Eyes);
                    Assert.Contains(Visible<TextBlock>(),t=>t.Text.Contains("设置已保存"));
                    // Validate and save from the same editable module list.
                    work.Text = "invalid";
                    await Click("保存设置");
                    Assert.Single(app.Commands.OfType<UpdateSettings>());
                    work.Text = "35";
                    Visible<Button>().Single(b => Equals(b.Content, "+ 休息")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(Visible<CheckBox>().Single(box => Equals(box.Content, "合并连续模块")).IsChecked);
                    await dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    Assert.Contains(Visible<TextBlock>(), t => t.Text == "休息 10 分钟 · 走动");
                    Visible<Button>().Single(b => Equals(b.Content, "保存为组合")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Single(app.Commands.OfType<UpdateSettings>());
                    Assert.Contains(Visible<TextBlock>(), t => t.Text.Contains("已加入组合1"));
                    window.Height = 1100; window.UpdateLayout();
                    SavePreview(window,"settings-save-as-combination.png");
                    await Click("保存设置");
                    var cycleCommand = app.Commands.OfType<UpdateSettings>().Last();
                    Assert.True(CyclePlan.Compose([new CycleWork(2100), new CycleRest(300, "walk"), new CycleRest(300, "walk")], true).SameAs(cycleCommand.CyclePlan));
                    var arrangement = Assert.IsType<CycleArrangement>(cycleCommand.Arrangement);
                    Assert.Equal(arrangement.Blocks.ToArray(), Assert.Single(arrangement.Combinations).Blocks.ToArray());
                    Assert.DoesNotContain(Visible<TextBlock>(), t => t.Text.Contains("已加入组合1"));
                    Assert.Equal(25, cycleCommand.WorkMinutes); Assert.Equal(5, cycleCommand.RestMinutes);
                    Assert.Contains(Visible<TextBlock>(), t => t.Text.Contains("新计划从下一轮生效"));
                    await Click("记录"); await Click("设置");
                    Assert.Contains(Visible<TextBlock>(), t => t.Text == "休息 10 分钟 · 走动");
                    window.Height = 760;
                    await Click("返回");Assert.Empty(Visible<TextBox>());
                    Assert.Equal(new[]{"设置","记录","开始工作"},Visible<Button>().Select(AutomationProperties.GetName));
                    Assert.Empty(app.Commands.OfType<PauseSession>());
                    finished.TrySetResult();
                }
                catch(Exception ex){finished.TrySetException(ex);}
                finally{if(window is not null){window.AllowClose=true;window.Close();}dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            });
            Dispatcher.Run();
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task SettingsWarnOnDirtyNavigationAndClosingAndDiscardRestoresSavedValues()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                MainWindow? window = null;
                try
                {
                    var app = new AppStub();
                    window = new MainWindow(app, new HistoryStub(), new ExportStub());
                    window.Show();
                    await Click("设置");
                    Assert.Equal("已保存", Badge());
                    var activity = Items<ComboBox>().Single(c => AutomationProperties.GetName(c) == "休息方式");
                    activity.SelectedValue = "look_away";
                    Assert.Equal("未保存", Badge());
                    activity.SelectedValue = "walk";
                    Assert.Equal("已保存", Badge());
                    Items<Button>().Single(button => Equals(button.Content, "新建组合")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("未保存", Badge());
                    Items<Button>().Single(button => AutomationProperties.GetName(button) == "从组合库删除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("已保存", Badge());

                    var merge = Items<CheckBox>().Single(c => Equals(c.Content, "合并连续模块"));
                    merge.IsChecked = false;
                    var work = Items<TextBox>().Single(box => AutomationProperties.GetName(box) == "工作时长");
                    work.Text = "19";
                    Assert.Equal("未保存", Badge());
                    SavePreview(window, "settings-unsaved.png");
                    await ChooseLeave(window, "继续编辑", () => Click("返回"));
                    Assert.Equal("19", work.Text);
                    Assert.Equal("未保存", Badge());
                    await ChooseLeave(window, "继续编辑", Close);
                    Assert.True(window.IsVisible);
                    await ChooseLeave(window, null, Close);
                    Assert.True(window.IsVisible);
                    await ChooseLeave(window, "放弃修改", Close);
                    Assert.False(window.IsVisible);
                    Assert.Empty(app.Commands.OfType<UpdateSettings>());

                    window.Show();
                    Assert.Equal("已保存", Badge());
                    Assert.Equal("25", Items<TextBox>().Single(box => AutomationProperties.GetName(box) == "工作时长").Text);
                    Assert.True(merge.IsChecked);
                    var eyes = Items<TextBox>().Single(box => AutomationProperties.GetName(box) == "眼睛每小时扣分");
                    eyes.Text = "41";
                    await ChooseLeave(window, "保存并离开", () => Click("记录"));
                    Assert.Equal("未保存", Badge());
                    Assert.Contains(Items<TextBlock>(), t => t.Text.Contains("眼睛每小时扣分须为"));

                    eyes.Text = "18";
                    app.RejectSettings = true;
                    await ChooseLeave(window, "保存并离开", () => Click("记录"));
                    Assert.Equal("未保存", Badge());
                    Assert.Equal("18", eyes.Text);
                    Assert.Contains(Items<TextBlock>(), t => t.Text.Contains("设置未保存"));
                    app.RejectSettings = false;
                    await ChooseLeave(window, "保存并离开", () => Click("记录"));
                    Assert.DoesNotContain(Items<TextBlock>(), t => t.Name == "SettingsSaveStatus");
                    await Click("设置");
                    Assert.Equal("已保存", Badge());
                    Assert.Equal(18, app.ReadSnapshot().DefaultSettings.ReminderRates.Eyes);
                    await Close();
                    Assert.False(window.IsVisible);
                    finished.SetResult();

                    T[] Items<T>() where T : DependencyObject => Walk((DependencyObject)window.Content).OfType<T>().ToArray();
                    string Badge() => Items<TextBlock>().Single(item => item.Name == "SettingsSaveStatus").Text;
                    async Task Click(string label)
                    {
                        Items<Button>().Single(b => AutomationProperties.GetName(b) == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    async Task Close()
                    {
                        window.Close();
                        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                }
                catch (Exception e) { finished.SetException(e); }
                finally { if (window is not null) { window.AllowClose = true; window.Close(); } dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task ChooseLeave(Window window, string? choice, Func<Task> leave)
    {
        var respond = window.Dispatcher.InvokeAsync(() =>
        {
            var dialog = window.OwnedWindows.Cast<Window>().SingleOrDefault(item => item.IsVisible && item.Title == "未保存的设置");
            try
            {
                Assert.NotNull(dialog);
                SavePreview(dialog, "settings-leave-confirmation.png");
                if (choice is null) dialog.Close();
                else Walk((DependencyObject)dialog.Content).OfType<Button>().Single(button => Equals(button.Content, choice))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            finally { if (dialog?.IsVisible == true) dialog.Close(); }
        }, DispatcherPriority.Background);
        await leave();
        await respond.Task;
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject node)
    {
        if(node is UIElement element&&element.Visibility!=Visibility.Visible)yield break;
        yield return node;
        foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach(var descendant in Walk(child))yield return descendant;
    }
    internal static void AssertAppFont(FontFamily family, FontWeight weight)
    {
        var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
        Assert.True(typeface.TryGetGlyphTypeface(out var glyphs), $"Could not resolve {family.Source}");
        Assert.Contains("/SandUp;component/Assets/Fonts/", glyphs.FontUri.OriginalString, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(weight, glyphs.Weight);
        Assert.All("计划休息25:00", character => Assert.True(glyphs.CharacterToGlyphMap.ContainsKey(character)));
    }

    private static void SavePreview(Window window,string filename)
    {
        var directory=Environment.GetEnvironmentVariable("SANDUP_TEST_PREVIEWS");
        if(string.IsNullOrEmpty(directory))return;
        Directory.CreateDirectory(directory);window.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(Path.Combine(directory,filename));encoder.Save(file);
    }
    internal static void SaveElementPreview(FrameworkElement element, string filename, Brush? background = null)
    {
        var directory = Environment.GetEnvironmentVariable("SANDUP_TEST_PREVIEWS");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            var bounds = new Rect(element.RenderSize);
            if (background is not null) dc.DrawRectangle(background, null, bounds);
            dc.DrawRectangle(new VisualBrush(element), null, bounds);
        }
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, filename)); encoder.Save(file);
    }
    private sealed class AppStub:IWorkClockApplication
    {
        private Action<AppSnapshot>? observer;
        public List<WorkClockCommand> Commands=[];
        public bool RejectSettings { get; set; }
        private AppSnapshot snapshot=new(1,new(SessionPhase.Idle,1500000,0,0,new HashSet<SessionAction>()),null,null,"",
            new(1500000,300000,30000,new long[]{180000,300000},1),new BuiltinActivityCatalog().List(),new(PersistenceStatus.Saved,null,null),null,new HashSet<CommandType>{CommandType.StartSession})
        { PlanProgress = new PlanProgress([new(SliceKind.Work, 1_500_000), new(SliceKind.Rest, 300_000)], 0, false) };
        public Task<AppSnapshot> InitializeAsync()=>Task.FromResult(snapshot);
        public AppSnapshot ReadSnapshot()=>snapshot;
        public void SetProgress(PlanProgress value)
        {
            snapshot = snapshot with { PlanProgress = value };
            observer?.Invoke(snapshot);
        }
        public void SetPersistence(PersistenceStatus status, string? failureReason = null)
        {
            var revision = status == PersistenceStatus.Saving ? snapshot.ViewRevision : snapshot.ViewRevision + 1;
            snapshot = snapshot with { Persistence = new(status, null, failureReason), ViewRevision = revision };
            observer?.Invoke(snapshot);
        }
        public Task<CommandResult> HandleAsync(CommandEnvelope envelope)
        {
            Commands.Add(envelope.Command);
            if (envelope.Command is UpdateSettings settings)
            {
                if (RejectSettings) return Task.FromResult(new CommandResult(CommandDisposition.Rejected, ErrorCode.StorageUnavailable, snapshot, snapshot.Persistence, null));
                var saved = snapshot.DefaultSettings with { CyclePlan = settings.CyclePlan, Arrangement = settings.Arrangement,
                    ReminderRates = settings.Rates ?? snapshot.DefaultSettings.ReminderRates };
                snapshot = snapshot with { DefaultSettings = saved, ViewRevision = snapshot.ViewRevision + 1 };
            }
            return Task.FromResult(new CommandResult(CommandDisposition.Accepted,null,snapshot,snapshot.Persistence,null));
        }
        public IDisposable SubscribeSnapshots(Action<AppSnapshot> observer)
        {
            this.observer = observer;
            return new Subscription(() => this.observer = null);
        }
        public void PostSystemEvent(SystemSignal signal){}
        public Task<CommandResult> RetryPendingSaveAsync()=>throw new NotSupportedException();
        public Task<ShutdownReport> ShutdownAsync(TimeSpan deadline)=>Task.FromResult(new ShutdownReport(true,true,null));
    }
    private sealed class HistoryStub:IHistoryService
    {
        public List<DateRange> Ranges { get; } = [];
        public List<SessionQuery> Queries { get; } = [];
        public Func<DateOnly, Task<DaySummary>>? DayOverride { get; set; }
        public DaySummary MakeDay(DateOnly date) => Day(date);
        public Task<DaySummary> GetDayAsync(DateOnly date)=>DayOverride?.Invoke(date) ?? Task.FromResult(Day(date));
        public Task<IReadOnlyList<DaySummary>> GetDaysAsync(DateRange range)
        {
            Ranges.Add(range);
            return Task.FromResult<IReadOnlyList<DaySummary>>(Enumerable.Range(0, range.To.DayNumber-range.From.DayNumber+1)
                .Select(index=>Day(range.From.AddDays(index))).ToArray());
        }
        private static DaySummary Day(DateOnly date)
        {
            int ago = DateOnly.FromDateTime(DateTime.Today).DayNumber - date.DayNumber;
            int[] work = [80,72,109,0,44,92,64];
            int[] rest = [15,12,17,0,8,16,11];
            int[] completed = [3,2,4,0,1,3,2];
            var activityCounts = new Dictionary<string,int>();
            if (ago == 0) { activityCounts["walk"] = 2; activityCounts["look_away"] = 1; activityCounts["relax_shoulders_hands"] = 1; }
            if (ago == 1) { activityCounts["walk"] = 1; activityCounts["other"] = 1; }
            return ago is >= 0 and < 7
                ? new(date,work[ago]*60000L,rest[ago]*60000L,completed[ago],ago==0?1:0,0,0,
                    new((ago==0 ? 75 : work[ago])*60000L,completed[ago]+(ago==0 ? 1 : 0)),
                    new(rest[ago]*60000L,completed[ago]),activityCounts,DateTimeOffset.UtcNow) { Reminder = new(new(ago == 0 ? 75 : 60, 50, 65, 70, 25, 80), ago != 3) { History = new(60, 61, 62, 63, 64, 65), HistoryDays = 3 } }
                : new(date,0,0,0,0,0,0,default,default,activityCounts,DateTimeOffset.UtcNow);
        }
        public Task<SessionPage> GetSessionsAsync(SessionQuery query)
        {
            Queries.Add(query);
            var date = query.Range.From;
            var now = date.ToDateTime(new TimeOnly(14,25),DateTimeKind.Local);
            SessionRecord MakeRecord(int minutes, CheckInStatus status) =>
                new(Guid.NewGuid(),now.AddMinutes(-minutes-5),now,"UTC",date,"UTC",date,minutes*60000L,300000,20000,false,0,0,minutes*60000L,300000,SessionOutcome.Completed,EndReason.RestTargetReached,status,"",now);
            IReadOnlyList<SessionRecord> items = Day(date).WorkMs > 0
                ? [MakeRecord(25,CheckInStatus.Reported),MakeRecord(30,CheckInStatus.Unrecorded)]
                : [];
            return Task.FromResult(new SessionPage(items,null,now));
        }
        public Task<IReadOnlyList<ActivityReport>> GetActivitiesAsync(Guid sessionId)=>Task.FromResult<IReadOnlyList<ActivityReport>>([]);
    }
    private sealed class ExportStub:IHistoryExporter
    {
        public Task<ExportReport> ExportAsync(ExportRequest request,CancellationToken cancellation)=>throw new NotSupportedException();
    }
}
