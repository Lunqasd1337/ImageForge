using ImageForge.Core.Appx;
using ImageForge.Core.Drivers;
using ImageForge.Core.Updates;
using ImageForge.Core.Execution;
using ImageForge.Core.Features;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;
using ImageForge.Core.Workspace;

namespace ImageForge.Core.Tests.Workspace;

internal sealed class TestPaths(string root) : IWorkspacePaths
{
    public string Root => root;

    public string Base => Path.Combine(root, "base");

    public string BasePartial => Path.Combine(root, "base.partial");

    public string StateFile => Path.Combine(root, "state.json");

    public string InspectMount => Path.Combine(root, "inspect");

    public string Mount(Guid imageId) => Path.Combine(root, "mount", imageId.ToString("N"));

    public string Journal(Guid runId) => Path.Combine(root, "journal", runId.ToString("N") + ".jsonl");

    public RunLayout Run(Guid runId)
    {
        var run = Path.Combine(root, "runs", runId.ToString("N"));
        return new RunLayout(run, Path.Combine(run, "working"), Path.Combine(run, "completed"), Path.Combine(run, "assemble"));
    }
}

/// <summary>Models a mounted image as its mount folder; commit appends "+M1" to the image file.</summary>
internal sealed class FakeServicing : IImageServicing
{
    private readonly Dictionary<string, string> _images = [];

    public List<string> Calls { get; } = [];

    public HashSet<string> Mounted { get; } = [];

    public HashSet<string> OwnedHives { get; } = [];

    public bool FailExport { get; set; }

    public bool LeaveHiveLoaded { get; set; }

    /// <summary>Full names of the provisioned packages every mounted image reports.</summary>
    public List<string> Provisioned { get; } = [];

    /// <summary>A removal that reports success but leaves the package in the list.</summary>
    public bool IgnoreRemoval { get; set; }

    public bool FailAppxList { get; set; }

    /// <summary>Package full name → the dependency families its manifest lists; written into every read-only mount.</summary>
    public Dictionary<string, string[]> Dependencies { get; } = [];

    /// <summary>Feature name → state as DISM prints it, the same for every mounted image.</summary>
    public Dictionary<string, string> Features { get; } = [];

    /// <summary>Direct parents the DISM API reports; a feature missing here fails to read.</summary>
    public Dictionary<string, IReadOnlyList<string>> FeatureParents { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Features DISM turns on by itself whenever it enables anything (its default sub-features).</summary>
    public List<string> AlsoEnabled { get; } = [];

    /// <summary>Enable and disable succeed but leave every feature as it was.</summary>
    public bool IgnoreFeatureChanges { get; set; }

    /// <summary>Capability name → state as DISM prints it.</summary>
    public Dictionary<string, string> Capabilities { get; } = [];

    public Task<FeatureInventory> GetFeaturesAsync(string mountDir, CancellationToken ct)
    {
        Calls.Add("feature-list");
        return Task.FromResult(new FeatureInventory(
            [.. Features.Select(f => DismFeatureListParser.Parse($"Feature Name : {f.Key}\nState : {f.Value}\n\nThe operation completed successfully.").Features.Single())], []));
    }

    public Task<FeatureInventory> ReadFeatureDetailsAsync(string mountDir, FeatureInventory features, CancellationToken ct)
    {
        Calls.Add("feature-parents");
        var complete = features.Features.All(f => FeatureParents.ContainsKey(f.Name));
        return Task.FromResult(features with
        {
            Parents = complete ? FeatureParents.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase) : null,
            DisplayNames = features.Features.ToDictionary(f => f.Name, f => "Компонент " + f.Name),
        });
    }

    public Task EnableFeaturesAsync(string mountDir, IReadOnlyList<string> features, string? source, CancellationToken ct)
    {
        Calls.Add($"enable {string.Join(",", features)}{(source is null ? string.Empty : " from " + Path.GetFileName(source))}");
        foreach (var feature in IgnoreFeatureChanges ? [] : features.Concat(AlsoEnabled))
        {
            Features[feature] = "Enable Pending";
        }

        return Task.CompletedTask;
    }

    public Task DisableFeaturesAsync(string mountDir, IReadOnlyList<string> features, CancellationToken ct)
    {
        Calls.Add($"disable {string.Join(",", features)}");
        foreach (var feature in IgnoreFeatureChanges ? [] : features)
        {
            Features[feature] = "Disabled";
        }

        return Task.CompletedTask;
    }

