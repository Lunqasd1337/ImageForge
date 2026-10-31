using ImageForge.Core.Changes;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;
using ImageForge.Core.Updates;

namespace ImageForge.Core.Tests.Updates;

public sealed class MediaPlanningTests
{
    private const string Folder = @"D:\upd";
    private const string Lcu = "Windows11.0-KB5134133-x64.msu";
    private const string SetupDu = @"setup\windows11.0-kb5128977-x64.cab";

    private static ScannedUpdate Scanned(string file, string sha256, UpdateKind kind, bool setupDu = false) => new(
        new UpdatePackageInfo(file, kind, UpdateFileName.Parse(file).Kb, "x64", null, [], [], false, []) { IsSetupDynamicUpdate = setupDu }, sha256);

    private static readonly UpdateFolder Source = new(Folder,
    [
        Scanned(Lcu, "aaa", UpdateKind.Msu),
        Scanned(SetupDu, "sss", UpdateKind.Cab, setupDu: true),
        Scanned("windows11.0-kb5128978-x64.cab", "ttt", UpdateKind.Cab, setupDu: true),
    ], null);

    private static PlanResult Plan(params ImageChange[] changes) =>
        new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid).Plan(new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), changes)
        {
            UpdateSource = Source,
        });

    [Fact]
    public void A_setup_dynamic_update_goes_to_the_media_together_with_boot_wim()
    {
        var plan = Plan(new AddBootUpdatePackage(Lcu), new ApplySetupDynamicUpdate(SetupDu)).Plan!;

        var media = plan.Media.ShouldNotBeNull();
        media.Source.ShouldBe(Folder);
        media.SetupUpdate.FileName.ShouldBe(SetupDu);
        media.SetupUpdate.Sha256.ShouldBe("sss");
        media.SetupUpdate.Description.ShouldContain("Setup DU");
        media.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Without_boot_wim_the_setup_dynamic_update_is_planned_with_a_warning()
    {
        var plan = Plan(new ApplySetupDynamicUpdate(SetupDu)).Plan!;

        plan.Media.ShouldNotBeNull().Warnings.ShouldHaveSingleItem().ShouldContain("boot.wim");
        plan.Boot.ShouldBeNull();
    }

    [Fact]
    public void A_setup_dynamic_update_cannot_go_into_an_image_and_an_image_package_cannot_go_to_the_media()
    {
        Plan(new AddUpdatePackage(SetupDu)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdateIsSetupDynamicUpdate);
        Plan(new AddWinReUpdatePackage(SetupDu)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdateIsSetupDynamicUpdate);
        Plan(new AddBootUpdatePackage(SetupDu)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdateIsSetupDynamicUpdate);
        Plan(new ApplySetupDynamicUpdate(Lcu)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdateNotSetupDynamicUpdate);
    }

    [Fact]
    public void Only_one_setup_dynamic_update_is_allowed()
    {
        Plan(new ApplySetupDynamicUpdate(SetupDu), new ApplySetupDynamicUpdate("windows11.0-kb5128978-x64.cab"))
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdateNotSetupDynamicUpdate);
    }

    [Fact]
    public void The_fingerprint_covers_the_setup_dynamic_update()
    {
        var boot = Plan(new AddBootUpdatePackage(Lcu)).Plan!;
        var withMedia = Plan(new AddBootUpdatePackage(Lcu), new ApplySetupDynamicUpdate(SetupDu)).Plan!;
        var other = Plan(new AddBootUpdatePackage(Lcu), new ApplySetupDynamicUpdate("windows11.0-kb5128978-x64.cab")).Plan!;

        withMedia.Fingerprint.ShouldNotBe(boot.Fingerprint);
        other.Fingerprint.ShouldNotBe(withMedia.Fingerprint);
    }
}
