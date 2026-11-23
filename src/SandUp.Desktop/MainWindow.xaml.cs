using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SandUp.Application;
using SandUp.Domain;
namespace SandUp.Desktop;
public partial class MainWindow : Window
{
    private readonly IWorkClockApplication app;
    private readonly IHistoryService history;
    private readonly IHistoryExporter exporter;
    private IDisposable? subscription;
    private readonly TextBlock stateText=new(){FontSize=20,TextWrapping=TextWrapping.Wrap}, timeText=new(){FontSize=64,Foreground=UiTheme.Green,VerticalAlignment=VerticalAlignment.Center}, status=new(){TextWrapping=TextWrapping.Wrap,Foreground=UiTheme.Warning};
    private readonly WrapPanel actions=new();
    private readonly StackPanel checks=new();
    private readonly StackPanel homePage=new(), settingsPage=new(){Visibility=Visibility.Collapsed}, recordsPage=new(){Visibility=Visibility.Collapsed};
    private readonly RecordsDashboard records;
    private readonly HomeReminderPanel reminderPanel=new();
    private readonly ReminderRateEditor rateEditor=new();
    private readonly CyclePlanEditor cycleEditor=new();
    private readonly HomePlanPanel homePlan=new();
    private int homeGeneration;
    private readonly TextBlock settingsFeedback=new(){TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock pageTitle=new(){Text=AppBranding.DisplayName,FontSize=26,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center};
    private readonly WrapPanel navigation=new();
    private readonly DockPanel header=new();
    private readonly DispatcherTimer summaryTimer=new(){Interval=TimeSpan.FromSeconds(5)};
    private readonly DispatcherTimer midnightTimer=new();
    private DateOnly displayedDate;
    private readonly Func<DateTime> localNow;
    private bool summaryLoading,closed;
    private bool deferFirstShow;
    private readonly Dictionary<string,CheckBox> activityChecks=new();
    private Guid? renderedCheckIn;
    private long renderedRevision=-1;
    private SessionPhase? previousPhase;
    private bool previousFullscreenCheckIn;
    public bool AllowClose {get;set;}
    public MainWindow(IWorkClockApplication application,IHistoryService historyService,IHistoryExporter historyExporter,FloatingClockPresenter? floating=null, Func<DateTime>? localClock=null, bool startInTray=false)
    {
        InitializeComponent();Icon=AppBranding.WindowIcon;app=application;history=historyService;exporter=historyExporter;
        deferFirstShow = startInTray;
        UiTheme.Apply(this, dim: true);
        FontFamily = UiTypography.Family;
        localNow=localClock ?? (() => DateTime.Now); displayedDate=DateOnly.FromDateTime(localNow());
        DockPanel.SetDock(navigation,Dock.Right);header.Children.Add(navigation);
        settingsStatus.Child = settingsStatusLabel;
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { pageTitle, settingsStatus } });
        Root.Children.Add(header);
        Root.Children.Add(homePage);Root.Children.Add(settingsPage);Root.Children.Add(recordsPage);
        timeText.Margin = new Thickness(0, 0, 14, 0);
        var timerControls = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { stateText, actions } };
        var clockRow = new Grid { Children = { timeText, timerControls } };
        clockRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        clockRow.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(timerControls, 1);
        homePage.Children.Add(clockRow);
        homePage.Children.Add(checks);homePage.Children.Add(status);homePage.Children.Add(homePlan);homePage.Children.Add(reminderPanel);
        var initialSettings = app.ReadSnapshot().DefaultSettings;
        RestoreSettings(initialSettings);
        settingsPage.Children.Add(cycleEditor);
        settingsPage.Children.Add(rateEditor);
        AddButton(settingsPage,"保存设置",SaveSettingsAsync);settingsPage.Children.Add(settingsFeedback);
        cycleEditor.Edited += OnSettingsEdited;
        rateEditor.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => OnSettingsEdited()));
        if(floating is not null)
        {
            var toggle=new CheckBox{Content="显示悬浮沙漏",IsChecked=floating.Enabled,Margin=new Thickness(0,20,0,8),ToolTip="立即应用，与托盘菜单同步。"};
            toggle.Checked+=(_,_)=>{if(!floating.Enabled)floating.SetEnabled(true);};toggle.Unchecked+=(_,_)=>{if(floating.Enabled)floating.SetEnabled(false);};
            void Changed(bool enabled)=>toggle.IsChecked=enabled;
            floating.EnabledChanged+=Changed;Closed+=(_,_)=>floating.EnabledChanged-=Changed;
            settingsPage.Children.Add(toggle);settingsPage.Children.Add(new TextBlock{Text="剩余 20 秒小人眨眼挥手，10 秒双手挥动并弹出预警，最后 2 秒变回沙漏。双击打开主页面，右键可隐藏。",TextWrapping=TextWrapping.Wrap});
        }
        records = new RecordsDashboard(history, app.ReadSnapshot().Activities, EditActivities, Export, Clear);
        recordsPage.Children.Add(records);
        ShowPage(homePage,AppBranding.DisplayName);
        Closing += CloseToTray;
        summaryTimer.Tick+=async(_,_)=>{if (!await CheckDateAsync()) await RefreshHomeSummaryAsync();};
        midnightTimer.Tick+=async(_,_)=>{if (!closed) { ScheduleMidnight(); await CheckDateAsync(); }};
        Activated+=async(_,_)=>{if (!closed) { ScheduleMidnight(); await CheckDateAsync(); }};
        Loaded+=async(_,_)=>{if (closed) return; deferFirstShow=false;ScheduleMidnight();summaryTimer.Start();if (!await CheckDateAsync()) await RefreshHomeSummaryAsync();};
        Closed+=(_,_)=>{closed=true;subscription?.Dispose();summaryTimer.Stop();midnightTimer.Stop();};
        subscription=app.SubscribeSnapshots(s=>Dispatcher.BeginInvoke(()=>Render(app.ReadSnapshot())));Render(app.ReadSnapshot());
    }
    private void ScheduleMidnight()
    {
        if (closed) return;
        midnightTimer.Stop();
        var now = localNow();
        midnightTimer.Interval = now.Date.AddDays(1).ToUniversalTime() - now.ToUniversalTime();
        midnightTimer.Start();
    }
    private async Task<bool> CheckDateAsync()
    {
        if (closed) return false;
        var current = DateOnly.FromDateTime(localNow());
        if (current == displayedDate) return false;
        displayedDate = current;
        homeGeneration++;
        homePlan.ShowTotalsLoading();
        reminderPanel.ShowLoading();
        if (recordsPage.Visibility == Visibility.Visible) await records.RefreshAsync();
        if (homePage.Visibility == Visibility.Visible) await RefreshHomeSummaryAsync();
        return true;
    }
    private async Task RefreshHomeSummaryAsync()
    {
        if (summaryLoading || closed || !IsVisible || homePage.Visibility != Visibility.Visible) return;
        summaryLoading = true;
        var generation = homeGeneration;
        var today = DateOnly.FromDateTime(localNow());
        bool IsCurrent() => !closed && IsVisible && generation == homeGeneration && homePage.Visibility == Visibility.Visible;
        bool SameDate() => today == DateOnly.FromDateTime(localNow());
        try
        {
            var day = await history.GetDayAsync(today);
            if (!IsCurrent()) return;
            if (!SameDate())
            {
                homePlan.ShowTotalsLoading();
                reminderPanel.ShowLoading();
                return;
            }
            homePlan.ShowTotals(day.WorkMs, day.RestMs);
            reminderPanel.ShowDays(today, [day]);
        }
        catch
        {
            if (!IsCurrent()) return;
            homePlan.ShowTotalsError();
            reminderPanel.ShowError();
        }
        finally
        {
            summaryLoading = false;
            if (!closed && (generation != homeGeneration || !SameDate()) && homePage.Visibility == Visibility.Visible)
                _ = RefreshHomeSummaryAsync();
        }
    }
    private void ShowPage(StackPanel page,string title)
    {
        if (displayedDate != DateOnly.FromDateTime(localNow()))
        {
            displayedDate = DateOnly.FromDateTime(localNow());
            homePlan.ShowTotalsLoading();
            reminderPanel.ShowLoading();
        }
        homeGeneration++;
        homePage.Visibility=page==homePage?Visibility.Visible:Visibility.Collapsed;
        settingsPage.Visibility=page==settingsPage?Visibility.Visible:Visibility.Collapsed;
        settingsStatus.Visibility = settingsPage.Visibility;
        Root.Margin = page == homePage ? new Thickness(24, 10, 24, 6) : new Thickness(28);
        header.Margin = new Thickness(0, 0, 0, page == homePage ? 3 : 22);
        recordsPage.Visibility=page==recordsPage?Visibility.Visible:Visibility.Collapsed;
        pageTitle.Text=title;navigation.Children.Clear();PageScroll.ScrollToTop();
        if(page!=homePage)AddButton(navigation,"返回",()=>Navigate(homePage,AppBranding.DisplayName));
        if(page!=settingsPage)AddButton(navigation,"设置",()=>Navigate(settingsPage,"设置"));
        if(page!=recordsPage)AddButton(navigation,"记录",()=>Navigate(recordsPage,"统计"));
    }
    private async Task Navigate(StackPanel page,string title)
    {
        if (settingsPage.Visibility == Visibility.Visible && page != settingsPage && !await ConfirmSettingsLeaveAsync()) return;
        ShowPage(page,title);
        if(page==recordsPage)await records.RefreshAsync();
        if(page==homePage)await RefreshHomeSummaryAsync();
    }
    private static void AddButton(Panel panel,string title,Func<Task> action)
    {
        var b=IconButtons.Create(title);
        b.Click+=async(_,_)=>{try{await action();}catch(Exception e){MessageBox.Show(e.Message,"操作未完成");}};panel.Children.Add(b);
    }
    private static void SetOptionalText(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private async Task<CommandResult> Send(WorkClockCommand command)
    {
        var s=app.ReadSnapshot();var result=await app.HandleAsync(new(Guid.NewGuid(),command,s.CurrentSessionId,s.ViewRevision));
        Render(result.Snapshot);if(result.Error.HasValue)SetOptionalText(status,result.Message??"操作未完成，请重试。");return result;
    }
    private void Render(AppSnapshot s)
    {
        if(s.ViewRevision<renderedRevision)return;
        stateText.Text=s.Session.Phase switch{SessionPhase.Idle=>"准备好后，开始一轮",SessionPhase.Working=>"正在专注",SessionPhase.PreBreak=>"准备收尾，马上休息",SessionPhase.Delaying=>"延后中 · 请先保存工作",SessionPhase.AwaitingRestMemo=>"等待完成工作备忘录",SessionPhase.EditingRestMemo=>"编辑工作备忘录 · 休息暂停",SessionPhase.BreakPending=>"正在准备休息窗口",SessionPhase.Resting=>"休息时间",SessionPhase.Paused=>"已暂停 · 手动继续",_=>"休息结束 · 自愿记录活动"};
        var nextDuration = s.NextRestDurationMs > 0 ? s.NextRestDurationMs :
            s.NextWorkDurationMs > 0 ? s.NextWorkDurationMs : s.DefaultSettings.WorkDurationMs;
        timeText.Text=BreakOverlayHost.FormatTime(s.Session.Phase==SessionPhase.Idle?nextDuration:s.Session.RemainingMs);
        homePlan.ShowPlan(s.PlanProgress);
        SetOptionalText(status,string.Join("\n",new[]{
            s.Persistence.Status is PersistenceStatus.PendingRetry or PersistenceStatus.Unavailable ? "记录未保存："+s.Persistence.FailureReason : "",
            s.Notice}.Where(text=>!string.IsNullOrWhiteSpace(text))));
        if(renderedRevision!=s.ViewRevision)
        {
            renderedRevision=s.ViewRevision;actions.Children.Clear();
            void Action(CommandType kind,string label,Func<WorkClockCommand> command){if(s.AllowedCommands.Contains(kind))AddButton(actions,label,()=>Send(command()));}
            Action(CommandType.StartSession,s.NextRestDurationMs>0?"开始休息":"开始工作",()=>new StartSession());Action(CommandType.PauseSession,"暂停",()=>new PauseSession());Action(CommandType.ResumeSession,"继续本轮",()=>new ResumeSession());
            if(s.Session.Phase!=SessionPhase.AwaitingRestMemo)Action(CommandType.BreakNow,"现在休息",()=>new BreakNow());
            Action(CommandType.DelayBreak,"延后 3 分钟",()=>new DelayBreak(3));Action(CommandType.DelayBreak,"延后 5 分钟",()=>new DelayBreak(5));
            Action(CommandType.EmergencyExit,"应急退出",()=>new EmergencyExit(s.CurrentSessionId??Guid.Empty,null));Action(CommandType.EndPausedSession,"结束本轮",()=>new EndPausedSession());
            if(s.Persistence.Status is PersistenceStatus.PendingRetry or PersistenceStatus.Unavailable)AddButton(actions,"重试保存",async()=>{await app.RetryPendingSaveAsync();Render(app.ReadSnapshot());});
        }
        if(s.Session.Phase==SessionPhase.AwaitingCheckIn&&!s.HasFullscreenCheckIn&&s.PendingCheckInSessionId is Guid id)
        {
            if(renderedCheckIn!=id){checks.Children.Clear();activityChecks.Clear();renderedCheckIn=id;checks.Children.Add(new TextBlock{Text="刚才做了哪些活动？可留空。"});
                foreach(var a in s.Activities){var c=new CheckBox{Content=a.DisplayName,Margin=new Thickness(5)};activityChecks[a.ActivityId]=c;checks.Children.Add(c);}
                string[] Selected()=>activityChecks.Where(x=>x.Value.IsChecked==true).Select(x=>x.Key).ToArray();
                AddButton(checks,"保存并开始下一轮",()=>Send(new SubmitCheckInAndContinue(id,Selected())));AddButton(checks,"不记录，继续",()=>Send(new SubmitCheckInAndContinue(id,[])));AddButton(checks,"稍后继续",()=>Send(new SaveCheckInAndStay(id,Selected())));
            }
        }else{checks.Children.Clear();renderedCheckIn=null;}
        if(s.Session.Phase!=previousPhase || s.HasFullscreenCheckIn!=previousFullscreenCheckIn)
        {
            if(IsLoaded)_=RefreshHomeSummaryAsync();
            if(s.Session.Phase==SessionPhase.AwaitingCheckIn&&!s.HasFullscreenCheckIn){ShowPage(homePage,AppBranding.DisplayName);_=RefreshHomeSummaryAsync();if(!IsVisible&&!deferFirstShow){ShowActivated=false;Show();}}
            previousPhase=s.Session.Phase;
            previousFullscreenCheckIn=s.HasFullscreenCheckIn;
        }
    }
    private async Task EditActivities(SessionRecord record)
    {
        var existing=(await history.GetActivitiesAsync(record.SessionId)).Select(x=>x.ActivityId).ToHashSet();
        var panel=new StackPanel{Margin=new Thickness(22)};var entries=new Dictionary<string,CheckBox>();
        panel.Children.Add(new TextBlock{Text="活动由你自报；留空表示未记录。",Margin=new Thickness(0,0,0,12)});
        foreach(var activity in app.ReadSnapshot().Activities){var box=new CheckBox{Content=activity.DisplayName,IsChecked=existing.Contains(activity.ActivityId),Margin=new Thickness(5)};entries.Add(activity.ActivityId,box);panel.Children.Add(box);}
        var dialog=new Window{Title="补填活动",Icon=AppBranding.WindowIcon,FontFamily=UiTypography.Family,Owner=this,Content=panel,Width=360,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        UiTheme.Apply(dialog);
        var feedback=new TextBlock{TextWrapping=TextWrapping.Wrap};panel.Children.Add(feedback);
        AddButton(panel,"保存",async()=>{var result=await Send(new UpdateHistoricalCheckIn(record.SessionId,entries.Where(x=>x.Value.IsChecked==true).Select(x=>x.Key).ToArray()));if(result.Disposition!=CommandDisposition.Rejected&&result.Persistence.Status==PersistenceStatus.Saved){dialog.Close();await records.RefreshAsync();}else feedback.Text="尚未保存，请重试；当前选择仍保留。";});
        AddButton(panel,"取消",()=>{dialog.Close();return Task.CompletedTask;});dialog.ShowDialog();
    }
    private async Task Export()
    {
        var picker=new Microsoft.Win32.OpenFolderDialog{Title="选择导出所在文件夹（会创建新子目录）"};if(picker.ShowDialog(this)!=true)return;
        var result=await exporter.ExportAsync(new(new(DateOnly.MinValue,DateOnly.MaxValue),Path.Combine(picker.FolderName,"SandUp-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"))),CancellationToken.None);records.ShowFeedback(result.Status==ExportStatus.Succeeded?"已导出："+result.OutputDirectory:"导出未完成："+result.Error);
    }
    private async Task Clear(){if(MessageBox.Show(this,"清空所有已结束记录？当前轮次、设置和接续事项会保留。","清空记录",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;var result=await Send(new ClearHistory(new(new(DateOnly.MinValue,DateOnly.MaxValue),true,false)));await records.RefreshAsync();await RefreshHomeSummaryAsync();records.ShowFeedback(result.Disposition!=CommandDisposition.Rejected&&result.Persistence.Status==PersistenceStatus.Saved?"已清空已结束记录。":result.Message??"未能清空，请重试。");}
}


