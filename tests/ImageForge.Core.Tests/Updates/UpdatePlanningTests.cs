using ImageForge.Core.Changes;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;
using ImageForge.Core.Updates;

namespace ImageForge.Core.Tests.Updates;

public sealed class UpdatePlanningTests
{
    private const string Folder = @"D:\upd";

    private static ScannedUpdate Scanned(string file, string? sha256, UpdateKind kind = UpdateKind.Msu, bool ssu = false, params string[] problems) => new(
        new UpdatePackageInfo(file, kind, UpdateFileName.Parse(file).Kb, "x64", null, [], [], ssu, problems, UpdateContainer.Wim), sha256);

    internal static readonly UpdateFolder Source = new(Folder,
    [
        Scanned("Windows11.0-KB5134133-x64.msu", "aaa"),
        Scanned(@"copy\Windows11.0-KB5134133-x64.msu", "aaa"),
        Scanned("SSU-26100.9703-x64.cab", "bbb", UpdateKind.Cab, ssu: true),
        Scanned("Windows11.0-KB5134200-x64.msu", "ccc"),
        Scanned("windows11.0-kb5054979-x64-ndp481.msu", "eee") with { Package = Scanned("windows11.0-kb5054979-x64-ndp481.msu", "eee").Package with { IsDotNet = true } },
        Scanned("broken.msu", "ddd", UpdateKind.Msu, false, "нет подписи MSCF: это не файл cabinet"),
    ], null);

    internal static readonly PackageInventory Packages = new(
    [
        new InstalledPackage("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.1742.1.10", "Staged", "SecurityUpdate"),
        new InstalledPackage("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9457.1.0", "Installed", "SecurityUpdate"),
        new InstalledPackage("Package_for_ServicingStack_9441~31bf3856ad364e35~amd64~~26100.9441.1.11", "Installed", "SecurityUpdate"),
        new InstalledPackage("Microsoft-Windows-Foundation-Package~31bf3856ad364e35~amd64~~10.0.26100.1", "Installed", "Foundation"),
    ], []);

