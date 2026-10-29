using ImageForge.Core.Changes;
using ImageForge.Core.Drivers;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;

namespace ImageForge.Core.Tests.Drivers;

public sealed class DriverPlanningTests
{
    private const string Folder = @"D:\drivers";

    internal static readonly DriverInventory Inventory = new(
    [
        new InstalledDriver("oem0.inf", "netkvm.inf", "Net", "Red Hat, Inc.", "100.103.104.30200", new DateTime(2026, 7, 22), BootCritical: false, "Signed"),
        new InstalledDriver("oem1.inf", "viostor.inf", "SCSIAdapter", "Red Hat, Inc.", "100.103.104.30200", new DateTime(2026, 7, 22), BootCritical: true, "Signed"),
    ], []);

    private static ScannedDriver Scanned(string path, string hash, bool unsigned = false, params string[] problems) => new(
        new DriverPackageInfo(path, new DriverInf("Net", null, "Red Hat, Inc.", "07/22/2026", "100.103.104.30200", unsigned ? null : "netkvm.cat", ["amd64"]), [], problems),
        hash);

    internal static readonly DriverFolder Source = new(Folder,
    [
        Scanned(@"w10\netkvm.inf", "aaa"),
        Scanned(@"2k22\netkvm.inf", "aaa"),
        Scanned(@"w11\netkvm.inf", "bbb"),
        Scanned(@"nocat\netkvm.inf", "ccc", unsigned: true),
        Scanned(@"broken\netkvm.inf", "ddd", false, "Нет файла netkvm.sys."),
        new(new DriverPackageInfo("readme.inf", null, [], ["Нет раздела [Version] с подписью: это не INF драйвера."]), "eee"),
    ], null);

    private static PlanResult Plan(params ImageChange[] changes) => Plan(Source, changes);

    private static PlanResult Plan(DriverFolder? source, params ImageChange[] changes) =>
        new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid).Plan(new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), changes)
        {
            Drivers = Inventory,
            DriverSource = source,
        });

    [Fact]
    public void Adds_packages_from_the_folder_and_removes_by_published_name()
    {
        var plan = Plan(new AddDriverPackage(@"w11\netkvm.inf"), new RemoveDriverPackage("oem0.inf")).Plan!;

        var drivers = plan.Drivers.ShouldNotBeNull();
        drivers.Remove.ShouldHaveSingleItem().PublishedName.ShouldBe("oem0.inf");
        var addition = drivers.Add.ShouldHaveSingleItem();
        addition.InfPath.ShouldBe(@"w11\netkvm.inf");
        addition.ContentHash.ShouldBe("bbb");
        addition.ForceUnsigned.ShouldBeFalse();
        drivers.Source.ShouldBe(Folder);
        drivers.Inventory.ShouldBe(Inventory.Fingerprint);
        plan.Steps.ShouldBeEmpty();
    }

    [Fact]
    public void Same_content_is_added_once_and_the_other_paths_are_shown()
    {
        var drivers = Plan(new AddDriverPackage(@"w10\netkvm.inf"), new AddDriverPackage(@"2k22\netkvm.inf"), new AddDriverPackage(@"w10\netkvm.inf")).Plan!.Drivers!;

        var addition = drivers.Add.ShouldHaveSingleItem();
        addition.InfPath.ShouldBe(@"w10\netkvm.inf");
        addition.SameContent.ShouldBe([@"2k22\netkvm.inf"]);
    }

    [Fact]
    public void An_unsigned_package_needs_force_unsigned_and_is_warned_about()
    {
        Plan(new AddDriverPackage(@"nocat\netkvm.inf")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.DriverUnsigned);

        var drivers = Plan(new AddDriverPackage(@"nocat\netkvm.inf", ForceUnsigned: true)).Plan!.Drivers!;
        drivers.Add.Single().ForceUnsigned.ShouldBeTrue();
        drivers.Warnings.ShouldContain(w => w.Contains("без подписи", StringComparison.Ordinal));
    }

    [Fact]
    public void Force_unsigned_is_not_passed_for_a_signed_package()
    {
        Plan(new AddDriverPackage(@"w11\netkvm.inf", ForceUnsigned: true)).Plan!.Drivers!.Add.Single().ForceUnsigned.ShouldBeFalse();
    }

    [Fact]
    public void A_boot_critical_driver_is_removed_only_when_allowed_and_with_a_warning()
    {
        Plan(new RemoveDriverPackage("oem1.inf")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.DriverBootCritical);

        var drivers = Plan(new RemoveDriverPackage("OEM1.INF", AllowBootCritical: true)).Plan!.Drivers!;
        drivers.Remove.ShouldHaveSingleItem().PublishedName.ShouldBe("oem1.inf");
        drivers.Remove.Single().BootCritical.ShouldBeTrue();
        drivers.Warnings.ShouldContain(w => w.Contains("oem1.inf", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"broken\netkvm.inf", PlanningCodes.DriverPackageInvalid)]
    [InlineData("readme.inf", PlanningCodes.DriverPackageInvalid)]
    [InlineData(@"missing\netkvm.inf", PlanningCodes.DriverSourceMissing)]
    public void Packages_with_problems_or_not_in_the_folder_block(string path, string code)
    {
        Plan(new AddDriverPackage(path)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact]
    public void Additions_need_a_usable_folder()
    {
        Plan((DriverFolder?)null, new AddDriverPackage(@"w11\netkvm.inf")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.DriverSourceMissing);
        var bad = Plan(new DriverFolder(Folder, [], "в папке нет файлов .inf"), new AddDriverPackage(@"w11\netkvm.inf")).Diagnostics.ShouldHaveSingleItem();
        bad.Code.ShouldBe(PlanningCodes.DriverSourceMissing);
        bad.Message.ShouldContain("в папке нет файлов .inf");
    }

    [Fact]
    public void Removal_needs_a_third_party_driver_of_the_image_and_a_read_list()
    {
        Plan(new RemoveDriverPackage("oem7.inf")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.DriverNotInstalled);

        var result = new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid).Plan(
            new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), [new RemoveDriverPackage("oem0.inf")]) { DriverSource = Source });
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.DriverInventoryMissing);
    }

    [Fact]
    public void A_package_like_one_already_in_the_image_is_warned_about()
    {
        Plan(new AddDriverPackage(@"w11\netkvm.inf")).Plan!.Drivers!.Warnings
            .ShouldContain(w => w.Contains("oem0.inf", StringComparison.Ordinal));
    }

    [Fact]
    public void Driver_changes_are_part_of_the_fingerprint()
    {
        var plan = Plan(new AddDriverPackage(@"w11\netkvm.inf"), new RemoveDriverPackage("oem0.inf")).Plan!;
        var drivers = plan.Drivers!;

        PlanFingerprint.Compute(plan).ShouldBe(plan.Fingerprint);
        PlanFingerprint.Compute(plan with { Drivers = drivers with { Add = [drivers.Add[0] with { ContentHash = "changed" }] } }).ShouldNotBe(plan.Fingerprint);
        PlanFingerprint.Compute(plan with { Drivers = drivers with { Add = [drivers.Add[0] with { ForceUnsigned = true }] } }).ShouldNotBe(plan.Fingerprint);
        PlanFingerprint.Compute(plan with { Drivers = drivers with { Remove = [] } }).ShouldNotBe(plan.Fingerprint);
        PlanFingerprint.Compute(plan with { Drivers = drivers with { Inventory = "other" } }).ShouldNotBe(plan.Fingerprint);
        PlanFingerprint.Compute(plan with { Drivers = null }).ShouldNotBe(plan.Fingerprint);
    }
}
