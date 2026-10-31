using ImageForge.Core.Safety;

namespace ImageForge.Core.Workspace;

public enum LocationProblemKind
{
    NotLocalPath,
    Same,
    Nested,

    /// <summary>A mapped network drive.</summary>
    NetworkVolume,

    /// <summary>The work folder's volume is not NTFS, which DISM mounts need.</summary>
    NotNtfs,

    /// <summary>OneDrive, Dropbox, Google Drive or iCloud: files may be online-only or be uploaded while they are written.</summary>
    SyncedFolder,

    /// <summary>Another ImageForge engine has the work folder open: one writer per workspace (plan, section 6).</summary>
    InUse,
}

/// <summary>A problem with the chosen folders. For <see cref="LocationProblemKind.Nested"/>, <see cref="Location"/> lies inside <see cref="Other"/>.</summary>
public sealed record LocationProblem(LocationProblemKind Kind, string Location, string? Other)
{
    /// <summary>Shown but not blocking: a network or synced source or output works, only slower or less safely.</summary>
    public bool IsWarning { get; init; }
}

public enum VolumeKind
{
    Local,
    Removable,
    Network,
}

/// <param name="FileSystem">NTFS, ReFS, exFAT and so on, as Windows names it; null when unknown.</param>
public sealed record VolumeFacts(VolumeKind Kind, string? FileSystem);

/// <summary>The source, work folder and output folder must be local drive paths that neither match nor contain one another.</summary>
public static class WorkspaceLocations
{
    public const string Source = "Source";
    public const string WorkDir = "WorkDir";
    public const string OutputDir = "OutputDir";

    public static IReadOnlyList<LocationProblem> Validate(string source, string workDir, string outputDir)
    {
        var problems = new List<LocationProblem>();
        var normalized = new List<(string Name, string Path)>();

        foreach (var (name, path) in new[] { (Source, source), (WorkDir, workDir), (OutputDir, outputDir) })
        {
            if (WindowsPath.TryNormalize(path, out var full) == WriteScopeViolation.None)
            {
                normalized.Add((name, full));
            }
            else
            {
                problems.Add(new LocationProblem(LocationProblemKind.NotLocalPath, name, null));
            }
        }

        for (var i = 0; i < normalized.Count; i++)
        {
            for (var j = i + 1; j < normalized.Count; j++)
            {
                var a = normalized[i];
                var b = normalized[j];
                var bInA = WindowsPath.IsSameOrUnder(b.Path, a.Path);
                var aInB = WindowsPath.IsSameOrUnder(a.Path, b.Path);
                if (aInB && bInA)
                {
                    problems.Add(new LocationProblem(LocationProblemKind.Same, a.Name, b.Name));
                }
                else if (bInA)
                {
                    problems.Add(new LocationProblem(LocationProblemKind.Nested, b.Name, a.Name));
                }
                else if (aInB)
                {
                    problems.Add(new LocationProblem(LocationProblemKind.Nested, a.Name, b.Name));
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// Plan, section 6: mount and scratch need local NTFS, so the work folder must not be on a network drive, another file
    /// system or a synced folder. A network or synced source or output is reported before copying but allowed. A volume
    /// whose facts are unknown passes.
    /// </summary>
    public static IReadOnlyList<LocationProblem> CheckVolumes(
        string source, string workDir, string outputDir, Func<string, VolumeFacts?> volume, Func<string, bool> isSynced)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(isSynced);
        var problems = new List<LocationProblem>();
        foreach (var (name, path) in new[] { (Source, source), (WorkDir, workDir), (OutputDir, outputDir) })
        {
            var warning = name != WorkDir;
            var facts = volume(Root(path));
            if (facts?.Kind == VolumeKind.Network)
            {
                problems.Add(new LocationProblem(LocationProblemKind.NetworkVolume, name, null) { IsWarning = warning });
            }
            else if (!warning && facts?.FileSystem is { } fileSystem && !fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new LocationProblem(LocationProblemKind.NotNtfs, name, fileSystem));
            }

            if (isSynced(path))
            {
                problems.Add(new LocationProblem(LocationProblemKind.SyncedFolder, name, null) { IsWarning = warning });
            }
        }

        return problems;
    }

    private static readonly string[] SyncedNames = ["Dropbox", "Google Drive", "My Drive", "iCloudDrive", "iCloud Drive"];

    /// <summary>Under one of the given sync roots (the OneDrive folders the engine reads from the environment), or under a folder with a sync client's usual name.</summary>
    public static bool IsSynced(string path, IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(roots);
        if (WindowsPath.TryNormalize(path, out var full) != WriteScopeViolation.None)
        {
            return false;
        }

        return roots.Any(r => WindowsPath.TryNormalize(r, out var root) == WriteScopeViolation.None && WindowsPath.IsSameOrUnder(full, root))
            || full.Split('\\').Any(segment => SyncedNames.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The drive root as Windows writes it, C:\.</summary>
    public static string Root(string path) =>
        path is { Length: >= 2 } && char.IsAsciiLetter(path[0]) && path[1] == ':' ? char.ToUpperInvariant(path[0]) + @":\" : path;
}
