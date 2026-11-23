using SandUp.Domain;
namespace SandUp.Domain.Tests;
public class EventContractTests
{
    [Fact] public void StaleOverlayFeedbackCarriesBatchIdentity()
    {
        var batch = Guid.NewGuid();
        SessionEvent shown = new OverlayShownEvent(batch, Guid.NewGuid());
        Assert.Equal(batch, Assert.IsType<OverlayShownEvent>(shown).OverlayBatchId);
        SessionEffect close = new CloseOverlayEffect(batch);
        Assert.Equal(batch, Assert.IsType<CloseOverlayEffect>(close).BatchId);
    }
    [Fact] public void InterruptionCarriesClockTrustAndReason()
    {
        SessionEvent signal = new SystemInterruptedEvent(PauseReason.TimingGap, false);
        var interruption = Assert.IsType<SystemInterruptedEvent>(signal);
        Assert.False(interruption.TimestampTrusted);
        Assert.Equal(PauseReason.TimingGap, interruption.Reason);
    }
    [Fact] public void StateCanCarryTheCurrentSliceWithoutReadingAStore()
    {
        var fields = typeof(SessionState).GetProperties();
        Assert.Contains(fields, p => p.Name == nameof(SessionState.CurrentSlice) && p.PropertyType == typeof(TimeSlice));
        Assert.Contains(fields, p => p.Name == nameof(SessionState.SubMillisecondTicks));
        Assert.Contains(fields, p => p.Name == nameof(SessionState.NextSliceSequence));
    }
}
