using System.Security.Cryptography;
using System.Text;

namespace SandUp.Domain;

/// <summary>Pure session reducer. IDs for split slices are derived from session and sequence.</summary>
public sealed class SessionEngine : ISessionEngine
{
    public static SessionState InitialState => new(null, SessionPhase.Idle, null, null, null, 0, 0, 0, false, 0, 0, null, null, null, null, 0, 0);
    private static bool IsWork(SessionPhase p) => p is SessionPhase.Working or SessionPhase.PreBreak or SessionPhase.Delaying;
    private static bool IsMemoWork(SessionPhase p) => p is SessionPhase.AwaitingRestMemo or SessionPhase.EditingRestMemo;
    private static bool IsRunning(SessionPhase p) => IsWork(p) || IsMemoWork(p) || p == SessionPhase.Resting;
    private static Guid Derived(Guid session, string token) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{session:N}:{token}")).AsSpan(0, 16));

    public Transition Reduce(SessionState state, SessionEvent @event, ClockSample clock)
    {
        var s = state;
        var slices = new List<TimeSlice>();
        var effects = new List<SessionEffect>();
        SessionResultChange? result = null;
        Transition Done(ErrorCode? error = null) => new(s, slices.ToArray(), result, effects.ToArray(), error);
        void Close()
        {
            if (s.OverlayBatchId is Guid id) effects.Add(new CloseOverlayEffect(id));
            s = s with { OverlayBatchId = null };
        }
        void Seal()
        {
            if (s.CurrentSlice is { } slice)
            {
                slices.RemoveAll(x => x.SliceId == slice.SliceId);
                if (slice.DurationMs > 0) slices.Add(slice with { Finalized = true });
            }
            s = s with { CurrentSlice = null, CurrentSliceId = null, SubMillisecondTicks = 0 };
        }
        void Pause(PauseReason reason)
        {
            var resume = s.Phase == SessionPhase.BreakPending || IsMemoWork(s.Phase) ? SessionPhase.Resting : s.Phase;
            Seal(); Close();
            s = s with { Phase = SessionPhase.Paused, ResumePhase = resume, PauseReason = reason, ClockAnchor = null, RestMemoEditing = false };
        }
        void Pending(Guid? batch)
        {
            Seal(); Close();
            var id = batch ?? Derived(s.SessionId!.Value, $"overlay:{clock.MonotonicTicks}:{s.NextSliceSequence}");
            // Reserve a sequence number for every batch, even if no time slice was
            // created. A pause and resume can occur at the very same clock sample.
            s = s with { Phase = SessionPhase.BreakPending, OverlayBatchId = id, ClockAnchor = clock, ResumePhase = null, PauseReason = null, RestMemoEditing = false, NextSliceSequence = checked(s.NextSliceSequence + 1) };
            effects.Add(new ShowOverlayEffect(s.SessionId!.Value, id));
        }
        void Finish(SessionOutcome outcome, EndReason reason, SessionPhase phase)
        {
            Seal();
            if (phase != SessionPhase.AwaitingCheckIn) Close();
            result = new(s.SessionId!.Value, outcome, reason);
            s = s with { Phase = phase, ResumePhase = null, PauseReason = null, ClockAnchor = null, RestMemoEditing = false };
        }

        // Feedback from a dead batch must never settle or alter a newer session.
        if (@event is OverlayShownEvent shownOld && (s.Phase != SessionPhase.BreakPending || shownOld.OverlayBatchId != s.OverlayBatchId))
        {
            if (shownOld.OverlayBatchId != s.OverlayBatchId) effects.Add(new CloseOverlayEffect(shownOld.OverlayBatchId));
            return Done(ErrorCode.StaleCommand);
        }
        if (@event is OverlayFailedEvent failedOld && (failedOld.OverlayBatchId != s.OverlayBatchId || s.Phase is not (SessionPhase.BreakPending or SessionPhase.Resting)))
            return Done(ErrorCode.StaleCommand);
        if (@event is EmergencyEvent emergencyOld &&
            (emergencyOld.SessionId != s.SessionId || (emergencyOld.OverlayBatchId.HasValue && emergencyOld.OverlayBatchId != s.OverlayBatchId)))
        {
            if (emergencyOld.OverlayBatchId is Guid old) effects.Add(new CloseOverlayEffect(old));
            return Done(ErrorCode.StaleCommand);
        }
        if (clock.Frequency <= 0) return Done(ErrorCode.InvalidArgument);
        if (@event is SetRestMemoEditingEvent memo && s.Phase != SessionPhase.AwaitingRestMemo)
        {
            // The editor is transient. A pulse still owns the deadline and settles the work slice.
            if (IsWork(s.Phase) && s.Settings!.RestDurationMs > 0)
                s = s with { RestMemoEditing = memo.Editing };
            return Done();
        }
        var trusted = @event is not SystemInterruptedEvent { TimestampTrusted: false };
        long elapsedPastWorkTarget = 0;
        long memoFraction = 0;
        DateTimeOffset memoStartedAt = clock.UtcNow;
        if (IsRunning(s.Phase) && s.ClockAnchor is { } anchor)
        {
            var discontinuity = ClockContinuity.Check(anchor, clock);
            if (!trusted || discontinuity.HasValue)
            {
                Pause(discontinuity ?? ((SystemInterruptedEvent)@event).Reason);
                if (@event is not EmergencyEvent) return Done();
            }
            else
            {
                var scaled = ((Int128)clock.MonotonicTicks - anchor.MonotonicTicks) * 1000 + s.SubMillisecondTicks;
                var delta = (long)(scaled / clock.Frequency);
                var remainder = (long)(scaled % clock.Frequency);
                var capacity = s.Phase switch
                {
                    SessionPhase.AwaitingRestMemo or SessionPhase.EditingRestMemo => long.MaxValue - s.WorkElapsedMs,
                    SessionPhase.Resting => s.Settings!.RestDurationMs - s.RestElapsedMs,
                    _ => s.CurrentWorkTargetMs - s.WorkElapsedMs
                };
                var counted = Math.Clamp(delta, 0, Math.Max(0, capacity));
                var fractionTicks = (long)((Int128)s.SubMillisecondTicks * TimeSpan.TicksPerMillisecond / clock.Frequency);
                var start = anchor.UtcNow.AddTicks(-fractionTicks);
                if (s.RestMemoEditing && IsWork(s.Phase))
                {
                    elapsedPastWorkTarget = delta - counted;
                    memoStartedAt = start.AddMilliseconds(counted);
                    memoFraction = remainder;
                }
                s = Append(s, counted, start, slices);
                s = s with { ClockAnchor = clock, SubMillisecondTicks = counted == capacity ? 0 : remainder };
            }
        }
        // Emergency and interruption take priority over an unprocessed deadline.
        if (@event is not (EmergencyEvent or SystemInterruptedEvent or EndPausedEvent) && IsRunning(s.Phase))
        {
            if (IsWork(s.Phase) && s.WorkElapsedMs >= s.CurrentWorkTargetMs)
            {
                if (s.Settings!.RestDurationMs == 0)
                    Finish(SessionOutcome.Completed, EndReason.WorkTargetReached, SessionPhase.Idle);
                else if (s.RestMemoEditing)
                {
                    Seal();
                    s = s with { Phase = SessionPhase.AwaitingRestMemo, RestMemoEditing = false, SubMillisecondTicks = memoFraction };
                    // Split a delayed pulse at the planned cutoff to keep the tail as memo work.
                    s = Append(s, elapsedPastWorkTarget, memoStartedAt, slices);
                    if (@event is BreakNowEvent startRest) Pending(startRest.OverlayBatchId);
                }
                else
                    Pending(@event is PulseEvent pulse ? pulse.OverlayBatchId : @event is BreakNowEvent now ? now.OverlayBatchId : null);
                return Done();
            }
            if (s.Phase == SessionPhase.Resting && s.RestElapsedMs >= s.Settings!.RestDurationMs)
            {
                Finish(SessionOutcome.Completed, EndReason.RestTargetReached, SessionPhase.AwaitingCheckIn);
                return Done();
            }
            if (s.Phase == SessionPhase.Working && s.Settings!.RestDurationMs > 0 && s.CurrentWorkTargetMs - s.WorkElapsedMs <= s.Settings.NoticeDurationMs)
                s = s with { Phase = SessionPhase.PreBreak };
        }
        switch (@event)
        {
            case StartEvent start:
                if (s.Phase != SessionPhase.Idle) return Done(ErrorCode.InvalidState);
                if (start.SessionId == Guid.Empty || !ValidSettings(start.Settings)) return Done(ErrorCode.InvalidArgument);
                s = InitialState with { SessionId = start.SessionId, Phase = SessionPhase.Working, Settings = start.Settings with { DelayOptionsMs = Array.AsReadOnly(start.Settings.DelayOptionsMs.ToArray()), CyclePlan = start.Settings.CyclePlan?.Copy() }, CurrentWorkTargetMs = start.Settings.WorkDurationMs, ClockAnchor = clock, CurrentSliceId = start.SliceId };
                if (start.Settings.WorkDurationMs == 0) Pending(null);
                break;
            case PulseEvent:
                break;
            case PauseEvent:
                if (!IsWork(s.Phase)) return Done(ErrorCode.InvalidState);
                Pause(PauseReason.User);
                break;
            case ResumeEvent resume:
                if (s.Phase != SessionPhase.Paused || s.ResumePhase is null) return Done(ErrorCode.InvalidState);
                if (s.ResumePhase == SessionPhase.Resting) Pending(resume.OverlayBatchId);
                else s = s with { Phase = s.ResumePhase.Value, ResumePhase = null, PauseReason = null, ClockAnchor = clock, CurrentSliceId = resume.SliceId, SubMillisecondTicks = 0 };
                break;
            case BreakNowEvent now:
                if (!(IsWork(s.Phase) || s.Phase == SessionPhase.AwaitingRestMemo) || s.Settings!.RestDurationMs == 0)
                    return Done(ErrorCode.InvalidState);
                Pending(now.OverlayBatchId);
                break;
            case SetRestMemoEditingEvent memoWait:
                if (!memoWait.Editing) Pending(null);
                break;
            case BeginBreakMemoEvent:
                if (s.Phase != SessionPhase.Resting) return Done(ErrorCode.InvalidState);
                Seal();
                s = s with { Phase = SessionPhase.EditingRestMemo, ClockAnchor = clock };
                break;
            case CompleteBreakMemoEvent:
                if (s.Phase != SessionPhase.EditingRestMemo) return Done(ErrorCode.InvalidState);
                Seal();
                s = s with { Phase = SessionPhase.Resting, ClockAnchor = clock };
                break;
            case DelayEvent delay:
                if (s.DelayUsed) return Done(ErrorCode.DelayAlreadyUsed);
                if (s.Phase != SessionPhase.PreBreak) return Done(ErrorCode.InvalidState);
                if (!s.Settings!.DelayOptionsMs.Contains(delay.DelayMs)) return Done(ErrorCode.InvalidArgument);
                var target = s.CurrentWorkTargetMs + delay.DelayMs;
                s = s with { Phase = SessionPhase.Delaying, DelayUsed = true, RequestedDelayMs = delay.DelayMs, CurrentWorkTargetMs = target, ClockAnchor = clock, CurrentSliceId = s.CurrentSliceId ?? delay.SliceId, RestMemoEditing = false };
                break;
            case OverlayShownEvent shown:
                s = s with { Phase = SessionPhase.Resting, ClockAnchor = clock, CurrentSliceId = shown.SliceId, SubMillisecondTicks = 0 };
                break;
            case OverlayFailedEvent:
                Pause(PauseReason.OverlayUnavailable);
                break;
            case DisplayChangedEvent display:
                if (s.Phase is SessionPhase.Resting or SessionPhase.EditingRestMemo or SessionPhase.BreakPending) Pending(display.OverlayBatchId);
                else if (s.Phase == SessionPhase.AwaitingCheckIn) Close();
                break;
            case SystemInterruptedEvent interrupted:
                if (IsRunning(s.Phase) || s.Phase == SessionPhase.BreakPending) Pause(interrupted.Reason);
                else if (s.Phase == SessionPhase.AwaitingCheckIn) Close();
                break;
            case EmergencyEvent:
                if (s.Phase == SessionPhase.AwaitingCheckIn)
                {
                    Close();
                    s = InitialState;
                    break;
                }
                if (s.Phase == SessionPhase.Idle) break;
                Finish(s.RestElapsedMs > 0 ? SessionOutcome.EndedEarly : SessionOutcome.Skipped, EndReason.Emergency, SessionPhase.Idle);
                break;
            case EndPausedEvent:
                if (s.Phase != SessionPhase.Paused) return Done(ErrorCode.InvalidState);
                Finish(SessionOutcome.Interrupted, EndReason.UserEndedPaused, SessionPhase.Idle);
                break;
            default:
                return Done(ErrorCode.InvalidArgument);
        }
        return Done();
    }

