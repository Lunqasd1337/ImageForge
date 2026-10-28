using ImageForge.Core.Appx;
using ImageForge.Core.Execution;
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
