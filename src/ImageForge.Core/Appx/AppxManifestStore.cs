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
        switch (MountFileReader.TryReadText(
            mountRoot, [.. WindowsApps, id.FullName, "AppxMetadata", "AppxBundleManifest.xml"], AppxManifestParser.MaxBytes, out var bundleXml))
        {
            case MountReadStatus.Missing:
                return ReadApplication(mountRoot, package, id.FullName, id.Version, out var problem) is { } facts
                    ? package with { Facts = facts, FactsProblem = null }
                    : package with { Facts = null, FactsProblem = problem };
            case MountReadStatus.Read:
                break;
            case var status:
                return package with { Facts = null, FactsProblem = $"AppxBundleManifest.xml {Describe(status)}" };
        }

        var bundle = AppxManifestParser.ParseBundle(bundleXml);
        if (bundle is null || bundle.Name != id.Name || bundle.PublisherId != id.PublisherId || bundle.Version != id.Version)
        {
            return package with { Facts = null, FactsProblem = "AppxBundleManifest.xml does not describe this package" };
        }

        // The x64 image installs the x64 application package, else neutral, else x86; take the first one present.
        var problems = new List<string>();
        foreach (var (architecture, version) in ArchitecturePreference.SelectMany(a => bundle.Applications.Where(p => p.Architecture == a)))
        {
            var folder = $"{id.Name}_{version.ToString(4)}_{architecture}__{id.PublisherId}";
            if (ReadApplication(mountRoot, package, folder, version, out var problem) is { } facts)
            {
                return package with { Facts = facts, FactsProblem = null };
            }

            problems.Add(problem!);
        }

        return package with
        {
            Facts = new AppxPackageFacts(IsFramework: false, DependsOn: null),
            FactsProblem = problems.Count == 0 ? "the bundle has no application package for x64" : string.Join("; ", problems),
        };
    }

    private static AppxPackageFacts? ReadApplication(string mountRoot, ProvisionedAppxPackage package, string folder, Version version, out string? problem)
    {
        var id = package.Identity;
        var status = MountFileReader.TryReadText(mountRoot, [.. WindowsApps, folder, "AppxManifest.xml"], AppxManifestParser.MaxBytes, out var xml);
        if (status != MountReadStatus.Read)
        {
            problem = $@"{folder}\AppxManifest.xml {Describe(status)}";
            return null;
        }

        if (AppxManifestParser.ParsePackage(xml) is not { } manifest || manifest.Name != id.Name || manifest.PublisherId != id.PublisherId || manifest.Version != version)
        {
            problem = $@"{folder}\AppxManifest.xml does not describe this package";
            return null;
        }

        problem = null;
        return new AppxPackageFacts(manifest.IsFramework, [.. manifest.Dependencies.Select(d => d.FamilyName).Distinct()]);
    }

    private static string Describe(MountReadStatus status) => status switch
    {
        MountReadStatus.Missing => "is missing",
        MountReadStatus.Refused => "is behind a symbolic link or junction",
        MountReadStatus.TooLarge => "is larger than 1 MB",
        _ => "cannot be read (access denied)",
    };

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

}