    private static bool ValidSettings(SettingsSnapshot settings)
    {
        var plan = settings.CyclePlan;
        var validDurations = plan?.Blocks is not null
            ? plan.IsValid() && settings.WorkDurationMs is >= 0 and <= CyclePlan.MaxSteps * 10_800_000L
                && settings.WorkDurationMs % 1_000 == 0 && settings.RestDurationMs is >= 0 and <= CyclePlan.MaxSteps * 1_800_000L
                && settings.RestDurationMs % 1_000 == 0 && (settings.WorkDurationMs > 0 || settings.RestDurationMs > 0)
            : settings.WorkDurationMs is >= 60_000 and <= 10_800_000 && settings.WorkDurationMs % 60_000 == 0 &&
                (plan is null
                    ? settings.RestDurationMs is >= 60_000 and <= 1_800_000 && settings.RestDurationMs % 60_000 == 0
                    : plan.IsValid() && settings.WorkDurationMs == plan.WorkMinutes * 60_000L &&
                        settings.RestDurationMs is >= 1_000 and <= 9_000_000 && settings.RestDurationMs % 1_000 == 0);
        return validDurations && settings.NoticeDurationMs is 20_000 or 30_000
            && settings.MaxDelayCount == 1 && settings.DelayOptionsMs.Order().SequenceEqual(new long[] { 180_000, 300_000 });
    }