    private static PlanResult Plan(UpdateFolder? source, params ImageChange[] changes) =>
        new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid).Plan(new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), changes)
        {
            UpdateSource = source,
        });

    [Fact]
    public void Adds_packages_from_the_folder_with_their_hash_servicing_stack_first()
    {
        var plan = Plan(Source, new AddUpdatePackage("Windows11.0-KB5134133-x64.msu"), new AddUpdatePackage("SSU-26100.9703-x64.cab")).Plan!;

        var updates = plan.Updates.ShouldNotBeNull();
        updates.Source.ShouldBe(Folder);
        updates.Add.Select(a => a.FileName).ShouldBe(["SSU-26100.9703-x64.cab", "Windows11.0-KB5134133-x64.msu"]);
        updates.Add[1].Sha256.ShouldBe("aaa");
        updates.Add[1].Description.ShouldContain("KB5134133");
        plan.Steps.ShouldBeEmpty();
    }

    [Fact]
    public void A_dotnet_update_goes_after_the_cleanup_and_is_not_a_second_cumulative_update()
    {
        var plan = Plan(Source, new AddUpdatePackage("windows11.0-kb5054979-x64-ndp481.msu"), new AddUpdatePackage("Windows11.0-KB5134133-x64.msu")).Plan!;

        plan.Updates.ShouldNotBeNull().Add.Select(a => a.FileName).ShouldBe(["Windows11.0-KB5134133-x64.msu"]);
        plan.Updates.Warnings.ShouldBeEmpty();
        var dotNet = plan.DotNetUpdates.ShouldNotBeNull();
        dotNet.Add.Select(a => a.FileName).ShouldBe(["windows11.0-kb5054979-x64-ndp481.msu"]);
        dotNet.Source.ShouldBe(Folder);
    }

    [Fact]
    public void Only_dotnet_updates_leave_the_main_update_step_empty()
    {
        var plan = Plan(Source, new AddUpdatePackage("windows11.0-kb5054979-x64-ndp481.msu")).Plan!;

        plan.Updates.ShouldBeNull();
        plan.DotNetUpdates.ShouldNotBeNull().Add.ShouldHaveSingleItem();
    }

    [Fact]
    public void Same_content_is_added_once_and_the_other_paths_are_shown()
    {
        var updates = Plan(Source, new AddUpdatePackage("Windows11.0-KB5134133-x64.msu"), new AddUpdatePackage(@"copy\Windows11.0-KB5134133-x64.msu")).Plan!.Updates!;

        var addition = updates.Add.ShouldHaveSingleItem();
        addition.FileName.ShouldBe("Windows11.0-KB5134133-x64.msu");
        addition.SameContent.ShouldBe([@"copy\Windows11.0-KB5134133-x64.msu"]);
    }

    [Fact]
    public void Two_cumulative_updates_are_warned_about()
    {
        var updates = Plan(Source, new AddUpdatePackage("Windows11.0-KB5134133-x64.msu"), new AddUpdatePackage("Windows11.0-KB5134200-x64.msu")).Plan!.Updates!;

        updates.Add.Count.ShouldBe(2);
        updates.Warnings.ShouldContain(w => w.Contains("KB5134133") && w.Contains("KB5134200"));
    }

    [Fact]
    public void A_missing_folder_package_or_a_package_with_problems_blocks_the_plan()
    {
        Plan(null, new AddUpdatePackage("Windows11.0-KB5134133-x64.msu")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdateSourceMissing);
        Plan(Source, new AddUpdatePackage("gone.msu")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdateSourceMissing);
        Plan(Source, new AddUpdatePackage("broken.msu")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdatePackageInvalid);
        Plan(new UpdateFolder(Folder, [Scanned("x.msu", null)], null), new AddUpdatePackage("x.msu"))
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdatePackageInvalid);
    }

    [Fact]
    public void The_fingerprint_covers_the_packages_and_their_hashes()
    {
        var one = Plan(Source, new AddUpdatePackage("Windows11.0-KB5134133-x64.msu")).Plan!.Fingerprint;
        var other = Plan(Source, new AddUpdatePackage("Windows11.0-KB5134200-x64.msu")).Plan!.Fingerprint;
        var changed = Plan(new UpdateFolder(Folder, [Scanned("Windows11.0-KB5134133-x64.msu", "zzz")], null), new AddUpdatePackage("Windows11.0-KB5134133-x64.msu")).Plan!.Fingerprint;

        one.ShouldNotBe(other);
        one.ShouldNotBe(changed);
    }

    [Fact]
    public void The_inventory_names_the_installed_rollup_and_servicing_stack()
    {
        Packages.Rollup.ShouldBe("26100.9457.1.0");
        Packages.ServicingStack.ShouldBe("26100.9441.1.11");
        new PackageInventory([], []).Rollup.ShouldBeNull();
    }

    [Fact]
    public void The_difference_lists_new_packages_and_state_changes()
    {
        var after = new PackageInventory(
        [
            new InstalledPackage("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.1742.1.10", "Staged", "SecurityUpdate"),
            new InstalledPackage("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9457.1.0", "Superseded", "SecurityUpdate"),
            new InstalledPackage("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9725.1.2", "Installed", "Update"),
            new InstalledPackage("Package_for_ServicingStack_9441~31bf3856ad364e35~amd64~~26100.9441.1.11", "Installed", "SecurityUpdate"),
            new InstalledPackage("Package_for_ServicingStack_9703~31bf3856ad364e35~amd64~~26100.9703.1.4", "Installed", "SecurityUpdate"),
            new InstalledPackage("Microsoft-Windows-Foundation-Package~31bf3856ad364e35~amd64~~10.0.26100.1", "Installed", "Foundation"),
        ], []);

        var change = PackageInventory.Compare(Packages, after);

        change.Added.ShouldBe(["Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9725.1.2", "Package_for_ServicingStack_9703~31bf3856ad364e35~amd64~~26100.9703.1.4"]);
        change.Superseded.ShouldBe(1);
        change.IsEmpty.ShouldBeFalse();
        after.Rollup.ShouldBe("26100.9725.1.2");
        after.ServicingStack.ShouldBe("26100.9703.1.4");
        PackageInventory.Compare(Packages, Packages).IsEmpty.ShouldBeTrue();
    }
}