    public Task<CapabilityInventory> GetCapabilitiesAsync(string mountDir, CancellationToken ct)
    {
        Calls.Add("capability-list");
        return Task.FromResult(new CapabilityInventory(
            [.. Capabilities.Select(c => new Capability(CapabilityIdentity.Parse(c.Key)!, c.Value == "Installed" ? CapabilityState.Installed : CapabilityState.NotPresent, c.Value))], []));
    }

    public Task<CapabilityInventory> ReadCapabilityNamesAsync(string mountDir, CapabilityInventory capabilities, CancellationToken ct)
    {
        Calls.Add("capability-names");
        return Task.FromResult(capabilities);
    }

    /// <summary>Capabilities DISM installs by itself whenever it adds one (language dependencies).</summary>
    public List<string> AlsoAdded { get; } = [];

    public Task AddCapabilityAsync(string mountDir, CapabilityIdentity capability, string source, CancellationToken ct)
    {
        Calls.Add($"capability-add {capability.Name} from {Path.GetFileName(source)}");
        foreach (var name in AlsoAdded.Append(capability.FullName))
        {
            Capabilities[name] = "Installed";
        }

        return Task.CompletedTask;
    }

    public Task RemoveCapabilityAsync(string mountDir, CapabilityIdentity capability, CancellationToken ct)
    {
        Calls.Add("capability-remove " + capability.Name);
        Capabilities[capability.FullName] = "Not Present";
        return Task.CompletedTask;
    }

    /// <summary>The third-party drivers every mounted image reports.</summary>
    public List<InstalledDriver> Drivers { get; } = [];

    /// <summary>Additions succeed without a new published name, as when the image already holds the package.</summary>
    public bool DriverAlreadyPresent { get; set; }

    public Task<DriverInventory> GetDriversAsync(string mountDir, CancellationToken ct)
    {
        Calls.Add("driver-list");
        return Task.FromResult(new DriverInventory([.. Drivers], []));
    }

    public Task AddDriverAsync(string mountDir, string infPath, bool forceUnsigned, CancellationToken ct)
    {
        Calls.Add($"driver-add {Path.GetFileName(infPath)}{(forceUnsigned ? " force-unsigned" : string.Empty)}");
        if (!DriverAlreadyPresent)
        {
            var number = Enumerable.Range(0, 100).First(n => !Drivers.Any(d => d.PublishedName == $"oem{n}.inf"));
            Drivers.Add(new InstalledDriver($"oem{number}.inf", @"C:\m\Windows\System32\DriverStore\FileRepository\x\" + Path.GetFileName(infPath),
                "Net", "Vendor", "1.0", DateTime.UnixEpoch, false, forceUnsigned ? "Unsigned" : "Signed"));
        }

        return Task.CompletedTask;
    }

    public Task RemoveDriverAsync(string mountDir, string publishedName, CancellationToken ct)
    {
        Calls.Add("driver-remove " + publishedName);
        Drivers.RemoveAll(d => d.PublishedName == publishedName);
        return Task.CompletedTask;
    }

    /// <summary>The servicing packages every mounted image reports.</summary>
    public List<InstalledPackage> Packages { get; } = [];

    /// <summary>Additions succeed without changing the package list, as when the image already holds the update.</summary>
    public bool PackageAlreadyPresent { get; set; }

    public Task<PackageInventory> GetPackagesAsync(string mountDir, CancellationToken ct)
    {
        Calls.Add("package-list");
        return Task.FromResult(new PackageInventory([.. Packages], []));
    }

    /// <summary>Supersedes the installed rollup and adds rollup 26100.9725.1.0, whatever the file.</summary>
    public Task AddPackageAsync(string mountDir, string packagePath, CancellationToken ct)
    {
        Calls.Add("package-add " + Path.GetFileName(packagePath));
        if (!PackageAlreadyPresent)
        {
            for (var i = 0; i < Packages.Count; i++)
            {
                if (Packages[i].IsInstalled && Packages[i].Name.StartsWith("Package_for_RollupFix~", StringComparison.Ordinal))
                {
                    Packages[i] = Packages[i] with { State = "Superseded" };
                }
            }

            Packages.Add(new InstalledPackage("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9725.1.0", "Installed", "Update"));
        }

        return Task.CompletedTask;
    }

