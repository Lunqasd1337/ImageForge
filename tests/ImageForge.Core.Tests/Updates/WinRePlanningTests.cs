using ImageForge.Core.Changes;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;
using ImageForge.Core.Updates;

namespace ImageForge.Core.Tests.Updates;

public sealed class WinRePlanningTests
{
    private const string Folder = @"D:\upd";
    private const string Lcu = "Windows11.0-KB5134133-x64.msu";
    private const string SafeOs = "windows11.0-kb5130881-x64.cab";

    private static ScannedUpdate Scanned(string file, string? sha256, UpdateKind kind, params string[] problems) => new(
        new UpdatePackageInfo(file, kind, UpdateFileName.Parse(file).Kb, "x64", null, [], [], false, problems), sha256);

    private static readonly UpdateFolder Source = new(Folder,
    [
        Scanned(Lcu, "aaa", UpdateKind.Msu),
        Scanned(SafeOs, "sss", UpdateKind.Cab),
        Scanned(@"copy\" + SafeOs, "sss", UpdateKind.Cab),
        Scanned("broken.cab", "ddd", UpdateKind.Cab, "в CAB нет update.mum: это не пакет обслуживания Windows"),
    ], null);

    private static PlanResult Plan(UpdateFolder? source, params ImageChange[] changes) =>
        new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid).Plan(new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), changes)
        {
            UpdateSource = source,
        });

    [Fact]
    public void WinRe_gets_the_cumulative_update_first_then_the_safe_os_update_with_cleanup_and_export()
    {
        var plan = Plan(Source, new AddWinReUpdatePackage(SafeOs), new AddWinReUpdatePackage(Lcu), new AddUpdatePackage(Lcu)).Plan!;

        var winRe = plan.WinRe.ShouldNotBeNull();
        winRe.Source.ShouldBe(Folder);
        winRe.Add.Select(a => a.FileName).ShouldBe([Lcu, SafeOs]);
        winRe.Add[1].Sha256.ShouldBe("sss");
        winRe.Warnings.ShouldBeEmpty();
        plan.Updates.ShouldNotBeNull().Add.ShouldHaveSingleItem().FileName.ShouldBe(Lcu);
    }

    [Fact]
    public void WinRe_alone_is_a_change()
    {
        var plan = Plan(Source, new AddWinReUpdatePackage(SafeOs)).Plan!;

        plan.WinRe.ShouldNotBeNull().Add.ShouldHaveSingleItem().FileName.ShouldBe(SafeOs);
        plan.Updates.ShouldBeNull();
    }

    [Fact]
    public void Only_a_cumulative_update_is_warned_about_because_it_brings_just_the_servicing_stack()
    {
        var winRe = Plan(Source, new AddWinReUpdatePackage(Lcu)).Plan!.WinRe!;

        winRe.Warnings.ShouldHaveSingleItem().ShouldContain("Safe OS");
    }

    [Fact]
    public void Same_content_goes_into_WinRe_once()
    {
        var winRe = Plan(Source, new AddWinReUpdatePackage(SafeOs), new AddWinReUpdatePackage(@"copy\" + SafeOs)).Plan!.WinRe!;

        winRe.Add.ShouldHaveSingleItem().SameContent.ShouldBe([@"copy\" + SafeOs]);
    }

    [Fact]
    public void A_missing_folder_package_or_a_package_with_problems_blocks_the_plan()
    {
        Plan(null, new AddWinReUpdatePackage(SafeOs)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdateSourceMissing);
        Plan(Source, new AddWinReUpdatePackage("gone.cab")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdateSourceMissing);
        Plan(Source, new AddWinReUpdatePackage("broken.cab")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdatePackageInvalid);
    }

    [Fact]
    public void The_fingerprint_covers_WinRe()
    {
        var install = Plan(Source, new AddUpdatePackage(Lcu)).Plan!;
        var both = Plan(Source, new AddUpdatePackage(Lcu), new AddWinReUpdatePackage(SafeOs)).Plan!;

        both.Fingerprint.ShouldNotBe(install.Fingerprint);
    }
}
