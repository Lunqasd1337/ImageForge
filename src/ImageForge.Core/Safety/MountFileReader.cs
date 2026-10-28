namespace ImageForge.Core.Safety;

public enum MountReadStatus
{
    Read,
    Missing,

    /// <summary>A link or reparse point on the way, a name that is not plain, or a file over the limit.</summary>
    Refused,

    /// <summary>The image's own permissions do not let the engine read it.</summary>
    Denied,
}

/// <summary>
/// Reads a small file below a mount root without passing through a link or reparse point, so that a junction in the
/// image cannot lead the read to the host. The segments are folder and file names, never paths.
/// </summary>
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
            if (info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
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
            return MountReadStatus.Refused;
        }

        text = File.ReadAllText(current);
        return MountReadStatus.Read;
    }
}
