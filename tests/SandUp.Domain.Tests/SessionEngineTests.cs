using SandUp.Domain;

namespace SandUp.Domain.Tests;

public class SessionEngineTests
{
    private sealed class Run
    {
        public readonly SessionEngine Engine = new();
        public SessionState State = SessionEngine.InitialState;
        public ClockSample Clock = new(Guid.NewGuid(), 0, 1000, new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero), "UTC");
        public Dictionary<Guid, TimeSlice> Slices = [];
        public List<SessionResultChange> Results = [];
        public Transition Send(SessionEvent e)
        {
            var t = Engine.Reduce(State, e, Clock);
            State = t.NextState;
            foreach (var slice in t.Slices) Slices[slice.SliceId] = slice;
            if (t.Result is { } result) Results.Add(result);
            Assert.Empty(Engine.Validate(State));
            return t;
        }
        public void Start() => Send(new StartEvent(Guid.NewGuid(), new(1500000, 300000, 30000, new long[] { 180000, 300000 }, 1), Guid.NewGuid()));
        public void Move(long ms) => Clock = Clock with { MonotonicTicks = Clock.MonotonicTicks + ms, UtcNow = Clock.UtcNow.AddMilliseconds(ms) };
        public void Advance(long ms)
        {
            while (ms > 0) { var step = Math.Min(ms, 250); Move(step); Send(new PulseEvent(null)); ms -= step; }
        }
        public void Show() => Send(new OverlayShownEvent(State.OverlayBatchId!.Value, Guid.NewGuid()));
    }

    [Fact] public void T01_NormalRoundSettlesOnceAndWaitsForOverlay()
    {
        var r = new Run(); r.Start(); r.Advance(1470000);
        Assert.Equal(SessionPhase.PreBreak, r.State.Phase);
        r.Advance(30000); Assert.Equal(SessionPhase.BreakPending, r.State.Phase);
        r.Advance(1500); Assert.Equal(0, r.State.RestElapsedMs);
        r.Show(); r.Advance(300000);
        Assert.Equal(SessionPhase.AwaitingCheckIn, r.State.Phase);
        Assert.Equal(1500000, r.State.WorkElapsedMs); Assert.Equal(300000, r.State.RestElapsedMs);
        Assert.Equal(SessionOutcome.Completed, Assert.Single(r.Results).Outcome);
        r.Send(new EmergencyEvent(r.State.SessionId!.Value, null));
        Assert.Single(r.Results);
        Assert.Equal(1800000, r.Slices.Values.Sum(x => x.DurationMs));
    }
    [Fact] public void T02_PreNoticeDelayExtendsOriginalDeadline()
    {
        var r = new Run(); r.Start(); r.Advance(1470000); r.Send(new DelayEvent(180000, null));
        Assert.Equal(210000, r.Engine.Project(r.State, r.Clock).RemainingMs);
        r.Advance(210000);
        Assert.Equal(SessionPhase.BreakPending, r.State.Phase);
        Assert.Equal(1680000, r.State.WorkElapsedMs); Assert.Equal(180000, r.State.ActualDelayMs);
        Assert.Equal(180000, r.Slices.Values.Where(x => x.WorkSubtype == WorkSubtype.Extension).Sum(x => x.DurationMs));
    }
    [Fact] public void T03_StoppingExtensionEarlyCountsActualOnly()
    {
        var r = new Run(); r.Start(); r.Advance(1470000); r.Send(new DelayEvent(300000, null)); r.Advance(210000);
        r.Send(new BreakNowEvent(Guid.NewGuid()));
        Assert.Equal(1680000, r.State.WorkElapsedMs); Assert.Equal(180000, r.State.ActualDelayMs);
        Assert.Equal(300000, r.State.RequestedDelayMs);
    }
    [Theory] [InlineData(180000)] [InlineData(300000)]
    public void T04_RestRejectsDelayAndContinuesRemainingRest(long delayMs)
    {
        var r = new Run(); r.Start(); r.Advance(1500000); r.Show(); r.Advance(120000);
        var delay = r.Send(new DelayEvent(delayMs, Guid.NewGuid()));
        Assert.Equal(ErrorCode.InvalidState, delay.Rejection);
        Assert.Empty(delay.SideEffects.OfType<CloseOverlayEffect>());
        Assert.DoesNotContain(SessionAction.Delay, r.Engine.Project(r.State, r.Clock).AllowedActions);
        Assert.False(r.State.DelayUsed);
        Assert.Equal(180000, r.Engine.Project(r.State, r.Clock).RemainingMs);
        r.Advance(180000);
        Assert.Equal(1500000, r.State.WorkElapsedMs); Assert.Equal(300000, r.State.RestElapsedMs);
        Assert.Single(r.Results);
    }
    [Fact] public void T05_T30_DelayRemainsUsedAfterPauseAndRestore()
    {
        var r = new Run(); r.Start(); r.Advance(1470000); r.Send(new DelayEvent(180000, null));
        r.Send(new PauseEvent()); r.Move(3600000); r.Send(new ResumeEvent(null, Guid.NewGuid()));
        r.Advance(40000); r.Send(new BreakNowEvent(Guid.NewGuid())); r.Show();
        Assert.Equal(ErrorCode.DelayAlreadyUsed, r.Send(new DelayEvent(180000, null)).Rejection);
        Assert.Equal(1510000, r.State.WorkElapsedMs); Assert.Equal(10000, r.State.ActualDelayMs);
    }
    [Fact] public void T06_EarlyRestDoesNotFillPlannedWork()
    {
        var r = new Run(); r.Start(); r.Advance(600000); r.Send(new BreakNowEvent(Guid.NewGuid())); r.Show(); r.Advance(300000);
        Assert.Equal(600000, r.State.WorkElapsedMs); Assert.Equal(SessionOutcome.Completed, Assert.Single(r.Results).Outcome);
    }
    [Fact] public void EarlyRestAlsoRejectsDelay()
    {
        var r = new Run(); r.Start(); r.Advance(600000); r.Send(new BreakNowEvent(Guid.NewGuid())); r.Show();
        Assert.Equal(ErrorCode.InvalidState,r.Send(new DelayEvent(180000, Guid.NewGuid())).Rejection);
        Assert.Equal(SessionPhase.Resting,r.State.Phase);
        Assert.Equal(0, r.State.ActualDelayMs);
        Assert.Equal(600000, r.State.WorkElapsedMs);
    }
    [Fact] public void T07_T10_PauseAndLockDoNotAccumulateAbsence()
    {
        var r = new Run(); r.Start(); r.Advance(600000); r.Send(new SystemInterruptedEvent(PauseReason.SystemInterrupted, true));
        r.Move(3600000); Assert.Equal(600000, r.Engine.Project(r.State, r.Clock).WorkElapsedMs);
        r.Send(new ResumeEvent(null, Guid.NewGuid())); r.Advance(300000);
        Assert.Equal(900000, r.State.WorkElapsedMs);
    }
    [Fact] public void T08_T09_EmergencyClosesAndDoesNotDuplicate()
    {
        var r = new Run(); r.Start(); r.Advance(1500000); var batch = r.State.OverlayBatchId!.Value; r.Show(); r.Advance(1000);
        var emergency = new EmergencyEvent(r.State.SessionId!.Value, batch);
        var t = r.Send(emergency);
        Assert.Equal(SessionPhase.Idle, r.State.Phase); Assert.Equal(SessionOutcome.EndedEarly, t.Result!.Outcome);
        Assert.Single(t.SideEffects.OfType<CloseOverlayEffect>());
        r.Send(emergency); r.Advance(1000); Assert.Single(r.Results);
    }
    [Fact] public void T11_T13_InterruptedRestRequiresNewBatchAndRetainsRemainder()
    {
        var r = new Run(); r.Start(); r.Advance(1500000); r.Show(); r.Advance(120000);
        r.Send(new SystemInterruptedEvent(PauseReason.SystemInterrupted, true)); r.Move(3600000);
        r.Send(new ResumeEvent(Guid.NewGuid(), null)); Assert.Equal(SessionPhase.BreakPending, r.State.Phase);
        r.Show(); Assert.Equal(180000, r.Engine.Project(r.State, r.Clock).RemainingMs);
        var old = r.State.OverlayBatchId; r.Send(new DisplayChangedEvent(Guid.NewGuid()));
        Assert.Equal(SessionPhase.BreakPending, r.State.Phase); Assert.NotEqual(old, r.State.OverlayBatchId);
    }
    [Fact] public void T12_OverlayFailurePausesWithoutCountingRest()
    {
        var r = new Run(); r.Start(); r.Advance(1500000);
        r.Send(new OverlayFailedEvent(r.State.OverlayBatchId!.Value));
        Assert.Equal(SessionPhase.Paused, r.State.Phase); Assert.Equal(PauseReason.OverlayUnavailable, r.State.PauseReason);
        Assert.Equal(0, r.State.RestElapsedMs);
    }
    [Theory] [InlineData(3600000)] [InlineData(-3600000)]
    public void T19_WallClockJumpFreezesLastTrustedTime(int jump)
    {
        var r = new Run(); r.Start(); r.Advance(1000); r.Clock = r.Clock with { UtcNow = r.Clock.UtcNow.AddMilliseconds(jump) };
        r.Send(new PulseEvent(null)); Assert.Equal(SessionPhase.Paused, r.State.Phase);
        Assert.Equal(PauseReason.ClockChanged, r.State.PauseReason); Assert.Equal(1000, r.State.WorkElapsedMs);
    }
    [Fact] public void T20_UnobservedGapDoesNotCountTenMinutes()
    {
        var r = new Run(); r.Start(); r.Advance(1000); r.Move(600000); r.Send(new PulseEvent(null));
        Assert.Equal(SessionPhase.Paused, r.State.Phase); Assert.Equal(1000, r.State.WorkElapsedMs);
    }
    [Fact] public void T21_MidnightSplitsStableSlicesWithoutDoubleCounting()
    {
        var r = new Run(); r.Clock = r.Clock with { UtcNow = new DateTimeOffset(2026, 11, 12, 23, 50, 0, TimeSpan.Zero) }; r.Start();
        r.Advance(1200000);
        Assert.Equal(2, r.Slices.Count);
        Assert.All(r.Slices.Values, s => Assert.Equal(600000, s.DurationMs));
        Assert.Equal(1200000, r.Slices.Values.Sum(s => s.DurationMs));
    }
    [Fact] public void T27_StaleFeedbackCannotResurrectAnOverlay()
    {
        var r = new Run(); r.Start(); r.Advance(1500000); var batch = r.State.OverlayBatchId!.Value;
        r.Send(new EmergencyEvent(r.State.SessionId!.Value, batch));
        var t = r.Send(new OverlayShownEvent(batch, Guid.NewGuid()));
        Assert.Equal(SessionPhase.Idle, r.State.Phase); Assert.Equal(ErrorCode.StaleCommand, t.Rejection);
        Assert.Single(t.SideEffects.OfType<CloseOverlayEffect>()); Assert.Single(r.Results);
    }
    [Fact] public void T13_T27_OldOverlayFeedbackCannotAffectReplacementBatch()
    {
        var r = new Run(); r.Start(); r.Advance(1500000); r.Show(); r.Advance(1000);
        var old = r.State.OverlayBatchId!.Value;
        var replacement = Guid.NewGuid();
        r.Send(new DisplayChangedEvent(replacement));
        var stale = r.Send(new OverlayShownEvent(old, Guid.NewGuid()));
        Assert.Equal(ErrorCode.StaleCommand, stale.Rejection);
        Assert.Contains(stale.SideEffects, e => e is CloseOverlayEffect close && close.BatchId == old);
        Assert.Equal(replacement, r.State.OverlayBatchId);
        Assert.Equal(SessionPhase.BreakPending, r.State.Phase);
        Assert.Equal(ErrorCode.StaleCommand, r.Send(new OverlayFailedEvent(old)).Rejection);
        r.Show(); r.Advance(1000);
        Assert.Equal(2000, r.State.RestElapsedMs);
    }
    [Fact] public void T10_UntrustedInterruptionFreezesAtLastTrustedSample()
    {
        var r = new Run(); r.Start(); r.Advance(1000); r.Move(1000);
        r.Send(new SystemInterruptedEvent(PauseReason.SystemInterrupted, false));
        Assert.Equal(SessionPhase.Paused, r.State.Phase);
        Assert.Equal(1000, r.State.WorkElapsedMs);
        r.Move(3600000); r.Send(new ResumeEvent(null, Guid.NewGuid())); r.Advance(1000);
        Assert.Equal(2000, r.State.WorkElapsedMs);
    }
    [Fact] public void T27_ResumeAtSameSampleDoesNotReuseGeneratedBatch()
    {
        var r = new Run(); r.Start(); r.Advance(1500000);
        var old = r.State.OverlayBatchId!.Value;
        r.Send(new SystemInterruptedEvent(PauseReason.SystemInterrupted, true));
        r.Send(new ResumeEvent(null, null));
        Assert.NotEqual(old, r.State.OverlayBatchId);
        Assert.Equal(ErrorCode.StaleCommand, r.Send(new OverlayShownEvent(old, Guid.NewGuid())).Rejection);
        Assert.Equal(SessionPhase.BreakPending, r.State.Phase);
    }
    [Fact] public void FractionalTicksCarryAcrossPulsesAndReducerIsDeterministic()
    {
        var r = new Run(); r.Clock = r.Clock with { Frequency = 3000 }; r.Start();
        var initial = r.State;
        for (int i = 0; i < 3000; i++)
        {
            r.Clock = r.Clock with { MonotonicTicks = i + 1, UtcNow = initial.ClockAnchor!.UtcNow.AddTicks((i + 1) * TimeSpan.TicksPerSecond / 3000) };
            r.Send(new PulseEvent(null));
        }
        Assert.Equal(1000, r.State.WorkElapsedMs); Assert.Equal(0, r.State.SubMillisecondTicks);
        Assert.Equal(initial.ClockAnchor!.UtcNow, Assert.Single(r.Slices.Values).StartedAtUtc);
        var a = r.Engine.Reduce(initial, new PulseEvent(null), r.Clock);
        var b = r.Engine.Reduce(initial, new PulseEvent(null), r.Clock);
        Assert.Equal(a.NextState, b.NextState); Assert.Equal(a.Slices, b.Slices);
    }
}
