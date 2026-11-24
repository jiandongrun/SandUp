using System.Windows;

namespace SandUp.Desktop;

internal sealed class SandSlosh
{
    internal const double MaximumSlope = .075;
    private const double HoldSeconds = .045;
    private const double SettleRate = .31;
    private double horizontalLoad;
    private double verticalLoad;
    private double holdRemaining;

    public Vector Surface { get; private set; }
    public bool Moving => Surface.LengthSquared > 0 || horizontalLoad != 0 || verticalLoad != 0;

    public void Push(Vector windowMovement)
    {
        var horizontal = Release(ref horizontalLoad, windowMovement.X, 12);
        var vertical = Release(ref verticalLoad, windowMovement.Y, 15) * .55;
        if (horizontal == 0 && vertical == 0) return;

        Surface = new(KeepShift(Surface.X, horizontal), KeepShift(Surface.Y, vertical));
        holdRemaining = HoldSeconds;
    }

    public void Advance(TimeSpan elapsed)
    {
        var seconds = elapsed.TotalSeconds;
        var decay = Math.Exp(-14 * seconds);
        horizontalLoad = FadeLoad(horizontalLoad * decay);
        verticalLoad = FadeLoad(verticalLoad * decay);

        var settle = Math.Max(0, seconds - holdRemaining) * SettleRate;
        holdRemaining = Math.Max(0, holdRemaining - seconds);
        Surface = new(Settle(Surface.X, settle), Settle(Surface.Y, settle));
    }

    private static double Release(ref double load, double movement, double threshold)
    {
        load += movement;
        var force = Math.Abs(load);
        if (force < threshold) return 0;

        // A pile resists small shifts until it slips once, then settles without a rebound.
        var shift = Math.CopySign(Math.Min(MaximumSlope, .024 + (force - threshold) * .003), load);
        load = 0;
        return shift;
    }

    private static double KeepShift(double current, double shift) =>
        shift == 0 || Math.Sign(current) == Math.Sign(shift) && Math.Abs(current) > Math.Abs(shift) ? current : shift;

    private static double FadeLoad(double load) => Math.Abs(load) < .1 ? 0 : load;

    private static double Settle(double value, double amount) =>
        Math.CopySign(Math.Max(0, Math.Abs(value) - amount), value);

    public void Reset()
    {
        Surface = default;
        horizontalLoad = 0;
        verticalLoad = 0;
        holdRemaining = 0;
    }
}
