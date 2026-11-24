using SandUp.Application;
using SandUp.Desktop;
using SandUp.Domain;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace SandUp.Desktop.Tests;
[Collection("Desktop windows")]
public class FloatingClockTests
{
    [Fact]
    public void SandResistsSmallMovesAndSettlesAfterOneSlip()
    {
        var slosh = new SandSlosh();
        var frame = TimeSpan.FromMilliseconds(1000d / 30);
        for (var i = 0; i < 10; i++)
        {
            slosh.Push(new Vector(2, -2));
            slosh.Advance(frame);
            Assert.Equal(default, slosh.Surface);
        }

        slosh.Push(new Vector(80, -80));
        var peak = slosh.Surface;
        Assert.Equal(SandSlosh.MaximumSlope, peak.X);
        Assert.InRange(peak.Y, -.05, -.03);

        slosh.Advance(frame);
        Assert.Equal(peak, slosh.Surface);
        for (var i = 0; i < 10; i++)
        {
            var previous = slosh.Surface;
            slosh.Advance(frame);
            Assert.InRange(slosh.Surface.X, 0, previous.X);
            Assert.InRange(slosh.Surface.Y, previous.Y, 0);
        }

        Assert.False(slosh.Moving);
        Assert.Equal(default, slosh.Surface);
    }

    [Fact]
    public async Task MenuHasDarkRowsOpensBesideHourglassAndRoutesActions()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var openCount = 0;
                    using var ui = new FloatingClockPresenter(new AppStub(), dispatcher, null, () => openCount++);
                    var window = ui.FloatingWindow;
                    window.Left = SystemParameters.WorkArea.Left + 120;
                    window.Top = SystemParameters.WorkArea.Top + 120;
                    var menu = window.ContextMenu;
                    menu.IsOpen = true;
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                    var panel = Assert.IsType<Border>(menu.Template.FindName("MenuPanel", menu));
                    Assert.Same(UiTheme.Raised, panel.Background);
                    var actions = menu.Items.Cast<MenuItem>().ToArray();
                    Assert.Equal(new[] { "打开沙立", "隐藏悬浮沙漏" }, actions.Select(item => item.Header));
                    Assert.All(actions, item => Assert.IsType<Border>(item.Template.FindName("MenuRow", item)));

                    var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                    void AssertBesideHourglass()
                    {
                        var floatLeft = content.PointToScreen(new Point()).X;
                        var floatRight = content.PointToScreen(new Point(content.ActualWidth, 0)).X;
                        var menuLeft = menu.PointToScreen(new Point()).X;
                        var menuRight = menu.PointToScreen(new Point(menu.ActualWidth, 0)).X;
                        Assert.True(menuRight <= floatLeft + 2 || menuLeft >= floatRight - 2);
                    }
                    AssertBesideHourglass();

                    menu.IsOpen = false;
                    window.Left = SystemParameters.WorkArea.Right - window.Width - 6;
                    window.Top = SystemParameters.WorkArea.Bottom - window.Height - 6;
                    menu.IsOpen = true;
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertBesideHourglass();

