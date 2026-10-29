using ImageForge.Core.Features;

namespace ImageForge.Core.Tests.Features;

public sealed class FeatureDependencyTests
{
    // IIS-WebSockets -> IIS-ApplicationDevelopment -> IIS-WebServer -> IIS-WebServerRole; NetFx4 is already on.
    internal static readonly FeatureInventory Inventory = new(
    [
        new("IIS-WebServerRole", FeatureState.Disabled, "Disabled"),
        new("IIS-WebServer", FeatureState.Disabled, "Disabled"),
        new("IIS-ApplicationDevelopment", FeatureState.Disabled, "Disabled"),
        new("IIS-WebSockets", FeatureState.Disabled, "Disabled"),
        new("IIS-ASPNET45", FeatureState.Disabled, "Disabled"),
        new("NetFx4-AdvSrvs", FeatureState.Enabled, "Enabled"),
        new("NetFx4Extended-ASPNET45", FeatureState.Disabled, "Disabled"),
        new("NetFx3", FeatureState.DisabledWithPayloadRemoved, "Disabled with Payload Removed"),
        new("WCF-HTTP-Activation", FeatureState.Disabled, "Disabled"),
        new("TelnetClient", FeatureState.Disabled, "Disabled"),
        new("Printing-Foundation-Features", FeatureState.Enabled, "Enabled"),
        new("Printing-Foundation-InternetPrinting-Client", FeatureState.Enabled, "Enabled"),
        new("Printing-Foundation-LPDPrintService", FeatureState.Disabled, "Disabled"),
    ], []);

