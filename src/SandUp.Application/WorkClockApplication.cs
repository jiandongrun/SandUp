using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using SandUp.Domain;

namespace SandUp.Application;

/// <summary>One ordered application queue owns the session. Emergency window closure never waits for that queue.</summary>
public sealed class WorkClockApplication : IWorkClockApplication
{
    private readonly ISessionEngine _engine;
    private readonly IClock _clock;
    private readonly IPulseSource _pulses;
    private readonly IWorkClockStore _store;
    private readonly IBreakOverlayHost _overlays;
    private readonly IDisplayTopology _displays;
    private readonly ISystemSignals? _signals;
    private readonly IReadOnlyList<ActivityDefinition> _activities;
    private readonly ConcurrentQueue<Func<Task>> _urgent = new(), _normal = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly ConcurrentDictionary<Guid, byte> _invalidBatches = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _presentations = new();
    private readonly ConcurrentDictionary<long, Action<AppSnapshot>> _observers = new();
    private readonly Dictionary<Guid, CommandResult> _receipts = new();
    private readonly Queue<AtomicWriteBatch> _pending = new();
    private readonly Dictionary<Guid, TimeSlice> _slices = new();
    private readonly Task _worker;
    private SessionState _state = EmptyState();
    private SessionRecord? _session;
    private SettingsSnapshot _settings = Defaults();
    private WorkspaceDraft _draft = new("", DateTimeOffset.MinValue);
    private PersistenceState _persistence = new(PersistenceStatus.Saved, null, null);
    private Guid? _pendingCheckIn;
    private string _prompt = "walk";
    private long _storageRevision, _viewRevision, _observerId;
    private long _cycleCompletedRounds, _cycleGeneration, _roundNumber, _roundGeneration;
    private CyclePlan? _roundPlan;
    private int _restSegment = -1;
    private IReadOnlyList<CycleSegment>? _roundSegments;
    private CycleSchedule? _cycleSchedule, _roundSchedule;
    private PlanProgressKey lastProgressKey;
    private PlanProgress? lastProgress;
    private long _roundCount, _roundRepeatStart;
    private bool _cursorDirty;
    private DateTimeOffset _lastCheckpoint;
    private AppSnapshot _snapshot;
    private bool _initialized, _desktopAvailable = true, _shutdown;
    private int _pulseQueued;
    private IDisposable? _pulseSubscription, _systemSubscription, _overlaySubscription;

    public WorkClockApplication(ISessionEngine engine, IClock clock, IPulseSource pulses,
        IWorkClockStore store, IBreakOverlayHost overlays, IDisplayTopology displays,
        ISystemSignals? signals = null, IActivityCatalog? catalog = null)
    {
        _engine = engine; _clock = clock; _pulses = pulses; _store = store;
        _overlays = overlays; _displays = displays; _signals = signals;
        _activities = Array.AsReadOnly((catalog ?? new BuiltinActivityCatalog()).List().ToArray());
        _snapshot = BuildSnapshot();
        _worker = Task.Run(PumpAsync);
    }

    public static SettingsSnapshot Defaults() => new(1_500_000, 300_000, 20_000, Array.AsReadOnly(new long[] { 180_000, 300_000 }), 1);
    private static SessionState EmptyState() => new(null, SessionPhase.Idle, null, null, null, 0, 0, 0, false, 0, 0, null, null, null, null, 0, 0);

    public Task<AppSnapshot> InitializeAsync() => Queue(async () =>
    {
        if (_initialized) return ReadSnapshot();
        try
        {
            var migration = await _store.OpenAndMigrateAsync();
            if (!migration.Succeeded) throw new InvalidOperationException(migration.Error ?? "数据库迁移失败");
            var saved = await _store.LoadAsync();
            _storageRevision = saved.StorageRevision;
            _cycleCompletedRounds = saved.Settings.CycleCompletedRounds;
            _cycleGeneration = saved.Settings.CycleGeneration;
            var stored = saved.Settings.Settings;
            _cycleSchedule = stored.CyclePlan?.Compile();
            if (stored.CyclePlan is { Blocks: not null } && _cycleSchedule is null)
                throw new InvalidOperationException("无法读取保存的循环计划。");
            var arrangement = stored.Arrangement?.UpgradeLegacyCounts();
            if (arrangement is not null && !arrangement.Matches(stored.CyclePlan))
                throw new InvalidOperationException("无法读取保存的组合库。");
            _settings = CopySettings(stored with
            {
                NoticeDurationMs = 20_000,
                Arrangement = arrangement,
                ReminderRates = stored.ReminderRates is { IsValid: true } rates ? rates : ReminderRates.Default
            });
            if (_cycleSchedule is { } schedule && (_cycleCompletedRounds < 0 || _cycleCompletedRounds >= schedule.Rounds.Count))
                _cycleCompletedRounds = 0;
            _draft = saved.WorkspaceDraft;
            _session = saved.CurrentSession;
            _pendingCheckIn = saved.PendingCheckInSessionId;
            foreach (var slice in saved.CurrentSlices) _slices[slice.SliceId] = slice;
            RestoreCheckpoint(saved.CurrentCheckpoint);
            if ((_state.Phase is SessionPhase.Idle or SessionPhase.AwaitingCheckIn) &&
                (_cycleCompletedRounds == 0 || _cycleCompletedRounds == _cycleSchedule?.RepeatStart))
                UpgradeWorkMerging();
            _initialized = true;
            _lastCheckpoint = _clock.Read().UtcNow;
            _persistence = new(PersistenceStatus.Saved, saved.CurrentCheckpoint?.SavedAtUtc, null);
            _pulseSubscription = _pulses.Start(TimeSpan.FromMilliseconds(250), OnPulse);
            _systemSubscription = _signals?.Subscribe(PostSystemEvent);
            _overlaySubscription = _overlays.SubscribeActions(OnOverlayAction);
            Publish();
        }
        catch (Exception)
        {
            _persistence = new(PersistenceStatus.Unavailable, null, "无法打开本地记录，原数据已保留。请检查数据目录后重试。");
            Publish();
        }
        return ReadSnapshot();
    });

