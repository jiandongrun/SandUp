namespace SandUp.Domain;

public enum BodyPart { Eyes, Shoulders, Back, Abdomen, Legs, Pelvic }

public sealed record ReminderRates(double Eyes, double Shoulders, double Back, double Abdomen, double Legs, double Pelvic)
{
    public static ReminderRates Default { get; } = new(12, 10, 8, 3, 9, 2);
    public double For(BodyPart part) => part switch
    {
        BodyPart.Eyes => Eyes, BodyPart.Shoulders => Shoulders, BodyPart.Back => Back,
        BodyPart.Abdomen => Abdomen, BodyPart.Legs => Legs, BodyPart.Pelvic => Pelvic,
        _ => throw new ArgumentOutOfRangeException(nameof(part))
    };
    public bool IsValid => Enum.GetValues<BodyPart>().All(part => double.IsFinite(For(part)) && For(part) is >= 0 and <= 40);
}