    private static SessionState Append(SessionState s, long delta, DateTimeOffset start, List<TimeSlice> changes)
    {
        if (delta <= 0) return s;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(s.ClockAnchor!.TimeZoneId);
        var kind = s.Phase == SessionPhase.Resting ? SliceKind.Rest : SliceKind.Work;
        var extensionStart = s.DelayUsed ? s.CurrentWorkTargetMs - s.RequestedDelayMs : long.MaxValue;
        while (delta > 0)
        {
            WorkSubtype? subtype = kind == SliceKind.Rest ? null : IsMemoWork(s.Phase) ? WorkSubtype.Memo :
                s.WorkElapsedMs >= extensionStart ? WorkSubtype.Extension : WorkSubtype.Base;
            var local = TimeZoneInfo.ConvertTime(start, zone);
            var date = DateOnly.FromDateTime(local.DateTime);
            var midnightLocal = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
            // A few zones skip midnight at a DST boundary. Find the next valid civil instant.
            while (zone.IsInvalidTime(midnightLocal)) midnightLocal = midnightLocal.AddMinutes(1);
            var midnightUtc = TimeZoneInfo.ConvertTimeToUtc(midnightLocal, zone);
            var untilMidnight = Math.Max(1L, (long)(midnightUtc - start.UtcDateTime).TotalMilliseconds);
            var take = Math.Min(delta, untilMidnight);
            if (subtype == WorkSubtype.Base && s.DelayUsed) take = Math.Min(take, extensionStart - s.WorkElapsedMs);
            if (s.CurrentSlice is { } old && (old.LocalDate != date || old.Kind != kind || old.WorkSubtype != subtype))
            {
                changes.RemoveAll(x => x.SliceId == old.SliceId);
                changes.Add(old with { Finalized = true });
                s = s with { CurrentSlice = null, CurrentSliceId = null };
            }
            var slice = s.CurrentSlice;
            if (slice is null)
            {
                var id = s.CurrentSliceId ?? Derived(s.SessionId!.Value, $"slice:{s.NextSliceSequence}");
                slice = new(id, s.SessionId!.Value, s.NextSliceSequence, kind, subtype, date, zone.Id, start, start, 0, false);
                s = s with { NextSliceSequence = s.NextSliceSequence + 1 };
            }
            slice = slice with { DurationMs = slice.DurationMs + take, EndedAtUtc = slice.EndedAtUtc.AddMilliseconds(take) };
            changes.RemoveAll(x => x.SliceId == slice.SliceId);
            changes.Add(slice);
            s = s with { CurrentSlice = slice, CurrentSliceId = slice.SliceId, WorkElapsedMs = s.WorkElapsedMs + (kind == SliceKind.Work ? take : 0), RestElapsedMs = s.RestElapsedMs + (kind == SliceKind.Rest ? take : 0), ActualDelayMs = s.ActualDelayMs + (subtype == WorkSubtype.Extension ? take : 0) };
            delta -= take;
            start = start.AddMilliseconds(take);
        }
        return s;
    }