    private void RestoreCheckpoint(RuntimeCheckpoint? checkpoint)
    {
        if (checkpoint is null || _session is null) return;
        _prompt = checkpoint.CurrentPromptId ?? "walk";
        _roundPlan = checkpoint.CyclePlan?.Copy();
        _roundNumber = checkpoint.CycleRound;
        _roundGeneration = checkpoint.CycleGeneration;
        var schedule = _roundPlan?.Compile();
        if (_roundPlan is { Blocks: not null } && (schedule is null || _roundNumber < 0 || _roundNumber >= schedule.Rounds.Count))
            throw new InvalidOperationException("无法读取保存的循环进度。");
        SetRoundSegments(schedule);
        if (_session.Outcome != SessionOutcome.Active)
        {
            if (_pendingCheckIn is not null)
                _state = EmptyState() with { SessionId = _session.SessionId, Phase = SessionPhase.AwaitingCheckIn,
                    Settings = SessionSettings(_session),
                    WorkElapsedMs = _session.WorkMs, RestElapsedMs = _session.RestMs };
            return;
        }
        var phase = checkpoint.SavedPhase == SessionPhase.Paused ? checkpoint.ResumePhase : checkpoint.SavedPhase;
        if (phase is SessionPhase.BreakPending or SessionPhase.Resting or SessionPhase.AwaitingRestMemo or SessionPhase.EditingRestMemo) phase = SessionPhase.Resting;
        var settings = SessionSettings(_session) with { CyclePlan = _roundPlan };
        // A new process never reuses a persisted monotonic timestamp or an open slice.
        foreach (var slice in _slices.Values.ToArray()) _slices[slice.SliceId] = slice with { Finalized = true };
        _state = new(_session.SessionId, SessionPhase.Paused, phase ?? SessionPhase.Working,
            checkpoint.PauseReason ?? PauseReason.SystemInterrupted, settings, checkpoint.WorkElapsedMs,
            checkpoint.RestElapsedMs, checkpoint.CurrentWorkTargetMs, checkpoint.DelayUsed,
            checkpoint.RequestedDelayMs, checkpoint.ActualDelayMs, null, null, null, null, 0,
            _slices.Count == 0 ? 0 : _slices.Values.Max(s => s.SequenceNo) + 1);
        _restSegment = SegmentIndex();
    }

    public Task<CommandResult> HandleAsync(CommandEnvelope envelope)
    {
        var captured = _clock.Read();
        var urgent = envelope.Command is EmergencyExit or PauseSession;
        if (envelope.Command is EmergencyExit emergency)
        {
            // Only a matching session may close the current batch. Old callbacks cannot affect a new round.
            var visible = ReadSnapshot();
            if (emergency.OverlayBatchId is Guid oldBatch) InvalidateAndClose(oldBatch, OverlayCloseReason.Emergency);
            else if (visible.CurrentSessionId == emergency.SessionId) InvalidateAndClose(null, OverlayCloseReason.Emergency);
        }
        return Queue(() => HandleCoreAsync(envelope, captured), urgent);
    }

    private async Task<CommandResult> HandleCoreAsync(CommandEnvelope envelope, ClockSample now)
    {
        if (!_initialized || _shutdown) return Reject(ErrorCode.InvalidState);
        if (_receipts.TryGetValue(envelope.CommandId, out var previous)) return previous;
        if (envelope.Command is not SetRestMemoEditing && _store is IWorkClockOperationReader reader && _pending.Count == 0)
        {
            try { if (await reader.FindReceiptByCommandAsync(envelope.CommandId) is not null) return Result(CommandDisposition.NoOp); }
            catch { return Reject(ErrorCode.StorageUnavailable); }
        }
        var safety = envelope.Command is EmergencyExit or PauseSession or EndPausedSession or DismissCheckIn;
        if (envelope.Command is not EmergencyExit && envelope.ExpectedViewRevision is long revision && revision != _viewRevision) return Reject(ErrorCode.StaleCommand);
        if (envelope.Command is not EmergencyExit && envelope.SessionId is Guid expected && expected != _state.SessionId) return Reject(ErrorCode.StaleCommand);
        if (!safety && _pending.Count > 0) return Reject(ErrorCode.StorageUnavailable);
        if (envelope.Command is StartSession or ResumeSession or SubmitCheckInAndContinue && !_desktopAvailable) return Reject(ErrorCode.DesktopUnavailable);

        CommandResult result;
        switch (envelope.Command)
        {
            case UpdateSettings settings:
                result = await UpdateSettingsAsync(settings, envelope.CommandId, now); break;
            case SaveResumeNote note:
                if (note.Text is null) return Reject(ErrorCode.InvalidArgument);
                if (new StringInfo(note.Text).LengthInTextElements > 200) return Reject(ErrorCode.TextTooLong);
                SetResumeNote(note.Text, now);
                result = await SaveMutationAsync(envelope.CommandId, now, draft: true); break;
            case SelectPrompt prompt:
                if (_state.Phase != SessionPhase.Resting) return Reject(ErrorCode.InvalidState);
                if (!_activities.Any(a => a.PromptId == prompt.PromptId)) return Reject(ErrorCode.UnknownPrompt);
                _prompt = prompt.PromptId;
                result = await SaveMutationAsync(envelope.CommandId, now);
                UpdateOverlay(); break;
            case SubmitCheckInAndContinue check:
                result = await CheckInAsync(check.SessionId, check.ActivityIds, true, false, envelope.CommandId, now); break;
            case SaveCheckInAndStay check:
                result = await CheckInAsync(check.SessionId, check.ActivityIds, false, false, envelope.CommandId, now); break;
            case UpdateHistoricalCheckIn check:
                result = await CheckInAsync(check.SessionId, check.ActivityIds, false, true, envelope.CommandId, now); break;
            case DismissCheckIn dismiss:
                if (_state.Phase != SessionPhase.AwaitingCheckIn || dismiss.SessionId != _pendingCheckIn) return Reject(ErrorCode.InvalidState);
                CloseCheckInOverlay();
                _state = EmptyState(); _pendingCheckIn = null;
                result = await SaveMutationAsync(envelope.CommandId, now); break;
            case ClearHistory clear:
                result = await ClearAsync(clear.Request); break;
            default:
                var memo = envelope.Command switch { BreakNow start => start.Memo, CompleteBreakMemo complete => complete.Memo, _ => null };
                if (memo is not null && new StringInfo(memo).LengthInTextElements > 200) return Reject(ErrorCode.TextTooLong);
                var domainEvent = MapEvent(envelope.Command);
                if (domainEvent is null) return Reject(ErrorCode.InvalidArgument);
                var transition = _engine.Reduce(_state, domainEvent, now);
                if (transition.Rejection is ErrorCode error) return Reject(error);
                if (domainEvent is SetRestMemoEditingEvent && transition.NextState.Phase == _state.Phase)
                {
                    _state = transition.NextState;
                    return Result(CommandDisposition.Accepted);
                }
                if (envelope.Command is StartSession) CreateSession(((StartEvent)domainEvent).SessionId, ((StartEvent)domainEvent).Settings, now);
                ApplyTransition(transition, now);
                var saveMemo = memo is not null && memo != _draft.Text &&
                    (envelope.Command is BreakNow && _state.Phase == SessionPhase.BreakPending ||
                     envelope.Command is CompleteBreakMemo && _state.Phase == SessionPhase.Resting);
                if (saveMemo) SetResumeNote(memo!, now);
                result = await SaveMutationAsync(envelope.CommandId, now, draft: saveMemo);
                if (_pending.Count == 0) RunShowEffects(transition);
                break;
        }
        if (result.Disposition != CommandDisposition.Rejected) _receipts[envelope.CommandId] = result;
        return result;
    }

