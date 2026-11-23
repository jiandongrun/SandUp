using System.Windows;
using SandUp.Application;
using SandUp.Infrastructure;
using Forms=System.Windows.Forms;
namespace SandUp.Desktop;
public partial class App : System.Windows.Application
{
    // Keep both application versions on the same single-instance lock while they share the data directory.
    private const string InstanceName = "Local\\HealthyWorkClock";

    private Mutex? mutex;private EventWaitHandle? activation;private RegisteredWaitHandle? activationWait;private Forms.NotifyIcon? tray;
    private IWorkClockApplication? service;private MainWindow? window;private bool quitting;private System.Drawing.Icon? trayIcon;private FloatingClockPresenter? floating;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);var smoke=e.Args.Contains("--smoke");var suffix=smoke?".Smoke":"";
        var startInTray = e.Args.Contains(StartupRegistration.StartupArgument);
        mutex=new Mutex(true,InstanceName+suffix,out var first);activation=new EventWaitHandle(false,EventResetMode.AutoReset,InstanceName+".Activate"+suffix);
        if(!first){if(!startInTray)activation.Set();Shutdown();return;}
        try
        {
            var data=smoke?Path.Combine(Path.GetTempPath(),"SandUp-Smoke-"+Guid.NewGuid()):Path.GetDirectoryName(SqliteWorkClockStore.DefaultPath)!;Directory.CreateDirectory(data);
            var c=CompositionRoot.Create(data);service=c.App;await service.InitializeAsync();
            floating=new FloatingClockPresenter(service,Dispatcher,Path.Combine(data,"desktop.json"),()=>{if(window is not null){window.Show();window.WindowState=WindowState.Normal;window.Activate();}});
            window=new MainWindow(service,c.History,c.Exporter,floating,startInTray:startInTray);
            window.AddStartupSetting(new StartupRegistration(Environment.ProcessPath));
            MainWindow=window;
            if (!startInTray) window.Show();
            void Show(){window.Show();window.Activate();if(window.WindowState==WindowState.Minimized)window.WindowState=WindowState.Normal;}
            activationWait=ThreadPool.RegisterWaitForSingleObject(activation,(_,_)=>Dispatcher.BeginInvoke(Show),null,Timeout.Infinite,false);
            var menu=new Forms.ContextMenuStrip
            {
                ShowImageMargin = false, BackColor = TrayMenuColors.ToColor(UiTheme.Surface),
                ForeColor = TrayMenuColors.ToColor(UiTheme.Text), Renderer = new Forms.ToolStripProfessionalRenderer(new TrayMenuColors())
            };
            menu.Items.Add("打开沙立",null,(_,_)=>Dispatcher.BeginInvoke(Show));menu.Items.Add("退出应用",null,async(_,_)=>await Quit());
            menu.Items.Insert(1,new Forms.ToolStripMenuItem("显示/隐藏悬浮沙漏",null,(_,_)=>Dispatcher.BeginInvoke(()=>floating.SetEnabled(!floating.Enabled))));
            foreach (Forms.ToolStripItem item in menu.Items) item.ForeColor = menu.ForeColor;
            trayIcon=AppBranding.LoadTrayIcon();tray=new Forms.NotifyIcon{Text=AppBranding.DisplayName,Icon=trayIcon,ContextMenuStrip=menu,Visible=true};tray.DoubleClick+=(_,_)=>Dispatcher.BeginInvoke(Show);
            if (smoke)
            {
                if (startInTray)
                {
                    if (window.IsVisible || !tray.Visible || service.ReadSnapshot().Session.Phase != SandUp.Domain.SessionPhase.Idle)
                        throw new InvalidOperationException("登录启动应进入托盘并保持待开始。");
                    Show();
                }
                await Task.Delay(3000);
                window.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var image = File.Create(Path.Combine(data, "smoke-window.png"))) encoder.Save(image);
                await service.HandleAsync(new(Guid.NewGuid(), new StartSession(), null, null));
                await Task.Delay(700);
                var running = service.ReadSnapshot();
                await service.HandleAsync(new(Guid.NewGuid(), new PauseSession(), running.CurrentSessionId, null));
                var paused = service.ReadSnapshot();
                var saved = await c.Store.LoadAsync();
                if (paused.Session.Phase != SandUp.Domain.SessionPhase.Paused || paused.Session.WorkElapsedMs <= 0 || saved.CurrentSession?.WorkMs != paused.Session.WorkElapsedMs)
                    throw new InvalidOperationException("启动/暂停/SQLite整合校验失败");
                File.WriteAllText(Path.Combine(data, "smoke-result.txt"),
                    $"Start → Pause → SQLite passed; workMs={paused.Session.WorkElapsedMs}; phase={paused.Session.Phase}; runtime={Environment.Version}; mode={(startInTray ? "tray" : "window")}");
                await Quit();
            }
        }catch(Exception ex){if(smoke){Console.Error.WriteLine(ex);Shutdown(1);}else{MessageBox.Show("启动失败，数据文件已保留。\n"+ex.Message,AppBranding.DisplayName);Shutdown(1);}}
    }
    private async Task Quit()
    {
        if(quitting)return;quitting=true;
        try
        {
            if (window is not null && !await window.ConfirmSettingsLeaveAsync()) return;
            try { if(service is not null)await service.ShutdownAsync(TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* Process termination also removes its windows. */ }
            finally { floating?.Dispose();if(window is not null){window.AllowClose=true;window.Close();}Shutdown(); }
        }
        finally { quitting = false; }
    }
    protected override void OnExit(ExitEventArgs e){floating?.Dispose();tray?.Dispose();trayIcon?.Dispose();activationWait?.Unregister(null);activation?.Dispose();mutex?.Dispose();base.OnExit(e);}
}



