namespace SandUp.Application;

public sealed class BuiltinActivityCatalog : IActivityCatalog
{
    private static readonly IReadOnlyList<ActivityDefinition> Items = Array.AsReadOnly(new[]
    {
        new ActivityDefinition("walk", "起身走动", "walk", "起身，走动一会儿", "平稳起身，放松肩膀，按舒适的步伐走动。"),
        new ActivityDefinition("look_away", "看向远处", "look_away", "把目光移向远处", "条件允许时看向约 6 米（20 英尺）外，自然眨眼。"),
        new ActivityDefinition("relax_shoulders_hands", "放松肩手", "relax_shoulders_hands", "轻松活动肩膀和双手", "放下肩膀，慢慢张开双手，再轻轻握拢。保持舒服的幅度，觉得疼就停。"),
        new ActivityDefinition("other", "其他活动", null, null, null)
    });
    public IReadOnlyList<ActivityDefinition> List() => Items;
}
