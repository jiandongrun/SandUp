using SandUp.Domain;

namespace SandUp.Application.Tests;

public class CoordinatorTests
{
    [Fact] public async Task T01_T24_T33_CompletedSlicesAndAtomicCheckInArePreserved()
    {
        var f = await Fixture.Create();
        await f.Complete();
        var finished = Assert.Single(f.Store.Sessions.Values);
        Assert.Equal(SessionOutcome.Completed, finished.Outcome);
        Assert.Equal(60_000, finished.WorkMs);
        Assert.Equal(60_000, finished.RestMs);
        Assert.Equal(120_000, f.Store.Slices.Values.Sum(x => x.DurationMs));
        Assert.Equal(CheckInStatus.Unrecorded, finished.CheckInStatus);
        Assert.True(f.App.ReadSnapshot().HasFullscreenCheckIn);
        var completion = Assert.IsType<OverlayViewModel>(f.Overlay.LastUpdate);
        Assert.Equal(f.Overlay.LastRequest!.BatchId, completion.BatchId);
        Assert.Equal(0, completion.RemainingRestMs);
        Assert.NotNull(completion.CheckInActivities);
        Assert.Equal(finished.EndedLocalDate, completion.CheckInLocalDate);
        Assert.Contains(OverlayActionKind.ContinueAfterBreak, completion.AllowedActions);
        Assert.DoesNotContain(completion.BatchId, f.Overlay.Closed);
        await f.Advance(2_000);
        Assert.Equal(60_000, f.App.ReadSnapshot().Session.RestElapsedMs);
        var forward = new OverlayAction(completion.BatchId, finished.SessionId, OverlayActionKind.ContinueAfterBreak)
        { ActivityIds = ["walk", "walk"] };
        f.Overlay.Fire(forward);
        f.Overlay.Fire(forward);
        await f.Barrier();
        var batch = f.Store.Batches.Last();
        Assert.Equal(2, batch.Sessions.Count);
        Assert.Single(Assert.Single(batch.ActivityReplacements).Activities);
        Assert.Equal(120_000, batch.Slices.Where(x => x.SessionId == finished.SessionId).Sum(x => x.DurationMs));
        Assert.Equal(2, f.Store.Sessions.Count);
        Assert.Equal(SessionPhase.Working, f.App.ReadSnapshot().Session.Phase);
        Assert.False(f.App.ReadSnapshot().HasFullscreenCheckIn);
        Assert.Single(f.Overlay.Closed, id => id == completion.BatchId);
        Assert.Equal(CheckInStatus.Reported, f.Store.Sessions[finished.SessionId].CheckInStatus);
    }

    [Theory]
    [InlineData(OverlayActionKind.EmergencyExit, CheckInStatus.Unrecorded)]
    [InlineData(OverlayActionKind.FinishBreakForNow, CheckInStatus.Reported)]
    public async Task FullscreenExitKeepsCompletedRoundAndDoesNotStartWork(OverlayActionKind action, CheckInStatus expected)
    {
        var f = await Fixture.Create();
        await f.Complete();
        var request = f.Overlay.LastRequest!;
        f.Overlay.Fire(new(request.BatchId, request.SessionId, action) { ActivityIds = ["walk"] });
        if (action == OverlayActionKind.EmergencyExit) Assert.Contains(request.BatchId, f.Overlay.Closed);
        await f.Barrier();
        Assert.Contains(request.BatchId, f.Overlay.Closed);
        Assert.Equal(SessionPhase.Idle, f.App.ReadSnapshot().Session.Phase);
        Assert.Null(f.App.ReadSnapshot().PendingCheckInSessionId);
        var ended = Assert.Single(f.Store.Sessions.Values);
        Assert.Equal(SessionOutcome.Completed, ended.Outcome);
        Assert.Equal(expected, ended.CheckInStatus);
        Assert.Equal(60_000, ended.RestMs);
    }

    [Fact]
    public async Task LockClosesFullscreenAndRejectsLateContinue()
    {
        var f = await Fixture.Create();
        await f.Complete();
        var request = f.Overlay.LastRequest!;
        f.App.PostSystemEvent(new(SystemSignalKind.Lock, f.Clock.Read(), null));
        f.Overlay.Fire(new(request.BatchId, request.SessionId, OverlayActionKind.ContinueAfterBreak));
        await f.Barrier();
        Assert.Contains(null, f.Overlay.Closed);
        Assert.False(f.App.ReadSnapshot().HasFullscreenCheckIn);
        f.App.PostSystemEvent(new(SystemSignalKind.Unlock, f.Clock.Read(), null));
        await f.Barrier();
        Assert.Equal(SessionPhase.AwaitingCheckIn, f.App.ReadSnapshot().Session.Phase);
        Assert.False(f.App.ReadSnapshot().HasFullscreenCheckIn);
        Assert.Equal(1, f.Overlay.ShowCount);
        Assert.Single(f.Store.Sessions);
    }

    [Fact] public async Task T16_T17_CommitAcknowledgementLossRetriesSameOperationWithoutDoubleCounting()
    {
        var f = await Fixture.Create(); await f.Send(new StartSession()); await f.Advance(10_000);
        f.Store.LoseNextAcknowledgement = true;
        var command = new CommandEnvelope(Guid.NewGuid(), new PauseSession(), f.App.ReadSnapshot().CurrentSessionId, null);
        var failed = await f.App.HandleAsync(command);
        Assert.Equal(PersistenceStatus.PendingRetry, failed.Persistence.Status);
        Assert.Equal(SessionPhase.Paused, failed.Snapshot.Session.Phase);
        Assert.Equal(PersistenceStatus.Saved, (await f.App.RetryPendingSaveAsync()).Persistence.Status);
        await f.App.HandleAsync(command);
        Assert.Single(f.Store.Sessions);
        Assert.Equal(10_000, f.Store.Slices.Values.Sum(x => x.DurationMs));
        Assert.Contains(f.Store.Batches.GroupBy(x => x.OperationId), g => g.Count() > 1);
    }

