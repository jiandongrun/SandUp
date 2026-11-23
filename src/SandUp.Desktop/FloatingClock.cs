using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Automation;
using System.Windows.Threading;
using SandUp.Application;
using SandUp.Domain;

namespace SandUp.Desktop;

public static class BreakNoticePolicy
{
    public static bool Running(SessionProjection s)=>s.Phase is SessionPhase.Working or SessionPhase.PreBreak or SessionPhase.Delaying;
    public static bool Warn(SessionProjection s)=>Running(s)&&s.RestFollows&&s.RemainingMs is >0 and <=20000;
    public static bool Popup(SessionProjection s)=>Running(s)&&s.RestFollows&&s.RemainingMs is >0 and <=10000;
}

public sealed record FloatingPreferences(bool Enabled=true,double? Left=null,double? Top=null);

/// <summary>Displays snapshots only: never starts a second timer or advances session time.</summary>
public sealed class FloatingClockPresenter:IDisposable
{
    private const double FloatingSize = 104;
    private readonly IWorkClockApplication app;
    private readonly Dispatcher dispatcher;
    private readonly string? preferencesPath;
    private readonly Action openMain;
    private readonly IDisposable subscription;
    private readonly SemaphoreSlim saveGate=new(1,1);
    private readonly Window floating;
    private readonly HourglassView hourglass = new();
    private readonly TextBlock time=new(){FontSize=12,FontWeight=FontWeights.SemiBold,Foreground=UiTheme.Text};
    private readonly Border countdown;
    private readonly Grid glyph;
    private FloatingPreferences preferences;
    private Window? warning;
    private TextBlock? warningTime;
    private Button[] warningDelayButtons = [];
    private string? dismissedSegment;
    private string? warningSegment;
    private bool closingWarning,disposed;
    private bool memoDismissalPending;
    private bool hovered;
    private int flipGeneration;
    private int companionGeneration;
    private bool companionShown, companionReady, companionBothHands, retractingForRest;
    private SessionPhase? previousPhase;
    private Guid? previousSession;
    public bool Enabled=>preferences.Enabled;
    public event Action<bool>? EnabledChanged;
    public Window FloatingWindow=>floating;
    public Window? WarningWindow=>warning;