    public SessionProjection Project(SessionState state, ClockSample clock)
    {
        var work = state.WorkElapsedMs;
        var rest = state.RestElapsedMs;
        if (IsRunning(state.Phase) && state.ClockAnchor is { } anchor && ClockContinuity.Check(anchor, clock) is null)
        {
            var delta = (long)((((Int128)clock.MonotonicTicks - anchor.MonotonicTicks) * 1000 + state.SubMillisecondTicks) / clock.Frequency);
            if (IsMemoWork(state.Phase)) work += delta;
            else if (IsWork(state.Phase)) work = Math.Min(state.CurrentWorkTargetMs, work + delta);
            else rest = Math.Min(state.Settings!.RestDurationMs, rest + delta);
        }
        var actions = new HashSet<SessionAction>();
        if (state.Phase == SessionPhase.Idle) actions.Add(SessionAction.Start);
        if (IsWork(state.Phase))
        {
            actions.Add(SessionAction.Pause);
            if (state.Settings!.RestDurationMs > 0) actions.Add(SessionAction.BreakNow);
        }
        if (state.Phase == SessionPhase.Paused) { actions.Add(SessionAction.Resume); actions.Add(SessionAction.EndPaused); }
        if (state.Phase == SessionPhase.AwaitingRestMemo) actions.Add(SessionAction.BreakNow);
        if (state.Phase is SessionPhase.PreBreak or SessionPhase.Delaying or SessionPhase.AwaitingRestMemo or SessionPhase.EditingRestMemo or SessionPhase.BreakPending or SessionPhase.Resting) actions.Add(SessionAction.Emergency);
        if (!state.DelayUsed && state.Phase == SessionPhase.PreBreak) actions.Add(SessionAction.Delay);
        var phase = state.Phase == SessionPhase.Paused ? state.ResumePhase : state.Phase;
        var remaining = phase is SessionPhase.Resting or SessionPhase.BreakPending or SessionPhase.EditingRestMemo ? (state.Settings?.RestDurationMs ?? 0) - rest : phase is SessionPhase.Working or SessionPhase.PreBreak or SessionPhase.Delaying ? state.CurrentWorkTargetMs - work : 0;
        return new(state.Phase, Math.Max(0, remaining), work, rest, actions) { RestFollows = state.Settings?.RestDurationMs != 0 };
    }

    public IReadOnlyList<string> Validate(SessionState state)
    {
        var errors = new List<string>();
        if (state.WorkElapsedMs < 0 || state.RestElapsedMs < 0 || state.ActualDelayMs < 0) errors.Add("Negative duration");
        if (state.Phase != SessionPhase.Idle && (state.SessionId is null || state.Settings is null)) errors.Add("Active state lacks session/settings");
        if (state.Phase == SessionPhase.Paused && state.ResumePhase is null) errors.Add("Paused state lacks resume phase");
        if (state.Phase is SessionPhase.Resting or SessionPhase.EditingRestMemo or SessionPhase.BreakPending && state.OverlayBatchId is null) errors.Add("Overlay phase lacks batch");
        if (state.Settings is { } settings && state.RestElapsedMs > settings.RestDurationMs) errors.Add("Rest exceeds target");
        if (!state.DelayUsed && (state.RequestedDelayMs != 0 || state.ActualDelayMs != 0)) errors.Add("Delay fields disagree");
        return errors;
    }
}
