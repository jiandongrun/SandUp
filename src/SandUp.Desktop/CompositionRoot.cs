using SandUp.Application;
using SandUp.Domain;
using SandUp.Infrastructure;
namespace SandUp.Desktop;
internal static class CompositionRoot
{
    internal static (IWorkClockApplication App, SqliteWorkClockStore Store, IHistoryService History, IHistoryExporter Exporter) Create(string directory)
    {
        var clock=new SystemClock();var store=new SqliteWorkClockStore(Path.Combine(directory,"healthy-work.db"));
        var history=new SqliteHistoryService(store);
        return(new WorkClockApplication(new SessionEngine(),clock,new PulseSource(),store,new BreakOverlayHost(System.Windows.Application.Current.Dispatcher, history:history),new DisplayTopology(),new WindowsSignals(clock)),store,history,new CsvHistoryExporter(store));
    }
}