    public FloatingClockPresenter(IWorkClockApplication app,Dispatcher dispatcher,string? preferencesPath,Action openMain)
    {
        this.app=app;this.dispatcher=dispatcher;this.preferencesPath=preferencesPath;this.openMain=openMain;
        preferences=LoadPreferences();
        glyph=new Grid();
        glyph.Children.Add(hourglass);
        time.Effect = new DropShadowEffect { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 3, Opacity = .9 };
        countdown = new Border
        {
            Child = new Viewbox { Child = time, MaxWidth = 36, StretchDirection = StretchDirection.DownOnly },
            Visibility = Visibility.Hidden, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 22)
        };
        glyph.Children.Add(countdown);
        glyph.MouseEnter+=(_,_)=>{hovered=true;UpdateHover();};
        glyph.MouseLeave+=(_,_)=>{hovered=false;UpdateHover();};
        // Transparent room around the glyph keeps the 180-degree turn from clipping.
        var surface=new Grid();surface.Children.Add(glyph);
        floating=new Window{Title=$"{AppBranding.DisplayName} · 悬浮沙漏",Icon=AppBranding.WindowIcon,FontFamily=UiTypography.Family,Content=surface,Width=FloatingSize,Height=FloatingSize,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,Topmost=true,ShowInTaskbar=false,ShowActivated=false};
        var menu=new ContextMenu{FontFamily=UiTypography.Family};UiTheme.AddControls(menu);var open=new MenuItem{Header="打开沙立"};open.Click+=(_,_)=>openMain();menu.Items.Add(open);
        var hide=new MenuItem{Header="隐藏悬浮沙漏"};hide.Click+=(_,_)=>SetEnabled(false);menu.Items.Add(hide);floating.ContextMenu=menu;
        floating.MouseLeftButtonDown+=async(_,e)=>{if(e.ClickCount==2){openMain();return;}floating.DragMove();ClampToDesktop(floating);preferences=preferences with{Left=floating.Left,Top=floating.Top};await SavePreferences();};
        floating.Closing+=(_,e)=>{if(!disposed){e.Cancel=true;SetEnabled(false);}};
        floating.LocationChanged += (_, _) => PositionWarning();
        floating.Left=preferences.Left??(SystemParameters.WorkArea.Right-floating.Width-20);
        floating.Top=preferences.Top??(SystemParameters.WorkArea.Bottom-floating.Height-20);ClampToDesktop(floating);
        subscription=app.SubscribeSnapshots(_=>dispatcher.BeginInvoke(()=>{if(!disposed)Render(app.ReadSnapshot());}));Render(app.ReadSnapshot());
    }
    public void SetEnabled(bool enabled)
    {
        if(disposed)return;preferences=preferences with{Enabled=enabled};Render(app.ReadSnapshot());EnabledChanged?.Invoke(enabled);_=SavePreferences();
    }
    private void UpdateHover()=>countdown.Visibility=hovered&&!hourglass.Turning&&!retractingForRest?Visibility.Visible:Visibility.Hidden;
    private void EndFlip(double angle)
    {
        flipGeneration++;
        hourglass.Turning = false;
        hourglass.TurnAngle = angle;
        hourglass.BeginAnimation(HourglassView.TurnAngleProperty, null);
        hourglass.InvalidateVisual();
        UpdateHover();
    }
    private void StartFlip()
    {
        EndFlip(0);
        hourglass.Turning = true;
        UpdateHover();
        var generation = flipGeneration;
        var animation = new DoubleAnimation(0, 180, TimeSpan.FromMilliseconds(800))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        animation.Completed+=(_,_)=>
        {
            if(disposed||generation!=flipGeneration)return;
            EndFlip(180);
            Render(app.ReadSnapshot());
        };
        hourglass.BeginAnimation(HourglassView.TurnAngleProperty, animation);
    }

    private void UpdateCompanion(bool show, bool animate, bool bothHands = false)
    {
        companionBothHands = bothHands;
        if (!show || !animate) hourglass.SetCompanionMotion(false);
        var target = show ? 1d : 0;
        if (companionShown == show && (animate || hourglass.CompanionProgress == target))
        {
            if (show && companionReady && animate) hourglass.SetCompanionMotion(true, companionBothHands);
            return;
        }

        var start = hourglass.CompanionProgress;
        var generation = ++companionGeneration;
        companionShown = show;
        companionReady = false;
        hourglass.CompanionProgress = start;
        hourglass.BeginAnimation(HourglassView.CompanionProgressProperty, null);
        if (!animate || start == target)
        {
            CompleteCompanionTransition(show, animate);
            return;
        }

        var animation = new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(show ? 320 : 260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            if (disposed || generation != companionGeneration) return;
            CompleteCompanionTransition(show, SystemParameters.ClientAreaAnimation);
        };
        hourglass.BeginAnimation(HourglassView.CompanionProgressProperty, animation);
    }

    private void CompleteCompanionTransition(bool show, bool animate)
    {
        hourglass.CompanionProgress = show ? 1d : 0d;
        hourglass.BeginAnimation(HourglassView.CompanionProgressProperty, null);
        companionReady = show;
        if (show) hourglass.SetCompanionMotion(animate, companionBothHands);
        else FinishRetraction();
    }

    private void FinishRetraction()
    {
        if (!retractingForRest) return;
        retractingForRest = false;
        floating.Hide();
        glyph.IsHitTestVisible = true;
    }
    private static void ClampToDesktop(Window window)
    {
        // Primary work area is a safe fallback after a monitor is disconnected.
        var area=SystemParameters.WorkArea;
        if(!double.IsFinite(window.Left)||!double.IsFinite(window.Top)||window.Left+window.Width<SystemParameters.VirtualScreenLeft||window.Left>SystemParameters.VirtualScreenLeft+SystemParameters.VirtualScreenWidth||window.Top+window.Height<SystemParameters.VirtualScreenTop||window.Top>SystemParameters.VirtualScreenTop+SystemParameters.VirtualScreenHeight)
        {window.Left=area.Right-window.Width-20;window.Top=area.Bottom-window.Height-20;}
    }
    private FloatingPreferences LoadPreferences()
    {
        try{return preferencesPath is not null&&File.Exists(preferencesPath)?JsonSerializer.Deserialize<FloatingPreferences>(File.ReadAllText(preferencesPath))??new():new();}catch{return new();}
    }
    private async Task SavePreferences()
    {
        if(preferencesPath is null)return;
        await saveGate.WaitAsync();
        try{var payload=JsonSerializer.Serialize(preferences);await Task.Run(()=>{var temp=preferencesPath+".tmp";File.WriteAllText(temp,payload);File.Move(temp,preferencesPath,true);});}
        catch{floating.ToolTip="位置或显示偏好未保存；本次仍可使用。";}
        finally{saveGate.Release();}
    }
    public void Render(AppSnapshot snapshot)
    {
        if(disposed)return;
        var s=snapshot.Session;var running=BreakNoticePolicy.Running(s);var warn=BreakNoticePolicy.Warn(s);var popupDue=BreakNoticePolicy.Popup(s);
        var ready=s.Phase is SessionPhase.Idle or SessionPhase.AwaitingCheckIn;
        var resting=s.Phase is SessionPhase.Resting or SessionPhase.BreakPending or SessionPhase.EditingRestMemo;
        var pausedRest=s.Phase==SessionPhase.Paused&&s.RestElapsedMs>0;
        var newRound=running&&previousPhase is not null&&(previousSession!=snapshot.CurrentSessionId||previousPhase is SessionPhase.Idle or SessionPhase.AwaitingCheckIn);
        previousPhase=s.Phase;previousSession=snapshot.CurrentSessionId;
        var waitingForMemo = s.Phase == SessionPhase.AwaitingRestMemo;
        var sandAtRest = ready || resting || pausedRest || waitingForMemo;
        var uprightAngle = sandAtRest ? 0 : 180;
        if (hourglass.Turning && (!running || !Enabled)) EndFlip(uprightAngle);
        if (newRound && Enabled) StartFlip();
        if (!hourglass.Turning) hourglass.TurnAngle = uprightAngle;
        var remaining = sandAtRest ? 0 : (double)s.RemainingMs / Math.Max(1, s.WorkElapsedMs + s.RemainingMs);
        hourglass.SetSand(remaining, running);
        var nextDuration = snapshot.NextRestDurationMs > 0 ? snapshot.NextRestDurationMs :
            snapshot.NextWorkDurationMs > 0 ? snapshot.NextWorkDurationMs : snapshot.DefaultSettings.WorkDurationMs;
        time.Text=BreakOverlayHost.FormatTime(ready?nextDuration:s.RemainingMs);
        var status=warn?"即将休息":s.Phase switch{SessionPhase.Paused=>"已暂停",SessionPhase.Idle=>"待开始",SessionPhase.AwaitingCheckIn=>"待确认",SessionPhase.AwaitingRestMemo=>"等待开始休息",SessionPhase.Delaying=>"延后收尾",_=>"工作中"};
        AutomationProperties.SetName(floating,$"悬浮沙漏，{status}，{time.Text}。拖动调整位置，双击打开沙立，右键隐藏。");
        var available = Enabled && !snapshot.HasFullscreenCheckIn;
        var showFloating = available && !resting;
        var animate = SystemParameters.ClientAreaAnimation && !hourglass.Turning;
        if (!available || !resting || !animate) retractingForRest = false;
        else if (companionShown && hourglass.CompanionProgress > 0 && floating.IsVisible) retractingForRest = true;
        var showDuringTransition = showFloating || retractingForRest;
        glyph.IsHitTestVisible = !retractingForRest;
        UpdateHover();
        if (showDuringTransition)
        {
            if (!floating.IsVisible) floating.Show();
        }
        else floating.Hide();
        UpdateCompanion(showFloating && warn && s.RemainingMs > 2000 && !hourglass.Turning,
            animate && showDuringTransition, bothHands: popupDue);
        var segment=$"{snapshot.CurrentSessionId}:{s.Phase==SessionPhase.Delaying}";
        if (waitingForMemo || popupDue || running && s.RemainingMs == 0 && warning is not null)
        {
            if (!waitingForMemo && warning is not null && warningSegment != segment) CloseWarning();
            if (warning is null && !memoDismissalPending && (waitingForMemo || dismissedSegment != segment)) ShowWarning(snapshot, segment);
            if (warningTime is not null)
                warningTime.Text = waitingForMemo ? "00:00 · 写完后请点「开始休息」" :
                    $"{(long)Math.Ceiling(s.RemainingMs / 1000d)} 秒后休息，请保存手头工作。";
            foreach (var delay in warningDelayButtons)
                delay.IsEnabled = s.RemainingMs > 0 && snapshot.AllowedCommands.Contains(CommandType.DelayBreak);
        }
        else CloseWarning();
    }

    private void ShowWarning(AppSnapshot snapshot, string segment)
    {
        warningSegment = segment;
        warningTime = new TextBlock { FontSize = 18, Foreground = UiTheme.Warning, Margin = new Thickness(0, 0, 0, 12) };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(warningTime);
        var initialMemo = snapshot.WorkspaceDraft;
        var memo = new TextBox
        {
            Text = initialMemo, AcceptsReturn = true, Height = 64, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 6), TabIndex = 1
        };
        var label = new TextBlock { Text = "工作备忘录（选填）" };
        AutomationProperties.SetLabeledBy(memo, label);
        AutomationProperties.SetName(memo, "工作备忘录");
        panel.Children.Add(label);
        panel.Children.Add(memo);
        var buttons = new WrapPanel();
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = UiTheme.Warning, Visibility = Visibility.Collapsed };
        panel.Children.Add(buttons);
        panel.Children.Add(feedback);

        var editing = false;
        var sending = false;
        async Task<bool> Send(WorkClockCommand command)
        {
            try
            {
                var result = await app.HandleAsync(new(Guid.NewGuid(), command, snapshot.CurrentSessionId, null));
                if (result.Error is null && result.Disposition != CommandDisposition.Rejected) return true;
                feedback.Text = result.Error == ErrorCode.TextTooLong ? "备忘录最多 200 个字，请缩短后再开始休息。" :
                    result.Message ?? "操作未完成，请重试。";
            }
            catch { feedback.Text = "操作未完成，请重试。"; }
            feedback.Visibility = Visibility.Visible;
            return false;
        }
        memo.GotKeyboardFocus += async (_, _) =>
        {
            if (editing) return;
            editing = true;
            if (!await Send(new SetRestMemoEditing(true))) editing = false;
        };
        Button Add(string title, Func<WorkClockCommand> command, bool visibleLabel = false)
        {
            var button = IconButtons.Create(title);
            if (visibleLabel)
            {
                button.Content = title;
                button.Width = double.NaN;
                button.TabIndex = 0;
            }
            buttons.Children.Add(button);
            button.Click += async (_, _) =>
            {
                var emergency = title == "应急退出";
                if (sending && !emergency) return;
                if (emergency) { await Send(command()); return; }
                sending = true;
                try { await Send(command()); }
                finally { sending = false; }
            };
            return button;
        }
        Add("开始休息", () => new BreakNow(memo.Text == initialMemo ? null : memo.Text), visibleLabel: true);
        warningDelayButtons = snapshot.AllowedCommands.Contains(CommandType.DelayBreak) ?
            [Add("延后 3 分钟", () => new DelayBreak(3)), Add("延后 5 分钟", () => new DelayBreak(5))] : [];
        Add("应急退出", () => new EmergencyExit(snapshot.CurrentSessionId ?? Guid.Empty, null));

        var window = new Window
        {
            Title = "即将休息", Icon = AppBranding.WindowIcon, FontFamily = UiTypography.Family, Content = panel,
            Width = 390, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Topmost = true,
            ShowInTaskbar = false, ShowActivated = false
        };
        warning = window;
        UiTheme.Apply(window);
        window.Closed += async (_, _) =>
        {
            var dismissed = !closingWarning;
            if (dismissed) dismissedSegment = segment;
            warning = null;
            warningTime = null;
            warningDelayButtons = [];
            if (dismissed && editing)
            {
                memoDismissalPending = true;
                try { await Send(new SetRestMemoEditing(false)); }
                finally { memoDismissalPending = false; }
                Render(app.ReadSnapshot());
            }
        };
        panel.Measure(new Size(window.Width - 20, double.PositiveInfinity));
        PositionWarning(panel.DesiredSize.Height + 48);
        window.SizeChanged += (_, _) => PositionWarning();
        window.Show();
    }

    private void PositionWarning(double? initialHeight = null)
    {
        if (warning is not { } window) return;
        var height = initialHeight ?? window.ActualHeight;
        if (height <= 0) return;
        var obstacle = new Rect(floating.Left, floating.Top, floating.Width, floating.Height);
        var point = WarningPosition(SystemParameters.WorkArea, obstacle, new Size(window.Width, height));
        window.Left = point.X;
        window.Top = point.Y;
    }

    internal static Point WarningPosition(Rect area, Rect floating, Size warning)
    {
        const double gap = 12;
        var right = Math.Max(area.Left, area.Right - warning.Width);
        var bottom = Math.Max(area.Top, area.Bottom - warning.Height);
        Point Clamp(double x, double y) => new(Math.Clamp(x, area.Left, right), Math.Clamp(y, area.Top, bottom));
        Point[] candidates =
        [
            Clamp(floating.Left - warning.Width - gap, floating.Bottom - warning.Height),
            Clamp(floating.Right + gap, floating.Bottom - warning.Height),
            Clamp(floating.Right - warning.Width, floating.Top - warning.Height - gap),
            Clamp(floating.Right - warning.Width, floating.Bottom + gap)
        ];
        return candidates.MinBy(point =>
        {
            var intersection = Rect.Intersect(new Rect(point, warning), floating);
            return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
        });
    }
    private void CloseWarning(){if(warning is null)return;closingWarning=true;try{warning.Close();}finally{closingWarning=false;warning=null;warningTime=null;}}
    public void Dispose(){if(disposed)return;disposed=true;EndFlip(0);UpdateCompanion(false,false);subscription.Dispose();CloseWarning();floating.Close();}
}
