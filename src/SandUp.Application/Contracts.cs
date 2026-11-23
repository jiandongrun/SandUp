using SandUp.Domain;
namespace SandUp.Application;

public enum CommandType { StartSession, PauseSession, ResumeSession, BreakNow, DelayBreak, EmergencyExit, EndPausedSession, SaveResumeNote, SelectPrompt, SubmitCheckInAndContinue, SaveCheckInAndStay, DismissCheckIn, UpdateHistoricalCheckIn, UpdateSettings, ClearHistory }
public enum CommandDisposition { Accepted, Rejected, NoOp }
public enum PersistenceStatus { Saved, Saving, PendingRetry, Unavailable }
public sealed record PersistenceState(PersistenceStatus Status, DateTimeOffset? LastSavedAtUtc, string? FailureReason);
public sealed record CommandEnvelope(Guid CommandId, WorkClockCommand Command, Guid? SessionId, long? ExpectedViewRevision);
public sealed record ActivityDefinition(string ActivityId, string DisplayName, string? PromptId, string? Title, string? Body);
public sealed record PlannedModule(SliceKind Kind, long DurationMs);
public sealed record PlanProgress(IReadOnlyList<PlannedModule> Modules, int CompletedCount, bool HasCurrent,
    bool HasEarlierModules = false, bool HasLaterModules = false);
public sealed record AppSnapshot(long ViewRevision, SessionProjection Session, Guid? CurrentSessionId, Guid? PendingCheckInSessionId, string WorkspaceDraft, SettingsSnapshot DefaultSettings, IReadOnlyList<ActivityDefinition> Activities, PersistenceState Persistence, string? Notice, IReadOnlySet<CommandType> AllowedCommands)
{
    public long NextWorkDurationMs { get; init; }
    public long NextRestDurationMs { get; init; }
    public PlanProgress? PlanProgress { get; init; }
    public bool HasFullscreenCheckIn { get; init; }
}
public sealed record CommandResult(CommandDisposition Disposition, ErrorCode? Error, AppSnapshot Snapshot, PersistenceState Persistence, string? Message);
public sealed record ShutdownReport(bool WindowsClosed, bool Saved, string? UnsavedMessage);
public interface IWorkClockApplication { Task<AppSnapshot> InitializeAsync(); Task<CommandResult> HandleAsync(CommandEnvelope envelope); AppSnapshot ReadSnapshot(); IDisposable SubscribeSnapshots(Action<AppSnapshot> observer); void PostSystemEvent(SystemSignal signal); Task<CommandResult> RetryPendingSaveAsync(); Task<ShutdownReport> ShutdownAsync(TimeSpan deadline); }
public interface IClock { ClockSample Read(); }
public interface IPulseSource { IDisposable Start(TimeSpan interval, Action callback); }
public interface ISystemSignals { IDisposable Subscribe(Action<SystemSignal> callback); }
public enum SystemSignalKind { Lock, Unlock, Suspend, Resume, SessionDisconnected, SessionConnected, SessionSwitch, Logoff, Exit, ClockChanged, TimeZoneChanged, DisplaysChanged }
public sealed record SystemSignal(SystemSignalKind Kind, ClockSample Clock, IReadOnlyList<DisplayInfo>? Displays);
public sealed record PhysicalBounds(int Left, int Top, int Width, int Height);
public sealed record DisplayInfo(string DisplayId, bool IsPrimary, PhysicalBounds PhysicalBounds, double DpiX, double DpiY);
public interface IDisplayTopology { IReadOnlyList<DisplayInfo> Read(); }
public enum InstanceResult { Primary, ExistingInstance }
public interface ISingleInstance { InstanceResult TryAcquire(); }
public enum OverlayActionKind { EmergencyExit, DelayThreeMinutes, DelayFiveMinutes, SelectPrompt, ContinueAfterBreak, FinishBreakForNow, BeginMemo, ConfirmMemo }
public enum OverlayCloseReason { Emergency, Completed, Delay, Interrupted, DisplayChanged, Failure, Shutdown }
public sealed record OverlayRequest(Guid BatchId, Guid SessionId, long ViewRevision, IReadOnlyList<DisplayInfo> Displays, long RemainingRestMs, string? PromptId, IReadOnlySet<OverlayActionKind> AllowedActions)
{
    public string? SegmentCaption { get; init; }
}
public sealed record OverlayResult(Guid BatchId, IReadOnlyList<string> ShownDisplays, IReadOnlyList<string> FailedDisplays, bool AllShown, ErrorCode? Error);
public sealed record OverlayViewModel(Guid BatchId, long RemainingRestMs, string? PromptId, IReadOnlySet<OverlayActionKind> AllowedActions)
{
    public string? SegmentCaption { get; init; }
    public string? Memo { get; init; }
    public IReadOnlyList<ActivityDefinition>? CheckInActivities { get; init; }
    public DateOnly? CheckInLocalDate { get; init; }
}
public sealed record OverlayAction(Guid BatchId, Guid SessionId, OverlayActionKind Action, string? PromptId = null)
{
    public IReadOnlyList<string>? ActivityIds { get; init; }
    public string? Memo { get; init; }
}
public interface IBreakOverlayHost { Task<OverlayResult> ShowAsync(OverlayRequest request, CancellationToken cancellation); void Update(OverlayViewModel model); void CloseAll(Guid? batchId, OverlayCloseReason reason); IDisposable SubscribeActions(Action<OverlayAction> callback); }
