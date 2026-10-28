using ImageForge.Core.Appx;
using ImageForge.Core.Changes;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;

namespace ImageForge.Core.Tests.Appx;

public sealed class AppxPlanningTests
{
    private static readonly Guid PlanId = Guid.Parse("8d7c1b7e-0000-4000-8000-000000000003");

    internal static ProvisionedAppxPackage Package(string fullName)
    {
        var identity = PackageIdentity.Parse(fullName).ShouldNotBeNull();
        return new ProvisionedAppxPackage(identity.Name, identity);
    }

    internal static AppxInventory Inventory(params string[] fullNames) => new([.. fullNames.Select(Package)], []);

    private const string Clipchamp = "Clipchamp.Clipchamp_4.3.10120.0_neutral_~_yxz26nhyzhsrt";
    private const string Weather = "Microsoft.BingWeather_4.54.63029.0_neutral_~_8wekyb3d8bbwe";
    private const string Store = "Microsoft.WindowsStore_22409.1401.5.0_x64__8wekyb3d8bbwe";
    private const string VcLibs = "Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe";

    private static PlanResult Plan(AppxInventory? inventory, params ImageChange[] changes) =>
        new PipelinePlanner(new RegistryPathMapper(), () => PlanId)
            .Plan(new PlanRequest(Guid.NewGuid(), 3, Guid.NewGuid(), changes) { Appx = inventory });

    [Fact]
    public void A_family_resolves_to_the_exact_package_of_the_inventory()
    {
        var result = Plan(Inventory(Clipchamp, Weather), new RemoveProvisionedAppx("Clipchamp.Clipchamp_yxz26nhyzhsrt"));

        result.Diagnostics.ShouldBeEmpty();
        var plan = result.Plan.ShouldNotBeNull();
        plan.Steps.ShouldBeEmpty();
        plan.AppxRemovals.ShouldHaveSingleItem().Package.FullName.ShouldBe(Clipchamp);
        plan.AppxInventory.ShouldBe(Inventory(Clipchamp, Weather).Fingerprint);
    }

    [Fact]
    public void Removals_keep_the_requested_order_and_ignore_repeats()
    {
        var plan = Plan(
            Inventory(Clipchamp, Weather),
            new RemoveProvisionedAppx("Microsoft.BingWeather_8wekyb3d8bbwe"),
            new RemoveProvisionedAppx("clipchamp.clipchamp_yxz26nhyzhsrt"),
            new RemoveProvisionedAppx("Microsoft.BingWeather_8wekyb3d8bbwe")).Plan.ShouldNotBeNull();

        plan.AppxRemovals.Select(r => r.Package.FullName).ShouldBe([Weather, Clipchamp]);
    }

    [Fact]
    public void Registry_changes_and_removals_go_into_one_plan()
    {
        var plan = Plan(Inventory(Clipchamp), ValidationMarker.M1, new RemoveProvisionedAppx("Clipchamp.Clipchamp_yxz26nhyzhsrt")).Plan.ShouldNotBeNull();

        plan.Steps.Count.ShouldBe(3);
        plan.AppxRemovals.Count.ShouldBe(1);
    }

    [Fact]
    public void Removal_without_an_inventory_is_blocked()
    {
        Plan(null, new RemoveProvisionedAppx("Clipchamp.Clipchamp_yxz26nhyzhsrt"))
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.AppxInventoryMissing);
        Plan(new AppxInventory([Package(Clipchamp)], ["broken block"]), new RemoveProvisionedAppx("Clipchamp.Clipchamp_yxz26nhyzhsrt"))
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.AppxInventoryMissing);
    }

    [Fact]
    public void A_family_the_image_does_not_have_is_blocked()
    {
        var result = Plan(Inventory(Clipchamp), new RemoveProvisionedAppx("Microsoft.BingWeather_8wekyb3d8bbwe"));

        result.Plan.ShouldBeNull();
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.AppxNotProvisioned);
    }

    [Fact]
    public void A_framework_is_blocked_because_its_dependents_are_not_resolved_yet()
    {
        var result = Plan(Inventory(VcLibs), new RemoveProvisionedAppx("Microsoft.VCLibs.140.00_8wekyb3d8bbwe"));

        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.AppxFrameworkBlocked);
    }

    [Fact]
    public void Store_removal_carries_its_warning()
    {
        var plan = Plan(Inventory(Store), new RemoveProvisionedAppx("Microsoft.WindowsStore_8wekyb3d8bbwe")).Plan.ShouldNotBeNull();

        plan.AppxRemovals.ShouldHaveSingleItem().Warning.ShouldNotBeNull();
    }

    [Fact]
    public void The_fingerprint_covers_removals_and_the_inventory()
    {
        var one = Plan(Inventory(Clipchamp, Weather), new RemoveProvisionedAppx("Clipchamp.Clipchamp_yxz26nhyzhsrt")).Plan!.Fingerprint;
        var other = Plan(Inventory(Clipchamp, Weather), new RemoveProvisionedAppx("Microsoft.BingWeather_8wekyb3d8bbwe")).Plan!.Fingerprint;
        var otherInventory = Plan(Inventory(Clipchamp), new RemoveProvisionedAppx("Clipchamp.Clipchamp_yxz26nhyzhsrt")).Plan!.Fingerprint;

        one.ShouldNotBe(other);
        one.ShouldNotBe(otherInventory);
    }

    [Fact]
    public void The_inventory_fingerprint_ignores_order()
    {
        Inventory(Clipchamp, Weather).Fingerprint.ShouldBe(Inventory(Weather, Clipchamp).Fingerprint);
        Inventory(Clipchamp).Fingerprint.ShouldNotBe(Inventory(Clipchamp, Weather).Fingerprint);
    }
}