    private void SetResumeNote(string text, ClockSample now)
    {
        _draft = new(text, now.UtcNow);
        if (_session is { Outcome: SessionOutcome.Active }) _session = _session with { ResumeNote = text };
    }

    private SessionEvent? MapEvent(WorkClockCommand command) => command switch
    {
        StartSession => new StartEvent(Guid.NewGuid(), NextRoundSettings(), Guid.NewGuid()),
        PauseSession => new PauseEvent(), ResumeSession => new ResumeEvent(Guid.NewGuid(), Guid.NewGuid()),
        BreakNow => new BreakNowEvent(Guid.NewGuid()), SetRestMemoEditing memo => new SetRestMemoEditingEvent(memo.Editing),
        BeginBreakMemo => new BeginBreakMemoEvent(), CompleteBreakMemo => new CompleteBreakMemoEvent(),
        DelayBreak d => new DelayEvent(d.Minutes * 60_000L, Guid.NewGuid()),
        EmergencyExit e => new EmergencyEvent(e.SessionId, e.OverlayBatchId), EndPausedSession => new EndPausedEvent(), _ => null
    };

    private void CreateSession(Guid id, SettingsSnapshot roundSettings, ClockSample now)
    {
        _roundPlan = _settings.CyclePlan;
        _roundNumber = _cycleCompletedRounds;
        _roundGeneration = _cycleGeneration;
        _restSegment = -1;
        SetRoundSegments(_cycleSchedule);
        if (_roundSegments is { Count: > 0 }) _prompt = _roundSegments[0].PromptId;
        _slices.Clear(); _pendingCheckIn = null;
        _session = new(id, now.UtcNow, null, now.TimeZoneId, LocalDate(now), null, null,
            roundSettings.WorkDurationMs, roundSettings.RestDurationMs, roundSettings.NoticeDurationMs, false, 0, 0, 0, 0,
            SessionOutcome.Active, null, CheckInStatus.Unrecorded, _draft.Text, now.UtcNow);
    }

    private void SetRoundSegments(CycleSchedule? schedule)
    {
        _roundSchedule = schedule;
        _roundSegments = schedule is null ? _roundPlan?.RestSegments(_roundNumber) : schedule.Rounds[(int)_roundNumber].Rests;
        _roundCount = schedule?.Rounds.Count ?? _roundPlan?.RoundCount ?? 0;
        _roundRepeatStart = schedule?.RepeatStart ?? 0;
    }

    private void ApplyTransition(Transition transition, ClockSample now)
    {
        _state = transition.NextState;
        foreach (var slice in transition.Slices) _slices[slice.SliceId] = slice;
        if (_state.CurrentSlice is { } current) _slices[current.SliceId] = current;
        foreach (var effect in transition.SideEffects.OfType<CloseOverlayEffect>()) InvalidateAndClose(effect.BatchId, OverlayCloseReason.Interrupted);
        if (_session is { Outcome: SessionOutcome.Active })
        {
            _session = _session with { WorkMs = _state.WorkElapsedMs, RestMs = _state.RestElapsedMs,
                DelayUsed = _state.DelayUsed, RequestedDelayMs = _state.RequestedDelayMs, ActualDelayMs = _state.ActualDelayMs, UpdatedAtUtc = now.UtcNow };
            if (transition.Result is { } end)
            {
                // Some terminal states clear their counters; slices are the authoritative persisted totals.
                var endedAt = end.Outcome == SessionOutcome.Completed
                    ? _slices.Values.Select(s => s.EndedAtUtc).DefaultIfEmpty(now.UtcNow).Max()
                    : now.UtcNow;
                _session = _session with { WorkMs = _slices.Values.Where(s => s.Kind == SliceKind.Work).Sum(s => s.DurationMs),
                    RestMs = _slices.Values.Where(s => s.Kind == SliceKind.Rest).Sum(s => s.DurationMs),
                    Outcome = end.Outcome, EndReason = end.EndReason, EndedAtUtc = endedAt,
                    EndTimeZoneId = now.TimeZoneId, EndedLocalDate = LocalDate(now with { UtcNow = endedAt }) };
                _pendingCheckIn = _state.Phase == SessionPhase.AwaitingCheckIn ? end.SessionId : null;
                if (end.Outcome == SessionOutcome.Completed && _roundPlan is not null && _roundGeneration == _cycleGeneration && SamePlan(_roundPlan, _settings.CyclePlan))
                {
                    var lapFinished = _roundNumber + 1 == _roundCount;
                    _cycleCompletedRounds = lapFinished ? _roundRepeatStart : _roundNumber + 1;
                    _cursorDirty = true;
                    if (lapFinished) UpgradeWorkMerging();
                }
            }
        }
        SyncRestSegment();
        if (_state.Phase == SessionPhase.Idle)
        {
            _state = EmptyState();
            _pendingCheckIn = null;
        }
    }

