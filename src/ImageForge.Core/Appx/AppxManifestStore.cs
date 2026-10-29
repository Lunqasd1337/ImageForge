using ImageForge.Core.Safety;

namespace ImageForge.Core.Appx;

/// <summary>
/// Reads the manifests of provisioned and inbox packages from a mounted image. A provisioned bundle is read through
/// the application package the x64 image installs. A manifest is trusted only when its identity matches the package.
/// </summary>
public static class AppxManifestStore
{
    private static readonly string[] WindowsApps = ["Program Files", "WindowsApps"];
    private static readonly string[] ArchitecturePreference = ["x64", "neutral", "x86"];

    public static AppxInventory Attach(string mountRoot, AppxInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return inventory with
        {
            Packages = [.. inventory.Packages.Select(p => Read(mountRoot, p))],
            InboxDependencies = ReadInbox(mountRoot),
        };
    }

    private static ProvisionedAppxPackage Read(string mountRoot, ProvisionedAppxPackage package)
    {
        var id = package.Identity;
        var bundleStatus = MountFileReader.TryReadText(
            mountRoot, [.. WindowsApps, id.FullName, "AppxMetadata", "AppxBundleManifest.xml"], AppxManifestParser.MaxBytes, out var bundleXml);
        string manifestFolder;
        Version expectedVersion;
        switch (bundleStatus)
        {
            case MountReadStatus.Refused:
                return Unknown(package, "the bundle manifest is behind a symbolic link or junction");
            case MountReadStatus.TooLarge:
                return Unknown(package, "the bundle manifest is larger than 1 MB");
            case MountReadStatus.Denied:
                return Unknown(package, "the bundle manifest cannot be read (access denied)");
            case MountReadStatus.Read:
                var bundle = AppxManifestParser.ParseBundle(bundleXml);
                if (bundle is null || bundle.Name != id.Name || bundle.PublisherId != id.PublisherId || bundle.Version != id.Version)
                {
                    return Unknown(package, "AppxBundleManifest.xml does not describe this package");
                }

                var application = ArchitecturePreference
                    .SelectMany(a => bundle.Applications.Where(p => p.Architecture == a))
                    .Cast<(string Architecture, Version Version)?>()
                    .FirstOrDefault();
                if (application is not { } chosen)
                {
                    return Unknown(package, "the bundle has no application package for x64");
                }

                manifestFolder = $"{id.Name}_{chosen.Version.ToString(4)}_{chosen.Architecture}__{id.PublisherId}";
                expectedVersion = chosen.Version;
                break;
            default:
                manifestFolder = id.FullName;
                expectedVersion = id.Version;
                break;
        }

        return MountFileReader.TryReadText(mountRoot, [.. WindowsApps, manifestFolder, "AppxManifest.xml"], AppxManifestParser.MaxBytes, out var xml) switch
        {
            MountReadStatus.Missing => Unknown(package, $@"{manifestFolder}\AppxManifest.xml is missing"),
            MountReadStatus.Refused => Unknown(package, $@"{manifestFolder}\AppxManifest.xml is behind a symbolic link or junction"),
            MountReadStatus.TooLarge => Unknown(package, $@"{manifestFolder}\AppxManifest.xml is larger than 1 MB"),
            MountReadStatus.Denied => Unknown(package, $@"{manifestFolder}\AppxManifest.xml cannot be read (access denied)"),
            _ => AppxManifestParser.ParsePackage(xml) is { } manifest && manifest.Name == id.Name && manifest.PublisherId == id.PublisherId && manifest.Version == expectedVersion
                ? package with { Facts = new AppxPackageFacts(manifest.IsFramework, [.. manifest.Dependencies.Select(d => d.FamilyName).Distinct()]) }
                : Unknown(package, $@"{manifestFolder}\AppxManifest.xml does not describe this package"),
        };
    }

    private static string[]? ReadInbox(string mountRoot)
    {
        var systemApps = Path.Combine(mountRoot, "Windows", "SystemApps");
        var dependencies = new SortedSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(systemApps))
        {
            return [];
        }

        string[] folders;
        try
        {
            folders = Directory.GetDirectories(systemApps);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }

        foreach (var folder in folders)
        {
            switch (MountFileReader.TryReadText(mountRoot, ["Windows", "SystemApps", Path.GetFileName(folder), "AppxManifest.xml"], AppxManifestParser.MaxBytes, out var xml))
            {
                case MountReadStatus.Missing:
                    continue;
                case MountReadStatus.Refused or MountReadStatus.Denied or MountReadStatus.TooLarge:
                    return null;
            }

            if (AppxManifestParser.ParsePackage(xml) is not { } manifest)
            {
                return null;
            }

            dependencies.UnionWith(manifest.Dependencies.Select(d => d.FamilyName));
        }

        return [.. dependencies];
    }

    private static ProvisionedAppxPackage Unknown(ProvisionedAppxPackage package, string problem) =>
        package with { Facts = null, FactsProblem = problem };
}
