using System.Text.Json.Serialization;

namespace SandUp.Domain;

public sealed record CycleCombination(Guid Id, int Number, IReadOnlyList<CycleBlock> Blocks)
{
    // v1.11.0 stored one repeat count on the definition. New settings omit this field.
    [JsonPropertyName("Count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LegacyCount { get; init; }
}

// The editor saves links. The timer receives a resolved plan containing only timed blocks and repeats.
public sealed record CycleArrangement(IReadOnlyList<CycleBlock> Blocks, IReadOnlyList<CycleCombination> Combinations)
{
    public static CycleArrangement FromBlocks(IReadOnlyList<CycleBlock> blocks)
    {
        var combinations = new List<CycleCombination>();
        return new CycleArrangement(Convert(blocks), combinations);

        IReadOnlyList<CycleBlock> Convert(IReadOnlyList<CycleBlock> sequence)
        {
            var result = new List<CycleBlock>();
            foreach (var block in sequence)
            {
                if (block is not CycleRepeat repeat)
                {
                    result.Add(block);
                    continue;
                }
                var id = Guid.NewGuid();
                var index = combinations.Count;
                var definition = new CycleCombination(id, index + 1, []);
                combinations.Add(definition);
                combinations[index] = definition with { Blocks = Convert(repeat.Blocks) };
                result.Add(new CycleCombinationReference(id, repeat.Count));
            }
            return result;
        }
    }

    public CycleArrangement UpgradeLegacyCounts()
    {
        if (!Combinations.Any(item => item.LegacyCount != 0)) return this;
        var counts = Combinations.Where(item => item.LegacyCount != 0).ToDictionary(item => item.Id, item => item.LegacyCount);
        var number = Combinations.Max(item => item.Number);
        return new CycleArrangement(UpdateReferences(Blocks), Combinations.Select(item => item with
        {
            Number = item.Number == 0 ? ++number : item.Number,
            LegacyCount = 0,
            Blocks = UpdateReferences(item.Blocks)
        }).ToArray());

        IReadOnlyList<CycleBlock> UpdateReferences(IReadOnlyList<CycleBlock> sequence) => sequence.Select(block =>
            block is CycleCombinationReference { RepeatCount: 0 } link && counts.TryGetValue(link.Id, out var count)
                ? link with { RepeatCount = count }
                : block).ToArray();
    }

    public CycleArrangement Copy() => new(
        Array.AsReadOnly(Blocks.ToArray()),
        Array.AsReadOnly(Combinations.Select(item => item with { Blocks = Array.AsReadOnly(item.Blocks.ToArray()) }).ToArray()));

    public bool Matches(CyclePlan? plan) => plan is not null && Resolve() is { } resolved &&
        plan.SameAs(plan with { Blocks = resolved });

    public IReadOnlyList<CycleBlock>? Resolve()
    {
        if (Blocks is null || Blocks.Count == 0 || Combinations is null) return null;
        var definitions = new Dictionary<Guid, CycleCombination>();
        var numbers = new HashSet<int>();
        foreach (var item in Combinations)
        {
            if (item is null || item.Id == Guid.Empty || item.Number < 1 || item.LegacyCount != 0 ||
                !numbers.Add(item.Number) || !definitions.TryAdd(item.Id, item))
                return null;
        }

        var cache = new Dictionary<Guid, (IReadOnlyList<CycleBlock> Blocks, int Height)>();
        var visiting = new HashSet<Guid>();
        var (plan, _) = ResolveSequence(Blocks, 0);
        if (plan is null) return null;
        foreach (var id in definitions.Keys)
            if (ResolveCombination(id, 0).Blocks is null) return null;
        return plan;

        (IReadOnlyList<CycleBlock>? Blocks, int Height) ResolveSequence(IReadOnlyList<CycleBlock>? sequence, int depth)
        {
            if (sequence is null) return (null, 0);
            var result = new List<CycleBlock>(sequence.Count);
            var height = 0;
            foreach (var block in sequence)
            {
                if (block is CycleCombinationReference reference)
                {
                    if (reference.RepeatCount is < 1 or > CyclePlan.MaxRepeatCount) return (null, 0);
                    var resolved = ResolveCombination(reference.Id, depth);
                    if (resolved.Blocks is not { Count: > 0 } children) return (null, 0);
                    height = Math.Max(height, resolved.Height);
                    result.Add(new CycleRepeat(reference.RepeatCount, children));
                }
                else if (block is CycleWork { Seconds: >= 1 and <= 10800 } ||
                    block is CycleRest { Seconds: >= 1 and <= 1800, PromptId: "walk" or "look_away" or "relax_shoulders_hands" })
                {
                    result.Add(block);
                }
                else return (null, 0);
            }
            return (result, height);
        }

        (IReadOnlyList<CycleBlock>? Blocks, int Height) ResolveCombination(Guid id, int depth)
        {
            if (depth >= CyclePlan.MaxDepth) return (null, 0);
            if (cache.TryGetValue(id, out var saved))
                return depth + saved.Height <= CyclePlan.MaxDepth ? saved : (null, 0);
            if (!definitions.TryGetValue(id, out var combination) || !visiting.Add(id))
                return (null, 0);
            var result = ResolveSequence(combination.Blocks, depth + 1);
            visiting.Remove(id);
            if (result.Blocks is null) return (null, 0);
            cache.Add(id, (result.Blocks, result.Height + 1));
            return (result.Blocks, result.Height + 1);
        }
    }
}
