using SandUp.Domain;
namespace SandUp.Application;
public sealed record WorkspaceDraft(string Text, DateTimeOffset UpdatedAtUtc);
public sealed record SessionRecord(Guid SessionId, DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc, string StartTimeZoneId, DateOnly StartedLocalDate, string? EndTimeZoneId, DateOnly? EndedLocalDate, long PlannedWorkMs, long PlannedRestMs, long NoticeMs, bool DelayUsed, long RequestedDelayMs, long ActualDelayMs, long WorkMs, long RestMs, SessionOutcome Outcome, EndReason? EndReason, CheckInStatus CheckInStatus, string ResumeNote, DateTimeOffset UpdatedAtUtc);
public sealed record RuntimeCheckpoint(Guid? SessionId, SessionPhase SavedPhase, SessionPhase? ResumePhase, PauseReason? PauseReason, long WorkElapsedMs, long RestElapsedMs, long CurrentWorkTargetMs, bool DelayUsed, long RequestedDelayMs, long ActualDelayMs, Guid? CurrentSliceId, string? CurrentPromptId, Guid? PendingCheckInSessionId, DateTimeOffset SavedAtUtc, long StorageRevision)
{
    public CyclePlan? CyclePlan { get; init; }
    public long CycleRound { get; init; }
    public long CycleGeneration { get; init; }
}
public sealed record OperationReceipt(Guid OperationId, Guid? CommandId, Guid? SessionId, long CommittedRevision, string? ResultReference);
public sealed record StoredSettings(SettingsSnapshot Settings, DateTimeOffset UpdatedAtUtc, int SchemaVersion)
{
    public long CycleCompletedRounds { get; init; }
    public long CycleGeneration { get; init; }
}
public sealed record PersistedAppState(long StorageRevision, StoredSettings Settings, WorkspaceDraft WorkspaceDraft, RuntimeCheckpoint? CurrentCheckpoint, SessionRecord? CurrentSession, IReadOnlyList<TimeSlice> CurrentSlices, Guid? PendingCheckInSessionId);
// Null means no change; ClearCheckpoint and SetCheckpoint are distinct operations.
public abstract record CheckpointChange;
public sealed record SetCheckpoint(RuntimeCheckpoint Value) : CheckpointChange;
public sealed record ClearCheckpoint : CheckpointChange;
// Replaces all activities for one session, including an empty list for Unrecorded.
public sealed record ActivityReplacement(Guid SessionId, IReadOnlyList<ActivityReport> Activities);
public sealed record AtomicWriteBatch(Guid OperationId, long ExpectedStorageRevision, IReadOnlyList<SessionRecord> Sessions, IReadOnlyList<TimeSlice> Slices, IReadOnlyList<ActivityReplacement> ActivityReplacements, WorkspaceDraft? Draft, StoredSettings? Settings, CheckpointChange? Checkpoint, OperationReceipt Receipt);
public enum CommitStatus { Committed, AlreadyCommitted, Conflict, Failed }
public sealed record CommitResult(CommitStatus Status, long? NewStorageRevision, string? DiagnosticError);
public sealed record MigrationReport(int PreviousVersion, int CurrentVersion, bool Succeeded, string? Error);
public sealed record DateRange(DateOnly From, DateOnly To);
public sealed record SessionQuery(DateRange Range, string? Cursor, int PageSize);
public sealed record SessionPage(IReadOnlyList<SessionRecord> Items, string? NextCursor, DateTimeOffset DataAsOfUtc);
public sealed record ClearHistoryRequest(DateRange Range, bool Confirmed, bool IncludeDraft);
public interface IWorkClockStore { Task<MigrationReport> OpenAndMigrateAsync(); Task<PersistedAppState> LoadAsync(); Task<CommitResult> CommitAsync(AtomicWriteBatch batch); Task<SessionPage> QuerySessionsAsync(SessionQuery query); Task<IReadOnlyList<TimeSlice>> QuerySlicesAsync(DateRange range); Task<int> ClearHistoryAsync(ClearHistoryRequest request); }
public readonly record struct RoundDurationTotals(long DurationMs, int RoundCount);
public sealed record DaySummary(DateOnly LocalDate, long WorkMs, long RestMs, int CompletedCount, int SkippedCount,
    int EndedEarlyCount, int InterruptedCount, RoundDurationTotals WorkRounds, RoundDurationTotals RestRounds,
    IReadOnlyDictionary<string, int> ActivityCounts, DateTimeOffset DataAsOfUtc)
{
    public ReminderDayState Reminder { get; init; } = new(ReminderLevels.Full, false);
}
public interface IHistoryService
{
    Task<DaySummary> GetDayAsync(DateOnly localDate);
    Task<IReadOnlyList<DaySummary>> GetDaysAsync(DateRange range);
    Task<SessionPage> GetSessionsAsync(SessionQuery query);
    Task<IReadOnlyList<ActivityReport>> GetActivitiesAsync(Guid sessionId);
}
public enum ExportStatus { Succeeded, Cancelled, Failed }
public enum ExportFormat { CsvPair }
public sealed record ExportRequest(DateRange Range, string TargetDirectory, bool IncludeNotes = false, ExportFormat Format = ExportFormat.CsvPair);
public sealed record ExportReport(ExportStatus Status, string? OutputDirectory, IReadOnlyList<string> OutputFiles, int SessionCount, int SliceCount, ErrorCode? Error);
public interface IHistoryExporter { Task<ExportReport> ExportAsync(ExportRequest request, CancellationToken cancellation); }
public interface IActivityCatalog { IReadOnlyList<ActivityDefinition> List(); }
public interface IWorkClockRecordReader { Task<SessionRecord?> FindSessionAsync(Guid sessionId); Task<IReadOnlyList<ActivityReport>> ReadActivitiesAsync(Guid sessionId); }
public interface IWorkClockOperationReader { Task<OperationReceipt?> FindReceiptByCommandAsync(Guid commandId); }
