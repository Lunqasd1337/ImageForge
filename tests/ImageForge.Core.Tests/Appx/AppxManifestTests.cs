using ImageForge.Core.Appx;

namespace ImageForge.Core.Tests.Appx;

public sealed class AppxManifestTests
{
    internal const string MicrosoftCorporation = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    internal const string MicrosoftWindows = "CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    [Theory]
    [InlineData(MicrosoftCorporation, "8wekyb3d8bbwe")]
    [InlineData(MicrosoftWindows, "cw5n1h2txyewy")]
    public void Publisher_id_is_computed_from_the_publisher_name(string publisher, string id)
    {
        PublisherId.From(publisher).ShouldBe(id);
    }

    internal static string AppManifest(string name, string version, string arch, bool framework = false, string dependencies = "") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3">
          <Identity Name="{name}" Publisher="{MicrosoftCorporation}" Version="{version}" ProcessorArchitecture="{arch}" />
          <Properties>
            <DisplayName>ms-resource:AppName</DisplayName>
            {(framework ? "<Framework>true</Framework>" : string.Empty)}
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.22621.0" />
            {dependencies}
          </Dependencies>
        </Package>
        """;

    [Fact]
    public void Reads_identity_framework_flag_and_dependencies()
    {
        var manifest = AppxManifestParser.ParsePackage(AppManifest(
            "Microsoft.GamingApp", "2026.514.51.0", "x64",
            dependencies: $"""
                <PackageDependency Name="Microsoft.VCLibs.140.00" MinVersion="14.0.30704.0" Publisher="{MicrosoftCorporation}" />
                <PackageDependency Name="Microsoft.UI.Xaml.2.8" MinVersion="8.2310.30001.0" Publisher="{MicrosoftCorporation}" />
                <uap3:MainPackageDependency Name="Microsoft.Xbox.TCUI" />
                """)).ShouldNotBeNull();

        manifest.Name.ShouldBe("Microsoft.GamingApp");
        manifest.PublisherId.ShouldBe("8wekyb3d8bbwe");
        manifest.Version.ShouldBe(new Version(2026, 514, 51, 0));
        manifest.Architecture.ShouldBe("x64");
        manifest.IsFramework.ShouldBeFalse();
        manifest.Dependencies.ShouldBe(
        [
            new AppxDependency("Microsoft.VCLibs.140.00_8wekyb3d8bbwe"),
            new AppxDependency("Microsoft.UI.Xaml.2.8_8wekyb3d8bbwe"),
            new AppxDependency("Microsoft.Xbox.TCUI_8wekyb3d8bbwe"),
        ]);
    }

    [Fact]
    public void Reads_the_framework_flag()
    {
        AppxManifestParser.ParsePackage(AppManifest("Microsoft.VCLibs.140.00", "14.0.33519.0", "x64", framework: true))!
            .IsFramework.ShouldBeTrue();
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<Package><Identity Name=\"A.B\" /></Package>")]
    [InlineData("<!DOCTYPE x [<!ENTITY e \"boom\">]><Package><Identity Name=\"&e;\" Publisher=\"CN=x\" Version=\"1.0.0.0\" /></Package>")]
    public void A_manifest_that_cannot_be_trusted_is_rejected(string xml)
    {
        AppxManifestParser.ParsePackage(xml).ShouldBeNull();
    }

    [Fact]
    public void Lists_the_application_packages_of_a_bundle()
    {
        var bundle = AppxManifestParser.ParseBundle($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle" SchemaVersion="5.0">
              <Identity Name="Microsoft.WindowsStore" Publisher="{MicrosoftCorporation}" Version="22506.1400.2.0" />
              <Packages>
                <Package Type="application" Version="22506.1400.2.0" Architecture="x64" FileName="x64.msix" />
                <Package Type="application" Version="22506.1400.2.0" Architecture="arm64" FileName="arm64.msix" />
                <Package Type="resource" Version="22506.1400.2.0" ResourceId="split.scale-100" FileName="scale.msix" />
              </Packages>
            </Bundle>
            """).ShouldNotBeNull();

        bundle.Name.ShouldBe("Microsoft.WindowsStore");
        bundle.PublisherId.ShouldBe("8wekyb3d8bbwe");
        bundle.Applications.ShouldBe([("x64", new Version(22506, 1400, 2, 0)), ("arm64", new Version(22506, 1400, 2, 0))]);
    }
}