    [Fact] public async Task T08_T16_T27_EmergencyClosesImmediatelyDuringBlockedWriteAndCancelsPendingPresentation()
    {
        var f = await Fixture.Create(); await f.Send(new StartSession());
        f.Overlay.HoldPresentation = true;
        await f.Send(new BreakNow());
        await f.Overlay.PresentationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var batch = f.Overlay.LastRequest!.BatchId;
        f.Store.BlockNext = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Store.WriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = f.Send(new SaveResumeNote("draft"));
        await f.Store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var emergency = f.Send(new EmergencyExit(f.App.ReadSnapshot().CurrentSessionId!.Value, batch));
        Assert.Contains(batch, f.Overlay.Closed);
        Assert.True(f.Overlay.LastCancellation.IsCancellationRequested);
        Assert.False(writing.IsCompleted);
        f.Store.BlockNext.SetResult();
        await writing; await emergency;
        Assert.Equal(SessionPhase.Idle, f.App.ReadSnapshot().Session.Phase);
        Assert.Equal(SessionOutcome.Skipped, Assert.Single(f.Store.Sessions.Values).Outcome);
        await f.Barrier();
        Assert.Equal(SessionPhase.Idle, f.App.ReadSnapshot().Session.Phase);
    }

    [Fact] public async Task T24_SubmitDoesNotStartClockWhileSavingOrAfterFailedSaveRetry()
    {
        var f = await Fixture.Create(); await f.Complete();
        var id = f.App.ReadSnapshot().CurrentSessionId!.Value;
        f.Store.BlockNext = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Store.WriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Store.FailNext = true;
        var command = new CommandEnvelope(Guid.NewGuid(), new SubmitCheckInAndContinue(id, []), id, null);
        var submit = f.App.HandleAsync(command);
        await f.Store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotEqual(SessionPhase.Working, f.App.ReadSnapshot().Session.Phase);
        f.Clock.Advance(20_000);
        f.Store.BlockNext.SetResult();
        Assert.Equal(ErrorCode.StorageUnavailable, (await submit).Error);
        Assert.Equal(SessionPhase.Paused, f.App.ReadSnapshot().Session.Phase);
        await f.App.RetryPendingSaveAsync();
        await f.App.HandleAsync(command);
        Assert.Equal(2, f.Store.Sessions.Count);
        Assert.Equal(SessionPhase.Paused, f.App.ReadSnapshot().Session.Phase);
        Assert.Equal(0, f.App.ReadSnapshot().Session.WorkElapsedMs);
        Assert.Equal(120_000, f.Store.Slices.Values.Sum(x => x.DurationMs));
    }

    [Fact] public async Task T15_DismissAndLaterMutationsCannotOverwriteCompletedTotals()
    {
        var f = await Fixture.Create(); await f.Complete();
        var id = f.App.ReadSnapshot().CurrentSessionId!.Value;
        await f.Send(new DismissCheckIn(id));
        await f.Advance(6_000);
        await f.Send(new UpdateSettings(40, 8));
        Assert.Equal(60_000, f.Store.Sessions[id].WorkMs);
        Assert.Equal(60_000, f.Store.Sessions[id].RestMs);
        Assert.Equal(SessionOutcome.Completed, f.Store.Sessions[id].Outcome);
    }

    [Fact] public async Task T14_T23_RecoveryIsPausedAndRetainsRoundSettingsAndDraft()
    {
        var f = await Fixture.Create(); await f.Send(new SaveResumeNote("next item"));
        await f.Send(new StartSession()); await f.Advance(10_000);
        await f.Send(new UpdateSettings(40, 8));
        var recovered = await Fixture.Create(f.Store);
        Assert.Equal(SessionPhase.Paused, recovered.App.ReadSnapshot().Session.Phase);
        Assert.Equal(10_000, recovered.App.ReadSnapshot().Session.WorkElapsedMs);
        Assert.Equal(50_000, recovered.App.ReadSnapshot().Session.RemainingMs);
        Assert.Equal(2_400_000, recovered.App.ReadSnapshot().DefaultSettings.WorkDurationMs);
        Assert.Equal("next item", recovered.App.ReadSnapshot().WorkspaceDraft);
        Assert.Equal(0, recovered.Overlay.ShowCount);
        await recovered.Advance(50_000);
        Assert.Equal(10_000, recovered.App.ReadSnapshot().Session.WorkElapsedMs);
    }

    [Fact] public async Task ReminderRatesPersistAndDurationOnlyChangesPreserveThem()
    {
        var f = await Fixture.Create();
        var rates = ReminderRates.Default with { Eyes = 17.5, Pelvic = 0 };
        Assert.Null((await f.Send(new UpdateSettings(25, 5, rates))).Error);
        Assert.Equal(ErrorCode.InvalidArgument, (await f.Send(new UpdateSettings(30, 3, rates with { Back = 41 }))).Error);
        Assert.Equal(25 * 60_000, f.App.ReadSnapshot().DefaultSettings.WorkDurationMs);
        await f.Send(new UpdateSettings(30, 6));
        var recovered = await Fixture.Create(f.Store);
        Assert.Equal(rates, recovered.App.ReadSnapshot().DefaultSettings.ReminderRates);
        Assert.Equal(30 * 60_000, recovered.App.ReadSnapshot().DefaultSettings.WorkDurationMs);
    }

