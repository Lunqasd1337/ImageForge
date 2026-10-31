using System.Globalization;

namespace ImageForge.Core.Workspace;

/// <param name="SourceBytes">All files of the source media.</param>
/// <param name="BaseCopyPresent">The verified base copy is already in the work folder and is reused.</param>
/// <param name="InstallWimBytes">sources\install.wim: the working copy and the exported result are each about this size.</param>
/// <param name="ImageExpandedBytes">TOTALBYTES of the serviced image from the WIM XML: what the mount takes; null when unknown.</param>
public sealed record SpaceInputs(long SourceBytes, bool BaseCopyPresent, long InstallWimBytes, long? ImageExpandedBytes)
{
    /// <summary>The update packages added to the image, WinRE and boot.wim.</summary>
    public long UpdateBytes { get; init; }

    public bool ServicesWinRe { get; init; }

    /// <summary>sources\boot.wim when it is serviced; 0 when it is not.</summary>
    public long BootWimBytes { get; init; }
}

/// <param name="WorkBytes">Peak need in the work folder's volume.</param>
/// <param name="OutputBytes">Need in the output folder's volume: the staging copy of the media.</param>
public sealed record SpaceEstimate(long WorkBytes, long OutputBytes);

/// <summary>
/// The peak disk need of a run, worked out before it starts and checked again before the large steps (plan, section 6).
/// The figures are deliberately generous: a run that stops for space halfway loses more time than one that asks for more.
/// </summary>
public static class DiskSpace
{
    public const long Reserve = 2L << 30;

    /// <summary>When the WIM XML gives no TOTALBYTES: a Windows 11 edition mounts to about 18 GB.</summary>
    public const long DefaultExpandedBytes = 24L << 30;

    /// <summary>WinRE is extracted, mounted and exported: about three copies of a 1 GB image.</summary>
    public const long WinReAllowance = 3L << 30;

    public static SpaceEstimate Estimate(SpaceInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var work = (inputs.BaseCopyPresent ? 0 : inputs.SourceBytes)
            + inputs.InstallWimBytes
            + (inputs.ImageExpandedBytes ?? DefaultExpandedBytes)
            // A package is copied in, DISM unpacks it into its scratch folder, and the image grows by about as much again.
            + 3 * inputs.UpdateBytes
            + (inputs.ServicesWinRe ? WinReAllowance : 0)
            + 2 * inputs.BootWimBytes
            + inputs.InstallWimBytes
            + Reserve;
        return new(work, inputs.SourceBytes + Reserve);
    }

    /// <summary>Volumes that lack space, as messages; empty when both have enough. A volume whose free space is unknown passes.</summary>
    public static IReadOnlyList<string> Check(SpaceEstimate estimate, string workDir, string outputDir, Func<string, long?> freeBytes)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(freeBytes);
        var workRoot = Root(workDir);
        var outputRoot = Root(outputDir);
        var needs = string.Equals(workRoot, outputRoot, StringComparison.OrdinalIgnoreCase)
            ? [(workRoot, estimate.WorkBytes + estimate.OutputBytes)]
            : new List<(string Root, long Bytes)> { (workRoot, estimate.WorkBytes), (outputRoot, estimate.OutputBytes) };
        var problems = new List<string>();
        foreach (var (root, bytes) in needs)
        {
            if (freeBytes(root) is { } free && free < bytes)
            {
                problems.Add($"Not enough space on {root}: the run needs {Gb(bytes)}, {Gb(free)} is free.");
            }
        }

        return problems;
    }

    /// <summary>The free space of the volume that holds the path, or null when it cannot be read.</summary>
    public static long? FreeBytes(string root)
    {
        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Drive roots are read as Windows writes them, so the rule is the same in tests on Linux.
    private static string Root(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/' ? path[..2].ToUpperInvariant() + "\\"
        : Path.GetPathRoot(path) is { Length: > 0 } root ? root : path;

    private static string Gb(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB");
}
