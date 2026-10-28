namespace ImageForge.Core.Planning;

/// <summary>Start request from the UI. ClientRequestId makes a retried request idempotent.</summary>
public sealed record StartPlanRequest(Guid PlanId, long ExpectedWorkspaceRevision, Guid ClientRequestId);

public enum StartPlanRejection
{
    None,
    UnknownPlan,
    ExpectedRevisionMismatch,
    StalePlan,
    IntegrityMismatch,

    /// <summary>No workspace is open in the engine.</summary>
    NotOpen,

    /// <summary>Another run is in progress.</summary>
    Busy,

    /// <summary>The ClientRequestId was already used for a different plan.</summary>
    RequestConflict,
}

/// <summary>Ensures the engine executes exactly the plan the UI showed, against the workspace state it was made for.</summary>
public static class PlanStartValidator
{
    public static StartPlanRejection Validate(ExecutionPlan? plan, StartPlanRequest request, long currentWorkspaceRevision)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (plan is null || plan.PlanId != request.PlanId)
        {
            return StartPlanRejection.UnknownPlan;
        }

        if (request.ExpectedWorkspaceRevision != plan.WorkspaceRevision)
        {
            return StartPlanRejection.ExpectedRevisionMismatch;
        }

        if (currentWorkspaceRevision != plan.WorkspaceRevision)
        {
            return StartPlanRejection.StalePlan;
        }

        return PlanFingerprint.Compute(plan) == plan.Fingerprint
            ? StartPlanRejection.None
            : StartPlanRejection.IntegrityMismatch;
    }
}
