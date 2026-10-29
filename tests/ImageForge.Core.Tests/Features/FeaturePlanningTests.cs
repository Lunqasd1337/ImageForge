using ImageForge.Core.Changes;
using ImageForge.Core.Features;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;

namespace ImageForge.Core.Tests.Features;

public sealed class FeaturePlanningTests
{
    private static readonly FeatureInventory Features = FeatureDependencyTests.Inventory with { Parents = FeatureDependencyTests.Parents };

    private static readonly CapabilityInventory Capabilities = DismCapabilityListParser.Parse("""
        Capability Identity : MathRecognizer~~~~0.0.1.0
        State : Installed

        Capability Identity : OpenSSH.Server~~~~0.0.1.0
        State : Not Present

        The operation completed successfully.
        """);

    private static PlanResult Plan(params ImageChange[] changes) => Plan(true, changes);

    private static PlanResult Plan(bool mediaHasNetFx3, params ImageChange[] changes) =>
        new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid).Plan(new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), changes)
        {
            Features = Features,
            Capabilities = Capabilities,
            MediaHasNetFx3 = mediaHasNetFx3,
        });

    [Fact]
    public void Enables_with_parents_and_disables_with_dependents_in_one_plan()
    {
        var plan = Plan(new EnableWindowsFeature("IIS-WebSockets"), new DisableWindowsFeature("Printing-Foundation-Features")).Plan!;

        plan.Features.ShouldNotBeNull();
        plan.Features.Enable.ShouldBe(["IIS-WebServerRole", "IIS-WebServer", "IIS-ApplicationDevelopment", "IIS-WebSockets"]);
        plan.Features.Disable.ShouldBe(["Printing-Foundation-InternetPrinting-Client", "Printing-Foundation-Features"]);
        plan.Features.Added.ShouldBe(["Printing-Foundation-InternetPrinting-Client", "IIS-WebServerRole", "IIS-WebServer", "IIS-ApplicationDevelopment"]);
        plan.Features.UsesMediaSxs.ShouldBeFalse();
        plan.Features.Inventory.ShouldBe(Features.Fingerprint);
        plan.Steps.ShouldBeEmpty();
    }

    [Fact]
    public void Shared_parents_are_enabled_once()
    {
        var plan = Plan(new EnableWindowsFeature("IIS-WebSockets"), new EnableWindowsFeature("IIS-ASPNET45")).Plan!;

        plan.Features!.Enable.ShouldBe(
            ["IIS-WebServerRole", "IIS-WebServer", "IIS-ApplicationDevelopment", "IIS-WebSockets", "NetFx4Extended-ASPNET45", "IIS-ASPNET45"]);
    }

    [Fact]
    public void NetFx3_comes_from_the_media_only_when_it_holds_the_package()
    {
        var plan = Plan(new EnableWindowsFeature("WCF-HTTP-Activation")).Plan!;
        plan.Features!.Enable.ShouldBe(["NetFx3", "WCF-HTTP-Activation"]);
        plan.Features.UsesMediaSxs.ShouldBeTrue();

        Plan(false, new EnableWindowsFeature("NetFx3")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.FeatureSourceMissing);
    }

    [Fact]
    public void Disabling_a_parent_of_a_feature_to_enable_blocks()
    {
        // LPDPrintService is off; its parent Printing-Foundation-Features is on and would be turned off.
        var result = Plan(new DisableWindowsFeature("Printing-Foundation-Features"), new EnableWindowsFeature("Printing-Foundation-LPDPrintService"));

        result.Plan.ShouldBeNull();
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.FeatureConflict);
        result.Diagnostics[0].Message.ShouldContain("Printing-Foundation-Features");
    }

    [Fact]
    public void Disabling_a_feature_that_is_off_while_enabling_its_child_still_plans()
    {
        // IIS-WebServerRole is off, so disabling it changes nothing; enabling its descendant turns it on.
        var plan = Plan(new DisableWindowsFeature("IIS-WebServerRole"), new EnableWindowsFeature("IIS-WebSockets")).Plan!;

        plan.Features!.Disable.ShouldBeEmpty();
        plan.Features.Enable.ShouldContain("IIS-WebServerRole");
    }

    [Fact]
    public void Feature_changes_need_the_inventory_and_its_parents()
    {
        var planner = new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid);
        planner.Plan(new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), [new EnableWindowsFeature("TelnetClient")]))
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.FeatureInventoryMissing);
        planner.Plan(new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), [new EnableWindowsFeature("TelnetClient")]) { Features = Features with { Parents = null } })
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.FeatureInventoryMissing);
    }

    [Fact]
    public void An_unknown_feature_or_one_already_in_state_does_not_plan()
    {
        Plan(new EnableWindowsFeature("NoSuchFeature")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.FeatureBlocked);
        Plan(new EnableWindowsFeature("NetFx4-AdvSrvs")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.EmptyChangeSet);
    }

    [Fact]
    public void Removes_installed_capabilities_only()
    {
        var plan = Plan(new RemoveCapability("mathrecognizer~~~~0.0.1.0")).Plan!;
        plan.Capabilities!.Remove.ShouldHaveSingleItem().FullName.ShouldBe("MathRecognizer~~~~0.0.1.0");
        plan.Capabilities.Inventory.ShouldBe(Capabilities.Fingerprint);

        Plan(new RemoveCapability("OpenSSH.Server~~~~0.0.1.0")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.CapabilityNotInstalled);
        new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid).Plan(new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), [new RemoveCapability("MathRecognizer~~~~0.0.1.0")]))
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.CapabilityInventoryMissing);
    }

    [Fact]
    public void The_fingerprint_covers_features_and_capabilities()
    {
        var a = Plan(new EnableWindowsFeature("TelnetClient")).Plan!;
        var b = Plan(new EnableWindowsFeature("IIS-WebSockets")).Plan!;
        var c = Plan(new RemoveCapability("MathRecognizer~~~~0.0.1.0")).Plan!;

        new[] { a.Fingerprint, b.Fingerprint, c.Fingerprint }.Distinct().Count().ShouldBe(3);
        PlanFingerprint.Compute(a with { Features = a.Features! with { Inventory = "other" } }).ShouldNotBe(a.Fingerprint);
    }
}
