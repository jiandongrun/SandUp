using SandUp.Domain;

namespace SandUp.Application;
// Each variant has exactly the payload described by the command table.
public abstract record WorkClockCommand;
public sealed record StartSession : WorkClockCommand;
public sealed record PauseSession : WorkClockCommand;
public sealed record ResumeSession : WorkClockCommand;
public sealed record BreakNow(string? Memo = null) : WorkClockCommand;
public sealed record SetRestMemoEditing(bool Editing) : WorkClockCommand;
internal sealed record BeginBreakMemo : WorkClockCommand;
internal sealed record CompleteBreakMemo(string Memo) : WorkClockCommand;
public sealed record DelayBreak(int Minutes) : WorkClockCommand;
public sealed record EmergencyExit(Guid SessionId, Guid? OverlayBatchId) : WorkClockCommand;
public sealed record EndPausedSession : WorkClockCommand;
public sealed record SaveResumeNote(string Text) : WorkClockCommand;
public sealed record SelectPrompt(string PromptId) : WorkClockCommand;
public sealed record SubmitCheckInAndContinue(Guid SessionId, IReadOnlyList<string> ActivityIds) : WorkClockCommand;
public sealed record SaveCheckInAndStay(Guid SessionId, IReadOnlyList<string> ActivityIds) : WorkClockCommand;
public sealed record DismissCheckIn(Guid SessionId) : WorkClockCommand;
public sealed record UpdateHistoricalCheckIn(Guid SessionId, IReadOnlyList<string> ActivityIds) : WorkClockCommand;
public sealed record UpdateSettings(int WorkMinutes, int RestMinutes, ReminderRates? Rates = null) : WorkClockCommand
{
    public CyclePlan? CyclePlan { get; init; }
    public CycleArrangement? Arrangement { get; init; }
}
public sealed record ClearHistory(ClearHistoryRequest Request) : WorkClockCommand;
public sealed record ActivityReport(Guid SessionId, string ActivityId, DateTimeOffset ReportedAtUtc);
