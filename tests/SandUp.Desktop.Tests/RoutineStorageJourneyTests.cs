using SandUp.Application;
using SandUp.Desktop;
using SandUp.Domain;
using SandUp.Infrastructure;
using System.IO;
using System.Text.Json;

namespace SandUp.Desktop.Tests;

public sealed class RoutineStorageJourneyTests
{
    [Fact]
    public async Task ThirdRoundResumesInLongBreakAndOnlyConfirmedActivitiesReachHistory()
    {
        var directory = Directory.CreateTempSubdirectory("SandUp-routine-");
        var database = Path.Combine(directory.FullName, "routine.db");
        var epoch = new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
        var run = new Journey(database, epoch);
        try
        {
            var plan = CyclePlan.Compose([
                new CycleRepeat(3, [new CycleWork(1200), new CycleRest(2, "look_away")]),
                new CycleRest(7, "walk")
            ], true);
            var id = Guid.NewGuid();
            var legacyJson = $$"""
                {"Blocks":[{"kind":"combination","Id":"{{id}}"},{"kind":"rest","Seconds":7,"PromptId":"walk"}],
                "Combinations":[{"Id":"{{id}}","Count":3,"Blocks":[{"kind":"work","Seconds":1200},{"kind":"rest","Seconds":2,"PromptId":"look_away"}]}]}
                """;
            var legacyArrangement = JsonSerializer.Deserialize<CycleArrangement>(legacyJson);
            Assert.True((await run.Store.OpenAndMigrateAsync()).Succeeded);
            var persisted = await run.Store.LoadAsync();
            var operation = Guid.NewGuid();
            var settings = persisted.Settings with { Settings = persisted.Settings.Settings with { CyclePlan = plan, Arrangement = legacyArrangement } };
            var batch = new AtomicWriteBatch(operation, persisted.StorageRevision, [], [], [], null, settings, null,
                new OperationReceipt(operation, null, null, persisted.StorageRevision + 1, null));
            Assert.Equal(CommitStatus.Committed, (await run.Store.CommitAsync(batch)).Status);
            var arrangement = Assert.IsType<CycleArrangement>((await run.App.InitializeAsync()).DefaultSettings.Arrangement);
            Assert.Equal(3, Assert.IsType<CycleCombinationReference>(arrangement.Blocks[0]).RepeatCount);
            var unused = new CycleCombination(Guid.NewGuid(), 2, []);
            await run.Accept(new StartSession());
            for (var round = 1; round <= 3; round++)
            {
                await run.Seconds(1);
                await run.Accept(new BreakNow());
                await run.WaitForRest();
                Assert.Equal("look_away", run.Request!.PromptId);
                Assert.Equal(round == 3 ? 9_000 : 2_000, run.Request.RemainingRestMs);
                await run.Seconds(2);
                if (round < 3)
                {
                    Assert.Equal(SessionPhase.AwaitingCheckIn, run.App.ReadSnapshot().Session.Phase);
                    await run.Accept(new SubmitCheckInAndContinue(run.Request.SessionId, []));
                    if (round == 1)
                    {
                        arrangement = arrangement with { Combinations = [.. arrangement.Combinations, unused] };
                        await run.Accept(new UpdateSettings(25, 5) { CyclePlan = plan, Arrangement = arrangement });
                    }
                }
            }

            var thirdRound = run.Request!.SessionId;
            Assert.Equal(SessionPhase.Resting, run.App.ReadSnapshot().Session.Phase);
            Assert.Equal("walk", run.Model!.PromptId);
            Assert.Equal(7_000, run.Model.RemainingRestMs);
            await run.Seconds(1);
            Assert.True((await run.App.ShutdownAsync(TimeSpan.FromSeconds(1))).Saved);

            // Reopen the same SQLite file with a fresh process clock, well after the saved rest.
            run = new Journey(database, epoch.AddHours(1));
            var reopened = await run.App.InitializeAsync();
            Assert.Equal(SessionPhase.Paused, reopened.Session.Phase);
            Assert.True(plan.SameAs(reopened.DefaultSettings.CyclePlan));
            var restored = Assert.IsType<CycleArrangement>(reopened.DefaultSettings.Arrangement);
            Assert.Equal(arrangement.Blocks, restored.Blocks);
            Assert.Equal(arrangement.Combinations.Select(item => item.Id), restored.Combinations.Select(item => item.Id));
            Assert.True(plan.SameAs(CyclePlan.Compose(restored.Resolve()!, true)));
            await run.Accept(new ResumeSession());
            await run.WaitForRest();
            Assert.Equal(thirdRound, run.Request!.SessionId);
            Assert.Equal("walk", run.Request.PromptId);
            Assert.Equal(6_000, run.Request.RemainingRestMs);
            await run.Seconds(6);
            Assert.Equal(SessionPhase.AwaitingCheckIn, run.App.ReadSnapshot().Session.Phase);
            await run.Accept(new SaveCheckInAndStay(thirdRound, ["walk"]));

            var day = await new SqliteHistoryService(run.Store).GetDayAsync(new DateOnly(2026, 11, 19));
            Assert.Equal(3, day.CompletedCount);
            Assert.Equal(3_000, day.WorkMs);
            Assert.Equal(13_000, day.RestMs);
            Assert.Equal(1, day.ActivityCounts["walk"]);
            Assert.Equal(0, day.ActivityCounts.GetValueOrDefault("look_away"));
            await run.Accept(new StartSession());
            await run.Accept(new BreakNow());
            await run.WaitForRest();
            Assert.Null(run.Request!.SegmentCaption);
            await run.Seconds(2);
            Assert.Equal(SessionPhase.AwaitingCheckIn, run.App.ReadSnapshot().Session.Phase);
        }
        finally
        {
            await run.App.ShutdownAsync(TimeSpan.FromSeconds(1));
            directory.Delete(recursive: true);
        }
    }

