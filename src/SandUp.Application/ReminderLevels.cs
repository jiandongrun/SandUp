using SandUp.Domain;
namespace SandUp.Application;

/// <summary>Continuous remaining durability, independent of the timing state.</summary>
public readonly record struct ReminderLevels(double Eyes, double Shoulders, double Back, double Abdomen, double Legs, double Pelvic)
{
    public const double Maximum = 100;
    public static ReminderLevels Full => new(100, 100, 100, 100, 100, 100);
    public double For(BodyPart part) => part switch
    {
        BodyPart.Eyes => Eyes, BodyPart.Shoulders => Shoulders, BodyPart.Back => Back,
        BodyPart.Abdomen => Abdomen, BodyPart.Legs => Legs, BodyPart.Pelvic => Pelvic,
        _ => throw new ArgumentOutOfRangeException(nameof(part))
    };
    public ReminderLevels Consume(long workMs, ReminderRates rates, ReminderLevels history)
    {
        var current = this;
        double Remaining(BodyPart part) => Math.Clamp(current.For(part) - Math.Max(0, workMs) / 3_600_000d * rates.For(part) *
            Math.Clamp(1 + (100 - history.For(part)) / 200, 1, 1.5), 0, Maximum);
        return new(Remaining(BodyPart.Eyes), Remaining(BodyPart.Shoulders), Remaining(BodyPart.Back),
            Remaining(BodyPart.Abdomen), Remaining(BodyPart.Legs), Remaining(BodyPart.Pelvic));
    }
    public ReminderLevels Restore(string activityId) => activityId switch
    {
        "look_away" => this with { Eyes = Math.Min(Maximum, Eyes + 12) },
        "relax_shoulders_hands" => this with { Shoulders = Math.Min(Maximum, Shoulders + 12) },
        "walk" => this with { Back = Math.Min(Maximum, Back + 6), Abdomen = Math.Min(Maximum, Abdomen + 3), Legs = Math.Min(Maximum, Legs + 12), Pelvic = Math.Min(Maximum, Pelvic + 4) },
        _ => this
    };
}
public sealed record ReminderDayState(ReminderLevels Levels, bool HasRecords)
{
    public ReminderLevels History { get; init; } = ReminderLevels.Full;
    public int HistoryDays { get; init; }
}