    private async Task<CommandResult> CheckInAsync(Guid id, IReadOnlyList<string> ids, bool startNext, bool historical, Guid commandId, ClockSample now)
    {
        if (ids is null || ids.Any(id => !_activities.Any(a => a.ActivityId == id))) return Reject(ErrorCode.InvalidActivity);
        if (!historical && (_state.Phase != SessionPhase.AwaitingCheckIn || _pendingCheckIn != id)) return Reject(ErrorCode.InvalidState);
        var record = _session?.SessionId == id ? _session : _store is IWorkClockRecordReader reader ? await reader.FindSessionAsync(id) : null;
        if (record is null) return Reject(ErrorCode.SessionNotFound);
        if (record.Outcome == SessionOutcome.Active) return Reject(ErrorCode.SessionStillActive);
        var selected = ids.ToHashSet();
        var activities = _activities.Where(a => selected.Contains(a.ActivityId)).Select(a => new ActivityReport(id, a.ActivityId, now.UtcNow)).ToArray();
        record = record with { CheckInStatus = activities.Length == 0 ? CheckInStatus.Unrecorded : CheckInStatus.Reported, UpdatedAtUtc = now.UtcNow };
        var records = new List<SessionRecord> { record };
        var completedSlices = _slices.Values.ToArray();
        Transition? nextTransition = null;
        if (!historical)
        {
            CloseCheckInOverlay();
            _pendingCheckIn = null; _state = EmptyState();
            if (startNext)
            {
                var next = new StartEvent(Guid.NewGuid(), NextRoundSettings(), Guid.NewGuid());
                nextTransition = _engine.Reduce(_state, next, now);
                CreateSession(next.SessionId, next.Settings, now); ApplyTransition(nextTransition, now); records.Add(_session!);
                // Stage a non-running next round until the atomic check-in/create transaction succeeds.
                // A retry leaves this round paused, as all failed-save recovery does.
                _state = _state with { Phase = SessionPhase.Paused,
                    ResumePhase = next.Settings.WorkDurationMs == 0 ? SessionPhase.Resting : SessionPhase.Working,
                    PauseReason = PauseReason.StorageUnavailable, ClockAnchor = null, OverlayBatchId = null };
            }
            else _session = record;
        }
        else if (_session?.SessionId == id) _session = record;
        var batch = BuildBatch(commandId, now, records: records, activities: [new(id, activities)]);
        if (startNext) batch = batch with { Slices = completedSlices.Concat(batch.Slices).ToArray() };
        var saved = await PersistAsync(batch, now);
        if (saved && nextTransition is not null)
            _state = nextTransition.NextState with { ClockAnchor = _clock.Read() };
        Publish();
        if (saved && nextTransition is not null) RunShowEffects(nextTransition);
        // The operation is retained under its original ID on failure; a duplicate cannot create another round.
        return Result(CommandDisposition.Accepted, saved ? null : ErrorCode.StorageUnavailable);
    }

    private async Task<CommandResult> ClearAsync(ClearHistoryRequest request)
    {
        if (!request.Confirmed) return Reject(ErrorCode.ConfirmationRequired);
        if (request.Range.From > request.Range.To) return Reject(ErrorCode.InvalidArgument);
        if (_pending.Count > 0) return Reject(ErrorCode.StorageUnavailable);
        try
        {
            await _store.ClearHistoryAsync(request);
            var saved = await _store.LoadAsync(); _storageRevision = saved.StorageRevision;
            if (request.IncludeDraft) _draft = saved.WorkspaceDraft;
            if (_session is { Outcome: not SessionOutcome.Active } && _session.EndedLocalDate is DateOnly date && date >= request.Range.From && date <= request.Range.To)
            { CloseCheckInOverlay(); _session = null; _state = EmptyState(); _pendingCheckIn = null; _slices.Clear(); }
            _receipts.Clear(); Publish(); return Result(CommandDisposition.Accepted);
        }
        catch { return Reject(ErrorCode.StorageUnavailable); }
    }

    private async Task<CommandResult> UpdateSettingsAsync(UpdateSettings change, Guid commandId, ClockSample now)
    {
        if (change.WorkMinutes is < 1 or > 180 || change.RestMinutes is < 1 or > 30 ||
            change.Rates is { IsValid: false } || change.CyclePlan is { } plan && !plan.IsValid() ||
            change.Arrangement is { } arrangement && !arrangement.Matches(change.CyclePlan))
            return Reject(ErrorCode.InvalidArgument);

        var unchanged = SamePlan(_settings.CyclePlan, change.CyclePlan);
        var next = _settings with
        {
            CyclePlan = change.CyclePlan?.Copy(),
            Arrangement = change.Arrangement?.Copy() ?? (unchanged ? _settings.Arrangement : null),
            WorkDurationMs = change.WorkMinutes * 60_000L, RestDurationMs = change.RestMinutes * 60_000L,
            ReminderRates = change.Rates ?? _settings.ReminderRates
        };
        var nextRound = unchanged ? _cycleCompletedRounds : 0;
        var generation = unchanged ? _cycleGeneration : _cycleGeneration + 1;
        // A failed settings write may still need record recovery. The settings stay in the editor until saved.
        var recovery = BuildBatch(null, now);
        var operation = Guid.NewGuid();
        var proposed = recovery with
        {
            OperationId = operation,
            Settings = new StoredSettings(next, now.UtcNow, 1) { CycleCompletedRounds = nextRound, CycleGeneration = generation },
            Receipt = recovery.Receipt with { OperationId = operation, CommandId = commandId }
        };
        var saved = await PersistAsync(proposed, now, recovery);
        if (saved)
        {
            _settings = next;
            _cycleCompletedRounds = nextRound;
            _cycleGeneration = generation;
            _cycleSchedule = next.CyclePlan?.Compile();
        }
        Publish();
        return saved ? Result(CommandDisposition.Accepted) : Reject(ErrorCode.StorageUnavailable);
    }

    private async Task<CommandResult> SaveMutationAsync(Guid? commandId, ClockSample now, bool draft = false)
    {
        var saved = await PersistAsync(BuildBatch(commandId, now, draft), now);
        Publish();
        return Result(CommandDisposition.Accepted, saved ? null : ErrorCode.StorageUnavailable);
    }

    private AtomicWriteBatch BuildBatch(Guid? commandId, ClockSample now, bool draft = false,
        IReadOnlyList<SessionRecord>? records = null, IReadOnlyList<ActivityReplacement>? activities = null)
    {
        var operation = Guid.NewGuid();
        var revision = _storageRevision + _pending.Count;
        CheckpointChange checkpoint = _state.Phase == SessionPhase.Idle ? new ClearCheckpoint() : new SetCheckpoint(new(
            _state.SessionId, _state.Phase, _state.ResumePhase, _state.PauseReason, _state.WorkElapsedMs, _state.RestElapsedMs,
            _state.CurrentWorkTargetMs, _state.DelayUsed, _state.RequestedDelayMs, _state.ActualDelayMs, _state.CurrentSliceId,
            _prompt, _pendingCheckIn, now.UtcNow, revision + 1)
            { CyclePlan = _roundPlan?.Copy(), CycleRound = _roundNumber, CycleGeneration = _roundGeneration });
        var storedSettings = _cursorDirty
            ? new StoredSettings(CopySettings(_settings), now.UtcNow, 1) { CycleCompletedRounds = _cycleCompletedRounds, CycleGeneration = _cycleGeneration } : null;
        _cursorDirty = false;
        return new(operation, revision, records ?? (_session is null ? [] : [_session]), _slices.Values.ToArray(), activities ?? [],
            draft ? _draft : null, storedSettings,
            checkpoint, new(operation, commandId, _state.SessionId, revision + 1, _state.Phase.ToString()));
    }