    // Real coordinator, clock reducer and storage. Only timer pulses and the monitor are simulated.
    private sealed class Journey : IClock, IPulseSource, IDisplayTopology, IBreakOverlayHost
    {
        private readonly Guid clockId = Guid.NewGuid();
        private readonly DateTimeOffset epoch;
        private long elapsed;
        private Action? tick;
        public SqliteWorkClockStore Store { get; }
        public WorkClockApplication App { get; }
        public OverlayRequest? Request { get; private set; }
        public OverlayViewModel? Model { get; private set; }

        public Journey(string database, DateTimeOffset epoch)
        {
            this.epoch = epoch;
            Store = new SqliteWorkClockStore(database);
            App = new WorkClockApplication(new SessionEngine(), this, this, Store, this, this);
        }

        ClockSample IClock.Read() => new(clockId, elapsed, 1000, epoch.AddMilliseconds(elapsed), "UTC");
        IReadOnlyList<DisplayInfo> IDisplayTopology.Read() => [new("test", true, new(0, 0, 800, 600), 96, 96)];
        public IDisposable Start(TimeSpan interval, Action callback)
        {
            tick = callback;
            return new Subscription(() => tick = null);
        }
        public Task<OverlayResult> ShowAsync(OverlayRequest request, CancellationToken cancellation)
        {
            Request = request;
            return Task.FromResult(new OverlayResult(request.BatchId, ["test"], [], true, null));
        }
        public void Update(OverlayViewModel model) => Model = model;
        public void CloseAll(Guid? batchId, OverlayCloseReason reason) { }
        public IDisposable SubscribeActions(Action<OverlayAction> callback) => new Subscription(() => { });

        private Task<CommandResult> Send(WorkClockCommand command) =>
            App.HandleAsync(new(Guid.NewGuid(), command, null, null));
        public async Task Accept(WorkClockCommand command) =>
            Assert.Equal(CommandDisposition.Accepted, (await Send(command)).Disposition);
        // A rejected command waits behind the queued pulse without changing persistent state.
        private Task Drain() => Send(new SelectPrompt("unknown"));
        public async Task Seconds(int seconds)
        {
            for (var step = 0; step < seconds; step++)
            {
                elapsed += 1000;
                tick!();
                await Drain();
            }
        }
        public async Task WaitForRest()
        {
            await Drain();
            Assert.Equal(SessionPhase.Resting, App.ReadSnapshot().Session.Phase);
        }
    }
}