                    actions[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.Equal(1, openCount);
                    actions[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.False(ui.Enabled);
                    Assert.False(window.IsVisible);
                    Assert.False(menu.IsOpen);
                    finished.SetResult();
                }
                catch (Exception error) { finished.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(20001,false,false)] [InlineData(20000,true,false)] [InlineData(10001,true,false)]
    [InlineData(10000,true,true)] [InlineData(1,true,true)] [InlineData(0,false,false)]
    public void NoticeThresholdsUseRemainingTime(long remaining,bool warn,bool popup)
    {
        var s=new SessionProjection(SessionPhase.PreBreak,remaining,0,0,new HashSet<SessionAction>());
        Assert.Equal(warn,BreakNoticePolicy.Warn(s));Assert.Equal(popup,BreakNoticePolicy.Popup(s));
        Assert.False(BreakNoticePolicy.Warn(s with{Phase=SessionPhase.Paused}));
        Assert.False(BreakNoticePolicy.Popup(s with{Phase=SessionPhase.Resting}));
    }
    [Fact]
    public async Task FloatingAndPopupFollowSnapshotsHidePauseDelayAndDispose()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(()=>
            {
                FloatingClockPresenter? ui=null;
                try
                {
                    var app=new AppStub();app.Snapshot=app.Snapshot with{Session=app.Snapshot.Session with{Phase=SessionPhase.Working}};ui=new(app,dispatcher,null,()=>{});
                    void Render(SessionPhase phase,long remaining,bool delay=true)
                    {
                        app.Snapshot=app.Snapshot with{Session=new(phase,remaining,60000-remaining,0,new HashSet<SessionAction>()),AllowedCommands=delay?new HashSet<CommandType>{CommandType.DelayBreak}:new HashSet<CommandType>()};ui.Render(app.Snapshot);
                    }
                    Render(SessionPhase.Working,21000);Assert.True(ui.FloatingWindow.IsVisible);Assert.Null(ui.WarningWindow);Save(ui.FloatingWindow,"floating-work.png");
                    Render(SessionPhase.PreBreak,20000);Assert.Null(ui.WarningWindow);
                    Render(SessionPhase.PreBreak,10000);var warning=ui.WarningWindow;Assert.NotNull(warning);Assert.True(warning.IsVisible);Save(warning,"warning-10.png");
                    Assert.False(warning.ShowActivated);
                    void CheckWarningPosition(Window window)
                    {
                        window.UpdateLayout();
                        var floating = ui.FloatingWindow;
                        var popupBounds = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
                        var floatingBounds = new Rect(floating.Left, floating.Top, floating.ActualWidth, floating.ActualHeight);
                        Assert.False(popupBounds.IntersectsWith(floatingBounds));
                    }
                    CheckWarningPosition(warning);
                    var originalLeft = ui.FloatingWindow.Left;
                    ui.FloatingWindow.Left = SystemParameters.WorkArea.Left + 5;
                    CheckWarningPosition(warning);
                    ui.FloatingWindow.Left = originalLeft;
                    PageNavigationTests.AssertAppFont(ui.FloatingWindow.FontFamily, FontWeights.Normal);
                    PageNavigationTests.AssertAppFont(((StackPanel)warning.Content).Children.OfType<TextBlock>().First().FontFamily, FontWeights.Normal);
                    Render(SessionPhase.PreBreak,9000);Assert.Same(warning,ui.WarningWindow);
                    var buttons=((StackPanel)warning.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
                    Assert.Contains(buttons,b=>AutomationProperties.GetName(b)=="延后 3 分钟");
                    buttons.Single(b=>AutomationProperties.GetName(b)=="延后 3 分钟").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(3,Assert.Single(app.Commands.OfType<DelayBreak>()).Minutes);
                    Render(SessionPhase.Delaying,190000,false);Assert.Null(ui.WarningWindow);
                    Render(SessionPhase.Delaying,10000,false);Assert.NotNull(ui.WarningWindow);
                    Assert.DoesNotContain(((StackPanel)ui.WarningWindow.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>(),b=>AutomationProperties.GetName(b).StartsWith("延后"));
                    var delayedWarning = ui.WarningWindow;
                    var delayedPanel = (StackPanel)delayedWarning.Content;
                    var memo = Assert.Single(delayedPanel.Children.OfType<TextBox>());
                    Assert.Empty(app.Commands.OfType<SetRestMemoEditing>());
                    memo.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, memo)
                    {
                        RoutedEvent = Keyboard.GotKeyboardFocusEvent
                    });
                    Assert.True(Assert.Single(app.Commands.OfType<SetRestMemoEditing>()).Editing);
                    memo.Text = "继续编辑方案";
                    Render(SessionPhase.Delaying, 0, false);
                    Render(SessionPhase.AwaitingRestMemo, 0, false);
                    Assert.Same(delayedWarning, ui.WarningWindow);
                    Assert.Equal("继续编辑方案", memo.Text);
                    Assert.Contains(delayedPanel.Children.OfType<TextBlock>(), t => t.Text.StartsWith("00:00"));
                    Save(delayedWarning, "warning-memo-waiting.png");
                    var startRest = delayedPanel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>()
                        .Single(b => AutomationProperties.GetName(b) == "开始休息");
                    startRest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("继续编辑方案", Assert.Single(app.Commands.OfType<BreakNow>()).Memo);
                    Render(SessionPhase.Paused,9000,false);Assert.Null(ui.WarningWindow);
                    ui.SetEnabled(false);Assert.False(ui.FloatingWindow.IsVisible);
                    Render(SessionPhase.Delaying,9000,false);Assert.NotNull(ui.WarningWindow);Assert.False(ui.FloatingWindow.IsVisible);
                    ui.WarningWindow.Close();Render(SessionPhase.Delaying,8000,false);Assert.Null(ui.WarningWindow);
                    Render(SessionPhase.Resting,60000,false);Assert.False(ui.FloatingWindow.IsVisible);
                    ui.SetEnabled(true);Assert.False(ui.FloatingWindow.IsVisible);
                    Render(SessionPhase.Idle,0);Assert.True(ui.FloatingWindow.IsVisible);
                    ui.Dispose();Assert.False(ui.FloatingWindow.IsVisible);Assert.Null(ui.WarningWindow);
                    done.SetResult();
                }
                catch(Exception ex){done.SetException(ex);}
                finally{ui?.Dispose();dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            });Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    [Fact]
    public async Task BareHourglassFlipsOnceAndOnlyShowsTimeOnHover()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async()=>
            {
                FloatingClockPresenter? ui=null;
                try
                {
                    var app=new AppStub();ui=new(app,dispatcher,null,()=>{});
                    var surface=Assert.IsType<Grid>(ui.FloatingWindow.Content);
                    var glyph=Assert.IsType<Grid>(Assert.Single(surface.Children.Cast<UIElement>()));
                    var glass=glyph.Children.OfType<HourglassView>().Single();
                    var countdown=glyph.Children.OfType<Border>().Single();
                    var countdownText = Assert.IsType<TextBlock>(Assert.IsType<Viewbox>(countdown.Child).Child);
                    Assert.Equal(new Size(104, 104), ui.FloatingWindow.RenderSize);
                    Assert.Null(countdown.Background);
                    bool Turning() => glass.Turning;
                    void Render(SessionPhase phase,long remaining)
                    {
                        app.Snapshot=app.Snapshot with{Session=new(phase,remaining,60000-remaining,0,new HashSet<SessionAction>())};ui.Render(app.Snapshot);
                    }
                    Assert.Null(surface.Background);Assert.Null(glyph.Background);
                    Assert.Equal(0,glass.RemainingFraction);Assert.False(glass.Running);
                    Assert.Equal(Visibility.Hidden,countdown.Visibility);Save(ui.FloatingWindow,"floating-ready.png");
                    Save(ui.FloatingWindow, "floating-on-light.png", Brushes.White);
                    Save(ui.FloatingWindow, "floating-on-dark.png", UiTheme.Canvas);
                    glyph.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=UIElement.MouseEnterEvent});
                    Assert.Equal(Visibility.Visible,countdown.Visibility);Save(ui.FloatingWindow,"floating-ready-hover.png");
                    glyph.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=UIElement.MouseLeaveEvent});
                    Render(SessionPhase.Working,60000);
                    Assert.True(Turning());Assert.Equal(Visibility.Hidden,countdown.Visibility);
                    await Task.Delay(370);Assert.InRange(glass.TurnAngle,1,179);Save(ui.FloatingWindow,"floating-turning.png");
                    Render(SessionPhase.Working,59000); // Another pulse must not restart the turn.
                    Assert.Equal(59d/60,glass.RemainingFraction,4); // A pulse must not replace the turn's sand with an empty chamber.
                    await Task.Delay(560);Assert.False(Turning());Assert.Equal(180,glass.TurnAngle);Assert.True(glass.Running);Save(ui.FloatingWindow,"floating-running.png");
                    // At 180 degrees, releasing the turn must not swap the chamber fill or redraw the ribbon in its idle pose.
                    glass.Turning=true;glass.SetSand(.98,false);
                    var lastTurnFrame=CapturePixels(glass);
                    glass.Turning=false;glass.SetSand(.98,false);
                    Assert.Equal(lastTurnFrame,CapturePixels(glass));
                    Render(SessionPhase.Working,59000);
                    glyph.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=UIElement.MouseEnterEvent});
                    Assert.Equal(Visibility.Visible,countdown.Visibility);Assert.Equal("00:59",countdownText.Text);Save(ui.FloatingWindow,"floating-running-hover.png");
                    Render(SessionPhase.Paused,59000);Render(SessionPhase.Working,59000);Assert.False(Turning());
                    Render(SessionPhase.Working,31000);Save(ui.FloatingWindow,"floating-midway.png");
                    Render(SessionPhase.AwaitingCheckIn,0);Assert.Equal(0,glass.RemainingFraction);Assert.False(glass.Running);Save(ui.FloatingWindow,"floating-finished.png");
                    app.Snapshot=app.Snapshot with{CurrentSessionId=Guid.NewGuid()};Render(SessionPhase.Working,60000);Assert.True(Turning());
                    Render(SessionPhase.Paused,60000);Assert.False(Turning());await Task.Delay(900);Assert.False(glass.Running);
                    Render(SessionPhase.Idle,0);Assert.Equal(0,glass.RemainingFraction);Assert.Equal(0,glass.TurnAngle);
                    done.SetResult();
                }
                catch(Exception ex){done.SetException(ex);}
                finally{ui?.Dispose();dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            });Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task CompanionBlinksWavesWithBothHandsAndRetractsAtTwoSeconds()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var app = new AppStub();
                    app.Snapshot = app.Snapshot with { Session = new(SessionPhase.Working, 21000, 39000, 0, new HashSet<SessionAction>()) };
                    using var presenter = new FloatingClockPresenter(app, dispatcher, null, () => { });
                    var window = presenter.FloatingWindow;
                    var glyph = Assert.IsType<Grid>(Assert.Single(Assert.IsType<Grid>(window.Content).Children.Cast<UIElement>()));
                    var glass = glyph.Children.OfType<HourglassView>().Single();
                    const System.Reflection.BindingFlags members = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    var progressProperty = typeof(HourglassView).GetProperty("CompanionProgress", members)!;
                    var arm = Assert.IsType<RotateTransform>(typeof(HourglassView).GetField("companionArm", members)!.GetValue(glass));
                    var rightArm = Assert.IsType<RotateTransform>(typeof(HourglassView).GetField("companionRightArm", members)!.GetValue(glass));
                    var eyes = Assert.IsType<ScaleTransform>(typeof(HourglassView).GetField("companionEyes", members)!.GetValue(glass));
                    var animate = SystemParameters.ClientAreaAnimation;
                    double Progress() => (double)progressProperty.GetValue(glass)!;
                    void Render(SessionPhase phase, long remaining, bool restFollows = true)
                    {
                        app.Snapshot = app.Snapshot with
                        {
                            Session = new(phase, remaining, 60000 - remaining, 0, new HashSet<SessionAction>()) { RestFollows = restFollows }
                        };
                        presenter.Render(app.Snapshot);
                    }

                    Assert.Equal(0, Progress());
                    Render(SessionPhase.Working, 18000, restFollows: false);
                    var bareGlass = WhiteVerticalBounds(glass);
                    Assert.Equal(0, Progress());
                    Assert.Null(presenter.WarningWindow);
                    Render(SessionPhase.PreBreak, 20000);
                    Assert.Null(presenter.WarningWindow);
                    if (animate)
                    {
                        await WaitFor(() => Progress() is > .15 and < .85);
                        Save(window, "floating-companion-growing.png", UiTheme.Canvas);
                    }
                    await WaitFor(() => Progress() == 1 && (!animate || arm.HasAnimatedProperties));
                    Save(window, "floating-20.png", UiTheme.Canvas);
                    Assert.True(glass.Running);
                    var standing = WhiteVerticalBounds(glass);
                    Assert.InRange(standing.Bottom, bareGlass.Bottom - 1, bareGlass.Bottom + 1);
                    Assert.True(standing.Top <= bareGlass.Top - 5);
                    if (animate)
                    {
                        await WaitFor(() => arm.Angle > 65);
                        Save(window, "floating-companion-wave.png", UiTheme.Canvas);
                        Assert.False(rightArm.HasAnimatedProperties);
                        await WaitFor(() => eyes.ScaleY < .35);
                        Save(window, "floating-companion-blink.png", UiTheme.Canvas);
                    }

                    Render(SessionPhase.PreBreak, 10001);
                    Assert.Null(presenter.WarningWindow);
                    Assert.False(rightArm.HasAnimatedProperties);
                    Render(SessionPhase.PreBreak, 10000);
                    Assert.NotNull(presenter.WarningWindow);
                    Assert.Equal(1, Progress());
                    if (animate)
                    {
                        await WaitFor(() => arm.Angle > 65 && rightArm.Angle > 65);
                        Save(window, "floating-companion-both-hands.png", UiTheme.Canvas);
                    }
                    Render(SessionPhase.Delaying, 188000);
                    Assert.False(arm.HasAnimatedProperties);
                    Assert.False(rightArm.HasAnimatedProperties);
                    Assert.False(eyes.HasAnimatedProperties);
                    await WaitFor(() => Progress() == 0);
                    Assert.True(window.IsVisible);
                    Assert.Null(presenter.WarningWindow);
                    Render(SessionPhase.Delaying, 20000);
                    await WaitFor(() => Progress() == 1 && (!animate || arm.HasAnimatedProperties));
                    Render(SessionPhase.Delaying, 10000);
                    await dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                    Render(SessionPhase.Delaying, 2001);
                    Assert.Equal(1, Progress());
                    var warning = Assert.IsType<Window>(presenter.WarningWindow);
                    Render(SessionPhase.Delaying, 2000);
                    Assert.False(arm.HasAnimatedProperties);
                    Assert.False(rightArm.HasAnimatedProperties);
                    Assert.False(eyes.HasAnimatedProperties);
                    Assert.Same(warning, presenter.WarningWindow);
                    Assert.True(window.IsVisible);
                    Assert.True(glass.Running);
                    if (animate)
                    {
                        Assert.Equal(1, Progress());
                        await WaitFor(() => Progress() is > .15 and < .85);
                        Save(window, "floating-companion-retracting.png", UiTheme.Canvas);
                    }
                    await WaitFor(() => Progress() == 0);
                    Save(window, "floating-companion-last-seconds.png", UiTheme.Canvas);
                    Render(SessionPhase.BreakPending, 5000);
                    Assert.Null(presenter.WarningWindow);
                    Assert.False(window.IsVisible);
                    Render(SessionPhase.Resting, 5000);
                    await WaitFor(() => !window.IsVisible);
                    Assert.Equal(0, Progress());
                    Render(SessionPhase.AwaitingCheckIn, 0);
                    Assert.True(window.IsVisible);
                    Assert.Equal(0, Progress());
                    Save(window, "floating-companion-retracted.png", UiTheme.Canvas);
                    done.SetResult();
                }
                catch (Exception ex) { done.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static (int Top, int Bottom) WhiteVerticalBounds(FrameworkElement element)
    {
        var pixels = CapturePixels(element);
        var stride = (int)element.ActualWidth * 4;
        var top = int.MaxValue;
        var bottom = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 230 || pixels[i + 1] < 230 || pixels[i + 2] < 230) continue;
            var row = i / stride;
            top = Math.Min(top, row);
            bottom = Math.Max(bottom, row);
        }
        return (top, bottom);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(12);
        Assert.True(condition(), "The floating companion did not reach the expected state.");
    }
    private static byte[] CapturePixels(FrameworkElement element)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        Assert.Contains(pixels, value => value != 0);
        return pixels;
    }
    private static void Save(Window window,string name,Brush? background=null)
    {
        var directory=Environment.GetEnvironmentVariable("SANDUP_TEST_PREVIEWS");if(string.IsNullOrEmpty(directory))return;
        Directory.CreateDirectory(directory);window.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        if (background is not null)
        {
            var drawing = new DrawingVisual();
            var bounds = new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(background, null, bounds);
                context.DrawImage(bitmap, bounds);
            }
            bitmap = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
        }
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(directory,name));encoder.Save(file);
    }
    private sealed class AppStub:IWorkClockApplication
    {
        public List<WorkClockCommand> Commands=[];
        public AppSnapshot Snapshot=new(1,new(SessionPhase.Idle,60000,0,0,new HashSet<SessionAction>()),Guid.NewGuid(),null,"",new(60000,60000,20000,new long[]{180000,300000},1),[],new(PersistenceStatus.Saved,null,null),null,new HashSet<CommandType>());
        public Task<AppSnapshot> InitializeAsync()=>Task.FromResult(Snapshot);
        public AppSnapshot ReadSnapshot()=>Snapshot;
        public IDisposable SubscribeSnapshots(Action<AppSnapshot> observer)=>new Subscription(()=>{});
        public Task<CommandResult> HandleAsync(CommandEnvelope envelope){Commands.Add(envelope.Command);return Task.FromResult(new CommandResult(CommandDisposition.Accepted,null,Snapshot,Snapshot.Persistence,null));}
        public void PostSystemEvent(SystemSignal signal){}
        public Task<CommandResult> RetryPendingSaveAsync()=>throw new NotSupportedException();
        public Task<ShutdownReport> ShutdownAsync(TimeSpan deadline)=>Task.FromResult(new ShutdownReport(true,true,null));
    }
}
