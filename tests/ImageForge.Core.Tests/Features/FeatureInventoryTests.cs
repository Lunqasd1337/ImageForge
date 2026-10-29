using ImageForge.Core.Features;
using ImageForge.Core.Servicing;

namespace ImageForge.Core.Tests.Features;

public sealed class FeatureInventoryTests
{
    private const string Features = """

        Deployment Image Servicing and Management tool
        Version: 10.0.26100.1

        Image Version: 10.0.26300.1000

        Features listing for package : Microsoft-Windows-Foundation-Package~31bf3856ad364e35~amd64~~10.0.26100.1

        Feature Name : Printing-PrintToPDFServices-Features
        State : Enabled

        Feature Name : TelnetClient
        State : Disabled

        Feature Name : NetFx3
        State : Disabled with Payload Removed

        Feature Name : Microsoft-Windows-Subsystem-Linux
        State : Enable Pending

        Feature Name : SMB1Protocol
        State : Something New

        The operation completed successfully.

        """;

    private const string Capabilities = """

        Deployment Image Servicing and Management tool
        Version: 10.0.26100.1

        Image Version: 10.0.26300.1000

        Capability listing:

        Capability Identity : Language.Basic~~~en-US~0.0.1.0
        State : Installed

        Capability Identity : OpenSSH.Server~~~~0.0.1.0
        State : Not Present

        Capability Identity : VBSCRIPT~~~~
        State : Installed

        Capability Identity : Media.WindowsMediaPlayer~~~~0.0.12.0
        State : Staged

        The operation completed successfully.

        """;

    [Fact]
    public void Parses_every_feature_with_its_state()
    {
        var result = DismFeatureListParser.Parse(Features);

        result.Problems.ShouldBeEmpty();
        result.Features.Select(f => (f.Name, f.State, f.RawState)).ShouldBe(
        [
            ("Printing-PrintToPDFServices-Features", FeatureState.Enabled, "Enabled"),
            ("TelnetClient", FeatureState.Disabled, "Disabled"),
            ("NetFx3", FeatureState.DisabledWithPayloadRemoved, "Disabled with Payload Removed"),
            ("Microsoft-Windows-Subsystem-Linux", FeatureState.Pending, "Enable Pending"),
            ("SMB1Protocol", FeatureState.Unknown, "Something New"),
        ]);
    }

    [Fact]
    public void A_feature_list_without_the_success_line_is_a_problem()
    {
        var result = DismFeatureListParser.Parse(Features.Replace("The operation completed successfully.", string.Empty, StringComparison.Ordinal));

        result.Problems.ShouldHaveSingleItem().ShouldContain("operation completed successfully");
    }

    [Theory]
    [InlineData("Feature Name : TelnetClient /All\nState : Disabled", "not a valid feature name")]
    [InlineData("Feature Name : TelnetClient", "has no State")]
    [InlineData("State : Disabled", "has no Feature Name")]
    [InlineData("Feature Name : TelnetClient\nState : Disabled\n\nFeature Name : TelnetClient\nState : Enabled", "listed twice")]
    public void A_bad_feature_block_is_a_problem(string blocks, string problem)
    {
        var result = DismFeatureListParser.Parse(blocks + "\n\nThe operation completed successfully.\n");

        result.Problems.ShouldHaveSingleItem().ShouldContain(problem);
    }

    [Fact]
    public void Parses_every_capability_with_its_identity_and_state()
    {
        var result = DismCapabilityListParser.Parse(Capabilities);

        result.Problems.ShouldBeEmpty();
        result.Capabilities.Select(c => (c.Identity.FullName, c.State, c.RawState)).ShouldBe(
        [
            ("Language.Basic~~~en-US~0.0.1.0", CapabilityState.Installed, "Installed"),
            ("OpenSSH.Server~~~~0.0.1.0", CapabilityState.NotPresent, "Not Present"),
            ("VBSCRIPT~~~~", CapabilityState.Installed, "Installed"),
            ("Media.WindowsMediaPlayer~~~~0.0.12.0", CapabilityState.Unknown, "Staged"),
        ]);
        var language = result.Capabilities[0].Identity;
        language.Name.ShouldBe("Language.Basic");
        language.Language.ShouldBe("en-US");
        language.Version.ShouldBe("0.0.1.0");
        result.Capabilities[2].Identity.Version.ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("Language.Basic~~~en-US~0.0.1.0", true)]
    [InlineData("Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0", true)]
    [InlineData("Microsoft.Windows.Ethernet.Client.Intel.E1i68x64~~~~0.0.1.0", true)]
    [InlineData("WMIC~~~~", true)]
    [InlineData("Some.Thing~amd64~~~1.0", true)]
    [InlineData("Language.Basic~~~en-US", false)]
    [InlineData("Language.Basic~~~en-US~0.0.1.0~", false)]
    [InlineData("~~~~0.0.1.0", false)]
    [InlineData("Language.Basic~~~en US~0.0.1.0", false)]
    [InlineData("Language.Basic~~~en-US~0.0.1.0 /Quiet", false)]
    [InlineData("Language.Basic~~~en-US~0.0.1", true)]
    [InlineData("Language.Basic~~~en-US~0..1.0", false)]
    [InlineData("Language.Basic~~~en-US~1.2.3.4.5", false)]
    public void Capability_identities_are_parsed_strictly(string identity, bool valid)
    {
        (CapabilityIdentity.Parse(identity) is not null).ShouldBe(valid);
    }

