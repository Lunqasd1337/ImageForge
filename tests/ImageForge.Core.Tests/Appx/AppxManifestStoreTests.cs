using ImageForge.Core.Appx;
using ImageForge.Core.Tests.Workspace;

namespace ImageForge.Core.Tests.Appx;

public sealed class AppxManifestStoreTests : IDisposable
{
    private const string Gaming = "Microsoft.GamingApp_2026.514.51.0_neutral_~_8wekyb3d8bbwe";
    private const string Teams = "MSTeams_1.0.0.1_x64__8wekyb3d8bbwe";
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Root => _dir.Combine("mount");

    internal static void WriteBundle(TempDirectory dir, string root, string name, string version, string appVersion, string appArch, string appManifest)
    {
        var folder = Path.Combine(root, "Program Files", "WindowsApps", $"{name}_{version}_neutral_~_8wekyb3d8bbwe");
        Directory.CreateDirectory(Path.Combine(folder, "AppxMetadata"));
        File.WriteAllText(Path.Combine(folder, "AppxMetadata", "AppxBundleManifest.xml"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle" SchemaVersion="5.0">
              <Identity Name="{name}" Publisher="{AppxManifestTests.MicrosoftCorporation}" Version="{version}" />
              <Packages>
                <Package Type="application" Version="{appVersion}" Architecture="arm64" FileName="arm64.msix" />
                <Package Type="application" Version="{appVersion}" Architecture="{appArch}" FileName="app.msix" />
              </Packages>
            </Bundle>
            """);
        var app = Path.Combine(root, "Program Files", "WindowsApps", $"{name}_{appVersion}_{appArch}__8wekyb3d8bbwe");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "AppxManifest.xml"), appManifest);
        _ = dir;
    }

    internal static void WritePackage(string root, string fullName, string manifest)
    {
        var folder = Path.Combine(root, "Program Files", "WindowsApps", fullName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "AppxManifest.xml"), manifest);
    }

    [Fact]
    public void Reads_a_bundle_through_its_application_package_for_x64()
    {
        WriteBundle(_dir, Root, "Microsoft.GamingApp", "2026.514.51.0", "2026.514.51.0", "x64", AppxManifestTests.AppManifest(
            "Microsoft.GamingApp", "2026.514.51.0", "x64",
            dependencies: $"<PackageDependency Name=\"Microsoft.Xbox.TCUI\" MinVersion=\"1.0.0.0\" Publisher=\"{AppxManifestTests.MicrosoftCorporation}\" />"));

        var inventory = AppxManifestStore.Attach(Root, AppxPlanningTests.Inventory(Gaming));

        var facts = inventory.Packages.Single().Facts.ShouldNotBeNull();
        facts.IsFramework.ShouldBeFalse();
        facts.DependsOn.ShouldBe(["Microsoft.Xbox.TCUI_8wekyb3d8bbwe"]);
    }

    [Fact]
    public void Reads_a_plain_package()
    {
        WritePackage(Root, Teams, AppxManifestTests.AppManifest("MSTeams", "1.0.0.1", "x64"));

        var package = AppxManifestStore.Attach(Root, AppxPlanningTests.Inventory(Teams)).Packages.Single();

        package.Facts.ShouldNotBeNull().DependsOn.ShouldBeEmpty();
        package.FactsProblem.ShouldBeNull();
    }

    [Fact]
    public void A_missing_manifest_leaves_the_dependencies_unknown()
    {
        var package = AppxManifestStore.Attach(Root, AppxPlanningTests.Inventory(Teams)).Packages.Single();

        package.Facts.ShouldBeNull();
        package.FactsProblem.ShouldNotBeNull().ShouldContain("AppxManifest.xml");
    }

    [Fact]
    public void A_manifest_of_another_package_is_not_trusted()
    {
        WritePackage(Root, Teams, AppxManifestTests.AppManifest("Other.App", "1.0.0.1", "x64"));

        AppxManifestStore.Attach(Root, AppxPlanningTests.Inventory(Teams)).Packages.Single().Facts.ShouldBeNull();
    }

    [Fact]
    public void A_linked_folder_is_not_followed()
    {
        var real = _dir.Combine("elsewhere");
        WritePackage(real, Teams, AppxManifestTests.AppManifest("MSTeams", "1.0.0.1", "x64"));
        Directory.CreateDirectory(Path.Combine(Root, "Program Files"));
        Directory.CreateSymbolicLink(Path.Combine(Root, "Program Files", "WindowsApps"), Path.Combine(real, "Program Files", "WindowsApps"));

        var package = AppxManifestStore.Attach(Root, AppxPlanningTests.Inventory(Teams)).Packages.Single();

        package.Facts.ShouldBeNull();
        package.FactsProblem.ShouldNotBeNull().ShouldContain("link");
    }

    [Fact]
    public void An_oversized_manifest_is_not_read()
    {
        WritePackage(Root, Teams, AppxManifestTests.AppManifest("MSTeams", "1.0.0.1", "x64") + new string(' ', AppxManifestParser.MaxBytes));

        AppxManifestStore.Attach(Root, AppxPlanningTests.Inventory(Teams)).Packages.Single().FactsProblem.ShouldNotBeNull().ShouldContain("larger");
    }

    [Fact]
    public void Inbox_apps_report_what_they_depend_on()
    {
        var folder = Path.Combine(Root, "Windows", "SystemApps", "MicrosoftWindows.Client.CBS_cw5n1h2txyewy");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "AppxManifest.xml"), AppxManifestTests.AppManifest(
            "MicrosoftWindows.Client.CBS", "1000.26100.1.0", "x64",
            dependencies: $"<PackageDependency Name=\"Microsoft.WindowsAppRuntime.1.6\" MinVersion=\"1.0.0.0\" Publisher=\"{AppxManifestTests.MicrosoftCorporation}\" />"));

        var inventory = AppxManifestStore.Attach(Root, AppxPlanningTests.Inventory(Array.Empty<string>()));

        inventory.InboxDependencies.ShouldNotBeNull().ShouldBe(["Microsoft.WindowsAppRuntime.1.6_8wekyb3d8bbwe"]);
    }

    [Fact]
    public void An_unreadable_inbox_app_makes_inbox_dependencies_unknown()
    {
        var folder = Path.Combine(Root, "Windows", "SystemApps", "Broken_cw5n1h2txyewy");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "AppxManifest.xml"), "not xml");

        AppxManifestStore.Attach(Root, AppxPlanningTests.Inventory(Array.Empty<string>())).InboxDependencies.ShouldBeNull();
    }
}
