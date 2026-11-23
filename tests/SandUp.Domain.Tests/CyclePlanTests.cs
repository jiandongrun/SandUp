using System.Text.Json;
using SandUp.Domain;

namespace SandUp.Domain.Tests;

public class CyclePlanTests
{
    [Fact] public void NestedRepeatsAppendEveryDueRestInOrder()
    {
        var plan = new CyclePlan(20, 20, "look_away", [new(3, 600, "walk"), new(2, 1200, "relax_shoulders_hands")]);
        Assert.True(plan.IsValid());
        Assert.Equal(6, plan.RoundCount);
        Assert.Single(plan.RestSegments(0));
        Assert.Equal(new long[] { 20000, 600000 }, plan.RestSegments(2).Select(x => x.DurationMs));
        Assert.Equal(new long[] { 20000, 600000, 1200000 }, plan.RestSegments(5).Select(x => x.DurationMs));
        Assert.Single(plan.RestSegments(6));
    }
    [Fact] public void BoundsAndOldSettingsRemainCompatible()
    {
        Assert.True(new CyclePlan(1, 1, "walk", []).IsValid());
        Assert.True(new CyclePlan(180, 1800, "walk", Enumerable.Repeat(new CycleLevel(20, 1800, "walk"), 4).ToArray()).IsValid());
        Assert.False((CyclePlan.EyeCare with { WorkMinutes = 0 }).IsValid());
        Assert.False((CyclePlan.EyeCare with { ShortRestSeconds = 1801 }).IsValid());
        Assert.False((CyclePlan.EyeCare with { ShortPromptId = "unknown" }).IsValid());
        Assert.False((CyclePlan.EyeCare with { Levels = [new(1, 1, "walk")] }).IsValid());
        Assert.False((CyclePlan.EyeCare with { Levels = Enumerable.Repeat(new CycleLevel(20, 1, "walk"), 5).ToArray() }).IsValid());
        var old = JsonSerializer.Deserialize<SettingsSnapshot>("{\"WorkDurationMs\":1500000,\"RestDurationMs\":300000,\"NoticeDurationMs\":20000,\"DelayOptionsMs\":[180000,300000],\"MaxDelayCount\":1}")!;
        Assert.Null(old.CyclePlan);
        var roundtrip = JsonSerializer.Deserialize<CyclePlan>(JsonSerializer.Serialize(CyclePlan.EyeCare))!;
        Assert.True(roundtrip.SameAs(CyclePlan.EyeCare));
    }

    [Fact]
    public void CustomSchedulePreservesOrderAndJoinsTheRepeatBoundary()
    {
        var plan = CyclePlan.Compose([
            new CycleRest(2, "walk"),
            new CycleRepeat(2, [new CycleWork(8), new CycleWork(19), new CycleRest(7, "look_away")]),
            new CycleRest(4, "relax_shoulders_hands")
        ], true);
        var schedule = Assert.IsType<CycleSchedule>(plan.Compile());
        Assert.Equal(1, schedule.RepeatStart);
        Assert.Equal(new long[] { 0, 27000, 27000 }, schedule.Rounds.Select(round => round.WorkMs));
        Assert.Equal(7000, Assert.Single(schedule.Rounds[1].Rests).DurationMs);
        Assert.Equal(new long[] { 7000, 4000, 2000 }, schedule.Rounds[^1].Rests.Select(rest => rest.DurationMs));
        Assert.Equal(2_000, Assert.Single(schedule.Rounds[0].Rests).DurationMs);
        var separated = (plan with { MergeAdjacentRests = false, MergeAdjacentWork = false }).Compile()!;
        Assert.Equal(0, separated.RepeatStart);
        Assert.Equal(new long[] { 0, 8000, 19000, 8000, 19000 }, separated.Rounds.Select(round => round.WorkMs));
        Assert.Equal(2, separated.Rounds[^1].Rests.Count);
        Assert.True(plan.SameAs(JsonSerializer.Deserialize<CyclePlan>(JsonSerializer.Serialize(plan))));
        Assert.True(CyclePlan.EyeCare.SameAs(CyclePlan.Compose(CyclePlan.EyeCare.EditorBlocks())));
        Assert.False(CyclePlan.Compose([new CycleRepeat(20, [new CycleRepeat(20, [new CycleRepeat(2, [new CycleWork(1)])])])]).IsValid());
    }

