using ImageForge.Core.Changes;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;

namespace ImageForge.Core.Tests.Planning;

public sealed class PlanStartValidatorTests
{
    private static ExecutionPlan Plan(long revision = 5) =>
        new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid)
            .Plan(new PlanRequest(Guid.NewGuid(), revision, Guid.NewGuid(), [ValidationMarker.M1]))
            .Plan!;

    [Fact]
    public void Accepts_the_shown_plan_at_the_current_revision()
    {
        var plan = Plan(5);

        PlanStartValidator.Validate(plan, new StartPlanRequest(plan.PlanId, 5, Guid.NewGuid()), currentWorkspaceRevision: 5)
            .ShouldBe(StartPlanRejection.None);
    }

    [Fact]
    public void Rejects_an_unknown_plan()
    {
        PlanStartValidator.Validate(null, new StartPlanRequest(Guid.NewGuid(), 5, Guid.NewGuid()), 5)
            .ShouldBe(StartPlanRejection.UnknownPlan);
    }

    [Fact]
    public void Rejects_a_request_for_a_different_plan_id()
    {
        var plan = Plan(5);

        PlanStartValidator.Validate(plan, new StartPlanRequest(Guid.NewGuid(), 5, Guid.NewGuid()), 5)
            .ShouldBe(StartPlanRejection.UnknownPlan);
    }

    [Fact]
    public void Rejects_when_the_client_saw_another_revision()
    {
        var plan = Plan(5);

        PlanStartValidator.Validate(plan, new StartPlanRequest(plan.PlanId, 4, Guid.NewGuid()), 5)
            .ShouldBe(StartPlanRejection.ExpectedRevisionMismatch);
    }

    [Fact]
    public void Rejects_a_plan_made_before_the_workspace_changed()
    {
        var plan = Plan(5);

        PlanStartValidator.Validate(plan, new StartPlanRequest(plan.PlanId, 5, Guid.NewGuid()), 6)
            .ShouldBe(StartPlanRejection.StalePlan);
    }

    [Fact]
    public void Rejects_a_plan_whose_content_no_longer_matches_its_fingerprint()
    {
        var plan = Plan(5);
        var tampered = plan with
        {
            Steps = [.. plan.Steps.Select(s => s is SetOfflineRegistryValue set ? set with { Value = RegistryValue.Sz("X") } : s)],
        };

        PlanStartValidator.Validate(tampered, new StartPlanRequest(plan.PlanId, 5, Guid.NewGuid()), 5)
            .ShouldBe(StartPlanRejection.IntegrityMismatch);
    }
}