    public Task MountReadOnlyAsync(string imageFile, int index, string mountDir, CancellationToken ct)
    {
        Calls.Add("mount read-only");
        File.WriteAllText(Path.Combine(mountDir, "marker.txt"), "mounted");
        foreach (var name in Provisioned.Where(n => n.EndsWith("_8wekyb3d8bbwe", StringComparison.Ordinal)))
        {
            WriteManifest(mountDir, PackageIdentity.Parse(name)!, Dependencies.GetValueOrDefault(name, []));
        }

        Mounted.Add(mountDir);
        _images[mountDir] = imageFile;
        return Task.CompletedTask;
    }

    private const string Microsoft = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    // A plain (non-bundle) layout for every package; only the Microsoft publisher is known here.
    private static void WriteManifest(string mountDir, PackageIdentity package, string[] dependsOn)
    {
        var folder = Path.Combine(mountDir, "Program Files", "WindowsApps", package.FullName);
        Directory.CreateDirectory(folder);
        var dependencies = string.Concat(dependsOn.Select(f => $"<PackageDependency Name=\"{f[..f.LastIndexOf('_')]}\" MinVersion=\"1.0.0.0\" Publisher=\"{Microsoft}\" />"));
        File.WriteAllText(Path.Combine(folder, "AppxManifest.xml"), $"""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="{package.Name}" Publisher="{Microsoft}" Version="{package.Version}" ProcessorArchitecture="{package.Architecture}" />
              <Dependencies>{dependencies}</Dependencies>
            </Package>
            """);
    }

    public Task<AppxInventory> GetProvisionedAppxAsync(string mountDir, CancellationToken ct)
    {
        Calls.Add("appx-list");
        if (FailAppxList)
        {
            throw new IOException("Dism.exe /Get-ProvisionedAppxPackages exit code 87");
        }

        if (!Mounted.Contains(mountDir))
        {
            throw new InvalidOperationException("not mounted");
        }

        return Task.FromResult(new AppxInventory(
            [.. Provisioned.Select(n => PackageIdentity.Parse(n)!).Select(i => new ProvisionedAppxPackage(i.Name, i))], []));
    }

    public Task RemoveProvisionedAppxAsync(string mountDir, PackageIdentity package, CancellationToken ct)
    {
        Calls.Add("appx-remove " + package.Name);
        if (!IgnoreRemoval)
        {
            Provisioned.Remove(package.FullName);
        }

        return Task.CompletedTask;
    }

    public Task MountAsync(string imageFile, int index, string mountDir, CancellationToken ct)
    {
        Calls.Add("mount");
        File.WriteAllText(Path.Combine(mountDir, "marker.txt"), "mounted");
        Mounted.Add(mountDir);
        _images[mountDir] = imageFile;
        return Task.CompletedTask;
    }

    public Task UnmountAsync(string mountDir, bool commit, CancellationToken ct)
    {
        Calls.Add(commit ? "unmount commit" : "unmount discard");
        if (!Mounted.Remove(mountDir))
        {
            throw new InvalidOperationException("not mounted");
        }

        if (commit)
        {
            File.AppendAllText(_images[mountDir], "+M1");
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(mountDir))
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsMountedAsync(string mountDir, CancellationToken ct) => Task.FromResult(Mounted.Contains(mountDir));

    public Task ExportAsync(string sourceImage, int index, string destinationImage, CancellationToken ct)
    {
        Calls.Add("export");
        if (FailExport)
        {
            throw new IOException("export failed");
        }

        File.WriteAllText(destinationImage, File.ReadAllText(sourceImage) + "+export");
        return Task.CompletedTask;
    }

    public ExecutionReport ApplyRegistryPlan(Guid runId, ExecutionPlan plan, string mountDir, CancellationToken ct)
    {
        Calls.Add("registry");
        return LeaveHiveLoaded
            ? new ExecutionReport(ExecutionOutcome.Failed, 1, 2, "unload failed", [OfflineHive.Software])
            : new ExecutionReport(ExecutionOutcome.Completed, plan.Steps.Count, null, null, []);
    }

    public IReadOnlyList<string> UnloadOwnedHives(Guid runId)
    {
        var owned = OwnedHives.Where(h => h.Contains(runId.ToString("N"), StringComparison.Ordinal)).ToList();
        OwnedHives.ExceptWith(owned);
        return owned;
    }
}