    private async Task<bool> PersistAsync(AtomicWriteBatch batch, ClockSample now, AtomicWriteBatch? recovery = null)
    {
        if (_pending.Count > 0) { _pending.Enqueue(batch); return false; }
        _persistence = new(PersistenceStatus.Saving, _persistence.LastSavedAtUtc, null);
        Publish(false);
        CommitResult result;
        try { result = await _store.CommitAsync(batch); }
        catch
        {
            result = new(CommitStatus.Failed, null, "保存失败");
            // A replay with the original ID can acknowledge a settings transaction that completed before an exception.
            if (recovery is not null)
                try { result = await _store.CommitAsync(batch); } catch { }
        }
        if (result.Status is CommitStatus.Committed or CommitStatus.AlreadyCommitted)
        {
            _storageRevision = result.NewStorageRevision ?? batch.ExpectedStorageRevision + 1;
            _persistence = new(PersistenceStatus.Saved, now.UtcNow, null); _lastCheckpoint = now.UtcNow;
            return true;
        }
        _pending.Enqueue(recovery ?? batch);
        InvalidateAndClose(_state.OverlayBatchId, OverlayCloseReason.Failure);
        var pause = _engine.Reduce(_state, new SystemInterruptedEvent(PauseReason.StorageUnavailable, false), now);
        if (pause.Rejection is null)
        {
            ApplyTransition(pause, now);
            _pending.Enqueue(BuildBatch(null, now));
        }
        _persistence = new(PersistenceStatus.PendingRetry, _persistence.LastSavedAtUtc, "记录尚未保存，计时已暂停；可重试保存或退出。");
        return false;
    }

    public Task<CommandResult> RetryPendingSaveAsync() => Queue(async () =>
    {
        if (!_initialized) return Reject(ErrorCode.StorageUnavailable);
        while (_pending.TryPeek(out var batch))
        {
            CommitResult result;
            try { result = await _store.CommitAsync(batch); } catch { return Reject(ErrorCode.StorageUnavailable); }
            if (result.Status is not (CommitStatus.Committed or CommitStatus.AlreadyCommitted)) return Reject(ErrorCode.StorageUnavailable);
            _storageRevision = result.NewStorageRevision ?? batch.ExpectedStorageRevision + 1;
            _pending.Dequeue();
        }
        _persistence = new(PersistenceStatus.Saved, _clock.Read().UtcNow, null); Publish();
        return Result(CommandDisposition.Accepted);
    }, true);

    private void OnPulse()
    {
        if (_shutdown || Interlocked.Exchange(ref _pulseQueued, 1) != 0) return;
        var now = _clock.Read();
        _ = Queue(async () =>
        {
            try
            {
                if (!_initialized || _shutdown || _pending.Count > 0) return false;
                var oldPhase = _state.Phase;
                var oldSegment = _restSegment;
                var transition = _engine.Reduce(_state, new PulseEvent(Guid.NewGuid()), now);
                if (transition.Rejection is not null) return false;
                // Commit the closing day's final interval at the first pulse that reaches local midnight.
                var crossedMidnight = transition.Slices.Count > 0 && _state.ClockAnchor is { } previous &&
                    LocalDate(previous) != LocalDate(now);
                ApplyTransition(transition, now);
                if (oldPhase != _state.Phase || oldSegment != _restSegment || crossedMidnight || now.UtcNow - _lastCheckpoint >= TimeSpan.FromSeconds(5) && _state.Phase is not (SessionPhase.Idle or SessionPhase.Paused or SessionPhase.AwaitingCheckIn))
                {
                    var saved = await PersistAsync(BuildBatch(null, now), now);
                    if (saved)
                    {
                        RunShowEffects(transition);
                        if (transition.Result is { EndReason: EndReason.WorkTargetReached } && _desktopAvailable)
                            await StartNextWorkBlockAsync();
                    }
                }
                Publish(oldPhase != _state.Phase || oldSegment != _restSegment);
                if (oldPhase != _state.Phase || _state.Phase == SessionPhase.Resting) UpdateOverlay();
                return true;
            }
            finally { Interlocked.Exchange(ref _pulseQueued, 0); }
        });
    }

    private async Task StartNextWorkBlockAsync()
    {
        // The prior block and cursor are already durable. A failure while entering the
        // next block leaves it paused and retriable, or ready to start after a restart.
        var now = _clock.Read();
        var next = new StartEvent(Guid.NewGuid(), NextRoundSettings(), Guid.NewGuid());
        var transition = _engine.Reduce(_state, next, now);
        if (transition.Rejection is not null) return;
        CreateSession(next.SessionId, next.Settings, now);
        ApplyTransition(transition, now);
        if (await PersistAsync(BuildBatch(null, now), now))
        {
            _state = _state with { ClockAnchor = _clock.Read() };
            RunShowEffects(transition);
        }
        Publish();
    }

    private void RunShowEffects(Transition transition)
    {
        foreach (var effect in transition.SideEffects.OfType<ShowOverlayEffect>())
            if (!_invalidBatches.ContainsKey(effect.BatchId) && !_shutdown && _desktopAvailable) _ = PresentAsync(effect);
    }

    private async Task PresentAsync(ShowOverlayEffect effect)
    {
        OverlayResult result;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        _presentations[effect.BatchId] = timeout;
        try
        {
            if (_invalidBatches.ContainsKey(effect.BatchId) || _shutdown) return;
            var targets = _displays.Read();
            var request = new OverlayRequest(effect.BatchId, effect.SessionId, _viewRevision, targets,
                SegmentRemaining(), _prompt, OverlayActions()) { SegmentCaption = SegmentCaption() };
            result = await _overlays.ShowAsync(request, timeout.Token).WaitAsync(timeout.Token);
            var shown = result.ShownDisplays.ToHashSet();
            if (targets.Count == 0 || targets.Any(d => !shown.Contains(d.DisplayId)) || result.FailedDisplays.Count > 0)
                result = result with { AllShown = false };
        }
        catch { result = new(effect.BatchId, [], [], false, ErrorCode.OverlayUnavailable); }
        finally { _presentations.TryRemove(effect.BatchId, out _); }
        await Queue(async () =>
        {
            if (_shutdown || _invalidBatches.ContainsKey(effect.BatchId) || _state.OverlayBatchId != effect.BatchId)
            { InvalidateAndClose(effect.BatchId, OverlayCloseReason.Interrupted); return false; }
            var now = _clock.Read();
            SessionEvent ev = result.AllShown ? new OverlayShownEvent(effect.BatchId, Guid.NewGuid()) : new OverlayFailedEvent(effect.BatchId);
            var transition = _engine.Reduce(_state, ev, now);
            if (transition.Rejection is null)
            {
                ApplyTransition(transition, now);
                await PersistAsync(BuildBatch(null, now), now);
                Publish();
                UpdateOverlay();
            }
            return true;
        }, true);
    }

