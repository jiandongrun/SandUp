namespace SandUp.Domain;

/// <summary>Checks two samples in one process clock domain. Does not read a clock.</summary>
public static class ClockContinuity
{
    public static PauseReason? Check(ClockSample previous, ClockSample current)
    {
        if (previous.ProcessClockId != current.ProcessClockId || previous.Frequency <= 0 ||
            previous.Frequency != current.Frequency || current.MonotonicTicks < previous.MonotonicTicks)
            return PauseReason.TimingGap;
        if (!StringComparer.Ordinal.Equals(previous.TimeZoneId, current.TimeZoneId))
            return PauseReason.ClockChanged;

        // Decimal avoids overflow in subtraction and preserves sub-millisecond comparison.
        decimal monotonicMs = ((decimal)current.MonotonicTicks - previous.MonotonicTicks) * 1000m / previous.Frequency;
        decimal wallMs = (current.UtcNow - previous.UtcNow).Ticks / (decimal)TimeSpan.TicksPerMillisecond;
        if (Math.Abs(wallMs - monotonicMs) > 2000m)
            return PauseReason.ClockChanged;
        return monotonicMs > 3000m ? PauseReason.TimingGap : null;
    }
}
