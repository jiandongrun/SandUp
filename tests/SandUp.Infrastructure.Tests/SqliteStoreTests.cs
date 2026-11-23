using SandUp.Application;
using SandUp.Domain;
using SandUp.Infrastructure;
using Microsoft.Data.Sqlite;

namespace SandUp.Infrastructure.Tests;

public sealed class SqliteStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SandUp-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateOnly Day = new(2026, 11, 12);
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 1, 0, 0, TimeSpan.Zero);
    public SqliteStoreTests() => Directory.CreateDirectory(directory);
    private static void AssertLevels(ReminderLevels expected, ReminderLevels actual)
    {
        foreach (var part in Enum.GetValues<BodyPart>()) Assert.Equal(expected.For(part), actual.For(part), 8);
    }
    private async Task<SqliteWorkClockStore> Store()
    {
        var store = new SqliteWorkClockStore(Path.Combine(directory, "test.db"));
        Assert.True((await store.OpenAndMigrateAsync()).Succeeded); return store;
    }
    private static SessionRecord Session(Guid id, long work = 60000, SessionOutcome outcome = SessionOutcome.Completed) => new(id, Now, outcome == SessionOutcome.Active ? null : Now.AddMinutes(1), "China Standard Time", Day, outcome == SessionOutcome.Active ? null : "China Standard Time", outcome == SessionOutcome.Active ? null : Day, 1500000, 300000, 30000, false, 0, 0, work, 0, outcome, outcome == SessionOutcome.Active ? null : EndReason.RestTargetReached, CheckInStatus.Unrecorded, "=SUM(1,2)\nprivate note", Now);
    private static TimeSlice Slice(Guid session, Guid? id = null, long duration = 60000, bool finalized = true) => new(id ?? Guid.NewGuid(), session, 0, SliceKind.Work, WorkSubtype.Base, Day, "China Standard Time", Now, Now.AddMilliseconds(duration), duration, finalized);
    private static AtomicWriteBatch Batch(SessionRecord session, TimeSlice slice, long revision = 0, Guid? operation = null)
    {
        var op = operation ?? Guid.NewGuid();
        return new(op, revision, new[] { session }, new[] { slice }, Array.Empty<ActivityReplacement>(), new WorkspaceDraft("draft", Now), null, null, new OperationReceipt(op, Guid.NewGuid(), session.SessionId, 0, "saved"));
    }
    [Fact] public async Task CycleCursorAndPlanCommitAtomicallyAndSurviveCheckpointClear()
    {
        var store = await Store(); var id = Guid.NewGuid();
        var settings = new StoredSettings(WorkClockApplication.Defaults() with { CyclePlan = CyclePlan.EyeCare }, Now, 1)
        { CycleCompletedRounds = 2, CycleGeneration = 7 };
        var batch = Batch(Session(id), Slice(id)) with { Settings = settings, Checkpoint = new ClearCheckpoint() };
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(batch)).Status);
        var restarted = await Store();
        var saved = await restarted.LoadAsync();
        Assert.Null(saved.CurrentCheckpoint);
        Assert.Equal(2, saved.Settings.CycleCompletedRounds);
        Assert.Equal(7, saved.Settings.CycleGeneration);
        Assert.True(CyclePlan.EyeCare.SameAs(saved.Settings.Settings.CyclePlan));
        var broken = Batch(Session(Guid.NewGuid()), Slice(Guid.NewGuid()), 1) with
        { Settings = settings with { CycleCompletedRounds = 0 } };
        Assert.Equal(CommitStatus.Failed, (await restarted.CommitAsync(broken)).Status);
        Assert.Equal(2, (await restarted.LoadAsync()).Settings.CycleCompletedRounds);
        Assert.Equal(CommitStatus.AlreadyCommitted, (await restarted.CommitAsync(batch)).Status);
        Assert.Equal(2, (await restarted.LoadAsync()).Settings.CycleCompletedRounds);
    }
    [Fact] public async Task HistoricalActivitiesCanBeReadForEditingAndClearedWithoutChangingDuration()
    {
        var store=await Store();var id=Guid.NewGuid();var session=Session(id);var slice=Slice(id);
        var batch=Batch(session with{CheckInStatus=CheckInStatus.Reported},slice) with
        { ActivityReplacements=new[]{new ActivityReplacement(id,new[]{new ActivityReport(id,"walk",Now)})} };
        Assert.Equal(CommitStatus.Committed,(await store.CommitAsync(batch)).Status);
        var service=new SqliteHistoryService(store);Assert.Equal("walk",Assert.Single(await service.GetActivitiesAsync(id)).ActivityId);
        Assert.Equal(1,(await service.GetDayAsync(Day)).ActivityCounts["walk"]);
        var clear=Batch(session,slice,1) with{ActivityReplacements=new[]{new ActivityReplacement(id,Array.Empty<ActivityReport>())}};
        Assert.Equal(CommitStatus.Committed,(await store.CommitAsync(clear)).Status);
        Assert.Empty(await service.GetActivitiesAsync(id));Assert.Equal(60000,(await service.GetDayAsync(Day)).WorkMs);
        Assert.Empty((await service.GetDayAsync(Day)).ActivityCounts);
    }
    [Fact] public async Task DailySummaryIsEmptyWithoutHistory()
    {
        var store=await Store();var summary=await new SqliteHistoryService(store).GetDayAsync(Day);
        Assert.Equal(0,summary.CompletedCount);Assert.Equal(0,summary.WorkMs);Assert.Equal(0,summary.RestMs);
    }
    [Fact] public async Task DailySummaryCountsActiveSlicesAndKeepsThemWhenClearingCompletedRounds()
    {
        var store=await Store();var closed=Guid.NewGuid();var active=Guid.NewGuid();var tomorrow=Day.AddDays(1);
        var first=Slice(closed,duration:30000);
        var second=first with{SliceId=Guid.NewGuid(),SequenceNo=1,LocalDate=tomorrow,StartedAtUtc=Now.AddDays(1),EndedAtUtc=Now.AddDays(1).AddSeconds(30)};
        var completed = Session(closed) with { EndedAtUtc = second.EndedAtUtc, EndedLocalDate = tomorrow };
        Assert.Equal(CommitStatus.Committed,(await store.CommitAsync(Batch(completed,first) with{Slices=new[]{first,second}})).Status);
        var current=Session(active,60000,SessionOutcome.Active) with{StartedAtUtc=Now.AddDays(1),StartedLocalDate=tomorrow,RestMs=20000};
        var work=Slice(active,finalized:false) with{LocalDate=tomorrow,StartedAtUtc=Now.AddDays(1),EndedAtUtc=Now.AddDays(1).AddMinutes(1)};
        var rest=work with{SliceId=Guid.NewGuid(),SequenceNo=1,Kind=SliceKind.Rest,WorkSubtype=null,DurationMs=20000,StartedAtUtc=work.EndedAtUtc,EndedAtUtc=work.EndedAtUtc.AddSeconds(20)};
        Assert.Equal(CommitStatus.Committed,(await store.CommitAsync(Batch(current,work,1) with{Slices=new[]{work,rest}})).Status);
        var service=new SqliteHistoryService(store);var summary=await service.GetDayAsync(tomorrow);
        Assert.Equal(1,summary.CompletedCount);
        Assert.Equal(90000,summary.WorkMs);Assert.Equal(20000,summary.RestMs);
        Assert.Equal(30000,(await service.GetDayAsync(Day)).WorkMs);
        await store.ClearHistoryAsync(new(new(Day,tomorrow),true,false));
        var cleared=await service.GetDayAsync(tomorrow);Assert.Equal(0,cleared.CompletedCount);Assert.Equal(60000,cleared.WorkMs);Assert.Equal(20000,cleared.RestMs);
    }
    [Fact] public async Task T17_DuplicateCommitAndRestartReceiptAreIdempotent()
    {
        var store = await Store(); var id = Guid.NewGuid(); var batch = Batch(Session(id), Slice(id));
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(batch)).Status);
        var restarted = await Store();
        Assert.Equal(CommitStatus.AlreadyCommitted, (await restarted.CommitAsync(batch)).Status);
        Assert.NotNull(await restarted.FindReceiptByCommandAsync(batch.Receipt.CommandId!.Value));
        Assert.Equal(60000, (await new SqliteHistoryService(restarted).GetDayAsync(Day)).WorkMs);
        Assert.Equal(1, (await restarted.LoadAsync()).StorageRevision);
    }
    [Fact] public async Task T18_InvalidForeignKeyRollsBackEntireBatch()
    {
        var store = await Store(); var id = Guid.NewGuid(); var batch = Batch(Session(id), Slice(Guid.NewGuid()));
        Assert.Equal(CommitStatus.Failed, (await store.CommitAsync(batch)).Status);
        Assert.Null(await store.FindSessionAsync(id));
        Assert.Equal("", (await store.LoadAsync()).WorkspaceDraft.Text);
        Assert.Equal(0, (await store.LoadAsync()).StorageRevision);
    }
    [Fact] public async Task T33_CheckpointsReplaceCumulativeSliceWithoutDoubleCounting()
    {
        var store = await Store(); var id = Guid.NewGuid(); var sliceId = Guid.NewGuid();
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(Session(id, 30000, SessionOutcome.Active), Slice(id, sliceId, 30000, false)))).Status);
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(Session(id, 60000, SessionOutcome.Active), Slice(id, sliceId, 60000, false), 1))).Status);
        Assert.Equal(60000, (await new SqliteHistoryService(store).GetDayAsync(Day)).WorkMs);
        Assert.Single(await store.QuerySlicesAsync(new(Day, Day)));
    }
    [Fact] public async Task FinalizedSlicesCannotBeChangedAndMismatchedTotalsRollback()
    {
        var store = await Store(); var id = Guid.NewGuid(); var slice = Slice(id);
        await store.CommitAsync(Batch(Session(id), slice));
        Assert.Equal(CommitStatus.Failed, (await store.CommitAsync(Batch(Session(id, 61000), slice with { DurationMs = 61000 }, 1))).Status);
        Assert.Equal(60000, (await store.FindSessionAsync(id))!.WorkMs);
    }
    [Fact] public async Task SliceIdentityCannotChangeDuringCheckpoint()
    {
        var store = await Store(); var id = Guid.NewGuid(); var slice = Slice(id, finalized: false);
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(Session(id, 60000, SessionOutcome.Active), slice))).Status);
        var changed = slice with { LocalDate = Day.AddDays(1) };
        Assert.Equal(CommitStatus.Failed, (await store.CommitAsync(Batch(Session(id, 60000, SessionOutcome.Active), changed, 1))).Status);
        Assert.Single(await store.QuerySlicesAsync(new(Day, Day)));
        Assert.Empty(await store.QuerySlicesAsync(new(Day.AddDays(1), Day.AddDays(1))));
    }
    [Fact] public async Task CrossDaySummaryAndExportUseSliceDateAndEndDate()
    {
        var store = await Store(); var id = Guid.NewGuid(); var next = Day.AddDays(1);
        var first = Slice(id, duration: 30000);
        var second = Slice(id, duration: 30000) with { SequenceNo = 1, LocalDate = next };
        var session = Session(id) with { EndedLocalDate = next };
        var batch = Batch(session, first) with { Slices = new[] { first, second } };
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(batch)).Status);
        var history = new SqliteHistoryService(store);
        Assert.Equal(30000, (await history.GetDayAsync(Day)).WorkMs);
        Assert.Equal(0, (await history.GetDayAsync(Day)).CompletedCount);
        Assert.Equal(30000, (await history.GetDayAsync(next)).WorkMs);
        Assert.Equal(1, (await history.GetDayAsync(next)).CompletedCount);
        var days = await history.GetDaysAsync(new(Day.AddDays(-1), next));
        Assert.Equal(new[] { Day.AddDays(-1), Day, next }, days.Select(day => day.LocalDate));
        Assert.Equal(new long[] { 0, 30000, 30000 }, days.Select(day => day.WorkMs));
        Assert.Equal(new[] { 0, 0, 1 }, days.Select(day => day.CompletedCount));
        Assert.Equal(default(RoundDurationTotals), days[1].WorkRounds);
        Assert.Equal(new RoundDurationTotals(60000, 1), days[2].WorkRounds);
        Assert.Single(days.Select(day => day.DataAsOfUtc).Distinct());
        var export = await new CsvHistoryExporter(store).ExportAsync(new(new(next, next), Path.Combine(directory, "cross-day")), default);
        Assert.Equal(ExportStatus.Succeeded, export.Status);
        Assert.Equal(1, export.SessionCount); Assert.Equal(1, export.SliceCount);
        var slices = await File.ReadAllTextAsync(export.OutputFiles[1]);
        Assert.Contains(second.SliceId.ToString(), slices); Assert.DoesNotContain(first.SliceId.ToString(), slices);
    }
    [Fact] public async Task ReminderReplaysWorkAndReportedActivitiesInEndTimeOrder()
    {
        var store = await Store();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var firstSlice = Slice(first, duration: 2_400_000) with { EndedAtUtc = Now.AddMinutes(40) };
        var firstSession = Session(first, 2_400_000) with { EndedAtUtc = firstSlice.EndedAtUtc, CheckInStatus = CheckInStatus.Reported };
        var firstBatch = Batch(firstSession, firstSlice) with { ActivityReplacements = [new(first, [new(first, "look_away", Now.AddDays(5)), new(first, "look_away", Now.AddDays(5)), new(first, "other", Now)])] };
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(firstBatch)).Status);
        var secondSlice = Slice(second, duration: 1_200_000) with { StartedAtUtc = Now.AddMinutes(40), EndedAtUtc = Now.AddMinutes(60) };
        var secondSession = Session(second, 1_200_000) with { EndedAtUtc = secondSlice.EndedAtUtc, CheckInStatus = CheckInStatus.Reported };
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(secondSession, secondSlice, 1) with { ActivityReplacements = [new(second, [new(second, "walk", Now)])] })).Status);
        var reminder = (await new SqliteHistoryService(store).GetDayAsync(Day)).Reminder;
        Assert.True(reminder.HasRecords);
        AssertLevels(new ReminderLevels(96, 90, 98, 100, 100, 100), reminder.Levels);
    }

    [Fact] public async Task ReminderResetsAtMidnightAndRecomputesHistoricalEdits()
    {
        var store = await Store(); var id = Guid.NewGuid(); var next = Day.AddDays(1);
        var first = Slice(id, duration: 1_800_000);
        var second = Slice(id, duration: 1_800_000) with { SequenceNo = 1, LocalDate = next, StartedAtUtc = first.EndedAtUtc, EndedAtUtc = first.EndedAtUtc.AddMinutes(30) };
        var session = Session(id, 3_600_000) with { EndedLocalDate = next, EndedAtUtc = second.EndedAtUtc };
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(session, first) with { Slices = [first, second] })).Status);
        var history = new SqliteHistoryService(store);
        var days = await history.GetDaysAsync(new(Day, next));
        Assert.Equal(new ReminderLevels(94, 95, 96, 98.5, 95.5, 99), days[0].Reminder.Levels);
        AssertLevels(new ReminderLevels(93.964, 94.975, 95.984, 98.49775, 95.47975, 98.999), days[1].Reminder.Levels);
        var edit = Batch(session with { CheckInStatus = CheckInStatus.Reported }, first, 1) with { Slices = [first, second], ActivityReplacements = [new(id, [new(id, "relax_shoulders_hands", Now.AddDays(10))])] };
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(edit)).Status);
        AssertLevels(new ReminderLevels(93.964, 100, 95.984, 98.49775, 95.47975, 98.999), (await history.GetDayAsync(next)).Reminder.Levels);
        Assert.False((await history.GetDayAsync(next.AddDays(1))).Reminder.HasRecords);
    }
    [Fact] public async Task ReminderConsumesFractionalWorkAcrossSlicesAndClampsAtZero()
    {
        var store = await Store(); var id = Guid.NewGuid();
        var first = Slice(id, duration: 600_000);
        var second = Slice(id, duration: 600_000) with { SequenceNo = 1, StartedAtUtc = first.EndedAtUtc, EndedAtUtc = first.EndedAtUtc.AddMinutes(10) };
        var third = Slice(id, duration: 200_000_000) with { SequenceNo = 2, StartedAtUtc = second.EndedAtUtc, EndedAtUtc = second.EndedAtUtc.AddMilliseconds(200_000_000) };
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(Session(id, 1_200_000, SessionOutcome.Active), first) with { Slices = [first, second] })).Status);
        var fractional = (await new SqliteHistoryService(store).GetDayAsync(Day)).Reminder.Levels;
        Assert.Equal(96, fractional.Eyes, 8); Assert.Equal(100 - 10d / 3, fractional.Shoulders, 8); Assert.Equal(99, fractional.Abdomen, 8);
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(Session(id, 201_200_000), first, 1) with { Slices = [first, second, third] })).Status);
        var reminder = (await new SqliteHistoryService(store).GetDayAsync(Day)).Reminder;
        Assert.True(reminder.HasRecords);
        Assert.Equal(new ReminderLevels(0, 0, 0, 0, 0, 0), reminder.Levels);
    }
    [Fact] public async Task ReminderHistoryUsesRecordedBaseDaysAndCurrentRatesWithoutChangingExportRange()
    {
        var store = await Store(); var id = Guid.NewGuid(); var prior = Day.AddDays(-2);
        var session = Session(id, 3_600_000) with { StartedLocalDate = prior, EndedLocalDate = prior, EndedAtUtc = Now.AddHours(1) };
        var slice = Slice(id, duration: 3_600_000) with { LocalDate = prior };
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(session, slice))).Status);
        var todayId = Guid.NewGuid();
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(Session(todayId, 3_600_000), Slice(todayId, duration: 3_600_000), 1))).Status);
        var service = new SqliteHistoryService(store);
        var single = await service.GetDayAsync(Day);
        Assert.Equal(1, single.Reminder.HistoryDays); Assert.Equal(97.6, single.Reminder.History.Eyes, 8);
        Assert.Equal(87.856, single.Reminder.Levels.Eyes, 8);
        Assert.Equal(single.Reminder, (await service.GetDaysAsync(new(prior, Day)))[2].Reminder);
        Assert.Equal(3_600_000, single.WorkMs); Assert.Equal(1, single.CompletedCount);
        var exported = await new CsvHistoryExporter(store).ExportAsync(new(new(Day, Day), Path.Combine(directory, "history-window")), default);
        Assert.Equal(1, exported.SessionCount); Assert.Equal(1, exported.SliceCount);
        var loaded = await store.LoadAsync();
        var settings = loaded.Settings with { Settings = loaded.Settings.Settings with { ReminderRates = new(0, 40, 1.5, 0, 0, 0) } };
        var update = Batch(Session(todayId, 3_600_000), Slice(todayId, duration: 3_600_000), 2) with { Slices = [], Settings = settings };
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(update)).Status);
        var recalculated = (await service.GetDayAsync(Day)).Reminder;
        Assert.Equal(100, recalculated.Levels.Eyes); Assert.Equal(58.4, recalculated.Levels.Shoulders, 8);
        Assert.Equal(92, recalculated.History.Shoulders, 8); Assert.Equal(98.49775, recalculated.Levels.Back, 8);
        Assert.Equal(2, (await service.GetDayAsync(Day.AddDays(8))).Reminder.HistoryDays);
    }

    [Fact] public async Task ReminderWeightsAllRecordedDaysAndAmplifiesOnlyTheAffectedBodyPart()
    {
        var store = await Store();
        async Task SaveDay(DateOnly day, long duration, long revision, string? activity)
        {
            var id = Guid.NewGuid();
            var session = Session(id, duration) with
            {
                StartedLocalDate = day, EndedLocalDate = day, EndedAtUtc = Now.AddMilliseconds(duration),
                CheckInStatus = activity is null ? CheckInStatus.Unrecorded : CheckInStatus.Reported
            };
            var batch = Batch(session, Slice(id, duration: duration) with { LocalDate = day }, revision);
            if (activity is not null) batch = batch with { ActivityReplacements = [new(id, [new(id, activity, Now)])] };
            Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(batch)).Status);
        }
        await SaveDay(Day.AddDays(-5), 1_800_000, 0, "relax_shoulders_hands");
        await SaveDay(Day.AddDays(-2), 3_600_000, 1, "relax_shoulders_hands");
        // Missing calendar dates and a zero-second round with an unmapped activity contribute no sample.
        await SaveDay(Day.AddDays(-1), 0, 2, "other");
        await SaveDay(Day, 3_600_000, 3, null);
        var service = new SqliteHistoryService(store);
        var result = (await service.GetDayAsync(Day)).Reminder;
        Assert.Equal(2, result.HistoryDays);
        Assert.Equal(96.6256, result.History.Eyes, 8);
        Assert.Equal(100, result.History.Shoulders, 8);
        Assert.Equal(87.797536, result.Levels.Eyes, 8);
        Assert.Equal(90, result.Levels.Shoulders, 8);
        Assert.False((await service.GetDayAsync(Day.AddDays(-1))).Reminder.HasRecords);
        Assert.Equal(result, (await service.GetDaysAsync(new(Day.AddDays(-5), Day)))[5].Reminder);
    }

    [Fact] public async Task ReminderReadsLegacySettingsWithoutRatesAndIncludesAllPriorDays()
    {
        var store = await Store(); var id = Guid.NewGuid(); var prior = Day.AddDays(-8);
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(
            Session(id, 3_600_000) with { StartedLocalDate = prior, EndedLocalDate = prior },
            Slice(id, duration: 3_600_000) with { LocalDate = prior }))).Status);
        using (var c = new SqliteConnection($"Data Source={Path.Combine(directory, "test.db")};Pooling=False"))
        {
            c.Open(); using var command = c.CreateCommand();
            command.CommandText = "UPDATE app_values SET payload=json_remove(payload,'$.Settings.ReminderRates') WHERE key='settings'";
            command.ExecuteNonQuery();
        }
        Assert.Equal(ReminderRates.Default, (await store.LoadAsync()).Settings.Settings.ReminderRates);
        var history = new SqliteHistoryService(store);
        Assert.Equal(88, (await history.GetDayAsync(prior)).Reminder.Levels.Eyes);
        var today = await history.GetDayAsync(Day);
        Assert.Equal(1, today.Reminder.HistoryDays); Assert.Equal(97.6, today.Reminder.History.Eyes, 8);
        Assert.False(today.Reminder.HasRecords);
    }

    [Fact] public async Task AllHistoryRollsSixIndependentScoresAcrossSparseDatesAndOutOfOrderQueries()
    {
        var store = await Store(); var service = new SqliteHistoryService(store);
        var rates = ReminderRates.Default;
        var expected = Enumerable.Repeat(100d, 6).ToArray();
        var parts = Enum.GetValues<BodyPart>();
        // More than thirty sample days and eighty calendar days: neither query bounds
        // nor a seven-day window may truncate the recurrence.
        for (int i = 0; i < 41; i++)
        {
            var day = Day.AddDays(i * 2); var id = Guid.NewGuid();
            var duration = (i % 4 + 1) * 900_000L;
            var session = Session(id, duration) with { StartedLocalDate = day, EndedLocalDate = day };
            Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(session, Slice(id, duration: duration) with { LocalDate = day }, i))).Status);
            var state = (await service.GetDayAsync(day)).Reminder;
            Assert.Equal(i, state.HistoryDays);
            for (int j = 0; j < parts.Length; j++)
            {
                Assert.Equal(expected[j], state.History.For(parts[j]), 8);
                double actual = Math.Max(0, 100 - duration / 3_600_000d * rates.For(parts[j]) * (1 + (100 - expected[j]) / 200));
                Assert.Equal(actual, state.Levels.For(parts[j]), 8);
                expected[j] = .8 * expected[j] + .2 * actual;
            }
        }
        var blank = (await service.GetDayAsync(Day.AddDays(400))).Reminder;
        Assert.False(blank.HasRecords); Assert.Equal(ReminderLevels.Full, blank.Levels);
        Assert.Equal(41, blank.HistoryDays);
        for (int j = 0; j < parts.Length; j++) Assert.Equal(expected[j], blank.History.For(parts[j]), 8);
        var first = (await service.GetDayAsync(Day)).Reminder;
        Assert.Equal(0, first.HistoryDays); Assert.Equal(ReminderLevels.Full, first.History);
        Assert.Equal(0, (await service.GetDayAsync(Day.AddDays(-1))).Reminder.HistoryDays);
        Assert.Equal(blank, (await service.GetDaysAsync(new(Day.AddDays(399), Day.AddDays(400))))[1].Reminder);
    }

    private static SortedList<DateOnly, ReminderDayState> Prefix(SqliteWorkClockStore store) =>
        (SortedList<DateOnly, ReminderDayState>)typeof(SqliteWorkClockStore)
            .GetField("reminderPrefix", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;

    [Fact] public async Task ActiveCrossMidnightRestartAndCheckpointReuseHistoricalPrefix()
    {
        var store = await Store(); var id = Guid.NewGuid(); var next = Day.AddDays(1);
        var first = Slice(id, duration: 1_800_000);
        var second = Slice(id, duration: 1_800_000, finalized: false) with
        { SequenceNo = 1, LocalDate = next, StartedAtUtc = Now.AddDays(1), EndedAtUtc = Now.AddDays(1).AddMinutes(30) };
        var active = Session(id, 3_600_000, SessionOutcome.Active);
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(active, first) with { Slices = [first, second] })).Status);
        var restarted = await Store(); var service = new SqliteHistoryService(restarted);
        var before = (await service.GetDayAsync(next)).Reminder;
        Assert.Equal(1, before.HistoryDays); Assert.Equal(98.8, before.History.Eyes, 8);
        var priorPrefix = Prefix(restarted)[Day];
        var currentPrefix = Prefix(restarted)[next];
        var updated = second with { DurationMs = 3_600_000, EndedAtUtc = second.EndedAtUtc.AddMinutes(30) };
        Assert.Equal(CommitStatus.Committed, (await restarted.CommitAsync(Batch(active with { WorkMs = 5_400_000 }, updated, 1) with { Slices = [first, updated] })).Status);
        var after = (await service.GetDayAsync(next)).Reminder;
        Assert.Same(priorPrefix, Prefix(restarted)[Day]);
        Assert.NotSame(currentPrefix, Prefix(restarted)[next]);
        Assert.Equal(87.928, after.Levels.Eyes, 8);
        Assert.Equal(before.History, after.History);
        var cached = Prefix(restarted)[next];
        await service.GetDayAsync(next);
        Assert.Same(cached, Prefix(restarted)[next]);
        Assert.Equal(after, (await new SqliteHistoryService(await Store()).GetDayAsync(next)).Reminder);
    }

    [Fact] public async Task BackfillActivityAndRawDeletionInvalidateEveryStoreWithoutRetainingDeletedDates()
    {
        var store = await Store(); var service = new SqliteHistoryService(store);
        var other = await Store(); var otherService = new SqliteHistoryService(other);
        var prior = Day.AddDays(-100); var id = Guid.NewGuid();
        var session = Session(id, 3_600_000) with { StartedLocalDate = prior, EndedLocalDate = prior, EndedAtUtc = Now.AddHours(1) };
        var slice = Slice(id, duration: 3_600_000) with { LocalDate = prior };
        Assert.Equal(0, (await service.GetDayAsync(Day)).Reminder.HistoryDays);
        Assert.Equal(0, (await otherService.GetDayAsync(Day)).Reminder.HistoryDays);
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(session, slice))).Status);
        Assert.Equal(97.6, (await service.GetDayAsync(Day)).Reminder.History.Eyes, 8);
        Assert.Equal(1, (await otherService.GetDayAsync(Day)).Reminder.HistoryDays);
        Assert.Equal(CommitStatus.Committed, (await other.CommitAsync(Batch(session with { CheckInStatus = CheckInStatus.Reported }, slice, 1) with
        { Slices = [], ActivityReplacements = [new(id, [new(id, "look_away", Now)])] })).Status);
        Assert.Equal(100, (await service.GetDayAsync(Day)).Reminder.History.Eyes, 8);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "test.db")};Pooling=False;Foreign Keys=True"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sessions"; command.ExecuteNonQuery();
            command.CommandText = "SELECT COUNT(*) FROM reminder_dirty_dates";
            Assert.Equal(0L, command.ExecuteScalar());
        }
        Assert.Equal(0, (await service.GetDayAsync(Day)).Reminder.HistoryDays);
        Assert.Equal(0, (await otherService.GetDayAsync(Day)).Reminder.HistoryDays);
        Assert.Empty(Prefix(store)); Assert.Empty(Prefix(other));
    }

    [Fact] public async Task CrossDayActivitiesAreNotSettledBeforeTheirEndDate()
    {
        var store = await Store(); var id = Guid.NewGuid(); var next = Day.AddDays(1);
        var first = Slice(id, duration: 1_800_000);
        var second = Slice(id, duration: 1_800_000) with
        { SequenceNo = 1, LocalDate = next, StartedAtUtc = Now.AddDays(1), EndedAtUtc = Now.AddDays(1).AddMinutes(30) };
        var session = Session(id, 3_600_000) with
        { EndedLocalDate = next, EndedAtUtc = second.EndedAtUtc, CheckInStatus = CheckInStatus.Reported };
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(session, first) with
        { Slices = [first, second], ActivityReplacements = [new(id, [new(id, "look_away", Now)])] })).Status);
        var service = new SqliteHistoryService(store);
        Assert.Equal(94, (await service.GetDayAsync(Day)).Reminder.Levels.Eyes, 8);
        Assert.Single(Prefix(store)); Assert.False(Prefix(store).ContainsKey(next));
        var endedDay = (await service.GetDayAsync(next)).Reminder;
        Assert.Equal(1, endedDay.HistoryDays); Assert.Equal(98.8, endedDay.History.Eyes, 8);
        Assert.Equal(100, endedDay.Levels.Eyes, 8);
        Assert.Equal(99.04, (await service.GetDayAsync(next.AddDays(1))).Reminder.History.Eyes, 8);
    }

    [Fact] public async Task ZeroSecondEndsAreNotSamplesButRestAndMappedActivitiesAre()
    {
        var store = await Store();
        var zero = Guid.NewGuid();
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(Session(zero, 0), Slice(zero, duration: 0)))).Status);
        var service = new SqliteHistoryService(store);
        Assert.False((await service.GetDayAsync(Day)).Reminder.HasRecords);
        Assert.Equal(0, (await service.GetDayAsync(Day.AddDays(1))).Reminder.HistoryDays);
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(Session(zero, 0) with { CheckInStatus = CheckInStatus.Reported }, Slice(zero, duration: 0), 1) with
        { Slices = [], ActivityReplacements = [new(zero, [new(zero, "look_away", Now)])] })).Status);
        Assert.True((await service.GetDayAsync(Day)).Reminder.HasRecords);
        Assert.Equal(1, (await service.GetDayAsync(Day.AddDays(1))).Reminder.HistoryDays);
        await store.ClearHistoryAsync(new(new(Day, Day), true, false));
        Assert.Equal(0, (await service.GetDayAsync(Day.AddDays(1))).Reminder.HistoryDays);
        var restId = Guid.NewGuid();
        var rest = Slice(restId) with { Kind = SliceKind.Rest, WorkSubtype = null };
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(Session(restId, 0) with { RestMs = 60_000 }, rest, 3))).Status);
        Assert.True((await service.GetDayAsync(Day)).Reminder.HasRecords);
        Assert.Equal(1, (await service.GetDayAsync(Day.AddDays(1))).Reminder.HistoryDays);
    }

    [Fact] public void ReminderRatesAndRecoveryRespectAllSixBounds()
    {
        Assert.True(ReminderRates.Default.IsValid);
        Assert.False((ReminderRates.Default with { Back = double.NaN }).IsValid);
        Assert.False((ReminderRates.Default with { Pelvic = 40.01 }).IsValid);
        Assert.False((ReminderRates.Default with { Eyes = -1 }).IsValid);
        Assert.False((ReminderRates.Default with { Legs = double.PositiveInfinity }).IsValid);
        Assert.True(new ReminderRates(0, 40, 0, 40, 0, 40).IsValid);
        Assert.Equal(ReminderLevels.Full, ReminderLevels.Full.Restore("walk").Restore("look_away").Restore("relax_shoulders_hands"));
        Assert.Equal(new ReminderLevels(0, 0, 6, 3, 12, 4), new ReminderLevels(0, 0, 0, 0, 0, 0).Restore("walk"));
        Assert.Equal(ReminderLevels.Full, ReminderLevels.Full.Restore("other"));
    }

    [Fact] public async Task T26_ClearPreservesActiveSessionAndDraftAndRejectsOldRevision()
    {
        var store = await Store(); var closed = Guid.NewGuid(); var active = Guid.NewGuid();
        await store.CommitAsync(Batch(Session(closed), Slice(closed)));
        await store.CommitAsync(Batch(Session(active, 60000, SessionOutcome.Active), Slice(active, finalized: false), 1));
        Assert.Equal(1, await store.ClearHistoryAsync(new(new(Day, Day), true, false)));
        Assert.Null(await store.FindSessionAsync(closed)); Assert.NotNull(await store.FindSessionAsync(active));
        Assert.Equal("draft", (await store.LoadAsync()).WorkspaceDraft.Text);
        Assert.Equal(CommitStatus.Conflict, (await store.CommitAsync(Batch(Session(closed), Slice(closed), 2))).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ClearHistoryAsync(new(new(Day, Day), false, true)));
    }
    [Fact] public async Task ClearRemovesRelatedDataAndOptionalDraft()
    {
        var store = await Store(); var id = Guid.NewGuid();
        var batch = Batch(Session(id) with { CheckInStatus = CheckInStatus.Reported }, Slice(id)) with { ActivityReplacements = new[] { new ActivityReplacement(id, new[] { new ActivityReport(id, "walk", Now) }) } };
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(batch)).Status);
        Assert.Equal(1, await store.ClearHistoryAsync(new(new(Day, Day), true, true)));
        Assert.Empty(await store.QuerySlicesAsync(new(Day, Day)));
        Assert.Empty(await store.ReadActivitiesAsync(id));
        Assert.Null(await store.FindReceiptByCommandAsync(batch.Receipt.CommandId!.Value));
        Assert.Equal("", (await store.LoadAsync()).WorkspaceDraft.Text);
        Assert.Equal(0, (await new SqliteHistoryService(store).GetDayAsync(Day)).CompletedCount);
    }
    [Fact] public async Task ClearFailureRollsBackAllChanges()
    {
        var store = await Store(); var id = Guid.NewGuid();
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(Batch(Session(id), Slice(id)))).Status);
        using (var c = new SqliteConnection($"Data Source={Path.Combine(directory, "test.db")};Pooling=False"))
        {
            c.Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER reject_clear BEFORE DELETE ON sessions BEGIN SELECT RAISE(ABORT, 'test failure'); END";
            cmd.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => store.ClearHistoryAsync(new(new(Day, Day), true, true)));
        Assert.NotNull(await store.FindSessionAsync(id));
        Assert.Single(await store.QuerySlicesAsync(new(Day, Day)));
        Assert.Equal("draft", (await store.LoadAsync()).WorkspaceDraft.Text);
        Assert.Equal(1, (await store.LoadAsync()).StorageRevision);
    }
    [Fact] public async Task T14_LoadRestoresCheckpointDataWithoutPersistingClockAnchor()
    {
        var store = await Store(); var id = Guid.NewGuid(); var slice = Slice(id, finalized: false);
        var checkpoint = new RuntimeCheckpoint(id, SessionPhase.Working, null, null, 60000, 0, 1500000, false, 0, 0, slice.SliceId, "walk", null, Now, 0);
        await store.CommitAsync(Batch(Session(id, 60000, SessionOutcome.Active), slice) with { Checkpoint = new SetCheckpoint(checkpoint) });
        var loaded = await (await Store()).LoadAsync();
        Assert.Equal(60000, loaded.CurrentCheckpoint!.WorkElapsedMs); Assert.Equal(id, loaded.CurrentSession!.SessionId); Assert.Single(loaded.CurrentSlices);
        // Converting stored running phase into paused is Application's responsibility.
    }
    [Fact] public async Task T24_ActivitiesAndOutcomeCountsAreSeparate()
    {
        var store = await Store(); var id = Guid.NewGuid();
        var batch = Batch(Session(id) with { CheckInStatus = CheckInStatus.Reported }, Slice(id)) with { ActivityReplacements = new[] { new ActivityReplacement(id, new[] { new ActivityReport(id, "walk", Now) }) } };
        Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(batch)).Status);
        var day = await new SqliteHistoryService(store).GetDayAsync(Day);
        Assert.Equal(1, day.CompletedCount); Assert.Equal(1, day.ActivityCounts["walk"]);
    }

    [Fact]
    public async Task RoundAveragesUseFinishedDurationsAndActivityCountsUseDistinctRounds()
    {
        var store = await Store();
        async Task SaveRound(long revision, long work, long rest, SessionOutcome outcome, params string[] activities)
        {
            var id = Guid.NewGuid();
            var session = Session(id, work, outcome) with
            {
                RestMs = rest,
                CheckInStatus = activities.Length == 0 ? CheckInStatus.Unrecorded : CheckInStatus.Reported
            };
            var workSlice = Slice(id, duration: work);
            TimeSlice[] slices = rest == 0 ? [workSlice] :
            [
                workSlice,
                workSlice with
                {
                    SliceId = Guid.NewGuid(), SequenceNo = 1, Kind = SliceKind.Rest, WorkSubtype = null,
                    StartedAtUtc = workSlice.EndedAtUtc, EndedAtUtc = workSlice.EndedAtUtc.AddMilliseconds(rest), DurationMs = rest
                }
            ];
            var batch = Batch(session, workSlice, revision) with
            {
                Slices = slices,
                ActivityReplacements = [new(id, activities.Select(activity => new ActivityReport(id, activity, Now)).ToArray())]
            };
            Assert.Equal(CommitStatus.Committed, (await store.CommitAsync(batch)).Status);
        }

        await SaveRound(0, 1500000, 300000, SessionOutcome.Completed, "walk", "look_away", "walk");
        await SaveRound(1, 2100000, 0, SessionOutcome.Skipped);
        await SaveRound(2, 300000, 60000, SessionOutcome.EndedEarly, "walk");
        await SaveRound(3, 420000, 120000, SessionOutcome.Active);

        var day = await new SqliteHistoryService(store).GetDayAsync(Day);
        Assert.Equal(4320000, day.WorkMs);
        Assert.Equal(480000, day.RestMs);
        Assert.Equal(new RoundDurationTotals(3900000, 3), day.WorkRounds);
        Assert.Equal(new RoundDurationTotals(360000, 2), day.RestRounds);
        Assert.Equal(2, day.ActivityCounts["walk"]);
        Assert.Equal(1, day.ActivityCounts["look_away"]);
        Assert.False(day.ActivityCounts.ContainsKey("other"));
    }
    [Fact] public async Task T28_CorruptAndNewerDatabasesArePreserved()
    {
        var file = Path.Combine(directory, "bad.db"); await File.WriteAllTextAsync(file, "not a sqlite database");
        Assert.False((await new SqliteWorkClockStore(file).OpenAndMigrateAsync()).Succeeded);
        Assert.Equal("not a sqlite database", await File.ReadAllTextAsync(file));
        var store = await Store();
        using (var c = new SqliteConnection($"Data Source={Path.Combine(directory, "test.db")};Pooling=False")) { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version=99"; cmd.ExecuteNonQuery(); }
        Assert.False((await new SqliteWorkClockStore(Path.Combine(directory, "test.db")).OpenAndMigrateAsync()).Succeeded);
        Assert.False((await store.OpenAndMigrateAsync()).Succeeded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync());
    }
    [Fact] public async Task T32_ExportDefaultsToNoNotesAndProtectsExplicitNoteText()
    {
        var store = await Store(); var id = Guid.NewGuid(); await store.CommitAsync(Batch(Session(id), Slice(id)));
        var exporter = new CsvHistoryExporter(store);
        var result = await exporter.ExportAsync(new(new(Day, Day), Path.Combine(directory, "export")), default);
        Assert.Equal(ExportStatus.Succeeded, result.Status); Assert.Equal(2, result.OutputFiles.Count);
        Assert.DoesNotContain("private note", await File.ReadAllTextAsync(result.OutputFiles[0]));
        var notes = await exporter.ExportAsync(new(new(Day, Day), Path.Combine(directory, "notes"), true), default);
        Assert.Contains("'=SUM(1,2)", await File.ReadAllTextAsync(notes.OutputFiles[0]));
        Assert.Equal(ExportStatus.Failed, (await exporter.ExportAsync(new(new(Day, Day), result.OutputDirectory!), default)).Status);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Equal(ExportStatus.Cancelled, (await exporter.ExportAsync(new(new(Day, Day), Path.Combine(directory, "cancel")), cancellation.Token)).Status);
        Assert.False(Directory.Exists(Path.Combine(directory, "cancel")));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