    public void PostSystemEvent(SystemSignal signal)
    {
        if (_shutdown) return;
        var resumes = signal.Kind is SystemSignalKind.Unlock or SystemSignalKind.Resume or SystemSignalKind.SessionConnected;
        if (!resumes) InvalidateAndClose(null, OverlayCloseReason.Interrupted);
        _ = Queue(async () =>
        {
            if (!_initialized || _shutdown) return false;
            if (resumes) { _desktopAvailable = true; Publish(); return true; }
            if (signal.Kind is SystemSignalKind.Lock or SystemSignalKind.Suspend or SystemSignalKind.SessionDisconnected or SystemSignalKind.SessionSwitch or SystemSignalKind.Logoff) _desktopAvailable = false;
            SessionEvent ev = signal.Kind == SystemSignalKind.DisplaysChanged
                ? new DisplayChangedEvent(Guid.NewGuid())
                : new SystemInterruptedEvent(signal.Kind is SystemSignalKind.ClockChanged or SystemSignalKind.TimeZoneChanged ? PauseReason.ClockChanged : PauseReason.SystemInterrupted, true);
            var transition = _engine.Reduce(_state, ev, signal.Clock);
            if (transition.Rejection is null)
            {
                ApplyTransition(transition, signal.Clock); await PersistAsync(BuildBatch(null, signal.Clock), signal.Clock);
                if (_pending.Count == 0) RunShowEffects(transition);
                Publish();
            }
            return true;
        }, true);
    }

    private void OnOverlayAction(OverlayAction action)
    {
        var state = Volatile.Read(ref _state);
        if (action.Action != OverlayActionKind.EmergencyExit &&
            (action.BatchId != state.OverlayBatchId || action.SessionId != state.SessionId || _invalidBatches.ContainsKey(action.BatchId))) return;
        WorkClockCommand? command = action.Action switch
        {
            OverlayActionKind.EmergencyExit => new EmergencyExit(action.SessionId, action.BatchId),
            OverlayActionKind.SelectPrompt when state.Phase == SessionPhase.Resting => new SelectPrompt(action.PromptId ?? ""),
            OverlayActionKind.BeginMemo when state.Phase == SessionPhase.Resting => new BeginBreakMemo(),
            OverlayActionKind.ConfirmMemo when state.Phase == SessionPhase.EditingRestMemo && action.Memo is { } memo =>
                new CompleteBreakMemo(memo),
            OverlayActionKind.ContinueAfterBreak when state.Phase == SessionPhase.AwaitingCheckIn =>
                new SubmitCheckInAndContinue(action.SessionId, action.ActivityIds ?? []),
            OverlayActionKind.FinishBreakForNow when state.Phase == SessionPhase.AwaitingCheckIn =>
                new SaveCheckInAndStay(action.SessionId, action.ActivityIds ?? []),
            _ => null
        };
        if (command is null) return;
        var envelope = new CommandEnvelope(Guid.NewGuid(), command, action.SessionId, null);
        if (command is EmergencyExit) _ = HandleAsync(envelope);
        else
        {
            var captured = _clock.Read();
            _ = Queue(async () =>
            {
                if (_state.OverlayBatchId != action.BatchId || _invalidBatches.ContainsKey(action.BatchId))
                    return Reject(ErrorCode.StaleCommand);
                var result = await HandleCoreAsync(envelope, captured);
                if (command is BeginBreakMemo or CompleteBreakMemo) UpdateOverlay();
                if (result.Disposition == CommandDisposition.Rejected && _state.Phase == SessionPhase.AwaitingCheckIn &&
                    _state.OverlayBatchId == action.BatchId)
                {
                    // The fullscreen controls accepted this click. Return to the main window if
                    // the command could not proceed and the completed round still awaits input.
                    CloseCheckInOverlay();
                    _state = _state with { OverlayBatchId = null };
                    Publish();
                }
                return result;
            });
        }
    }

    private void InvalidateAndClose(Guid? batch, OverlayCloseReason reason)
    {
        if (batch is Guid id)
        {
            if (!_invalidBatches.TryAdd(id, 0)) return;
        }
        else if (_state.OverlayBatchId is Guid current) _invalidBatches.TryAdd(current, 0);
        if (batch is Guid specified)
        { if (_presentations.TryGetValue(specified, out var pending)) { try { pending.Cancel(); } catch (ObjectDisposedException) { } } }
        else foreach (var pending in _presentations.Values) { try { pending.Cancel(); } catch (ObjectDisposedException) { } }
        try { _overlays.CloseAll(batch, reason); } catch { /* Exit remains possible even when a window has already disappeared. */ }
    }
    private IReadOnlySet<OverlayActionKind> OverlayActions() => _state.Phase switch
    {
        SessionPhase.Resting => RestActions,
        SessionPhase.EditingRestMemo => MemoActions,
        _ => PreparingRestActions
    };
    private static readonly IReadOnlySet<OverlayActionKind> RestActions =
        new[] { OverlayActionKind.EmergencyExit, OverlayActionKind.SelectPrompt, OverlayActionKind.BeginMemo }.ToFrozenSet();
    private static readonly IReadOnlySet<OverlayActionKind> MemoActions =
        new[] { OverlayActionKind.EmergencyExit, OverlayActionKind.ConfirmMemo }.ToFrozenSet();
    private static readonly IReadOnlySet<OverlayActionKind> PreparingRestActions =
        new[] { OverlayActionKind.EmergencyExit }.ToFrozenSet();

    private static readonly IReadOnlySet<OverlayActionKind> CheckInActions =
        new[] { OverlayActionKind.EmergencyExit, OverlayActionKind.ContinueAfterBreak, OverlayActionKind.FinishBreakForNow }.ToFrozenSet();

    private void CloseCheckInOverlay()
    {
        if (_state.Phase == SessionPhase.AwaitingCheckIn && _state.OverlayBatchId is Guid batch)
            InvalidateAndClose(batch, OverlayCloseReason.Completed);
    }

