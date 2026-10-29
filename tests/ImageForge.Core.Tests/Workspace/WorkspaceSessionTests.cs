using System.Text;
using ImageForge.Core.Contracts;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry.RegFile;
using ImageForge.Core.Workspace;

namespace ImageForge.Core.Tests.Workspace;

public sealed class WorkspaceSessionTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeServicing _servicing = new();
    private readonly WorkspaceSession _session;
    private readonly OpenWorkspaceRequest _folders;

    public WorkspaceSessionTests()
    {
        _dir.WriteFile("media/setup.exe", "setup");
        _dir.WriteFile("media/sources/install.wim", "WIM");
        _folders = new OpenWorkspaceRequest(_dir.Combine("media"), _dir.Combine("work"), _dir.Combine("out", "Win11"));
        _session = new WorkspaceSession(_ => _servicing, Guid.NewGuid, dir => new TestPaths(dir), (_, _, _) => []);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task The_shown_plan_runs_and_reports_progress()
    {
        _session.Open(_folders).Problems.ShouldBeEmpty();
        var plan = _session.PlanM1();
        plan.Preview.ShouldHaveSingleItem().ValueName.ShouldBe("Milestone");
        var lines = new List<string>();

        var outcome = await _session.StartAsync(Start(plan), new SyncProgress(lines), Ct);

        outcome.Rejection.ShouldBe(StartPlanRejection.None);
        outcome.Status.ShouldBe(RunStatus.Published, outcome.Error);
        outcome.PublishedPath.ShouldBe(_folders.OutputDir);
        lines.ShouldContain("publish...");
        _session.Summary().Target.ShouldBe(TargetStatus.Completed);
    }

    [Fact]
    public async Task A_plan_cannot_start_twice_or_after_the_workspace_changed()
    {
        _session.Open(_folders);
        var plan = _session.PlanM1();
        (await _session.StartAsync(Start(plan), null, Ct)).Status.ShouldBe(RunStatus.Published);

        (await _session.StartAsync(Start(plan), null, Ct)).Rejection.ShouldBe(StartPlanRejection.UnknownPlan);

        var fresh = _session.PlanM1();
        _session.Open(_folders);
        (await _session.StartAsync(Start(fresh), null, Ct)).Rejection.ShouldBe(StartPlanRejection.UnknownPlan);
    }

    [Fact]
    public async Task A_stale_revision_is_rejected()
    {
        _session.Open(_folders);
        var plan = _session.PlanM1();

        var outcome = await _session.StartAsync(new StartPlanRequest(plan.PlanId, plan.WorkspaceRevision - 1, Guid.NewGuid()), null, Ct);

        outcome.Rejection.ShouldBe(StartPlanRejection.ExpectedRevisionMismatch);
    }

    [Fact]
    public async Task A_retried_start_returns_the_same_operation_instead_of_running_again()
    {
        _session.Open(_folders);
        var plan = _session.PlanM1();
        var request = Start(plan);

        var first = await _session.StartAsync(request, null, Ct);
        var retry = await _session.StartAsync(request, null, Ct);

        retry.ShouldBe(first);
        _servicing.Calls.Count(c => c == "mount").ShouldBe(1);
    }

    [Fact]
    public async Task Starting_without_an_open_workspace_is_rejected()
    {
        var outcome = await _session.StartAsync(new StartPlanRequest(Guid.NewGuid(), 1, Guid.NewGuid()), null, Ct);

        outcome.Rejection.ShouldBe(StartPlanRejection.NotOpen);
    }

    [Fact]
    public void Unusable_folders_open_nothing()
    {
        var session = new WorkspaceSession(_ => _servicing, Guid.NewGuid, dir => new TestPaths(dir),
            (_, _, _) => [new LocationProblem(LocationProblemKind.Nested, WorkspaceLocations.WorkDir, WorkspaceLocations.Source)]);

        session.Open(_folders).Problems.ShouldHaveSingleItem().Kind.ShouldBe(LocationProblemKind.Nested);
        Should.Throw<InvalidOperationException>(session.PlanM1);
    }

    [Fact]
    public async Task A_failed_run_marks_the_target_and_a_new_plan_can_run()
    {
        _session.Open(_folders);
        _session.Failure = InjectedFailure.BeforePublish;
        var failed = await _session.StartAsync(Start(_session.PlanM1()), null, Ct);
        failed.Status.ShouldBe(RunStatus.Failed);
        failed.FailedStep.ShouldBe("publish");
        _session.Summary().Target.ShouldBe(TargetStatus.NeedsRebuild);

        _session.Failure = InjectedFailure.None;
        (await _session.StartAsync(Start(_session.PlanM1()), null, Ct)).Status.ShouldBe(RunStatus.Published);
    }

    [Fact]
    public async Task WhenIdle_waits_for_the_run_and_its_cleanup()
    {
        _session.Open(_folders);
        var plan = _session.PlanM1();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await cts.CancelAsync();

        var run = _session.StartAsync(Start(plan), null, cts.Token);
        await _session.WhenIdleAsync();

        run.IsCompleted.ShouldBeTrue();
        (await run).Status.ShouldBe(RunStatus.Failed);
        _servicing.Mounted.ShouldBeEmpty();
        await _session.WhenIdleAsync();
    }

    [Fact]
    public async Task A_reg_file_is_planned_and_its_changes_run()
    {
        _session.Open(_folders);

        var plan = _session.PlanRegFile(RegFile("[HKEY_LOCAL_MACHINE\\SOFTWARE\\X]\r\n\"a\"=dword:00000001\r\n"));

        plan.Diagnostics.ShouldBeEmpty();
        plan.FileDiagnostics.ShouldBeEmpty();
        plan.FileEncoding.ShouldBe("UTF-16");
        plan.Preview.Select(p => p.SubKey).ShouldBe(["X", "X"]);
        (await _session.StartAsync(Start(plan), null, Ct)).Status.ShouldBe(RunStatus.Published);
    }

    [Fact]
    public async Task A_broken_reg_file_shows_its_problems_and_drops_the_previous_plan()
    {
        _session.Open(_folders);
        var previous = _session.PlanM1();

        var plan = _session.PlanRegFile(RegFile("\"a\"=dword:1\r\n"));

        plan.PlanId.ShouldBe(Guid.Empty);
        plan.FileDiagnostics.Single().Code.ShouldBe(RegFileCodes.ValueOutsideKey);
        (await _session.StartAsync(Start(previous), null, Ct)).Rejection.ShouldBe(StartPlanRejection.UnknownPlan);
    }

    [Fact]
    public void A_reg_file_whose_paths_cannot_be_applied_gets_planning_diagnostics()
    {
        _session.Open(_folders);

        var plan = _session.PlanRegFile(RegFile("[HKEY_LOCAL_MACHINE\\SAM\\X]\r\n\"a\"=dword:1\r\n"));

        plan.PlanId.ShouldBe(Guid.Empty);
        plan.FileDiagnostics.ShouldBeEmpty();
        plan.Diagnostics.ShouldNotBeEmpty();
    }

    private const string Weather = "Microsoft.BingWeather_4.54.63029.0_neutral_~_8wekyb3d8bbwe";
    private const string VcLibs = "Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe";

    [Fact]
    public async Task Inspected_packages_can_be_planned_for_removal_and_removed()
    {
        _servicing.Provisioned.AddRange([Weather, VcLibs]);
        _servicing.Dependencies[Weather] = ["Microsoft.VCLibs.140.00_8wekyb3d8bbwe"];
        _session.Open(_folders);

        var inspection = await _session.InspectAsync(null, Ct);

        inspection.Problems.ShouldBeEmpty();
        inspection.Packages.Select(p => p.PackageName).ShouldBe([Weather, VcLibs]);
        inspection.Packages.ShouldAllBe(p => p.BlockedReason == null);
        inspection.Packages[1].NeededBy.ShouldBe(["Microsoft.BingWeather"]);
        _session.Plan(new PlanOptions(null, ["Microsoft.VCLibs.140.00_8wekyb3d8bbwe"])).Diagnostics
            .ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.AppxRequiredByKept);

        var plan = _session.Plan(new PlanOptions(null, ["Microsoft.BingWeather_8wekyb3d8bbwe"]));

        plan.Diagnostics.ShouldBeEmpty();
        plan.Preview.ShouldBeEmpty();
        plan.AppxRemovals.ShouldHaveSingleItem().PackageName.ShouldBe(Weather);
        (await _session.StartAsync(Start(plan), null, Ct)).Status.ShouldBe(RunStatus.Published);
        _servicing.Provisioned.ShouldBe([VcLibs]);
    }

    [Fact]
    public void Removal_before_inspection_is_blocked()
    {
        _session.Open(_folders);

        var plan = _session.Plan(new PlanOptions(null, ["Microsoft.BingWeather_8wekyb3d8bbwe"]));

        plan.PlanId.ShouldBe(Guid.Empty);
        plan.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.AppxInventoryMissing);
    }

    [Fact]
    public async Task Reopening_the_workspace_forgets_the_inventory()
    {
        _servicing.Provisioned.Add(Weather);
        _session.Open(_folders);
        await _session.InspectAsync(null, Ct);

        _session.Open(_folders);

        _session.Plan(new PlanOptions(null, ["Microsoft.BingWeather_8wekyb3d8bbwe"])).Diagnostics
            .ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.AppxInventoryMissing);
    }

    [Fact]
    public async Task A_failed_inspection_is_reported_and_leaves_no_inventory()
    {
        _servicing.Provisioned.Add(Weather);
        _session.Open(_folders);
        await _session.InspectAsync(null, Ct);
        _servicing.FailAppxList = true;

        var inspection = await _session.InspectAsync(null, Ct);

        inspection.Packages.ShouldBeEmpty();
        inspection.Problems.ShouldHaveSingleItem().ShouldContain("exit code 87");
        _servicing.Mounted.ShouldBeEmpty();
        _session.Plan(new PlanOptions(null, ["Microsoft.BingWeather_8wekyb3d8bbwe"])).PlanId.ShouldBe(Guid.Empty);
    }

    [Fact]
    public async Task A_reg_file_and_removals_plan_together()
    {
        _servicing.Provisioned.Add(Weather);
        _session.Open(_folders);
        await _session.InspectAsync(null, Ct);

        var plan = _session.Plan(new PlanOptions(RegFile("[HKEY_LOCAL_MACHINE\\SOFTWARE\\X]\r\n\"a\"=dword:00000001\r\n"), ["Microsoft.BingWeather_8wekyb3d8bbwe"]));

        plan.Preview.Count.ShouldBe(2);
        plan.AppxRemovals.Count.ShouldBe(1);
    }

    private static RegFileInput RegFile(string body) =>
        new("t.reg", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("Windows Registry Editor Version 5.00\r\n\r\n" + body)], RegFileEncoding.Auto, 1251);

    private static StartPlanRequest Start(PlanSummary plan) => new(plan.PlanId, plan.WorkspaceRevision, Guid.NewGuid());

    private sealed class SyncProgress(List<string> lines) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (lines)
            {
                lines.Add(value);
            }
        }
    }
}