    [Fact]
    public void ClosingWorkMergesWithNextLapAndAllWorkPlansKeepFiniteLaps()
    {
        var plan = CyclePlan.Compose([new CycleWork(25), new CycleRest(5, "walk"), new CycleWork(15)], true);
        var schedule = plan.Compile()!;
        Assert.Equal(1, schedule.RepeatStart);
        Assert.Equal(new long[] { 25_000, 40_000 }, schedule.Rounds.Select(round => round.WorkMs));
        Assert.All(schedule.Rounds, round => Assert.Equal(5000, Assert.Single(round.Rests).DurationMs));

        var legacy = JsonSerializer.Deserialize<CyclePlan>(JsonSerializer.Serialize(plan with { MergeAdjacentWork = null }))!;
        Assert.Null(legacy.MergeAdjacentWork);
        Assert.Equal(0, legacy.Compile()!.RepeatStart);
        Assert.Equal(new long[] { 25_000, 15_000 }, legacy.Compile()!.Rounds.Select(round => round.WorkMs));

        schedule = CyclePlan.Compose([new CycleWork(10800), new CycleWork(10800)], true).Compile()!;
        Assert.Equal(0, schedule.RepeatStart);
        Assert.Equal(21_600_000, Assert.Single(schedule.Rounds).WorkMs);
    }

    [Fact]
    public void ReusableCombinationsResolveEveryPlacementAndRejectInvalidLinks()
    {
        var inner = new CycleCombination(Guid.NewGuid(), 1, [new CycleWork(25), new CycleRest(5, "walk")]);
        var innerLink = new CycleCombinationReference(inner.Id, 1);
        var outer = new CycleCombination(Guid.NewGuid(), 2, [innerLink, new CycleRest(8, "look_away")]);
        var outerLink = new CycleCombinationReference(outer.Id, 3);
        var empty = new CycleCombination(Guid.NewGuid(), 3, []);
        var layout = new CycleArrangement([innerLink with { RepeatCount = 2 }, outerLink], [outer, inner, empty]);
        var plan = CyclePlan.Compose(Assert.IsAssignableFrom<IReadOnlyList<CycleBlock>>(layout.Resolve()), true);
        Assert.Equal(Enumerable.Repeat(25_000L, 5), plan.Compile()!.Rounds.Select(round => round.WorkMs));

        var edited = layout with { Combinations = [outer, inner with { Blocks = [new CycleWork(42)] }] };
        var newPlan = CyclePlan.Compose(edited.Resolve()!);
        Assert.All(newPlan.Compile()!.Rounds, round => Assert.Equal(42_000, round.WorkMs));
        Assert.Null((layout with { Combinations = [outer, inner with { Blocks = [outerLink] }] }).Resolve());
        Assert.Null((layout with { Combinations = [outer] }).Resolve());
        Assert.Null((layout with { Combinations = [outer, inner, inner] }).Resolve());
        Assert.Null((layout with { Blocks = [new CycleCombinationReference(empty.Id, 1)] }).Resolve());
        Assert.Null((layout with { Blocks = [innerLink with { RepeatCount = 0 }] }).Resolve());
        Assert.Null((layout with { Blocks = [innerLink with { RepeatCount = CyclePlan.MaxRepeatCount + 1 }] }).Resolve());

        var definitions = new List<CycleCombination> { inner };
        for (var i = 0; i < CyclePlan.MaxDepth; i++)
            definitions.Add(new CycleCombination(Guid.NewGuid(), i + 2, [new CycleCombinationReference(definitions[^1].Id, 1)]));
        Assert.Null(new CycleArrangement([innerLink, new CycleCombinationReference(definitions[^1].Id, 1)], definitions).Resolve());
    }

    [Fact]
    public void LegacyLibraryCountsMoveToEachReferenceWithoutChangingThePlan()
    {
        var inner = Guid.NewGuid();
        var outer = Guid.NewGuid();
        var json = $$"""
            {"Blocks":[{"kind":"combination","Id":"{{inner}}"},{"kind":"combination","Id":"{{outer}}"}],"Combinations":[
            {"Id":"{{inner}}","Count":3,"Blocks":[{"kind":"work","Seconds":25}]},
            {"Id":"{{outer}}","Count":2,"Blocks":[{"kind":"combination","Id":"{{inner}}"},{"kind":"rest","Seconds":5,"PromptId":"walk"}]}
            ]}
            """;
        var layout = JsonSerializer.Deserialize<CycleArrangement>(json)!.UpgradeLegacyCounts();
        Assert.Equal(new[] { 1, 2 }, layout.Combinations.Select(item => item.Number));
        var innerBlock = new CycleRepeat(3, [new CycleWork(25)]);
        var plan = CyclePlan.Compose([innerBlock, new CycleRepeat(2, [innerBlock, new CycleRest(5, "walk")])]);
        Assert.True(layout.Matches(plan));
        var saved = JsonSerializer.Serialize(layout);
        Assert.DoesNotContain("\"Count\"", saved);
        Assert.True(JsonSerializer.Deserialize<CycleArrangement>(saved)!.UpgradeLegacyCounts().Matches(plan));
    }
}