    [Fact] public async Task FailedSettingsStayUnappliedAndAreNotReplayedWithRecordRecovery()
    {
        var f = await Fixture.Create();
        await f.Send(new StartSession());
        await f.Advance(2_000);
        f.Store.FailNext = true;
        f.Store.BlockNext = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = f.Send(new UpdateSettings(30, 5));
        await f.Store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(60_000, f.App.ReadSnapshot().DefaultSettings.WorkDurationMs);
        f.Store.BlockNext.SetResult();
        var failed = await pending;
        Assert.Equal(PersistenceStatus.PendingRetry, failed.Persistence.Status);
        Assert.Equal(ErrorCode.StorageUnavailable, failed.Error);
        Assert.Equal(60_000, failed.Snapshot.DefaultSettings.WorkDurationMs);
        Assert.Equal(SessionPhase.Paused, (await f.App.RetryPendingSaveAsync()).Snapshot.Session.Phase);
        Assert.Equal(60_000, (await f.Store.LoadAsync()).Settings.Settings.WorkDurationMs);

        f.Store.LoseNextAcknowledgement = true;
        var saved = await f.Send(new UpdateSettings(20, 5));
        Assert.Null(saved.Error);
        Assert.Equal(20 * 60_000, saved.Snapshot.DefaultSettings.WorkDurationMs);
        Assert.Equal(20 * 60_000, (await f.Store.LoadAsync()).Settings.Settings.WorkDurationMs);
    }

    [Fact] public async Task T15_RestartCompletedSessionShowsOnlyPendingCheckIn()
    {
        var f = await Fixture.Create(); await f.Complete();
        var recovered = await Fixture.Create(f.Store);
        Assert.Equal(SessionPhase.AwaitingCheckIn, recovered.App.ReadSnapshot().Session.Phase);
        Assert.Equal(0, recovered.Overlay.ShowCount);
        Assert.Equal(CheckInStatus.Unrecorded, Assert.Single(f.Store.Sessions.Values).CheckInStatus);
    }

    [Fact] public async Task T17_OldPauseAndEmergencyCannotAffectNewSession()
    {
        var f = await Fixture.Create(); await f.Send(new StartSession());
        var old = f.App.ReadSnapshot().CurrentSessionId;
        await f.Send(new PauseSession()); await f.Send(new EndPausedSession()); await f.Send(new StartSession());
        Assert.Equal(ErrorCode.StaleCommand, (await f.App.HandleAsync(new(Guid.NewGuid(), new PauseSession(), old, null))).Error);
        Assert.Equal(ErrorCode.StaleCommand, (await f.Send(new EmergencyExit(old!.Value, null))).Error);
        Assert.Equal(SessionPhase.Working, f.App.ReadSnapshot().Session.Phase);
    }

    [Fact] public async Task T05_RecoveryDoesNotRestorePreBreakDelayAllowance()
    {
        var f = await Fixture.Create(); await f.Send(new UpdateSettings(1,1)); await f.Send(new StartSession()); await f.Advance(40_000);
        Assert.Contains(CommandType.DelayBreak,f.App.ReadSnapshot().AllowedCommands);
        await f.Send(new DelayBreak(3)); await f.Advance(10_000);
        var recovered = await Fixture.Create(f.Store);
        Assert.Equal(0, recovered.App.ReadSnapshot().Session.RestElapsedMs);
        await recovered.Send(new ResumeSession());
        Assert.Equal(ErrorCode.DelayAlreadyUsed, (await recovered.Send(new DelayBreak(3))).Error);
        Assert.True(Assert.Single(f.Store.Sessions.Values).DelayUsed);
    }

    [Fact] public async Task T04_RestHasNoDelayActionsAndRejectsDelayCommands()
    {
        var f=await Fixture.Create();await f.Send(new UpdateSettings(1,1));await f.Send(new StartSession());
        await f.Advance(60_000);await f.Overlay.PresentationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for(var i=0;i<50&&f.App.ReadSnapshot().Session.Phase!=SessionPhase.Resting;i++){await Task.Delay(1);await f.Barrier();}
        Assert.Equal(SessionPhase.Resting,f.App.ReadSnapshot().Session.Phase);
        Assert.DoesNotContain(CommandType.DelayBreak,f.App.ReadSnapshot().AllowedCommands);
        Assert.DoesNotContain(OverlayActionKind.DelayThreeMinutes,f.Overlay.LastRequest!.AllowedActions);
        Assert.DoesNotContain(OverlayActionKind.DelayFiveMinutes,f.Overlay.LastRequest!.AllowedActions);
        Assert.Equal(ErrorCode.InvalidState,(await f.Send(new DelayBreak(3))).Error);
        Assert.Equal(ErrorCode.InvalidState,(await f.Send(new DelayBreak(5))).Error);
        Assert.Equal(SessionPhase.Resting,f.App.ReadSnapshot().Session.Phase);
    }

    [Fact] public async Task T16_CompletedWriteFailureRetainsUniqueResultAndNeverReopensOverlay()
    {
        var f = await Fixture.Create(); await f.Send(new StartSession()); await f.Advance(10_000);
        await f.Send(new BreakNow()); await f.Barrier(); await f.Advance(59_000);
        f.Store.FailNext = true;
        await f.Advance(1_000);
        Assert.Equal(SessionPhase.AwaitingCheckIn, f.App.ReadSnapshot().Session.Phase);
        Assert.Equal(PersistenceStatus.PendingRetry, f.App.ReadSnapshot().Persistence.Status);
        Assert.Single(f.Overlay.Closed, x => x == f.Overlay.LastRequest!.BatchId);
        await f.App.RetryPendingSaveAsync();
        Assert.Equal(SessionOutcome.Completed, Assert.Single(f.Store.Sessions.Values).Outcome);
        Assert.Equal(70_000, f.Store.Slices.Values.Sum(x => x.DurationMs));
        Assert.Equal(1, f.Overlay.ShowCount);
    }

