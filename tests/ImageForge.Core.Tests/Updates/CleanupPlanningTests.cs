using ImageForge.Core.Changes;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;
using ImageForge.Core.Servicing;
using ImageForge.Core.Updates;

namespace ImageForge.Core.Tests.Updates;

public sealed class CleanupPlanningTests
{
    private static PlanResult Plan(params ImageChange[] changes) =>
        new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid).Plan(new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), changes)
        {
            UpdateSource = UpdatePlanningTests.Source,
        });

    [Fact]
    public void Pending_actions_in_the_image_are_shown_with_the_cleanup()
    {
        var packages = new PackageInventory(
        [
            new InstalledPackage("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9457.1.0", "Installed", "SecurityUpdate"),
            new InstalledPackage("Microsoft-Windows-Foo-Package~31bf3856ad364e35~amd64~~10.0.26100.1", "Install Pending", "Feature Pack"),
        ], []);
        packages.Pending.ShouldBe(["Microsoft-Windows-Foo-Package~31bf3856ad364e35~amd64~~10.0.26100.1"]);

        var cleanup = new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid)
            .Plan(new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), [new CleanupComponentStore(false)]) { Packages = packages })
            .Plan.ShouldNotBeNull().Cleanup.ShouldNotBeNull();

        cleanup.Warnings.ShouldHaveSingleItem().ShouldContain("Microsoft-Windows-Foo-Package");
        Plan(new CleanupComponentStore(false)).Plan.ShouldNotBeNull().Cleanup.ShouldNotBeNull().Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void A_cleanup_alone_is_a_plan()
    {
        var result = Plan(new CleanupComponentStore(ResetBase: false));

        result.Diagnostics.ShouldBeEmpty();
        var cleanup = result.Plan.ShouldNotBeNull().Cleanup.ShouldNotBeNull();
        cleanup.ResetBase.ShouldBeFalse();
        cleanup.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void ResetBase_warns_that_updates_can_no_longer_be_removed()
    {
        var cleanup = Plan(new CleanupComponentStore(ResetBase: true)).Plan.ShouldNotBeNull().Cleanup.ShouldNotBeNull();

        cleanup.ResetBase.ShouldBeTrue();
        cleanup.Warnings.ShouldHaveSingleItem().ShouldContain("/ResetBase");
    }

    [Fact]
    public void Asked_twice_it_runs_once_with_ResetBase_if_either_asked()
    {
        Plan(new CleanupComponentStore(true), new CleanupComponentStore(false)).Plan.ShouldNotBeNull().Cleanup.ShouldNotBeNull().ResetBase.ShouldBeTrue();
        Plan(new CleanupComponentStore(false), new CleanupComponentStore(true)).Plan.ShouldNotBeNull().Cleanup.ShouldNotBeNull().ResetBase.ShouldBeTrue();
    }

    [Fact]
    public void The_fingerprint_holds_the_cleanup_and_its_mode()
    {
        var update = new AddUpdatePackage("Windows11.0-KB5134133-x64.msu");
        var plain = Plan(update).Plan.ShouldNotBeNull();
        var cleanup = Plan(update, new CleanupComponentStore(false)).Plan.ShouldNotBeNull();
        var resetBase = Plan(update, new CleanupComponentStore(true)).Plan.ShouldNotBeNull();

        plain.Cleanup.ShouldBeNull();
        new[] { plain.Fingerprint, cleanup.Fingerprint, resetBase.Fingerprint }.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void Dism_exe_cleans_the_mounted_image_only()
    {
        DismCliArguments.CleanupComponentStore(@"C:\IF\m", resetBase: false)
            .ShouldBe(["/English", @"/Image:C:\IF\m", "/Cleanup-Image", "/StartComponentCleanup"]);
        DismCliArguments.CleanupComponentStore(@"C:\IF\m", resetBase: true)
            .ShouldBe(["/English", @"/Image:C:\IF\m", "/Cleanup-Image", "/StartComponentCleanup", "/ResetBase"]);
        Should.Throw<ArgumentException>(() => DismCliArguments.CleanupComponentStore("m", resetBase: false));
    }
}
