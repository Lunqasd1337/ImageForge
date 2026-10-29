namespace ImageForge.Core.Safety;

public enum MountReadStatus
{
    Read,
    Missing,

    /// <summary>A symbolic link or junction on the way, or a name that is not plain.</summary>
    Refused,

    TooLarge,

    /// <summary>The image's own permissions do not let the engine read it.</summary>
    Denied,
}

/// <summary>
/// Reads a small file below a mount root without passing through a symbolic link or junction, so that a link in the
/// image cannot lead the read to the host. The segments are folder and file names, never paths.
/// </summary>
/// <remarks>
/// Other reparse points are read through: a mounted WIM marks its not yet extracted files with the WIM reparse tag
/// (and compressed system files carry WOF), and those are data, not names of other places (found in the VM).
/// </remarks>
public static class MountFileReader
{
    public static MountReadStatus TryReadText(string mountRoot, IReadOnlyList<string> segments, int maxBytes, out string text)
    {
        ArgumentNullException.ThrowIfNull(segments);
        text = string.Empty;
        try
        {
            return Read(mountRoot, segments, maxBytes, out text);
        }
        catch (UnauthorizedAccessException)
        {
            return MountReadStatus.Denied;
        }
        catch (IOException)
        {
            return MountReadStatus.Denied;
        }
    }

    private static MountReadStatus Read(string mountRoot, IReadOnlyList<string> segments, int maxBytes, out string text)
    {
        text = string.Empty;
        var current = mountRoot;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0 || segment is "." or ".." || segment.Contains('\\', StringComparison.Ordinal) || segment.Contains('/', StringComparison.Ordinal))
            {
                return MountReadStatus.Refused;
            }

            current = Path.Combine(current, segment);
            FileSystemInfo info = i < segments.Count - 1 ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
            {
                return MountReadStatus.Refused;
            }

            if (!info.Exists)
            {
                return MountReadStatus.Missing;
            }
        }

        if (new FileInfo(current).Length > maxBytes)
        {
            return MountReadStatus.TooLarge;
        }

        text = File.ReadAllText(current);
        return MountReadStatus.Read;
    }
}
