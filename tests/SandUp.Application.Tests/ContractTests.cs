using SandUp.Application;
using SandUp.Domain;
namespace SandUp.Application.Tests;
public class ContractTests
{
    [Fact] public void EmptyActivityReplacementAndCheckpointClearAreExplicit()
    {
        var id = Guid.NewGuid();
        var receipt = new OperationReceipt(Guid.NewGuid(), null, id, 4, null);
        var batch = new AtomicWriteBatch(receipt.OperationId, 3, [], [],
            [new ActivityReplacement(id, [])], null, null, new ClearCheckpoint(), receipt);
        Assert.Empty(Assert.Single(batch.ActivityReplacements).Activities);
        Assert.IsType<ClearCheckpoint>(batch.Checkpoint);
        Assert.Null(batch.Draft);
        Assert.Equal(batch.OperationId, batch.Receipt.OperationId);
    }
    [Fact] public void EmptyCheckInIsAnExplicitCommandRatherThanCompletion()
    {
        WorkClockCommand command = new SubmitCheckInAndContinue(Guid.NewGuid(), []);
        Assert.Empty(Assert.IsType<SubmitCheckInAndContinue>(command).ActivityIds);
        Assert.NotEqual(CheckInStatus.Reported, CheckInStatus.Unrecorded);
    }
    [Fact] public void CheckpointDoesNotContainProcessClockSample()
    {
        Assert.DoesNotContain(typeof(RuntimeCheckpoint).GetProperties(), p => p.PropertyType == typeof(ClockSample));
        Assert.NotEqual(typeof(SetCheckpoint), typeof(ClearCheckpoint));
    }
}
