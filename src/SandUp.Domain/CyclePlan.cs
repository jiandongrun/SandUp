using System.Text.Json.Serialization;

namespace SandUp.Domain;

public sealed record CycleLevel(int Repeats, int RestSeconds, string PromptId);
public sealed record CycleSegment(long DurationMs, string PromptId);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CycleWork), "work")]
[JsonDerivedType(typeof(CycleRest), "rest")]
[JsonDerivedType(typeof(CycleRepeat), "repeat")]
[JsonDerivedType(typeof(CycleCombinationReference), "combination")]
public abstract record CycleBlock;

public sealed record CycleWork(int Seconds) : CycleBlock;
public sealed record CycleRest(int Seconds, string PromptId) : CycleBlock;
public sealed record CycleRepeat(int Count, IReadOnlyList<CycleBlock> Blocks) : CycleBlock;
public sealed record CycleCombinationReference(Guid Id, int RepeatCount) : CycleBlock;
public sealed record CycleRound(long WorkMs, IReadOnlyList<CycleSegment> Rests);
public sealed record CycleSchedule(IReadOnlyList<CycleRound> Rounds, int RepeatStart);

/// <summary>Levels run from the innermost repeat to the outermost repeat.</summary>
public sealed record CyclePlan(int WorkMinutes, int ShortRestSeconds, string ShortPromptId, IReadOnlyList<CycleLevel> Levels)
{
    public const int MaxDepth = 4;
    public const int MaxSteps = 512;
    public const int MaxRepeatCount = 20;
    public IReadOnlyList<CycleBlock>? Blocks { get; init; }
    public bool MergeAdjacentRests { get; init; }
    // A missing value keeps the round indices of checkpoints from before work merging.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MergeAdjacentWork { get; init; }

    public static CyclePlan Compose(IReadOnlyList<CycleBlock> blocks, bool mergeAdjacentModules = false) =>
        new(0, 0, "", []) { Blocks = blocks, MergeAdjacentRests = mergeAdjacentModules, MergeAdjacentWork = mergeAdjacentModules };

    public static CyclePlan EyeCare => new(20, 20, "look_away", [new(3, 600, "walk")]);
    private static bool Prompt(string? id) => id is "walk" or "look_away" or "relax_shoulders_hands";
    public bool IsValid()
    {
        if (Blocks is not null) return Compile() is not null;
        if (WorkMinutes is < 1 or > 180 || ShortRestSeconds is < 1 or > 1800 || !Prompt(ShortPromptId) || Levels is null || Levels.Count > 4) return false;
        long rounds = 1;
        foreach (var level in Levels)
        {
            if (level is null || level.Repeats is < 2 or > 20 || level.RestSeconds is < 1 or > 1800 || !Prompt(level.PromptId)) return false;
            rounds *= level.Repeats;
            if (rounds > int.MaxValue) return false;
        }
        return true;
    }
    [JsonIgnore]
    public long RoundCount => Blocks is not null ? Compile()?.Rounds.Count ?? 0 :
        IsValid() ? Levels.Aggregate(1L, (n, level) => n * level.Repeats) : 0;

    public IReadOnlyList<CycleBlock> EditorBlocks()
    {
        if (Blocks is not null) return Blocks;
        IReadOnlyList<CycleBlock> blocks = [new CycleWork(WorkMinutes * 60), new CycleRest(ShortRestSeconds, ShortPromptId)];
        foreach (var level in Levels)
            blocks = [new CycleRepeat(level.Repeats, blocks), new CycleRest(level.RestSeconds, level.PromptId)];
        return blocks;
    }

    public CyclePlan Copy() => this with
    {
        Levels = Array.AsReadOnly(Levels.ToArray()),
        Blocks = Blocks is null ? null : CopyBlocks(Blocks)
    };

    private static IReadOnlyList<CycleBlock> CopyBlocks(IReadOnlyList<CycleBlock> blocks) =>
        Array.AsReadOnly(blocks.Select(block => block is CycleRepeat group ? group with { Blocks = CopyBlocks(group.Blocks) } : block).ToArray());

