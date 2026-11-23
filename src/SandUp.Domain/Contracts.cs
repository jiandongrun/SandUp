namespace SandUp.Domain;

public enum SessionPhase { Idle, Working, PreBreak, Delaying, BreakPending, Resting, AwaitingCheckIn, Paused, AwaitingRestMemo, EditingRestMemo }
public enum PauseReason { User, SystemInterrupted, TimingGap, ClockChanged, OverlayUnavailable, StorageUnavailable }
public enum SessionOutcome { Active, Completed, Skipped, EndedEarly, Interrupted }
public enum EndReason { RestTargetReached, Emergency, UserEndedPaused, WorkTargetReached }
public enum CheckInStatus { Unrecorded, Reported }
public enum SliceKind { Work, Rest }
public enum WorkSubtype { Base, Extension, Memo }
public enum SessionEventKind { Start, Pause, Resume, BreakNow, Delay, Emergency, EndPaused, Pulse, OverlayShown, OverlayFailed, SystemInterrupted, DisplayChanged }
public enum ErrorCode { InvalidState, InvalidArgument, StaleCommand, SessionNotFound, SessionStillActive, DelayAlreadyUsed, DesktopUnavailable, OverlayUnavailable, StorageUnavailable, StorageConflict, CorruptDatabase, TextTooLong, InvalidActivity, UnknownPrompt, ConfirmationRequired, ExportFailed }
public sealed record ClockSample(Guid ProcessClockId, long MonotonicTicks, long Frequency, DateTimeOffset UtcNow, string TimeZoneId);
public sealed record SettingsSnapshot(long WorkDurationMs, long RestDurationMs, long NoticeDurationMs, IReadOnlyList<long> DelayOptionsMs, int MaxDelayCount)
{
    public CyclePlan? CyclePlan { get; init; }
    public CycleArrangement? Arrangement { get; init; }
    public ReminderRates ReminderRates { get; init; } = ReminderRates.Default;
}
public sealed record SessionState(Guid? SessionId, SessionPhase Phase, SessionPhase? ResumePhase, PauseReason? PauseReason, SettingsSnapshot? Settings, long WorkElapsedMs, long RestElapsedMs, long CurrentWorkTargetMs, bool DelayUsed, long RequestedDelayMs, long ActualDelayMs, Guid? CurrentSliceId, Guid? OverlayBatchId, ClockSample? ClockAnchor, TimeSlice? CurrentSlice, long SubMillisecondTicks, int NextSliceSequence)
{
    public bool RestMemoEditing { get; init; }
}
public abstract record SessionEvent;
public sealed record StartEvent(Guid SessionId, SettingsSnapshot Settings, Guid SliceId) : SessionEvent;
public sealed record PauseEvent : SessionEvent;
public sealed record ResumeEvent(Guid? OverlayBatchId, Guid? SliceId) : SessionEvent;
public sealed record BreakNowEvent(Guid OverlayBatchId) : SessionEvent;
public sealed record SetRestMemoEditingEvent(bool Editing) : SessionEvent;
public sealed record BeginBreakMemoEvent : SessionEvent;
public sealed record CompleteBreakMemoEvent : SessionEvent;
public sealed record DelayEvent(long DelayMs, Guid? SliceId) : SessionEvent;
public sealed record EmergencyEvent(Guid SessionId, Guid? OverlayBatchId) : SessionEvent;
public sealed record EndPausedEvent : SessionEvent;
public sealed record PulseEvent(Guid? OverlayBatchId) : SessionEvent;
public sealed record OverlayShownEvent(Guid OverlayBatchId, Guid SliceId) : SessionEvent;
public sealed record OverlayFailedEvent(Guid OverlayBatchId) : SessionEvent;
public sealed record SystemInterruptedEvent(PauseReason Reason, bool TimestampTrusted) : SessionEvent;
public sealed record DisplayChangedEvent(Guid OverlayBatchId) : SessionEvent;
public enum SessionAction { Start, Pause, Resume, BreakNow, Delay, Emergency, EndPaused }
public sealed record SessionProjection(SessionPhase Phase, long RemainingMs, long WorkElapsedMs, long RestElapsedMs, IReadOnlySet<SessionAction> AllowedActions)
{
    public bool RestFollows { get; init; } = true;
}
public sealed record TimeSlice(Guid SliceId, Guid SessionId, int SequenceNo, SliceKind Kind, WorkSubtype? WorkSubtype, DateOnly LocalDate, string TimeZoneId, DateTimeOffset StartedAtUtc, DateTimeOffset EndedAtUtc, long DurationMs, bool Finalized);
public abstract record SessionEffect;
public sealed record ShowOverlayEffect(Guid SessionId, Guid BatchId) : SessionEffect;
public sealed record CloseOverlayEffect(Guid BatchId) : SessionEffect;
public sealed record SessionResultChange(Guid SessionId, SessionOutcome Outcome, EndReason EndReason);
public sealed record Transition(SessionState NextState, IReadOnlyList<TimeSlice> Slices, SessionResultChange? Result, IReadOnlyList<SessionEffect> SideEffects, ErrorCode? Rejection);
public interface ISessionEngine { Transition Reduce(SessionState state, SessionEvent @event, ClockSample clock); SessionProjection Project(SessionState state, ClockSample clock); IReadOnlyList<string> Validate(SessionState state); }