    [Theory]
    [InlineData("TelnetClient", true)]
    [InlineData("Microsoft-Hyper-V-All", true)]
    [InlineData("WCF-HTTP-Activation45", true)]
    [InlineData("Printing-Foundation-LPDPrintService", true)]
    [InlineData("", false)]
    [InlineData("-TelnetClient", false)]
    [InlineData("Telnet Client", false)]
    [InlineData("TelnetClient:x", false)]
    public void Feature_names_are_checked(string name, bool valid)
    {
        WindowsFeature.IsValidName(name).ShouldBe(valid);
    }

    [Fact]
    public void Feature_and_capability_arguments_keep_downloads_off()
    {
        DismCliArguments.EnableFeatures(@"C:\IF\m", ["TelnetClient"])
            .ShouldBe(["/English", @"/Image:C:\IF\m", "/Enable-Feature", "/FeatureName:TelnetClient", "/LimitAccess"]);
        DismCliArguments.EnableFeatures(@"C:\IF\m", ["IIS-WebServerRole", "IIS-WebServer"], @"C:\IF\media\sources\sxs")
            .ShouldBe(["/English", @"/Image:C:\IF\m", "/Enable-Feature", "/FeatureName:IIS-WebServerRole", "/FeatureName:IIS-WebServer",
                @"/Source:C:\IF\media\sources\sxs", "/LimitAccess"]);
        DismCliArguments.DisableFeatures(@"C:\IF\m", ["TelnetClient"])
            .ShouldBe(["/English", @"/Image:C:\IF\m", "/Disable-Feature", "/FeatureName:TelnetClient"]);
        DismCliArguments.GetFeatures(@"C:\IF\m").ShouldBe(["/English", @"/Image:C:\IF\m", "/Get-Features"]);
        DismCliArguments.GetFeatureInfo(@"C:\IF\m", "NetFx3")
            .ShouldBe(["/English", @"/Image:C:\IF\m", "/Get-FeatureInfo", "/FeatureName:NetFx3"]);
        DismCliArguments.GetCapabilities(@"C:\IF\m").ShouldBe(["/English", @"/Image:C:\IF\m", "/Get-Capabilities"]);

        var id = CapabilityIdentity.Parse("OpenSSH.Client~~~~0.0.1.0")!;
        DismCliArguments.GetCapabilityInfo(@"C:\IF\m", id)
            .ShouldBe(["/English", @"/Image:C:\IF\m", "/Get-CapabilityInfo", "/CapabilityName:OpenSSH.Client~~~~0.0.1.0"]);
        DismCliArguments.RemoveCapability(@"C:\IF\m", id)
            .ShouldBe(["/English", @"/Image:C:\IF\m", "/Remove-Capability", "/CapabilityName:OpenSSH.Client~~~~0.0.1.0"]);
    }

    [Theory]
    [InlineData("TelnetClient /All")]
    [InlineData("")]
    public void Feature_arguments_refuse_a_bad_name(string name)
    {
        Should.Throw<ArgumentException>(() => DismCliArguments.EnableFeatures(@"C:\IF\m", ["TelnetClient", name]));
        Should.Throw<ArgumentException>(() => DismCliArguments.DisableFeatures(@"C:\IF\m", [name]));
        Should.Throw<ArgumentException>(() => DismCliArguments.GetFeatureInfo(@"C:\IF\m", name));
    }

    [Fact]
    public void Feature_arguments_need_a_name_and_a_local_source()
    {
        Should.Throw<ArgumentException>(() => DismCliArguments.EnableFeatures(@"C:\IF\m", []));
        Should.Throw<ArgumentException>(() => DismCliArguments.EnableFeatures(@"C:\IF\m", ["NetFx3"], @"\\server\share\sxs"));
        Should.Throw<ArgumentException>(() => DismCliArguments.EnableFeatures(@"C:\IF\m", ["NetFx3"], "sources\\sxs"));
    }

    [Fact]
    public void Capability_arguments_refuse_a_hand_built_identity()
    {
        var forged = new CapabilityIdentity("OpenSSH.Client~~~~0.0.1.0 /Quiet", "OpenSSH.Client", "", "", "", "0.0.1.0");

        Should.Throw<ArgumentException>(() => DismCliArguments.RemoveCapability(@"C:\IF\m", forged));
    }

    [Fact]
    public void Reads_the_fields_of_an_info_block()
    {
        const string Info = """
            Deployment Image Servicing and Management tool
            Version: 10.0.26100.1

            Image Version: 10.0.26300.1000

            Feature Information:

            Feature Name : NetFx3
            Display Name : .NET Framework 3.5 (includes .NET 2.0 and 3.0)
            Description : .NET Framework 3.5 (includes .NET 2.0 and 3.0)
            Restart Required : Possible
            State : Disabled with Payload Removed

            Custom Properties:

            FWLink : http://go.microsoft.com/fwlink/?LinkId=296822

            The operation completed successfully.
            """;

        var fields = DismInfoParser.Parse(Info);

        fields.ShouldNotBeNull();
        fields["Display Name"].ShouldBe(".NET Framework 3.5 (includes .NET 2.0 and 3.0)");
        fields["State"].ShouldBe("Disabled with Payload Removed");
        fields["FWLink"].ShouldBe("http://go.microsoft.com/fwlink/?LinkId=296822");
        fields.ContainsKey("Version").ShouldBeFalse();
        DismInfoParser.Parse(Info.Replace("The operation completed successfully.", string.Empty, StringComparison.Ordinal)).ShouldBeNull();
    }
}