    [Fact] public async Task T29_PresentationWaitIsNeitherWorkNorRest()
    {
        var f = await Fixture.Create(); await f.Send(new StartSession()); await f.Advance(10_000);
        f.Overlay.PresentationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Send(new BreakNow());
        await f.Overlay.PresentationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await f.Advance(1_500);
        Assert.Equal(SessionPhase.BreakPending, f.App.ReadSnapshot().Session.Phase);
        Assert.Equal(10_000, f.App.ReadSnapshot().Session.WorkElapsedMs);
        Assert.Equal(0, f.App.ReadSnapshot().Session.RestElapsedMs);
        f.Overlay.PresentationGate.SetResult();
        for (var i = 0; i < 50 && f.App.ReadSnapshot().Session.Phase != SessionPhase.Resting; i++) { await Task.Delay(1); await f.Barrier(); }
        await f.Advance(1_000);
        Assert.Equal(1_000, f.App.ReadSnapshot().Session.RestElapsedMs);
    }

    [Fact] public async Task T22_ShutdownClosesWindowsBeforeBlockedStorageAndReturnsAtDeadline()
    {
        var f = await Fixture.Create(); await f.Send(new StartSession()); await f.Send(new BreakNow()); await f.Barrier();
        f.Store.BlockNext = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Store.WriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = f.Send(new SaveResumeNote("draft"));
        await f.Store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var shutdown = await f.App.ShutdownAsync(TimeSpan.FromMilliseconds(20)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(shutdown.WindowsClosed); Assert.False(shutdown.Saved);
        Assert.Contains(null, f.Overlay.Closed);
        f.Store.BlockNext.SetResult(); await writing;
    }

    [Fact] public async Task T21_LateCompletionPulseUsesActualDeadlineForEndDate()
    {
        var f = await Fixture.Create();
        f.Clock.UtcOrigin = new DateTimeOffset(2026, 11, 12, 23, 57, 59, TimeSpan.Zero).AddMilliseconds(500);
        await f.Send(new StartSession()); await f.Advance(60_000); await f.Barrier();
        await f.Advance(59_000);
        f.Clock.Advance(2_000); f.Pulse.Fire(); await f.Barrier();
        var record = Assert.Single(f.Store.Sessions.Values);
        Assert.Equal(new DateOnly(2026, 11, 12), record.EndedLocalDate);
        Assert.Equal(f.Clock.UtcOrigin.AddMinutes(2), record.EndedAtUtc);
        Assert.Equal(120_000, f.Store.Slices.Values.Sum(s => s.DurationMs));
    }

    [Fact] public async Task TwentySecondNoticeKeepsOriginalDeadlineAndTenSecondDelayAddsToIt()
    {
        var f=await Fixture.Create();await f.Send(new UpdateSettings(1,1));await f.Send(new StartSession());
        await f.Advance(39000);Assert.Equal(SessionPhase.Working,f.App.ReadSnapshot().Session.Phase);
        await f.Advance(1000);Assert.Equal(SessionPhase.PreBreak,f.App.ReadSnapshot().Session.Phase);Assert.Equal(20000,f.App.ReadSnapshot().Session.RemainingMs);
        await f.Advance(10000);Assert.Equal(10000,f.App.ReadSnapshot().Session.RemainingMs);
        await f.Send(new DelayBreak(3));Assert.Equal(190000,f.App.ReadSnapshot().Session.RemainingMs);
        Assert.Equal(SessionPhase.Delaying,f.App.ReadSnapshot().Session.Phase);
        Assert.Equal(ErrorCode.DelayAlreadyUsed,(await f.Send(new DelayBreak(5))).Error);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineWaitsOnlyForAnOpenMemoEditor(bool editing)
    {
        var f = await Fixture.Create();
        await f.Send(new UpdateSettings(1, 1));
        await f.Send(new StartSession());
        await f.Advance(50_000);
        if (editing) await f.Send(new SetRestMemoEditing(true));
        await f.Advance(10_000);
        if (editing)
        {
            Assert.Equal(SessionPhase.AwaitingRestMemo, f.App.ReadSnapshot().Session.Phase);
            await f.Advance(15_000);
            var waiting = f.App.ReadSnapshot().Session;
            Assert.Equal(SessionPhase.AwaitingRestMemo, waiting.Phase);
            Assert.Equal(0, waiting.RemainingMs);
            Assert.Equal(75_000, waiting.WorkElapsedMs);
            Assert.Equal(0, waiting.RestElapsedMs);
            Assert.Equal(0, f.Overlay.ShowCount);
            Assert.Equal(ErrorCode.TextTooLong, (await f.Send(new BreakNow(new string('字', 201)))).Error);
            Assert.Equal(SessionPhase.AwaitingRestMemo, f.App.ReadSnapshot().Session.Phase);
            await f.Send(new BreakNow("回来改第二段"));
            Assert.Equal("回来改第二段", f.App.ReadSnapshot().WorkspaceDraft);
            Assert.Equal("回来改第二段", (await f.Store.LoadAsync()).CurrentSession!.ResumeNote);
        }
        await f.Overlay.PresentationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (var i = 0; i < 50 && f.App.ReadSnapshot().Session.Phase != SessionPhase.Resting; i++) { await Task.Delay(1); await f.Barrier(); }
        Assert.Equal(SessionPhase.Resting, f.App.ReadSnapshot().Session.Phase);
        Assert.Equal(editing ? 75_000 : 60_000, f.App.ReadSnapshot().Session.WorkElapsedMs);
        Assert.Equal(0, f.App.ReadSnapshot().Session.RestElapsedMs);
        Assert.Equal(1, f.Overlay.ShowCount);
    }

    [Fact]
    public async Task FullscreenMemoAddsWorkWithoutAdvancingRestAndResumesTheSameBatch()
    {
        var f = await Fixture.Create();
        await f.Send(new UpdateSettings(1, 1));
        await f.Send(new StartSession());
        await f.Advance(60_000);
        await f.Overlay.PresentationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (var i = 0; i < 50 && f.App.ReadSnapshot().Session.Phase != SessionPhase.Resting; i++) { await Task.Delay(1); await f.Barrier(); }
        Assert.Equal(SessionPhase.Resting, f.App.ReadSnapshot().Session.Phase);
        await f.Advance(4_000);

        var request = f.Overlay.LastRequest!;
        f.Overlay.Fire(new(request.BatchId, request.SessionId, OverlayActionKind.BeginMemo));
        await f.Barrier();
        Assert.Equal(SessionPhase.EditingRestMemo, f.App.ReadSnapshot().Session.Phase);
        Assert.Contains(OverlayActionKind.ConfirmMemo, f.Overlay.LastUpdate!.AllowedActions);
        await f.Advance(3_000);
        Assert.Equal(4_000, f.App.ReadSnapshot().Session.RestElapsedMs);
        Assert.Equal(56_000, f.App.ReadSnapshot().Session.RemainingMs);
        Assert.Equal(63_000, f.App.ReadSnapshot().Session.WorkElapsedMs);

        f.Overlay.Fire(new(request.BatchId, request.SessionId, OverlayActionKind.ConfirmMemo) { Memo = "下一步核对数据" });
        await f.Barrier();
        Assert.Equal(SessionPhase.Resting, f.App.ReadSnapshot().Session.Phase);
        Assert.Equal("下一步核对数据", f.Overlay.LastUpdate!.Memo);
        Assert.Equal(request.BatchId, f.Overlay.LastUpdate.BatchId);
        Assert.Equal(1, f.Overlay.ShowCount);
        await f.Advance(2_000);
        Assert.Equal(6_000, f.App.ReadSnapshot().Session.RestElapsedMs);
        var work = Assert.Single(f.Store.Slices.Values, slice => slice.WorkSubtype == WorkSubtype.Memo);
        Assert.True(work.Finalized);
        Assert.Equal(3_000, work.DurationMs);
        Assert.Equal("下一步核对数据", (await f.Store.LoadAsync()).CurrentSession!.ResumeNote);
    }

    [Fact]
    public async Task DismissedEditorDoesNotRequireManualRest()
    {
        var f = await Fixture.Create();
        await f.Send(new UpdateSettings(1, 1));
        await f.Send(new StartSession());
        await f.Advance(50_000);
        await f.Send(new SetRestMemoEditing(true));
        await f.Send(new SetRestMemoEditing(false));
        await f.Advance(10_000);
        await f.Overlay.PresentationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, f.Overlay.ShowCount);
        Assert.Equal("", f.App.ReadSnapshot().WorkspaceDraft);
    }

    [Fact] public async Task LocalMidnightPersistsTheClosingIntervalBeforeTheFiveSecondCheckpoint()
    {
        var f = await Fixture.Create();
        f.Clock.UtcOrigin = new DateTimeOffset(2026, 11, 18, 15, 59, 59, TimeSpan.Zero);
        f.Clock.ZoneId = "China Standard Time";
        await f.Send(new StartSession());
        await f.Advance(1000);
        var closedDay = Assert.Single(f.Store.Slices.Values);
        Assert.Equal(new DateOnly(2026, 11, 18), closedDay.LocalDate);
        Assert.Equal(1000, closedDay.DurationMs);
        Assert.Equal(SessionPhase.Working, f.App.ReadSnapshot().Session.Phase);
        await f.Advance(1000);
        await f.Send(new PauseSession());
        var newDay = Assert.Single(f.Store.Slices.Values, s => s.LocalDate == new DateOnly(2026, 11, 19));
        Assert.Equal(1000, newDay.DurationMs);
    }

    private static CyclePlan TinyPlan(int repeats = 2) => new(1, 1, "look_away", [new(repeats, 2, "walk")]);
    private static async Task RestNow(Fixture f)
    {
        await f.Send(new BreakNow());
        for (var i = 0; i < 100 && f.App.ReadSnapshot().Session.Phase != SessionPhase.Resting; i++) { await Task.Delay(1); await f.Barrier(); }
        Assert.Equal(SessionPhase.Resting, f.App.ReadSnapshot().Session.Phase);
    }
    [Fact] public async Task CycleCursorSurvivesIdleReopenEmergencyAndAcknowledgementLoss()
    {
        var f = await Fixture.Create();
        await f.Send(new UpdateSettings(1, 1) { CyclePlan = TinyPlan() });
        await f.Send(new StartSession()); await RestNow(f);
        f.Store.LoseNextAcknowledgement = true;
        await f.Advance(1000);
        await f.App.RetryPendingSaveAsync();
        Assert.Equal(SessionPhase.AwaitingCheckIn, f.App.ReadSnapshot().Session.Phase);
        await f.Send(new DismissCheckIn(f.App.ReadSnapshot().PendingCheckInSessionId!.Value));
        await f.App.ShutdownAsync(TimeSpan.FromSeconds(1));
        f = await Fixture.Create(f.Store);
        Assert.Equal(2, f.App.ReadSnapshot().PlanProgress!.CompletedCount);
        await f.Send(new StartSession()); await RestNow(f);
        var batch = f.Overlay.LastRequest!.BatchId;
        Assert.Equal(1000, f.Overlay.LastRequest.RemainingRestMs);
        Assert.Contains("1/2", f.Overlay.LastRequest.SegmentCaption);
        await f.Advance(1000);
        Assert.Equal(SessionPhase.Resting, f.App.ReadSnapshot().Session.Phase);
        Assert.Equal(batch, f.Overlay.LastUpdate!.BatchId);
        Assert.Equal("walk", f.Overlay.LastUpdate.PromptId);
        Assert.Equal(2000, f.Overlay.LastUpdate.RemainingRestMs);
        await f.Send(new EmergencyExit(f.App.ReadSnapshot().CurrentSessionId!.Value, batch));
        Assert.Equal(2, f.App.ReadSnapshot().PlanProgress!.CompletedCount);
        await f.Send(new StartSession()); await RestNow(f); await f.Advance(3000);
        Assert.Equal(SessionPhase.AwaitingCheckIn, f.App.ReadSnapshot().Session.Phase);
        Assert.Equal(5, f.App.ReadSnapshot().PlanProgress!.CompletedCount);
        Assert.Equal(3000, f.Store.Sessions[f.App.ReadSnapshot().CurrentSessionId!.Value].RestMs);
        Assert.All(f.Store.Sessions.Values, row => Assert.Equal(CheckInStatus.Unrecorded, row.CheckInStatus));
        await f.Send(new DismissCheckIn(f.App.ReadSnapshot().PendingCheckInSessionId!.Value));
        Assert.Equal(0, f.App.ReadSnapshot().PlanProgress!.CompletedCount);
    }
    [Fact] public async Task CycleEditDoesNotAdvanceReplacementAndEquivalentEditPreservesCursor()
    {
        var f = await Fixture.Create(); var plan = TinyPlan();
        await f.Send(new UpdateSettings(1, 1) { CyclePlan = plan });
        await f.Send(new StartSession()); await RestNow(f); await f.Advance(1000);
        await f.Send(new DismissCheckIn(f.App.ReadSnapshot().PendingCheckInSessionId!.Value));
        await f.Send(new UpdateSettings(2, 2) { CyclePlan = plan.Copy() });
        Assert.Equal(2, f.App.ReadSnapshot().PlanProgress!.CompletedCount);
        await f.Send(new StartSession());
        await f.Send(new UpdateSettings(2, 2) { CyclePlan = TinyPlan(3) });
        await RestNow(f); await f.Advance(3000);
        Assert.Equal(5, f.App.ReadSnapshot().PlanProgress!.CompletedCount);
        await f.Send(new DismissCheckIn(f.App.ReadSnapshot().PendingCheckInSessionId!.Value));
        Assert.Equal(7, f.App.ReadSnapshot().PlanProgress!.Modules.Count);
        Assert.Equal(0, f.App.ReadSnapshot().PlanProgress!.CompletedCount);
        await f.Send(new UpdateSettings(2, 2));
        Assert.Equal(new[] { new PlannedModule(SliceKind.Work, 120_000), new PlannedModule(SliceKind.Rest, 120_000) },
            f.App.ReadSnapshot().PlanProgress!.Modules.ToArray());
        Assert.Equal(120000, f.App.ReadSnapshot().NextWorkDurationMs);
    }
    [Fact] public async Task CyclePausedSecondSegmentRestoresExactCountdown()
    {
        var f = await Fixture.Create();
        await f.Send(new UpdateSettings(1, 1) { CyclePlan = TinyPlan() });
        await f.Send(new StartSession()); await RestNow(f); await f.Advance(1000);
        await f.Send(new SubmitCheckInAndContinue(f.App.ReadSnapshot().CurrentSessionId!.Value, []));
        await RestNow(f); await f.Advance(2000);
        await f.App.ShutdownAsync(TimeSpan.FromSeconds(1));
        f = await Fixture.Create(f.Store);
        Assert.Equal(SessionPhase.Paused, f.App.ReadSnapshot().Session.Phase);
        Assert.Equal(4, f.App.ReadSnapshot().PlanProgress!.CompletedCount);
        Assert.True(f.App.ReadSnapshot().PlanProgress!.HasCurrent);
        await f.Send(new ResumeSession());
        for (var i = 0; i < 100 && f.App.ReadSnapshot().Session.Phase != SessionPhase.Resting; i++) { await Task.Delay(1); await f.Barrier(); }
        Assert.Equal("walk", f.Overlay.LastRequest!.PromptId);
        Assert.Equal(1000, f.Overlay.LastRequest.RemainingRestMs);
        await f.Advance(1000);
        Assert.Equal(SessionPhase.AwaitingCheckIn, f.App.ReadSnapshot().Session.Phase);
        Assert.Equal(5, f.App.ReadSnapshot().PlanProgress!.CompletedCount);
    }

    [Fact]
    public async Task CustomCycleStartsWithRestContinuesAcrossWorkAndMergesAcrossTheLoop()
    {
        var f = await Fixture.Create();
        var plan = CyclePlan.Compose([
            new CycleRest(1, "relax_shoulders_hands"), new CycleWork(1), new CycleWork(2),
            new CycleRest(2, "look_away"), new CycleRest(3, "walk")
        ], true);
        Assert.Null((await f.Send(new UpdateSettings(25, 5) { CyclePlan = plan })).Error);
        Assert.Equal(new[] { SliceKind.Rest, SliceKind.Work, SliceKind.Rest }, Progress().Modules.Select(module => module.Kind));
        Assert.Equal(0, Progress().CompletedCount);
        Assert.False(Progress().HasCurrent);
        Assert.Equal(1000, f.App.ReadSnapshot().NextRestDurationMs);
        await f.Send(new StartSession());
        await WaitForRest();
        Assert.Equal(1000, f.Overlay.LastRequest!.RemainingRestMs);
        await f.Advance(1000);
        await Continue();
        Assert.Equal(2, Progress().Modules.Count);
        Assert.Equal(0, Progress().CompletedCount);
        Assert.True(Progress().HasCurrent);
        Assert.True(f.App.ReadSnapshot().Session.RestFollows);
        var workId = f.App.ReadSnapshot().CurrentSessionId!.Value;
        Assert.Equal(3000, f.App.ReadSnapshot().Session.RemainingMs);
        await f.Advance(1000);
        Assert.Equal(SessionPhase.PreBreak, f.App.ReadSnapshot().Session.Phase);
        Assert.Equal(workId, f.App.ReadSnapshot().CurrentSessionId);
        Assert.Equal(2000, f.App.ReadSnapshot().Session.RemainingMs);
        Assert.Equal(0, Progress().CompletedCount);

        await f.Advance(2000);
        await WaitForRest();
        Assert.Equal(6000, f.Overlay.LastRequest!.RemainingRestMs);
        Assert.Equal(1, Progress().CompletedCount);
        Assert.Equal(new PlannedModule(SliceKind.Rest, 6000), Progress().Modules[^1]);
        Assert.Equal("look_away", f.Overlay.LastRequest.PromptId);
        await f.Advance(2000);
        Assert.Equal("walk", f.Overlay.LastUpdate!.PromptId);
        Assert.Equal(4000, f.Overlay.LastUpdate.RemainingRestMs);
        Assert.Equal(1, Progress().CompletedCount);
        Assert.Equal(f.Overlay.LastRequest.BatchId, f.Overlay.LastUpdate.BatchId);
        await f.Advance(4000);
        Assert.Equal(SessionPhase.AwaitingCheckIn, f.App.ReadSnapshot().Session.Phase);
        Assert.Equal(2, Progress().CompletedCount);
        Assert.False(Progress().HasCurrent);
        Assert.Equal(3000, f.App.ReadSnapshot().NextWorkDurationMs);
        Assert.Equal(0, f.App.ReadSnapshot().NextRestDurationMs);
        await Continue();
        Assert.Equal(SessionPhase.Working, f.App.ReadSnapshot().Session.Phase);
        Assert.Equal(0, Progress().CompletedCount);

        PlanProgress Progress() => f.App.ReadSnapshot().PlanProgress!;

        async Task WaitForRest()
        {
            for (var i = 0; i < 100 && f.App.ReadSnapshot().Session.Phase != SessionPhase.Resting; i++)
                await f.Barrier();
            Assert.Equal(SessionPhase.Resting, f.App.ReadSnapshot().Session.Phase);
        }

        Task<CommandResult> Continue() => f.Send(new SubmitCheckInAndContinue(f.App.ReadSnapshot().PendingCheckInSessionId!.Value, []));
    }

    [Fact]
    public async Task LegacyCheckpointKeepsItsWorkIndexAndUpgradesOnTheNextLap()
    {
        var f = await Fixture.Create();
        var legacy = CyclePlan.Compose([new CycleWork(1), new CycleWork(2), new CycleRest(1, "walk")], true) with { MergeAdjacentWork = null };
        await f.Send(new UpdateSettings(25, 5) { CyclePlan = legacy });
        await f.Send(new StartSession());
        await f.Advance(1000);
        var lastWork = f.App.ReadSnapshot().CurrentSessionId;
        await f.Advance(1000);
        await f.App.ShutdownAsync(TimeSpan.FromSeconds(1));

        f = await Fixture.Create(f.Store);
        var paused = f.App.ReadSnapshot();
        Assert.Equal(SessionPhase.Paused, paused.Session.Phase);
        Assert.Equal(1000, paused.Session.RemainingMs);
        Assert.Equal(lastWork, paused.CurrentSessionId);
        Assert.Equal(1, paused.PlanProgress!.CompletedCount);
        Assert.Null(paused.DefaultSettings.CyclePlan!.MergeAdjacentWork);
        await f.Send(new ResumeSession());
        await f.Advance(1000);
        for (var i = 0; i < 100 && f.App.ReadSnapshot().Session.Phase != SessionPhase.Resting; i++) await f.Barrier();
        Assert.Equal(SessionPhase.Resting, f.App.ReadSnapshot().Session.Phase);
        await f.Advance(1000);
        Assert.Equal(3000, f.App.ReadSnapshot().NextWorkDurationMs);
        Assert.True(f.App.ReadSnapshot().DefaultSettings.CyclePlan!.MergeAdjacentWork);
        await f.Send(new SubmitCheckInAndContinue(f.App.ReadSnapshot().PendingCheckInSessionId!.Value, []));
        Assert.Equal(3000, f.App.ReadSnapshot().Session.RemainingMs);
        Assert.Equal(2, f.App.ReadSnapshot().PlanProgress!.Modules.Count);
    }

    [Fact]
    public async Task CombinedWorkCanExceedTheIndividualModuleLimit()
    {
        var f = await Fixture.Create();
        var plan = CyclePlan.Compose([new CycleWork(10800), new CycleWork(10800), new CycleRest(60, "walk")], true);
        Assert.Null((await f.Send(new UpdateSettings(25, 5) { CyclePlan = plan })).Error);
        Assert.Null((await f.Send(new StartSession())).Error);
        Assert.Equal(21_600_000, f.App.ReadSnapshot().Session.RemainingMs);
    }

    private sealed class Fixture
    {
        public TestClock Clock = new(); public TestPulse Pulse = new(); public MemoryStore Store = null!;
        public TestOverlay Overlay = new(); public WorkClockApplication App = null!;
        public static async Task<Fixture> Create(MemoryStore? store = null)
        {
            var f = new Fixture { Store = store ?? new MemoryStore() };
            f.App = new(new SessionEngine(), f.Clock, f.Pulse, f.Store, f.Overlay, new TestDisplays());
            await f.App.InitializeAsync(); return f;
        }
        public Task<CommandResult> Send(WorkClockCommand command) => App.HandleAsync(new(Guid.NewGuid(), command, null, null));
        public Task<CommandResult> Barrier() => Send(new SelectPrompt("unknown"));
        public async Task Advance(long milliseconds)
        {
            while (milliseconds > 0) { var step = Math.Min(milliseconds, 1_000); Clock.Advance(step); Pulse.Fire(); await Barrier(); milliseconds -= step; }
        }
        public async Task Complete()
        {
            await Send(new UpdateSettings(1, 1)); await Send(new StartSession()); await Advance(60_000);
            await Overlay.PresentationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (var i = 0; i < 50 && App.ReadSnapshot().Session.Phase != SessionPhase.Resting; i++) { await Task.Delay(1); await Barrier(); }
            Assert.Equal(SessionPhase.Resting, App.ReadSnapshot().Session.Phase);
            await Advance(60_000); Assert.Equal(SessionPhase.AwaitingCheckIn, App.ReadSnapshot().Session.Phase);
        }
    }

    private sealed class TestClock : IClock
    {
        private readonly Guid _id = Guid.NewGuid(); private long _ms;
        public DateTimeOffset UtcOrigin = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
        public string ZoneId = "UTC";
        public void Advance(long ms) => _ms += ms;
        public ClockSample Read() => new(_id, _ms, 1000, UtcOrigin.AddMilliseconds(_ms), ZoneId);
    }
    private sealed class TestPulse : IPulseSource
    {
        private Action? _callback; public IDisposable Start(TimeSpan interval, Action callback) { _callback = callback; return new Disposable(); }
        public void Fire() => _callback?.Invoke();
    }
    private sealed class Disposable : IDisposable { public void Dispose() { } }
    private sealed class TestDisplays : IDisplayTopology
    { public IReadOnlyList<DisplayInfo> Read() => [new("one", true, new(0, 0, 1920, 1080), 96, 96)]; }
    private sealed class TestOverlay : IBreakOverlayHost
    {
        public List<Guid?> Closed = []; public bool HoldPresentation; public int ShowCount;
        public OverlayRequest? LastRequest; public CancellationToken LastCancellation;
        public OverlayViewModel? LastUpdate;
        private Action<OverlayAction>? _onAction;
        public TaskCompletionSource? PresentationGate;
        public TaskCompletionSource PresentationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<OverlayResult> ShowAsync(OverlayRequest request, CancellationToken cancellation)
        {
            ShowCount++; LastRequest = request; LastCancellation = cancellation; PresentationStarted.TrySetResult();
            if (HoldPresentation) await Task.Delay(Timeout.Infinite, cancellation);
            if (PresentationGate is { } gate) await gate.Task.WaitAsync(cancellation);
            return new(request.BatchId, ["one"], [], true, null);
        }
        public void Update(OverlayViewModel model) => LastUpdate = model;
        public void CloseAll(Guid? batchId, OverlayCloseReason reason) => Closed.Add(batchId);
        public IDisposable SubscribeActions(Action<OverlayAction> callback) { _onAction = callback; return new Disposable(); }
        public void Fire(OverlayAction action) => _onAction?.Invoke(action);
    }
    private sealed class MemoryStore : IWorkClockStore
    {
        public Dictionary<Guid, SessionRecord> Sessions = []; public Dictionary<Guid, TimeSlice> Slices = [];
        public Dictionary<Guid, AtomicWriteBatch> Operations = []; public List<AtomicWriteBatch> Batches = [];
        public bool FailNext, LoseNextAcknowledgement; public TaskCompletionSource? BlockNext;
        public TaskCompletionSource WriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _revision; private RuntimeCheckpoint? _checkpoint;
        private StoredSettings _settings = new(WorkClockApplication.Defaults() with { WorkDurationMs = 60_000, RestDurationMs = 60_000 }, DateTimeOffset.MinValue, 1);
        private WorkspaceDraft _draft = new("", DateTimeOffset.MinValue);
        public Task<MigrationReport> OpenAndMigrateAsync() => Task.FromResult(new MigrationReport(1, 1, true, null));
        public Task<PersistedAppState> LoadAsync()
        {
            var id = _checkpoint?.SessionId;
            return Task.FromResult(new PersistedAppState(_revision, _settings, _draft, _checkpoint,
                id.HasValue ? Sessions.GetValueOrDefault(id.Value) : null, Slices.Values.Where(x => x.SessionId == id).ToArray(), _checkpoint?.PendingCheckInSessionId));
        }
        public async Task<CommitResult> CommitAsync(AtomicWriteBatch batch)
        {
            Batches.Add(batch);
            if (BlockNext is { } block) { WriteStarted.TrySetResult(); await block.Task; BlockNext = null; }
            if (Operations.ContainsKey(batch.OperationId)) return new(CommitStatus.AlreadyCommitted, Operations[batch.OperationId].ExpectedStorageRevision + 1, null);
            if (FailNext) { FailNext = false; return new(CommitStatus.Failed, null, "injected"); }
            if (batch.ExpectedStorageRevision != _revision) return new(CommitStatus.Conflict, null, "revision");
            foreach (var row in batch.Sessions) Sessions[row.SessionId] = row;
            foreach (var row in batch.Slices) Slices[row.SliceId] = row;
            _draft = batch.Draft ?? _draft; _settings = batch.Settings ?? _settings;
            if (batch.Checkpoint is SetCheckpoint set) _checkpoint = set.Value;
            if (batch.Checkpoint is ClearCheckpoint) _checkpoint = null;
            Operations[batch.OperationId] = batch; _revision++;
            if (LoseNextAcknowledgement) { LoseNextAcknowledgement = false; throw new IOException("ack lost"); }
            return new(CommitStatus.Committed, _revision, null);
        }
        public Task<SessionPage> QuerySessionsAsync(SessionQuery query) => throw new NotSupportedException();
        public Task<IReadOnlyList<TimeSlice>> QuerySlicesAsync(DateRange range) => throw new NotSupportedException();
        public Task<int> ClearHistoryAsync(ClearHistoryRequest request) => throw new NotSupportedException();
    }
}
