using SandUp.Domain;

namespace SandUp.Desktop;

internal static class BodyPartDisplay
{
    public static IReadOnlyList<BodyPart> Order { get; } = Array.AsReadOnly(Enum.GetValues<BodyPart>());

    public static string Name(BodyPart part) => part switch
    {
        BodyPart.Eyes => "眼睛",
        BodyPart.Shoulders => "肩颈",
        BodyPart.Back => "腰背",
        BodyPart.Abdomen => "腹部",
        BodyPart.Legs => "臀腿",
        BodyPart.Pelvic => "肛门·前列腺",
        _ => throw new ArgumentOutOfRangeException(nameof(part))
    };
}
