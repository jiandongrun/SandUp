using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Automation;
using Microsoft.Win32;
using SandUp.Application;
using SandUp.Domain;
using Forms = System.Windows.Forms;

namespace SandUp.Desktop;

public sealed class SystemClock : IClock
{
    private readonly Guid processId = Guid.NewGuid();
    public ClockSample Read() => new(processId, Stopwatch.GetTimestamp(), Stopwatch.Frequency,
        DateTimeOffset.UtcNow, TimeZoneInfo.Local.Id);
}
public sealed class PulseSource : IPulseSource
{
    public IDisposable Start(TimeSpan interval, Action callback) => new Timer(_ => callback(), null, interval, interval);
}
public sealed class Subscription(Action dispose) : IDisposable
{
    private Action? action = dispose;
    public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
}
public sealed class WindowsSignals(IClock clock) : ISystemSignals
{
    public IDisposable Subscribe(Action<SystemSignal> callback)
    {
        void Send(SystemSignalKind kind) => callback(new(kind, clock.Read(), null));
        SessionSwitchEventHandler session = (_, e) => Send(e.Reason switch
        {
            SessionSwitchReason.SessionLock => SystemSignalKind.Lock,
            SessionSwitchReason.SessionUnlock => SystemSignalKind.Unlock,
            SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect => SystemSignalKind.SessionDisconnected,
            SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect => SystemSignalKind.SessionConnected,
            SessionSwitchReason.SessionLogoff => SystemSignalKind.Logoff,
            _ => SystemSignalKind.SessionSwitch
        });
        PowerModeChangedEventHandler power = (_, e) => { if(e.Mode == PowerModes.Suspend) Send(SystemSignalKind.Suspend); else if(e.Mode == PowerModes.Resume) Send(SystemSignalKind.Resume); };
        EventHandler display = (_, _) => Send(SystemSignalKind.DisplaysChanged);
        EventHandler time = (_, _) => { TimeZoneInfo.ClearCachedData(); Send(SystemSignalKind.ClockChanged); };
        SessionEndingEventHandler ending = (_, _) => Send(SystemSignalKind.Exit);
        SystemEvents.SessionSwitch += session; SystemEvents.PowerModeChanged += power;
        SystemEvents.DisplaySettingsChanged += display; SystemEvents.TimeChanged += time; SystemEvents.SessionEnding += ending;
        return new Subscription(() => { SystemEvents.SessionSwitch -= session; SystemEvents.PowerModeChanged -= power; SystemEvents.DisplaySettingsChanged -= display; SystemEvents.TimeChanged -= time; SystemEvents.SessionEnding -= ending; });
    }
}
public sealed class DisplayTopology : IDisplayTopology
{
    public IReadOnlyList<DisplayInfo> Read() => Forms.Screen.AllScreens.Select(s =>
    {
        var b=s.Bounds;var monitor=MonitorFromPoint(new MonitorPoint(b.Left+b.Width/2,b.Top+b.Height/2),2);
        if(GetDpiForMonitor(monitor,0,out var x,out var y)!=0)throw new InvalidOperationException("无法读取显示器缩放。");
        return new DisplayInfo(s.DeviceName,s.Primary,new(b.X,b.Y,b.Width,b.Height),x,y);
    }).ToArray();
    [StructLayout(LayoutKind.Sequential)] private readonly record struct MonitorPoint(int X,int Y);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(MonitorPoint point,uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint x,out uint y);
}

