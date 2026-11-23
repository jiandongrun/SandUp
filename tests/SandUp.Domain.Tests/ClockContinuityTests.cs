using SandUp.Domain;
namespace SandUp.Domain.Tests;
public class ClockContinuityTests
{
    private static readonly Guid Domain = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static ClockSample Sample(long milliseconds, long wallMilliseconds, string zone = "China Standard Time") =>
        new(Domain, milliseconds, 1000, Epoch.AddMilliseconds(wallMilliseconds), zone);

    [Fact] public void T20_UnknownTenMinuteGapIsNotTrusted()
    {
        Assert.Equal(PauseReason.TimingGap, ClockContinuity.Check(Sample(0, 0), Sample(600_000, 600_000)));
    }
    [Theory]
    [InlineData(3600000)] [InlineData(-3600000)]
    public void T19_WallClockChangeIsNotElapsedTime(int wallShift)
    {
        Assert.Equal(PauseReason.ClockChanged, ClockContinuity.Check(Sample(0, 0), Sample(250, 250 + wallShift)));
    }
    [Fact] public void ExactThresholdsRemainTrusted()
    {
        Assert.Null(ClockContinuity.Check(Sample(0, 0), Sample(3000, 5000)));
        Assert.Equal(PauseReason.TimingGap, ClockContinuity.Check(Sample(0, 0), Sample(3001, 3001)));
        Assert.Equal(PauseReason.ClockChanged, ClockContinuity.Check(Sample(0, 0), Sample(250, 2251)));
    }
    [Fact] public void DifferentProcessClockOrReversedClockCannotBeCompared()
    {
        Assert.Equal(PauseReason.TimingGap, ClockContinuity.Check(Sample(10, 10), Sample(9, 9)));
        var other = Sample(10, 10) with { ProcessClockId = Guid.NewGuid() };
        Assert.Equal(PauseReason.TimingGap, ClockContinuity.Check(Sample(0, 0), other));
    }
    [Fact] public void TimeZoneChangePausesEvenIfTimeDeltasMatch()
    {
        Assert.Equal(PauseReason.ClockChanged, ClockContinuity.Check(Sample(0, 0), Sample(250, 250, "UTC")));
    }
}
