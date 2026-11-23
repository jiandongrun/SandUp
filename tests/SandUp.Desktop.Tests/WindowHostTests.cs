using SandUp.Application;
using SandUp.Desktop;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Automation;

namespace SandUp.Desktop.Tests;

[Collection("Desktop windows")]
public sealed class WindowHostTests
{
    [Fact]
    public async Task RealWindowsCloseAndRejectInvalidatedOrPreCancelledBatches()
    {
        var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            var errors=new List<Exception>();var host=new BreakOverlayHost(dispatcher,errors.Add);
            dispatcher.BeginInvoke(async()=>
            {
                try
                {
                    // Deliberately a small region, not full-screen: verifies real window lifecycle, not monitor coverage.
                    var display=new DisplayInfo("test-region",true,new(50,50,480,640),96,96);
                    // Even a stale request from the old contract must not restore rest-screen delay buttons.
                    var actions=Enum.GetValues<OverlayActionKind>().ToHashSet();
                    for(var i=0;i<10;i++)
                    {
                        var request=new OverlayRequest(Guid.NewGuid(),Guid.NewGuid(),1,new[]{display},60000,"walk",actions);
                        using var cancellation=new CancellationTokenSource();
                        Assert.True((await host.ShowAsync(request,cancellation.Token)).AllShown,string.Join("\n",errors));
                        await dispatcher.InvokeAsync(()=>
                        {
                            var window=PresentationSource.CurrentSources.Cast<PresentationSource>()
                                .Where(s=>s.Dispatcher==dispatcher).Select(s=>s.RootVisual).OfType<Window>().Single();
                            var labels=((StackPanel)((ScrollViewer)window.Content).Content).Children.OfType<WrapPanel>().SelectMany(p=>p.Children.OfType<Button>()).Select(AutomationProperties.GetName).ToArray();
                            Assert.Equal(new[]{"应急退出  Esc","换一个提示"},labels);
                        });
                        host.CloseAll(request.BatchId,OverlayCloseReason.Emergency);
                        Assert.False((await host.ShowAsync(request,CancellationToken.None)).AllShown);
                    }
                    var cancelled=new OverlayRequest(Guid.NewGuid(),Guid.NewGuid(),1,new[]{display},60000,"walk",actions);
                    using var token=new CancellationTokenSource();token.Cancel();
                    Assert.False((await host.ShowAsync(cancelled,token.Token)).AllShown);
                    Assert.DoesNotContain(PresentationSource.CurrentSources.Cast<PresentationSource>(),s=>s.Dispatcher==dispatcher&&s.RootVisual is Window);
                    finished.TrySetResult();
                }
                catch(Exception e){finished.TrySetException(e);}
                finally{host.CloseAll(null,OverlayCloseReason.Shutdown);dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            });
            Dispatcher.Run();
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task CompletionUsesSameWindowsPreservesSelectionAndSubmitsOnce()
    {
        var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            var history = new PreviewHistory();
            var host=new BreakOverlayHost(dispatcher, history: history);
            var received=new List<OverlayAction>();using var subscription=host.SubscribeActions(received.Add);
            dispatcher.BeginInvoke(async()=>
            {
                try
                {
                    var displays=new[]{new DisplayInfo("one",true,new(50,50,480,300),96,96),new DisplayInfo("two",false,new(550,50,960,880),96,96)};
                    var allowed=Enum.GetValues<OverlayActionKind>().ToHashSet();
                    var request=new OverlayRequest(Guid.NewGuid(),Guid.NewGuid(),1,displays,20_000,"look_away",allowed) { SegmentCaption="本轮休息 1/2 · 接下来起身走动 10 分钟" };
                    Assert.True((await host.ShowAsync(request,CancellationToken.None)).AllShown);
                    Window[] Windows()=>PresentationSource.CurrentSources.Cast<PresentationSource>().Where(x=>x.Dispatcher==dispatcher).Select(x=>x.RootVisual).OfType<Window>().ToArray();
                    var original=Windows();Assert.Equal(2,original.Length);
                    var initialPanel=(StackPanel)((ScrollViewer)original[0].Content).Content;
                    var text=initialPanel.Children.OfType<TextBlock>().ToArray();
                    PageNavigationTests.AssertAppFont(text[2].FontFamily, FontWeights.Normal);
                    Assert.Equal(request.SegmentCaption,text[0].Text);
                    Assert.Equal("把目光移向远处",text[2].Text);
                    Assert.Contains("6 米",text[3].Text);Assert.Contains("自然眨眼",text[3].Text);
                    foreach (var window in original)
                    {
                        var viewer = (ScrollViewer)window.Content;
                        viewer.UpdateLayout();
                        Assert.Equal(0, viewer.ScrollableWidth);
                    }
                    var largerWindow = original.MaxBy(window => window.ActualHeight)!;
                    var largerPanel = (StackPanel)((ScrollViewer)largerWindow.Content).Content;
                    var largerIllustration = largerPanel.Children.OfType<FrameworkElement>().Single(x => x.GetType().Name == "BreakBodyIllustration");
                    PageNavigationTests.SaveElementPreview((FrameworkElement)largerWindow.Content, "cycle-eye-rest.png", largerWindow.Background);
                    await SavePose("rest-companion-look.png", 620);
                    var change=initialPanel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single(b=>AutomationProperties.GetName(b)=="换一个提示");
                    change.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("relax_shoulders_hands",Assert.Single(received).PromptId);received.Clear();
                    host.Update(new OverlayViewModel(request.BatchId,900,"relax_shoulders_hands",allowed));await dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    Assert.Equal(original,Windows());Assert.Equal("轻松活动肩膀和双手",text[2].Text);Assert.Contains("轻轻握拢",text[3].Text);
                    await SavePose("rest-companion-stretch.png", 780);
                    host.Update(new OverlayViewModel(request.BatchId,800,"walk",allowed));await dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    Assert.Equal("起身，走动一会儿",text[2].Text);Assert.Contains("平稳起身",text[3].Text);
                    await SavePose("rest-companion-walk.png", 220);
                    var illustration=((StackPanel)((ScrollViewer)original[0].Content).Content).Children.OfType<FrameworkElement>().Single(x=>x.GetType().Name=="BreakBodyIllustration");
                    Assert.False(illustration.IsHitTestVisible); Assert.False(illustration.Focusable);
                    Assert.Equal(Visibility.Visible,illustration.Visibility);
                    async Task SavePose(string filename, int delayMs)
                    {
                        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SANDUP_TEST_PREVIEWS"))) return;
                        if (SystemParameters.ClientAreaAnimation) await Task.Delay(delayMs);
                        PageNavigationTests.SaveElementPreview(largerIllustration, filename, largerWindow.Background);
                    }
                    var memoIdle = new OverlayViewModel(request.BatchId, 12_000, "walk",
                        new HashSet<OverlayActionKind> { OverlayActionKind.EmergencyExit, OverlayActionKind.BeginMemo })
                    { Memo = "上次记录" };
                    host.Update(memoIdle);
                    await dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                    PageNavigationTests.SaveElementPreview((FrameworkElement)largerWindow.Content, "rest-memo.png", largerWindow.Background);
                    var memoPanel = Assert.Single(largerPanel.Children.OfType<StackPanel>());
                    var memo = Assert.Single(memoPanel.Children.OfType<TextBox>());
                    Assert.True(memo.IsReadOnly);
                    Assert.Equal("上次记录", memo.Text);
                    memo.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
                        System.Windows.Input.Keyboard.PrimaryDevice, 0, null, memo)
                    {
                        RoutedEvent = System.Windows.Input.Keyboard.GotKeyboardFocusEvent
                    });
                    Assert.Equal(OverlayActionKind.BeginMemo, Assert.Single(received).Action);
                    received.Clear();
                    // A queued update captured before focus must not steal editor ownership.
                    host.Update(memoIdle);
                    host.Update(memoIdle with { AllowedActions = new HashSet<OverlayActionKind> { OverlayActionKind.EmergencyExit, OverlayActionKind.ConfirmMemo } });
                    await dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                    Assert.False(memo.IsReadOnly);
                    memo.Text = "继续核对";
                    PageNavigationTests.SaveElementPreview((FrameworkElement)largerWindow.Content, "rest-memo-editing.png", largerWindow.Background);
                    var confirm = Assert.Single(memoPanel.Children.OfType<Button>());
                    confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("继续核对", Assert.Single(received).Memo);
                    received.Clear();
                    host.Update(memoIdle with { Memo = "继续核对" });
                    await dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                    Assert.Equal(original, Windows());
                    Assert.True(memo.IsReadOnly);
                    Assert.Equal(Visibility.Visible, illustration.Visibility);

                    var date = new DateOnly(2026, 11, 17);
                    var model = new OverlayViewModel(request.BatchId, 0, "walk", allowed)
                    {
                        CheckInLocalDate = date,
                        CheckInActivities = [new("walk", "走动", null, null, null), new("relax_shoulders_hands", "伸展", null, null, null), new("look_away", "远眺", null, null, null), new("other", "其他", null, null, null)]
                    };
                    host.Update(model);await dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    Assert.Equal(original,Windows());
                    Assert.Equal(Visibility.Collapsed,illustration.Visibility);
                    PageNavigationTests.SaveElementPreview((FrameworkElement)largerWindow.Content, "rest-check-in.png", largerWindow.Background);
                    var scroll=(ScrollViewer)original[0].Content;
                    scroll.UpdateLayout();
                    Assert.True(scroll.ScrollableHeight > 0);
                    scroll.ScrollToBottom();
                    scroll.UpdateLayout();
                    Assert.True(scroll.VerticalOffset > 0);
                    var panel=(StackPanel)scroll.Content;
                    CheckBox[] Choices() => panel.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<CheckBox>()).ToArray();
                    var choices = Choices();
                    Assert.All(choices,c=>Assert.NotEqual(true,c.IsChecked));
                    Assert.Equal(new[] { date }, history.Queries);
                    Assert.Equal("走动，今日耐久度 腰背 20/100，腹部 50/100，臀腿 60/100，肛门·前列腺 100/100", AutomationProperties.GetName(choices[0]));
                    Assert.Equal("伸展，今日耐久度 肩颈 30/100", AutomationProperties.GetName(choices[1]));
                    Assert.Equal("远眺，今日耐久度 眼睛 40/100", AutomationProperties.GetName(choices[2]));
                    choices[0].IsChecked = true;
                    Assert.Equal("走动，今日耐久度 腰背 26/100，腹部 53/100，臀腿 72/100，肛门·前列腺 100/100", AutomationProperties.GetName(choices[0]));
                    choices[0].IsChecked = false;
                    choices[2].IsChecked = true;
                    Assert.Equal("远眺，今日耐久度 眼睛 52/100", AutomationProperties.GetName(choices[2]));
                    choices[2].IsChecked = false;
                    choices[1].IsChecked = true;
                    Assert.Equal("伸展，今日耐久度 肩颈 42/100", AutomationProperties.GetName(choices[1]));
                    Assert.Equal("走动，今日耐久度 腰背 20/100，腹部 50/100，臀腿 60/100，肛门·前列腺 100/100", AutomationProperties.GetName(choices[0]));
                    host.Update(model);await dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    Assert.Same(choices[1], Choices()[1]);Assert.True(choices[1].IsChecked);
                    var buttons=panel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
                    Assert.Contains(buttons,b=>Equals(b.Content,"稍后继续"));
                    Assert.Contains(buttons,b=>AutomationProperties.GetName(b)=="应急退出  Esc");
                    buttons.Single(b=>Equals(b.Content,"开始下一轮")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var action=Assert.Single(received);Assert.Equal(OverlayActionKind.ContinueAfterBreak,action.Action);Assert.Equal(new[]{"relax_shoulders_hands"},action.ActivityIds);
                    var other=((StackPanel)((ScrollViewer)original[1].Content).Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single(b=>Equals(b.Content,"稍后继续"));
                    other.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Single(received);
                    var key=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(original[0]),0,System.Windows.Input.Key.Escape) { RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                    original[0].RaiseEvent(key);Assert.Empty(Windows());Assert.Equal(OverlayActionKind.EmergencyExit,received.Last().Action);
                    var nextRequest = request with { BatchId = Guid.NewGuid() };
                    Assert.True((await host.ShowAsync(nextRequest, CancellationToken.None)).AllShown);
                    Windows()[0].Close();
                    Assert.Empty(Windows());
                    Assert.Equal(nextRequest.BatchId, received.Last().BatchId);
                    Assert.Equal(OverlayActionKind.EmergencyExit, received.Last().Action);
                    finished.TrySetResult();
                }
                catch(Exception e){finished.TrySetException(e);}
                finally{host.CloseAll(null,OverlayCloseReason.Shutdown);dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            });
            Dispatcher.Run();
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task BlockedDispatcherTimesOutAndLateCreationCannotShowAWindow()
    {
        using var release=new ManualResetEventSlim();
        var ready=new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(()=>{ready.SetResult(dispatcher);release.Wait(TimeSpan.FromSeconds(10));});
            Dispatcher.Run();
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        var ui=await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var host=new BreakOverlayHost(ui);
        try
        {
            var request=new OverlayRequest(Guid.NewGuid(),Guid.NewGuid(),1,
                new[]{new DisplayInfo("test-region",true,new(50,50,480,640),96,96)},60000,"walk",
                new HashSet<OverlayActionKind>{OverlayActionKind.EmergencyExit});
            var result=await host.ShowAsync(request,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.False(result.AllShown);
            release.Set();
            // Queued creation is processed before this idle-priority inspection.
            await ui.InvokeAsync(()=>Assert.DoesNotContain(PresentationSource.CurrentSources.Cast<PresentationSource>(),
                s=>s.Dispatcher==ui&&s.RootVisual is Window),DispatcherPriority.ApplicationIdle).Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally{release.Set();host.CloseAll(null,OverlayCloseReason.Shutdown);ui.BeginInvokeShutdown(DispatcherPriority.Background);}
    }
    private sealed class PreviewHistory : IHistoryService
    {
        public List<DateOnly> Queries { get; } = [];

        public Task<DaySummary> GetDayAsync(DateOnly date)
        {
            Queries.Add(date);
            return Task.FromResult(new DaySummary(date, 0, 0, 0, 0, 0, 0, default, default,
                new Dictionary<string, int>(), DateTimeOffset.UtcNow) { Reminder = new(new(40, 30, 20, 50, 60, 100), true) });
        }

        public Task<IReadOnlyList<DaySummary>> GetDaysAsync(DateRange range) => throw new NotSupportedException();
        public Task<SessionPage> GetSessionsAsync(SessionQuery query) => throw new NotSupportedException();
        public Task<IReadOnlyList<ActivityReport>> GetActivitiesAsync(Guid sessionId) => throw new NotSupportedException();
    }
}