public sealed class BreakOverlayHost(Dispatcher dispatcher,Action<Exception>? reportFailure=null, IHistoryService? history=null) : IBreakOverlayHost
{
    private sealed class Surface(Guid sessionId, Window window, StackPanel panel, TextBlock heading, TextBlock time, TextBlock prompt, TextBlock guidance, WrapPanel buttons, Button emergency, Button changePrompt)
    {
        public Guid SessionId { get; } = sessionId;
        public Window Window { get; } = window;
        public StackPanel Panel { get; } = panel;
        public TextBlock Heading { get; } = heading;
        public TextBlock Time { get; } = time;
        public TextBlock Prompt { get; } = prompt;
        public TextBlock Guidance { get; } = guidance;
        public WrapPanel Buttons { get; } = buttons;
        public Button Emergency { get; } = emergency;
        public Button ChangePrompt { get; } = changePrompt;
        public BreakBodyIllustration Illustration { get; set; } = null!;
        public StackPanel MemoPanel { get; init; } = null!;
        public TextBox Memo { get; init; } = null!;
        public TextBlock MemoFeedback { get; init; } = null!;
        public Button ConfirmMemo { get; init; } = null!;
        public bool MemoSelected { get; set; }
        public StackPanel? Activities { get; set; }
        public Button? Continue { get; set; }
        public Button? Finish { get; set; }
        public bool Closed { get; set; }
        public string PromptId { get; set; } = "walk";
    }
    private readonly HashSet<Guid> checkInSubmitted = new();
    private static readonly ActivityDefinition[] Prompts = new BuiltinActivityCatalog().List().Where(a => a.PromptId is not null).ToArray();
    private readonly Dictionary<Guid,List<Surface>> batches = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid,byte> invalidated = new();
    private event Action<OverlayAction>? Actions;
    public IDisposable SubscribeActions(Action<OverlayAction> callback) { Actions += callback; return new Subscription(() => Actions -= callback); }
    public async Task<OverlayResult> ShowAsync(OverlayRequest request, CancellationToken cancellation)
    {
        if(request.Displays.Count == 0) return new(request.BatchId, [], [], false, ErrorCode.OverlayUnavailable);
        using var registration = cancellation.Register(() => CloseAll(request.BatchId, OverlayCloseReason.Interrupted));
        try
        {
            var deadline = Stopwatch.StartNew();
            var rendered = await dispatcher.InvokeAsync(() => Create(request), DispatcherPriority.Normal, cancellation).Task.WaitAsync(TimeSpan.FromSeconds(2), cancellation).ConfigureAwait(false);
            await Task.WhenAll(rendered).WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1,2000-deadline.ElapsedMilliseconds)), cancellation).ConfigureAwait(false);
            return await dispatcher.InvokeAsync(() =>
            {
                if(invalidated.ContainsKey(request.BatchId) || !batches.TryGetValue(request.BatchId,out var windows) || windows.Any(w=>!w.Window.IsVisible))
                    return new OverlayResult(request.BatchId, [], request.Displays.Select(d=>d.DisplayId).ToArray(), false, ErrorCode.OverlayUnavailable);
                return new OverlayResult(request.BatchId, request.Displays.Select(d=>d.DisplayId).ToArray(), [], true, null);
            }, DispatcherPriority.Normal, cancellation).Task.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1,2000-deadline.ElapsedMilliseconds)), cancellation).ConfigureAwait(false);
        }
        catch (Exception error) { CloseAll(request.BatchId, OverlayCloseReason.Failure); try{reportFailure?.Invoke(error);}catch{} return new(request.BatchId, [], request.Displays.Select(d=>d.DisplayId).ToArray(), false, ErrorCode.OverlayUnavailable); }
    }
    private Task[] Create(OverlayRequest request)
    {
        if(invalidated.ContainsKey(request.BatchId)) throw new OperationCanceledException();
        var surfaces = new List<Surface>(); batches.Add(request.BatchId, surfaces);
        var tasks = new List<Task>();
        foreach(var display in request.Displays)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); tasks.Add(done.Task);
            var panel = new StackPanel { VerticalAlignment=VerticalAlignment.Center, HorizontalAlignment=HorizontalAlignment.Center, MaxWidth=650, Margin=new Thickness(30) };
            var time = new TextBlock { FontSize=64, Foreground=UiTheme.Green, TextAlignment=TextAlignment.Center, Margin=new Thickness(12) };
            var prompt = new TextBlock { FontSize=28, TextAlignment=TextAlignment.Center, TextWrapping=TextWrapping.Wrap, Margin=new Thickness(20) };
            var heading = new TextBlock { Text="休息时间 · 可以离开屏幕", TextAlignment=TextAlignment.Center, TextWrapping=TextWrapping.Wrap, FontSize=18 };
            panel.Children.Add(heading);
            panel.Children.Add(time); panel.Children.Add(prompt);
            var guidance = new TextBlock { FontSize=20, Foreground=UiTheme.Muted, TextWrapping=TextWrapping.Wrap, TextAlignment=TextAlignment.Center, Margin=new Thickness(10) };
            panel.Children.Add(guidance);
            var illustration=new BreakBodyIllustration { HorizontalAlignment=HorizontalAlignment.Center }; panel.Children.Add(illustration);
            var window = new Window { Title=$"{AppBranding.DisplayName} · 休息", Icon=AppBranding.WindowIcon, FontFamily=UiTypography.Family, WindowStyle=WindowStyle.None, ResizeMode=ResizeMode.NoResize, Topmost=true, ShowInTaskbar=false,
                Content=new ScrollViewer { Content=panel, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled, VerticalContentAlignment=VerticalAlignment.Center, HorizontalContentAlignment=HorizontalAlignment.Stretch } };
            UiTheme.Apply(window, dim: true);
            void Fire(OverlayActionKind action, string? id=null)
            {
                if(action==OverlayActionKind.EmergencyExit) CloseAll(request.BatchId,OverlayCloseReason.Emergency);
                Actions?.Invoke(new(request.BatchId,request.SessionId,action,id));
            }
            var memoPanel = new StackPanel
            {
                Width = 340, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 4), Visibility = Visibility.Collapsed
            };
            var memoLabel = new TextBlock { Text = "工作备忘录 · 点选暂停休息", Foreground = UiTheme.Muted };
            var memo = new TextBox
            {
                Height = 62, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsReadOnly = true,
                Margin = new Thickness(0, 4, 0, 4), TabIndex = 1
            };
            AutomationProperties.SetName(memo, "工作备忘录");
            AutomationProperties.SetLabeledBy(memo, memoLabel);
            AutomationProperties.SetHelpText(memo, "选择输入框可暂停休息并编辑备忘录。");
            var memoFeedback = new TextBlock { Foreground = UiTheme.Warning, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var confirmMemo = new Button
            {
                Content = "确定", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(18, 7, 18, 7),
                Background = UiTheme.Rest, Foreground = UiTheme.Green, Visibility = Visibility.Collapsed
            };
            memoPanel.Children.Add(memoLabel);
            memoPanel.Children.Add(memo);
            memoPanel.Children.Add(memoFeedback);
            memoPanel.Children.Add(confirmMemo);
            panel.Children.Add(memoPanel);

            var overlayButtons=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Center};panel.Children.Add(overlayButtons);
            Button Button(string text, Action action) { var button=IconButtons.Create(text);button.Click+=(_,_)=>action();overlayButtons.Children.Add(button);return button; }
            var emergency=Button("应急退出  Esc",()=>Fire(OverlayActionKind.EmergencyExit));
            emergency.TabIndex = 0;
            Surface? surface = null;
            var changePrompt=Button("换一个提示",()=> { var index=Array.FindIndex(Prompts,p=>p.PromptId==surface!.PromptId); Fire(OverlayActionKind.SelectPrompt,Prompts[(index+1)%Prompts.Length].PromptId); });
            surface = new Surface(request.SessionId, window, panel, heading, time, prompt, guidance, overlayButtons, emergency, changePrompt)
            {
                Illustration = illustration, MemoPanel = memoPanel, Memo = memo, MemoFeedback = memoFeedback, ConfirmMemo = confirmMemo
            };
            memo.GotKeyboardFocus += (_, _) =>
            {
                if (!memo.IsReadOnly || !memo.IsEnabled || surfaces.Any(s => s.MemoSelected)) return;
                surface.MemoSelected = true;
                Fire(OverlayActionKind.BeginMemo);
            };
            confirmMemo.Click += (_, _) =>
            {
                if (!surface.MemoSelected || memo.IsReadOnly || !confirmMemo.IsEnabled) return;
                if (new StringInfo(memo.Text).LengthInTextElements > 200)
                {
                    memoFeedback.Text = "备忘录最多 200 个字，请缩短后再确定。";
                    memoFeedback.Visibility = Visibility.Visible;
                    memo.Focus();
                    return;
                }
                confirmMemo.IsEnabled = false;
                Actions?.Invoke(new OverlayAction(request.BatchId, request.SessionId, OverlayActionKind.ConfirmMemo) { Memo = memo.Text });
            };
            window.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Escape) return;
                e.Handled = true;
                Fire(OverlayActionKind.EmergencyExit);
            };
            window.Closed += (_, _) =>
            {
                surface.Closed = true;
                if (!invalidated.ContainsKey(request.BatchId)) Fire(OverlayActionKind.EmergencyExit);
            };
            window.ContentRendered+=(_,_)=>
            {
                var b=display.PhysicalBounds;
                if(SetWindowPos(new WindowInteropHelper(window).Handle,new IntPtr(-1),b.Left,b.Top,b.Width,b.Height,0x0040))done.TrySetResult();
                else done.TrySetException(new InvalidOperationException("休息窗口定位失败。"));
            };
            window.SourceInitialized+=(_,_)=> {var b=display.PhysicalBounds;SetWindowPos(new WindowInteropHelper(window).Handle,new IntPtr(-1),b.Left,b.Top,b.Width,b.Height,0x0040);};
            surfaces.Add(surface); window.Show();
        }
        UpdateCore(new(request.BatchId,request.RemainingRestMs,request.PromptId,request.AllowedActions) { SegmentCaption=request.SegmentCaption });
        return tasks.ToArray();
    }
    public void Update(OverlayViewModel model) => dispatcher.BeginInvoke(()=>UpdateCore(model));
    private void UpdateCore(OverlayViewModel model)
    {
        if(invalidated.ContainsKey(model.BatchId)||!batches.TryGetValue(model.BatchId,out var surfaces))return;
        bool enteringCheckIn = model.CheckInActivities is not null && surfaces.Any(s => s.Activities is null);
        foreach(var s in surfaces)
        {
            s.Emergency.IsEnabled=model.AllowedActions.Contains(OverlayActionKind.EmergencyExit);
            if(model.CheckInActivities is null)
            {
                var editingMemo = model.Memo is not null && model.AllowedActions.Contains(OverlayActionKind.ConfirmMemo);
                var ownsEditor = editingMemo && s.MemoSelected;
                s.Heading.Text = editingMemo ? "编辑工作备忘录 · 休息已暂停" :
                    string.IsNullOrWhiteSpace(model.SegmentCaption) ? "休息时间 · 可以离开屏幕" : model.SegmentCaption;
                s.Time.Text=FormatTime(model.RemainingRestMs);
                s.Time.Foreground = editingMemo ? UiTheme.Warning : UiTheme.Green;
                s.MemoPanel.Visibility = model.Memo is null ? Visibility.Collapsed : Visibility.Visible;
                s.Memo.IsEnabled = ownsEditor || model.AllowedActions.Contains(OverlayActionKind.BeginMemo);
                if (!editingMemo && !s.Memo.IsReadOnly)
                {
                    s.MemoSelected = false;
                    if (s.Memo.IsKeyboardFocusWithin || s.ConfirmMemo.IsKeyboardFocusWithin) s.Emergency.Focus();
                }
                s.Memo.IsReadOnly = !ownsEditor;
                if (!ownsEditor)
                {
                    var savedMemo = model.Memo ?? "";
                    if (s.Memo.Text != savedMemo) s.Memo.Text = savedMemo;
                    s.MemoFeedback.Visibility = Visibility.Collapsed;
                }
                s.ConfirmMemo.IsEnabled = ownsEditor;
                s.ConfirmMemo.Visibility = ownsEditor ? Visibility.Visible : Visibility.Collapsed;
                s.Illustration.Visibility = s.Prompt.Visibility = s.Guidance.Visibility = s.ChangePrompt.Visibility =
                    editingMemo ? Visibility.Collapsed : Visibility.Visible;
                var activity=Prompts.FirstOrDefault(a=>a.PromptId==model.PromptId) ?? Prompts[0];
                s.PromptId=activity.PromptId!;
                s.Illustration.ShowPrompt(s.PromptId);
                s.Prompt.Text=activity.Title;
                s.Guidance.Text=activity.Body;
                s.ChangePrompt.IsEnabled=model.AllowedActions.Contains(OverlayActionKind.SelectPrompt);
                continue;
            }
            if(s.Activities is null)
            {
                s.Heading.Text="休息完成";s.Heading.FontSize=36;s.Heading.FontWeight=FontWeights.Bold;
                s.Time.Visibility=Visibility.Collapsed;s.Prompt.Visibility=Visibility.Collapsed;s.Illustration.Visibility=Visibility.Collapsed;
                s.MemoPanel.Visibility = Visibility.Collapsed;
                s.Guidance.Visibility = Visibility.Visible;
                s.Guidance.Text="刚才做了哪些活动？可多选，也可以直接继续。";
                s.ChangePrompt.Visibility=Visibility.Collapsed;
                s.Activities=new StackPanel { HorizontalAlignment=HorizontalAlignment.Center, Margin=new Thickness(10) };
                foreach(var activity in model.CheckInActivities)
                    s.Activities.Children.Add(new CheckBox { Content=activity.DisplayName, Tag=activity.ActivityId, FontSize=20, Margin=new Thickness(8,5,8,5) });
                s.Panel.Children.Insert(s.Panel.Children.IndexOf(s.Buttons),s.Activities);
                Button Choice(string text, OverlayActionKind action, bool primary)
                {
                    var button=new Button { Content=text, FontSize=18, FontWeight=primary?FontWeights.Bold:FontWeights.Normal,
                        Padding=new Thickness(22,12,22,12), Margin=new Thickness(5),
                        Background=primary?UiTheme.Rest:UiTheme.Raised,
                        Foreground=primary?UiTheme.Green:UiTheme.Text,
                        BorderBrush=UiTheme.Line };
                    button.Click+=(_,_)=>
                    {
                        if(!button.IsEnabled || invalidated.ContainsKey(model.BatchId) || !checkInSubmitted.Add(model.BatchId))return;
                        foreach(var surface in surfaces) { if(surface.Continue is not null)surface.Continue.IsEnabled=false; if(surface.Finish is not null)surface.Finish.IsEnabled=false; }
                        var selected=s.Activities.Children.OfType<CheckBox>().Where(c=>c.IsChecked==true).Select(c=>(string)c.Tag).ToArray();
                        Actions?.Invoke(new OverlayAction(model.BatchId,s.SessionId,action) { ActivityIds=selected });
                    };
                    return button;
                }
                s.Continue=Choice("开始下一轮",OverlayActionKind.ContinueAfterBreak,true);
                s.Finish=Choice("稍后继续",OverlayActionKind.FinishBreakForNow,false);
                s.Buttons.Children.Insert(0,s.Continue);s.Buttons.Children.Insert(1,s.Finish);
            }
            s.Continue!.IsEnabled=!checkInSubmitted.Contains(model.BatchId)&&model.AllowedActions.Contains(OverlayActionKind.ContinueAfterBreak);
            s.Finish!.IsEnabled=!checkInSubmitted.Contains(model.BatchId)&&model.AllowedActions.Contains(OverlayActionKind.FinishBreakForNow);
        }
        if (enteringCheckIn && history is not null && model.CheckInLocalDate is DateOnly day)
            _ = LoadReminderPreviewAsync(surfaces, model.BatchId, day);
    }

    private async Task LoadReminderPreviewAsync(IReadOnlyList<Surface> surfaces, Guid batchId, DateOnly date)
    {
        try
        {
            var day = await history!.GetDayAsync(date).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                if (invalidated.ContainsKey(batchId)) return;
                foreach (var surface in surfaces)
                {
                    if (surface.Closed || surface.Activities is null) continue;
                    foreach (var choice in surface.Activities.Children.OfType<CheckBox>())
                        AddReminderPreview(choice, day.Reminder.Levels);
                }
            });
        }
        catch { /* A failed preview must never block completion. */ }
    }

    private static void AddReminderPreview(CheckBox choice, ReminderLevels levels)
    {
        var id = (string)choice.Tag;
        var parts = id switch
        {
            "look_away" => new[] { BodyPart.Eyes },
            "relax_shoulders_hands" => new[] { BodyPart.Shoulders },
            "walk" => new[] { BodyPart.Back, BodyPart.Abdomen, BodyPart.Legs, BodyPart.Pelvic },
            _ => Array.Empty<BodyPart>()
        };
        if (parts.Length == 0) return;
        var restored = levels.Restore(id);
        var name = (string)choice.Content;
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = name });
        var meters = new List<(BodyPart Part, string Label, ProgressBar Bar, TextBlock Value)>();
        foreach (var part in parts)
        {
            var label = BodyPartDisplay.Name(part);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
            row.Children.Add(new TextBlock { Text = label, Width = 90, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var bar = new ProgressBar { Minimum = 0, Maximum = ReminderLevels.Maximum, Width = 100, Height = 8,
                Foreground = UiTheme.Green, Background = UiTheme.Line,
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Focusable = false };
            var value = new TextBlock { FontSize = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(bar); row.Children.Add(value); content.Children.Add(row);
            meters.Add((part, label, bar, value));
        }
        choice.Content = content;
        choice.ToolTip = "勾选后预览，保存选择后更新今日耐久度。";
        void Refresh()
        {
            var selected = choice.IsChecked == true ? restored : levels;
            var descriptions = new List<string>();
            foreach (var (part, label, bar, value) in meters)
            {
                var amount = selected.For(part);
                bar.Value = amount;
                value.Text = $"{amount:0.#}/{ReminderLevels.Maximum}";
                AutomationProperties.SetName(bar, $"{label}，今日耐久度 {value.Text}");
                descriptions.Add($"{label} {value.Text}");
            }
            AutomationProperties.SetName(choice, $"{name}，今日耐久度 {string.Join("，", descriptions)}");
        }
        choice.Checked += (_, _) => Refresh();
        choice.Unchecked += (_, _) => Refresh();
        Refresh();
    }

    public void CloseAll(Guid? batchId, OverlayCloseReason reason)
    {
        if(batchId.HasValue) invalidated.TryAdd(batchId.Value,0);
        void Close()
        {
            var ids = batchId.HasValue ? new[] { batchId.Value } : batches.Keys.ToArray();
            foreach (var id in ids)
            {
                invalidated.TryAdd(id, 0);
                checkInSubmitted.Remove(id);
                if (!batches.Remove(id, out var surfaces)) continue;
                foreach (var surface in surfaces)
                    if (!surface.Closed) surface.Window.Close();
            }
        }
        if(dispatcher.CheckAccess())Close();else dispatcher.BeginInvoke(Close,DispatcherPriority.Send);
    }
    public static string FormatTime(long ms) {var seconds=Math.Max(0,(long)Math.Ceiling(ms/1000d));return $"{seconds/60:00}:{seconds%60:00}";}
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
}

