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
            File.Delete(entry);
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
