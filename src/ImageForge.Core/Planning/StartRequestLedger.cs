namespace ImageForge.Core.Planning;

public enum StartRegistrationKind
{
    New,
    Duplicate,
    Conflict,
}

public readonly record struct StartRegistration(StartRegistrationKind Kind, Guid OperationId);

/// <summary>Maps ClientRequestId to the operation it started, so a start retried after a lost connection does not run twice.</summary>
public sealed class StartRequestLedger(Func<Guid> newOperationId)
{
    private readonly Dictionary<Guid, (Guid PlanId, Guid OperationId)> _started = [];
    private readonly Lock _gate = new();

    public StartRegistration Register(Guid clientRequestId, Guid planId)
    {
        lock (_gate)
        {
            if (_started.TryGetValue(clientRequestId, out var existing))
            {
                return existing.PlanId == planId
                    ? new StartRegistration(StartRegistrationKind.Duplicate, existing.OperationId)
                    : new StartRegistration(StartRegistrationKind.Conflict, Guid.Empty);
            }

            var operationId = newOperationId();
            _started.Add(clientRequestId, (planId, operationId));
            return new StartRegistration(StartRegistrationKind.New, operationId);
        }
    }
}