    internal static readonly Dictionary<string, IReadOnlyList<string>> Parents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IIS-WebServerRole"] = [],
        ["IIS-WebServer"] = ["IIS-WebServerRole"],
        ["IIS-ApplicationDevelopment"] = ["IIS-WebServer"],
        ["IIS-WebSockets"] = ["IIS-ApplicationDevelopment"],
        ["IIS-ASPNET45"] = ["IIS-ApplicationDevelopment", "NetFx4Extended-ASPNET45"],
        ["NetFx4-AdvSrvs"] = [],
        ["NetFx4Extended-ASPNET45"] = ["NetFx4-AdvSrvs"],
        ["NetFx3"] = [],
        ["WCF-HTTP-Activation"] = ["NetFx3"],
        ["TelnetClient"] = [],
        ["Printing-Foundation-Features"] = [],
        ["Printing-Foundation-InternetPrinting-Client"] = ["Printing-Foundation-Features"],
        ["Printing-Foundation-LPDPrintService"] = ["Printing-Foundation-Features"],
    };

    [Fact]
    public void Enabling_adds_every_disabled_parent_first()
    {
        var change = FeatureDependencies.Enable(Inventory, Parents, "iis-websockets");

        change.Problems.ShouldBeEmpty();
        change.Features.ShouldBe(["IIS-WebServerRole", "IIS-WebServer", "IIS-ApplicationDevelopment", "IIS-WebSockets"]);
        change.Added.ShouldBe(["IIS-WebServerRole", "IIS-WebServer", "IIS-ApplicationDevelopment"]);
        change.NeedsSource.ShouldBeEmpty();
    }

    [Fact]
    public void A_shared_parent_is_enabled_once_and_enabled_parents_are_left_alone()
    {
        var change = FeatureDependencies.Enable(Inventory, Parents, "IIS-ASPNET45");

        change.Problems.ShouldBeEmpty();
        change.Features.ShouldBe(["IIS-WebServerRole", "IIS-WebServer", "IIS-ApplicationDevelopment", "NetFx4Extended-ASPNET45", "IIS-ASPNET45"]);
        change.Features.ShouldNotContain("NetFx4-AdvSrvs");
    }

    [Fact]
    public void A_parent_without_payload_is_reported_as_needing_a_source()
    {
        var change = FeatureDependencies.Enable(Inventory, Parents, "WCF-HTTP-Activation");

        change.Problems.ShouldBeEmpty();
        change.Features.ShouldBe(["NetFx3", "WCF-HTTP-Activation"]);
        change.NeedsSource.ShouldBe(["NetFx3"]);
    }

    [Fact]
    public void Enabling_an_enabled_feature_changes_nothing()
    {
        var change = FeatureDependencies.Enable(Inventory, Parents, "NetFx4-AdvSrvs");

        change.Problems.ShouldBeEmpty();
        change.Features.ShouldBeEmpty();
    }

    [Fact]
    public void Disabling_turns_off_enabled_dependents_first()
    {
        var change = FeatureDependencies.Disable(Inventory, Parents, "Printing-Foundation-Features");

        change.Problems.ShouldBeEmpty();
        change.Features.ShouldBe(["Printing-Foundation-InternetPrinting-Client", "Printing-Foundation-Features"]);
        change.Added.ShouldBe(["Printing-Foundation-InternetPrinting-Client"]);
        change.NeedsSource.ShouldBeEmpty();
    }

    [Fact]
    public void Disabling_a_disabled_feature_changes_nothing()
    {
        FeatureDependencies.Disable(Inventory, Parents, "TelnetClient").Features.ShouldBeEmpty();
    }

    [Fact]
    public void Unknown_parents_stop_the_change()
    {
        var parents = new Dictionary<string, IReadOnlyList<string>>(Parents, StringComparer.OrdinalIgnoreCase);
        parents.Remove("IIS-WebServer");

        var enable = FeatureDependencies.Enable(Inventory, parents, "IIS-WebSockets");
        var disable = FeatureDependencies.Disable(Inventory, parents, "Printing-Foundation-Features");

        enable.Features.ShouldBeEmpty();
        enable.Problems.ShouldHaveSingleItem().ShouldContain("'IIS-WebServer' are unknown");
        disable.Features.ShouldBeEmpty();
        disable.Problems.ShouldHaveSingleItem().ShouldContain("IIS-WebServer");
    }

    [Fact]
    public void A_missing_or_unknown_feature_stops_the_change()
    {
        var inventory = With(("IIS-WebServer", FeatureState.Unknown, "Something New"));

        FeatureDependencies.Enable(inventory, Parents, "IIS-WebSockets").Problems.ShouldHaveSingleItem().ShouldContain("Something New");
        FeatureDependencies.Enable(Inventory, Parents, "NoSuchFeature").Problems.ShouldHaveSingleItem().ShouldContain("not in this image");
        FeatureDependencies.Disable(Inventory, Parents, "NoSuchFeature").Problems.ShouldHaveSingleItem().ShouldContain("not in this image");
    }

    [Fact]
    public void Enable_pending_counts_as_on()
    {
        // Found in the VM: an offline enable leaves some features Enable Pending until first boot.
        var inventory = With(
            ("IIS-WebServerRole", FeatureState.Enabled, "Enabled"),
            ("IIS-WebServer", FeatureState.EnablePending, "Enable Pending"),
            ("IIS-ApplicationDevelopment", FeatureState.Enabled, "Enabled"),
            ("IIS-WebSockets", FeatureState.EnablePending, "Enable Pending"));

        FeatureDependencies.Enable(inventory, Parents, "IIS-WebSockets").Features.ShouldBeEmpty();
        FeatureDependencies.Enable(inventory, Parents, "IIS-ASPNET45").Features.ShouldBe(["NetFx4Extended-ASPNET45", "IIS-ASPNET45"]);
        var disable = FeatureDependencies.Disable(inventory, Parents, "IIS-WebServerRole");
        disable.Problems.ShouldBeEmpty();
        disable.Features.ShouldBe(["IIS-WebSockets", "IIS-ApplicationDevelopment", "IIS-WebServer", "IIS-WebServerRole"]);
    }

    [Fact]
    public void Disable_pending_counts_as_off_but_is_not_enabled_again()
    {
        var inventory = With(("IIS-WebServerRole", FeatureState.DisablePending, "Disable Pending"));

        FeatureDependencies.Disable(inventory, Parents, "IIS-WebServerRole").Features.ShouldBeEmpty();
        FeatureDependencies.Enable(inventory, Parents, "IIS-WebServer").Problems.ShouldHaveSingleItem().ShouldContain("Disable Pending");
    }

    [Fact]
    public void Compare_lists_every_feature_that_changed()
    {
        var after = With(
            ("IIS-WebServerRole", FeatureState.Enabled, "Enabled"),
            ("IIS-WebSockets", FeatureState.EnablePending, "Enable Pending"));

        FeatureDependencies.Compare(Inventory, after).ShouldBe(
        [
            new FeatureStateChange("IIS-WebServerRole", "Disabled", "Enabled"),
            new FeatureStateChange("IIS-WebSockets", "Disabled", "Enable Pending"),
        ]);
        FeatureDependencies.Compare(Inventory, Inventory).ShouldBeEmpty();
    }

    private static FeatureInventory With(params (string Name, FeatureState State, string Raw)[] changes) => Inventory with
    {
        Features = [.. Inventory.Features.Select(f => changes.FirstOrDefault(c => c.Name == f.Name) is { Name: not null } c
            ? f with { State = c.State, RawState = c.Raw }
            : f)],
    };

    [Fact]
    public void A_parent_cycle_stops_the_change()
    {
        var parents = new Dictionary<string, IReadOnlyList<string>>(Parents, StringComparer.OrdinalIgnoreCase)
        {
            ["IIS-WebServerRole"] = ["IIS-ApplicationDevelopment"],
        };

        FeatureDependencies.Enable(Inventory, parents, "IIS-WebSockets").Problems.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("microsoft-windows-netfx3-ondemand-package~31bf3856ad364e35~amd64~~.cab", true)]
    [InlineData("Microsoft-Windows-NetFx3-OnDemand-Package~31bf3856ad364e35~amd64~ru-RU~.cab", true)]
    [InlineData("microsoft-windows-netfx3-ondemand-package~31bf3856ad364e35~arm64~~.cab", false)]
    [InlineData("readme.txt", false)]
    public void The_netfx3_source_must_hold_the_amd64_package(string file, bool usable)
    {
        (FeatureSources.NetFx3Problem([file, "other.cab"]) is null).ShouldBe(usable);
    }

    [Fact]
    public void A_fod_folder_needs_packages_and_its_metadata()
    {
        string[] cabs = ["OpenSSH-Server-Package~31bf3856ad364e35~amd64~~.cab", "Microsoft-Windows-WMIC-FoD-Package~31bf3856ad364e35~amd64~~.cab"];
        string[] metadata = ["FoDMetadata_Client.cab", "DesktopTargetCompDB_Neutral.xml.cab"];

        FeatureSources.FodProblem(cabs, metadata).ShouldBeNull();
        FeatureSources.FodProblem(cabs, null).ShouldNotBeNull().ShouldContain("metadata");
        FeatureSources.FodProblem(cabs, ["readme.txt"]).ShouldNotBeNull().ShouldContain("FoDMetadata_Client.cab");
        FeatureSources.FodProblem(["readme.txt"], metadata).ShouldNotBeNull().ShouldContain(".cab");
    }
}
