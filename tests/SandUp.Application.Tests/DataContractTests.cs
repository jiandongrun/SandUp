using SandUp.Application;
using SandUp.Domain;
namespace SandUp.Application.Tests;
public class DataContractTests
{
    [Fact] public void BatchDistinguishesEmptySelfReportFromNoChange()
    {
        var operation = Guid.NewGuid(); var session = Guid.NewGuid();
        var receipt = new OperationReceipt(operation, Guid.NewGuid(), session, 1, null);
        var unchanged = new AtomicWriteBatch(operation, 0, [], [], [], null, null, null, receipt);
        var clear = unchanged with { ActivityReplacements = [new ActivityReplacement(session, [])], Checkpoint = new ClearCheckpoint() };
        Assert.Empty(unchanged.ActivityReplacements);
        Assert.Null(unchanged.Checkpoint);
        Assert.Empty(Assert.Single(clear.ActivityReplacements).Activities);
        Assert.IsType<ClearCheckpoint>(clear.Checkpoint);
    }
    [Fact] public void EmergencyCommandIdentifiesOldSessionAndBatch()
    {
        var oldSession = Guid.NewGuid(); var oldBatch = Guid.NewGuid();
        WorkClockCommand command = new EmergencyExit(oldSession, oldBatch);
        var emergency = Assert.IsType<EmergencyExit>(command);
        Assert.Equal(oldSession, emergency.SessionId);
        Assert.Equal(oldBatch, emergency.OverlayBatchId);
    }
    [Fact] public void StorageAndViewRevisionsAreSeparateInputs()
    {
        var command = new CommandEnvelope(Guid.NewGuid(), new PauseSession(), Guid.NewGuid(), 7);
        var operation = Guid.NewGuid();
        var batch = new AtomicWriteBatch(operation, 11, [], [], [], null, null, null,
            new OperationReceipt(operation, command.CommandId, command.SessionId, 12, null));
        Assert.Equal(7, command.ExpectedViewRevision);
        Assert.Equal(11, batch.ExpectedStorageRevision);
        Assert.Equal(command.CommandId, batch.Receipt.CommandId);
    }
    [Fact] public void CheckpointContainsNoMonotonicAnchor()
    {
        var properties = typeof(RuntimeCheckpoint).GetProperties();
        Assert.DoesNotContain(properties, p => p.PropertyType == typeof(ClockSample));
        Assert.Contains(properties, p => p.Name == nameof(RuntimeCheckpoint.DelayUsed));
        Assert.Contains(properties, p => p.Name == nameof(RuntimeCheckpoint.RestElapsedMs));
    }
}