    public bool SameAs(CyclePlan? other) =>
        other is not null && MergeAdjacentRests == other.MergeAdjacentRests &&
        MergeAdjacentWork.GetValueOrDefault() == other.MergeAdjacentWork.GetValueOrDefault() &&
        SameBlocks(EditorBlocks(), other.EditorBlocks());

    private static bool SameBlocks(IReadOnlyList<CycleBlock> left, IReadOnlyList<CycleBlock> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair is (CycleRepeat a, CycleRepeat b) ? a.Count == b.Count && SameBlocks(a.Blocks, b.Blocks) : pair.First == pair.Second);

    // Preserve the edited sequence before combining consecutive or wraparound modules.
    public IReadOnlyList<CycleBlock>? ExpandTimedBlocks()
    {
        if (Blocks is null) return null;
        var expanded = new List<CycleBlock>();
        return Expand(Blocks, 0, expanded) ? expanded : null;
    }

    public CycleSchedule? Compile()
    {
        if (ExpandTimedBlocks() is not { } expanded) return null;
        var rounds = new List<CycleRound>();
        var rests = new List<CycleSegment>();
        long workMs = 0;
        foreach (var block in expanded)
        {
            if (block is CycleRest rest)
            {
                rests.Add(new CycleSegment(rest.Seconds * 1000L, rest.PromptId));
                continue;
            }
            var duration = ((CycleWork)block).Seconds * 1000L;
            if (MergeAdjacentWork == true && rests.Count == 0)
            {
                workMs += duration;
                continue;
            }
            if (workMs > 0 || rests.Count > 0) rounds.Add(new CycleRound(workMs, rests.ToArray()));
            rests.Clear();
            workMs = duration;
        }
        rounds.Add(new CycleRound(workMs, rests.ToArray()));

        // On the first run play an opening rest once. On subsequent laps it connects to
        // the closing rest and is played there, so there is only one countdown at the seam.
        if (rounds.Count > 1 && rounds[0].WorkMs == 0 && (MergeAdjacentRests || rounds[^1].Rests.Count == 0))
        {
            rounds[^1] = rounds[^1] with { Rests = rounds[^1].Rests.Concat(rounds[0].Rests).ToArray() };
            return new CycleSchedule(rounds, RepeatStart: 1);
        }
        // Keep the opening round for the first lap. Later laps play it after the closing work.
        if (MergeAdjacentWork == true && rounds.Count > 1 && rounds[0].WorkMs > 0 && rounds[^1].Rests.Count == 0)
        {
            rounds[^1] = new CycleRound(rounds[^1].WorkMs + rounds[0].WorkMs, rounds[0].Rests);
            return new CycleSchedule(rounds, RepeatStart: 1);
        }
        return new CycleSchedule(rounds, RepeatStart: 0);
    }

    private static bool Expand(IReadOnlyList<CycleBlock>? blocks, int depth, List<CycleBlock> expanded)
    {
        if (blocks is null || blocks.Count == 0) return false;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case CycleRepeat group when depth < MaxDepth && group.Count is >= 1 and <= MaxRepeatCount:
                    for (var i = 0; i < group.Count; i++)
                        if (!Expand(group.Blocks, depth + 1, expanded)) return false;
                    break;
                case CycleWork { Seconds: >= 1 and <= 10800 }:
                case CycleRest { Seconds: >= 1 and <= 1800 } rest when Prompt(rest.PromptId):
                    if (expanded.Count == MaxSteps) return false;
                    expanded.Add(block);
                    break;
                default:
                    return false;
            }
        }
        return true;
    }
    public IReadOnlyList<CycleSegment> RestSegments(long completedRounds)
    {
        var segments = new List<CycleSegment> { new(ShortRestSeconds * 1000L, ShortPromptId) };
        long divisor = 1;
        foreach (var level in Levels)
        {
            divisor = checked(divisor * level.Repeats);
            if ((completedRounds + 1) % divisor != 0) break;
            segments.Add(new(level.RestSeconds * 1000L, level.PromptId));
        }
        return segments;
    }
}