    private void UpdateOverlay()
    {
        if (_state.OverlayBatchId is not Guid batch || _invalidBatches.ContainsKey(batch)) return;
        if (_state.Phase is SessionPhase.Resting or SessionPhase.EditingRestMemo)
            _overlays.Update(new(batch, SegmentRemaining(), _prompt, OverlayActions()) { SegmentCaption = SegmentCaption(), Memo = _draft.Text });
        else if (_state.Phase == SessionPhase.AwaitingCheckIn && _persistence.Status == PersistenceStatus.Saved)
            _overlays.Update(new(batch, 0, null, CheckInActions)
            {
                CheckInActivities = _activities,
                CheckInLocalDate = _session?.EndedLocalDate
            });
    }

    public AppSnapshot ReadSnapshot() => Volatile.Read(ref _snapshot);
    private AppSnapshot BuildSnapshot()
    {
        var projection = _engine.Project(_state, _clock.Read());
        projection = projection with { AllowedActions = projection.AllowedActions.ToFrozenSet() };
        var allowed = new HashSet<CommandType>();
        if (_initialized && !_shutdown)
        {
            allowed.UnionWith([CommandType.SaveResumeNote, CommandType.UpdateSettings, CommandType.UpdateHistoricalCheckIn, CommandType.ClearHistory]);
            foreach (var action in projection.AllowedActions)
                allowed.Add(action switch { SessionAction.Start => CommandType.StartSession, SessionAction.Pause => CommandType.PauseSession,
                    SessionAction.Resume => CommandType.ResumeSession, SessionAction.BreakNow => CommandType.BreakNow, SessionAction.Delay => CommandType.DelayBreak,
                    SessionAction.Emergency => CommandType.EmergencyExit, _ => CommandType.EndPausedSession });
            if (_state.Phase == SessionPhase.Resting) allowed.Add(CommandType.SelectPrompt);
            if (_state.Phase == SessionPhase.AwaitingCheckIn) allowed.UnionWith([CommandType.SubmitCheckInAndContinue, CommandType.SaveCheckInAndStay, CommandType.DismissCheckIn]);
            if (_pending.Count > 0) allowed.IntersectWith([CommandType.PauseSession, CommandType.EmergencyExit, CommandType.EndPausedSession, CommandType.DismissCheckIn]);
            if (!_desktopAvailable) allowed.ExceptWith([CommandType.StartSession, CommandType.ResumeSession]);
        }
        var nextRound = _cycleSchedule?.Rounds[(int)_cycleCompletedRounds];
        return new(_viewRevision, projection, _state.SessionId, _pendingCheckIn, _draft.Text, _settings, _activities,
            _persistence, _persistence.FailureReason, allowed.ToFrozenSet())
        {
            NextWorkDurationMs = nextRound?.WorkMs ?? (_settings.CyclePlan is { } nextPlan ? nextPlan.WorkMinutes * 60_000L : _settings.WorkDurationMs),
            NextRestDurationMs = nextRound is { WorkMs: 0 }
                ? (_settings.CyclePlan!.MergeAdjacentRests ? nextRound.Rests.Sum(rest => rest.DurationMs) : nextRound.Rests[0].DurationMs) : 0,
            PlanProgress = BuildPlanProgress(),
            HasFullscreenCheckIn = _state.Phase == SessionPhase.AwaitingCheckIn &&
                _state.OverlayBatchId is Guid batch && !_invalidBatches.ContainsKey(batch)
        };
    }
    private void Publish(bool logicalChange = true)
    {
        if (logicalChange) _viewRevision++;
        var snapshot = BuildSnapshot(); Volatile.Write(ref _snapshot, snapshot);
        foreach (var observer in _observers.Values) { try { observer(snapshot); } catch { /* One UI observer cannot stop the coordinator. */ } }
    }
    public IDisposable SubscribeSnapshots(Action<AppSnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(observer); var id = Interlocked.Increment(ref _observerId);
        _observers[id] = observer; observer(ReadSnapshot()); return new Subscription(() => _observers.TryRemove(id, out _));
    }
    private sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    private CommandResult Reject(ErrorCode error) => Result(CommandDisposition.Rejected, error);
    private CommandResult Result(CommandDisposition disposition, ErrorCode? error = null) => new(disposition, error, ReadSnapshot(), _persistence, error?.ToString());
    private static SettingsSnapshot CopySettings(SettingsSnapshot source) => source with
    {
        DelayOptionsMs = Array.AsReadOnly(source.DelayOptionsMs.ToArray()),
        CyclePlan = source.CyclePlan?.Copy(), Arrangement = source.Arrangement?.Copy()
    };
    private static bool SamePlan(CyclePlan? a, CyclePlan? b) => a is null ? b is null : a.SameAs(b);

    private void UpgradeWorkMerging()
    {
        if (_settings.CyclePlan is not { Blocks: not null, MergeAdjacentRests: true, MergeAdjacentWork: null } legacy) return;
        // The old lap may have already played its opening rest at the seam. A newly merged
        // opening work block must still play at the start of the first lap under the new rules.
        var openingRestAlreadyPlayed = _cycleCompletedRounds != 0;
        var plan = legacy with { MergeAdjacentWork = true };
        var schedule = plan.Compile()!;
        _settings = _settings with { CyclePlan = plan };
        _cycleSchedule = schedule;
        _cycleCompletedRounds = openingRestAlreadyPlayed ? schedule.RepeatStart : 0;
        _cycleGeneration++;
        _cursorDirty = true;
    }

