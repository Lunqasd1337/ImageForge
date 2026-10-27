using ImageForge.Core.Planning;

namespace ImageForge.Core.Tests.Planning;

public sealed class StartRequestLedgerTests
{
    [Fact]
    public void First_request_gets_a_new_operation()
    {
        var operationId = Guid.NewGuid();
        var ledger = new StartRequestLedger(() => operationId);

        var outcome = ledger.Register(Guid.NewGuid(), Guid.NewGuid());

        outcome.ShouldBe(new StartRegistration(StartRegistrationKind.New, operationId));
    }

    [Fact]
    public void Repeating_the_same_request_returns_the_same_operation()
    {
        var ledger = new StartRequestLedger(Guid.NewGuid);
        var clientRequestId = Guid.NewGuid();
        var planId = Guid.NewGuid();

        var first = ledger.Register(clientRequestId, planId);
        var retry = ledger.Register(clientRequestId, planId);

        retry.Kind.ShouldBe(StartRegistrationKind.Duplicate);
        retry.OperationId.ShouldBe(first.OperationId);
    }

    [Fact]
    public void Reusing_a_client_request_id_for_another_plan_is_a_conflict()
    {
        var ledger = new StartRequestLedger(Guid.NewGuid);
        var clientRequestId = Guid.NewGuid();
        ledger.Register(clientRequestId, Guid.NewGuid());

        var outcome = ledger.Register(clientRequestId, Guid.NewGuid());

        outcome.Kind.ShouldBe(StartRegistrationKind.Conflict);
        outcome.OperationId.ShouldBe(Guid.Empty);
    }
}
