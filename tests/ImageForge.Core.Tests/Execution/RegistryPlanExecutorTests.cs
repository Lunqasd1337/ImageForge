using ImageForge.Core.Changes;
using ImageForge.Core.Execution;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;

namespace ImageForge.Core.Tests.Execution;

public sealed class RegistryPlanExecutorTests
{
    private const string HivePath = @"D:\Work\mount\img\Windows\System32\config\SOFTWARE";

    private readonly FakeHiveLoader _loader = new();

    private static ExecutionPlan Plan(params ImageChange[] changes) =>
        new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid)
            .Plan(new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), changes.Length == 0 ? [ValidationMarker.M1] : changes))
            .Plan!;

    private RegistryPlanExecutor Executor() => new(_loader, hiveFile => @"D:\Work\mount\img\" + hiveFile);

    [Fact]
    public void Executes_load_set_unload_in_order()
    {
        var report = Executor().Execute(Plan(), TestContext.Current.CancellationToken);

        report.Outcome.ShouldBe(ExecutionOutcome.Completed);
        report.CompletedSteps.ShouldBe(3);
        report.RequiresRecovery.ShouldBeFalse();
        _loader.Log.ShouldBe(
        [
            $"load Software {HivePath}",
            @"set Software ImageForge\Validation Milestone=M1",
            "unload Software",
        ]);
    }

    [Fact]
    public void A_failed_write_still_unloads_the_hive_and_stops()
    {
        _loader.FailOnSet = true;
        var second = new SetRegistryValue(@"HKLM\SOFTWARE\B", "x", RegistryValue.Sz("y"));

        var report = Executor().Execute(Plan(ValidationMarker.M1, second), TestContext.Current.CancellationToken);

        report.Outcome.ShouldBe(ExecutionOutcome.Failed);
        report.FailedStepIndex.ShouldBe(1);
        report.Error.ShouldNotBeNull().ShouldContain("set failed");
        report.RequiresRecovery.ShouldBeFalse();
        _loader.Log.Count(l => l.StartsWith("set", StringComparison.Ordinal)).ShouldBe(1);
        _loader.Log[^1].ShouldBe("unload Software");
    }

    [Fact]
    public void A_failed_load_writes_nothing()
    {
        _loader.FailOnLoad = true;

        var report = Executor().Execute(Plan(), TestContext.Current.CancellationToken);

        report.Outcome.ShouldBe(ExecutionOutcome.Failed);
        report.FailedStepIndex.ShouldBe(0);
        report.CompletedSteps.ShouldBe(0);
        _loader.Log.ShouldBeEmpty();
    }

    [Fact]
    public void A_hive_that_cannot_be_unloaded_requires_recovery()
    {
        _loader.FailOnUnload = true;

        var report = Executor().Execute(Plan(), TestContext.Current.CancellationToken);

        report.Outcome.ShouldBe(ExecutionOutcome.Failed);
        report.FailedStepIndex.ShouldBe(2);
        report.RequiresRecovery.ShouldBeTrue();
        report.HivesLeftLoaded.ShouldBe([OfflineHive.Software]);
    }

    [Fact]
    public void Cancellation_before_a_write_unloads_and_reports_cancelled()
    {
        using var cts = new CancellationTokenSource();
        _loader.OnLoad = cts.Cancel;

        var report = Executor().Execute(Plan(), cts.Token);

        report.Outcome.ShouldBe(ExecutionOutcome.Cancelled);
        report.CompletedSteps.ShouldBe(1);
        _loader.Log.ShouldBe([$"load Software {HivePath}", "unload Software"]);
    }

    [Fact]
    public void Already_cancelled_token_does_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var report = Executor().Execute(Plan(), cts.Token);

        report.Outcome.ShouldBe(ExecutionOutcome.Cancelled);
        _loader.Log.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_that_writes_to_an_unloaded_hive_is_rejected_without_side_effects()
    {
        var plan = Plan() with
        {
            Steps = [new SetOfflineRegistryValue(OfflineHive.Software, "A", "b", RegistryValue.Sz("c"))],
        };

        var report = Executor().Execute(plan, TestContext.Current.CancellationToken);

        report.Outcome.ShouldBe(ExecutionOutcome.Failed);
        report.FailedStepIndex.ShouldBe(0);
        _loader.Log.ShouldBeEmpty();
    }

    [Fact]
    public void Hive_path_resolution_failure_fails_the_load_step()
    {
        var executor = new RegistryPlanExecutor(_loader, _ => throw new UnauthorizedAccessException("outside scope"));

        var report = executor.Execute(Plan(), TestContext.Current.CancellationToken);

        report.Outcome.ShouldBe(ExecutionOutcome.Failed);
        report.Error.ShouldNotBeNull().ShouldContain("outside scope");
        _loader.Log.ShouldBeEmpty();
    }

    private sealed class FakeHiveLoader : IOfflineHiveLoader
    {
        public List<string> Log { get; } = [];

        public bool FailOnLoad { get; set; }

        public bool FailOnSet { get; set; }

        public bool FailOnUnload { get; set; }

        public Action? OnLoad { get; set; }

        public ILoadedOfflineHive Load(OfflineHive hive, string hiveFilePath)
        {
            if (FailOnLoad)
            {
                throw new IOException("load failed");
            }

            Log.Add($"load {hive} {hiveFilePath}");
            OnLoad?.Invoke();
            return new FakeHive(this, hive);
        }

        private sealed class FakeHive(FakeHiveLoader owner, OfflineHive hive) : ILoadedOfflineHive
        {
            public OfflineHive Hive => hive;

            public void SetValue(string subKey, string valueName, RegistryValue value)
            {
                if (owner.FailOnSet)
                {
                    owner.Log.Add($"set {hive} {subKey} {valueName}=FAILED");
                    throw new IOException("set failed");
                }

                owner.Log.Add($"set {hive} {subKey} {valueName}={value.Data}");
            }

            public void Unload()
            {
                if (owner.FailOnUnload)
                {
                    throw new IOException("unload failed");
                }

                owner.Log.Add($"unload {hive}");
            }
        }
    }
}
