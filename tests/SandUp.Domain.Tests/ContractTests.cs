using SandUp.Domain;
namespace SandUp.Domain.Tests;
public class ContractTests
{
    [Fact] public void DomainDoesNotReferenceDesktopOrSqlite()
    {
        var references = typeof(SessionState).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToArray();
        Assert.DoesNotContain(references, x => x != null && (x.StartsWith("Presentation") || x.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)));
    }
    [Fact] public void OutcomeAndCheckInAreIndependentTypes()
    {
        Assert.Contains(SessionOutcome.Completed, Enum.GetValues<SessionOutcome>());
        Assert.Contains(CheckInStatus.Unrecorded, Enum.GetValues<CheckInStatus>());
    }
}
