using ImageForge.Core.Changes;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;
using ImageForge.Core.Updates;

namespace ImageForge.Core.Tests.Updates;

public sealed class BootPlanningTests
{
    private const string Folder = @"D:\upd";
    private const string Lcu = "Windows11.0-KB5134133-x64.msu";
    private const string Ssu = "SSU-26100.9703-x64.cab";

    private static ScannedUpdate Scanned(string file, string? sha256, UpdateKind kind, bool ssu = false, params string[] problems) => new(
        new UpdatePackageInfo(file, kind, UpdateFileName.Parse(file).Kb, "x64", null, [], [], ssu, problems), sha256);

    private static readonly UpdateFolder Source = new(Folder,
    [
        Scanned(Lcu, "aaa", UpdateKind.Msu),
        Scanned(Ssu, "bbb", UpdateKind.Cab, ssu: true),
        Scanned("broken.msu", "ddd", UpdateKind.Msu, false, "нет подписи MSCF: это не файл cabinet"),
    ], null);

    private static PlanResult Plan(UpdateFolder? source, params ImageChange[] changes) =>
        new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid).Plan(new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), changes)
        {
            UpdateSource = source,
        });

    [Fact]
    public void Boot_wim_gets_its_packages_servicing_stack_first()
    {
        var plan = Plan(Source, new AddBootUpdatePackage(Lcu), new AddBootUpdatePackage(Ssu)).Plan!;

        var boot = plan.Boot.ShouldNotBeNull();
        boot.Source.ShouldBe(Folder);
        boot.Add.Select(a => a.FileName).ShouldBe([Ssu, Lcu]);
        boot.Add[1].Sha256.ShouldBe("aaa");
        plan.Updates.ShouldBeNull();
        plan.WinRe.ShouldBeNull();
    }

    [Fact]
    public void The_same_package_can_go_into_the_image_WinRe_and_boot_wim()
    {
        var plan = Plan(Source, new AddUpdatePackage(Lcu), new AddWinReUpdatePackage(Lcu), new AddBootUpdatePackage(Lcu)).Plan!;

        plan.Updates.ShouldNotBeNull().Add.ShouldHaveSingleItem().FileName.ShouldBe(Lcu);
        plan.WinRe.ShouldNotBeNull().Add.ShouldHaveSingleItem().FileName.ShouldBe(Lcu);
        plan.Boot.ShouldNotBeNull().Add.ShouldHaveSingleItem().FileName.ShouldBe(Lcu);
    }

    [Fact]
    public void A_missing_folder_package_or_a_package_with_problems_blocks_the_plan()
    {
        Plan(null, new AddBootUpdatePackage(Lcu)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdateSourceMissing);
        Plan(Source, new AddBootUpdatePackage("gone.msu")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdateSourceMissing);
        Plan(Source, new AddBootUpdatePackage("broken.msu")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdatePackageInvalid);
    }

    [Fact]
    public void The_fingerprint_covers_boot_wim()
    {
        var install = Plan(Source, new AddUpdatePackage(Lcu)).Plan!;
        var both = Plan(Source, new AddUpdatePackage(Lcu), new AddBootUpdatePackage(Lcu)).Plan!;

        both.Fingerprint.ShouldNotBe(install.Fingerprint);
    }
}