    private SettingsSnapshot NextRoundSettings()
    {
        var settings = _settings with { Arrangement = null };
        if (_cycleSchedule is { } schedule)
        {
            var round = schedule.Rounds[(int)_cycleCompletedRounds];
            return settings with { WorkDurationMs = round.WorkMs, RestDurationMs = round.Rests.Sum(rest => rest.DurationMs) };
        }
        var plan = settings.CyclePlan;
        return plan is null ? CopySettings(settings) : CopySettings(settings) with
        {
            WorkDurationMs = plan.WorkMinutes * 60_000L,
            RestDurationMs = plan.RestSegments(_cycleCompletedRounds).Sum(x => x.DurationMs)
        };
    }
    private int SegmentIndex()
    {
        if (_roundSegments is not { Count: > 0 } segments) return 0;
        long end = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            end += segments[i].DurationMs;
            if (_state.RestElapsedMs < end) return i;
        }
        return segments.Count - 1;
    }
    private void SyncRestSegment()
    {
        if (_roundSegments is not { Count: > 0 }) return;
        var index = SegmentIndex();
        if (_restSegment != index)
        {
            _restSegment = index;
            _prompt = _roundSegments[index].PromptId;
        }
    }
    private long SegmentRemaining()
    {
        if (_roundPlan is null || _roundPlan.MergeAdjacentRests)
            return Math.Max(0, (_state.Settings?.RestDurationMs ?? 0) - _state.RestElapsedMs);
        var segments = _roundSegments!;
        return Math.Max(0, segments.Take(SegmentIndex() + 1).Sum(x => x.DurationMs) - _state.RestElapsedMs);
    }
    private string? SegmentCaption()
    {
        var segments = _roundSegments;
        if (segments is null || segments.Count < 2) return null;
        var index = SegmentIndex();
        var caption = _roundPlan!.MergeAdjacentRests ? $"合并休息 · 动作 {index + 1}/{segments.Count}" : $"本轮休息 {index + 1}/{segments.Count}";
        if (index + 1 < segments.Count)
        {
            var next = segments[index + 1];
            var name = next.PromptId switch { "walk" => "起身走动", "look_away" => "远眺", _ => "放松肩颈和双手" };
            var duration = next.DurationMs % 60000 == 0 ? $"{next.DurationMs / 60000} 分钟" : $"{next.DurationMs / 1000} 秒";
            caption += $" · 接下来{name} {duration}";
        }
        return caption;
    }
    private readonly record struct PlanProgressKey(CyclePlan? Plan, CycleSchedule? Schedule, long Round, long WorkMs,
        long RestMs, bool Current, bool InRest, bool Finished, int RestIndex);

    private PlanProgress BuildPlanProgress()
    {
        var hasRound = _state.Phase != SessionPhase.Idle;
        var finished = _state.Phase == SessionPhase.AwaitingCheckIn;
        var phase = _state.Phase == SessionPhase.Paused ? _state.ResumePhase : _state.Phase;
        var inRest = phase is SessionPhase.Resting or SessionPhase.BreakPending or SessionPhase.EditingRestMemo;
        var plan = hasRound ? _roundPlan : _settings.CyclePlan;
        var schedule = hasRound ? _roundSchedule : _cycleSchedule;
        var round = hasRound ? _roundNumber : _cycleCompletedRounds;
        var workMs = plan is not null ? plan.WorkMinutes * 60_000L : hasRound ? _session?.PlannedWorkMs ?? _settings.WorkDurationMs : _settings.WorkDurationMs;
        var restMs = plan is null && hasRound ? _session?.PlannedRestMs ?? _settings.RestDurationMs : _settings.RestDurationMs;
        var restIndex = inRest ? Math.Max(0, _restSegment) : 0;
        var key = new PlanProgressKey(plan, schedule, round, workMs, restMs, hasRound && !finished, inRest, finished, restIndex);
        if (lastProgress is not null && lastProgressKey == key) return lastProgress;

        var first = schedule is not null && round >= schedule.RepeatStart ? schedule.RepeatStart : 0L;
        var count = schedule?.Rounds.Count ?? plan?.RoundCount ?? 1;
        // Very large legacy plans keep their original execution order and show the portion near the cursor.
        const int legacyWindowSize = 64;
        var start = schedule is null && count > legacyWindowSize ? Math.Clamp(round - 16, first, count - legacyWindowSize) : first;
        var end = schedule is null ? Math.Min(count, start + legacyWindowSize) : count;
        var modules = new List<PlannedModule>();
        var completed = 0;
        for (var index = start; index < end; index++)
        {
            var before = modules.Count;
            var entry = schedule?.Rounds[(int)index];
            var work = entry?.WorkMs ?? workMs;
            if (work > 0) modules.Add(new PlannedModule(SliceKind.Work, work));
            if ((entry?.Rests ?? plan?.RestSegments(index)) is { } rests)
            {
                if (plan?.MergeAdjacentRests == true && rests.Count > 0)
                    modules.Add(new PlannedModule(SliceKind.Rest, rests.Sum(rest => rest.DurationMs)));
                else
                    modules.AddRange(rests.Select(rest => new PlannedModule(SliceKind.Rest, rest.DurationMs)));
            }
            else if (restMs > 0) modules.Add(new PlannedModule(SliceKind.Rest, restMs));

            if (index < round || index == round && finished) completed = modules.Count;
            else if (index == round && inRest)
            {
                var passed = (work > 0 ? 1 : 0) + (plan?.MergeAdjacentRests == true ? 0 : restIndex);
                completed = Math.Min(before + passed, modules.Count);
            }
        }
        lastProgressKey = key;
        return lastProgress = new PlanProgress(modules.AsReadOnly(), completed, key.Current && completed < modules.Count,
            start > first, end < count);
    }
    private static SettingsSnapshot SessionSettings(SessionRecord session) => new(session.PlannedWorkMs, session.PlannedRestMs,
        session.NoticeMs, Array.AsReadOnly(new long[] { 180_000, 300_000 }), 1);
    private static DateOnly LocalDate(ClockSample now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(now.TimeZoneId)).DateTime);

    private Task<T> Queue<T>(Func<Task<T>> action, bool urgent = false)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        (urgent ? _urgent : _normal).Enqueue(async () =>
        {
            try { completion.TrySetResult(await action()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        _available.Release(); return completion.Task;
    }
    private async Task PumpAsync()
    {
        while (true)
        {
            await _available.WaitAsync();
            if (_urgent.TryDequeue(out var action) || _normal.TryDequeue(out action)) await action();
        }
    }

    public async Task<ShutdownReport> ShutdownAsync(TimeSpan deadline)
    {
        InvalidateAndClose(null, OverlayCloseReason.Shutdown);
        _pulseSubscription?.Dispose(); _systemSubscription?.Dispose(); _overlaySubscription?.Dispose();
        var save = Queue(async () =>
        {
            if (_initialized)
            {
                var now = _clock.Read(); var transition = _engine.Reduce(_state, new SystemInterruptedEvent(PauseReason.SystemInterrupted, true), now);
                if (transition.Rejection is null) { ApplyTransition(transition, now); await PersistAsync(BuildBatch(null, now), now); }
            }
            _shutdown = true; Publish(); return _pending.Count == 0;
        }, true);
        try
        {
            var saved = await save.WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp(deadline.TotalMilliseconds, 1, 1000)));
            return new(true, saved, saved ? null : "部分记录尚未保存。");
        }
        catch (TimeoutException) { _shutdown = true; return new(true, false, "保存超时，窗口已关闭。"); }
    }
}
