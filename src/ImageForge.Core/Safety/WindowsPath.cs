using System.Buffers;

namespace ImageForge.Core.Safety;

/// <summary>
/// Lexical normalization of Windows drive paths, independent of the host OS.
/// Filesystem checks (reparse points, file identity) belong to the engine.
/// </summary>
internal static class WindowsPath
{
    private static readonly SearchValues<char> InvalidSegmentChars = SearchValues.Create("<>:\"|?*");

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    public static WriteScopeViolation TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return WriteScopeViolation.NotAbsolute;
        }

        var unified = path.Replace('/', '\\');
        if (unified.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return WriteScopeViolation.UnsupportedPathForm;
        }

        if (unified.Length < 3 || !char.IsAsciiLetter(unified[0]) || unified[1] != ':' || unified[2] != '\\')
        {
            return WriteScopeViolation.NotAbsolute;
        }

        var segments = new List<string>();
        foreach (var segment in unified[3..].Split('\\'))
        {
            switch (segment)
            {
                case "":
                case ".":
                    continue;
                case "..":
                    if (segments.Count == 0)
                    {
                        // Above the drive root: cannot be inside any scope.
                        return WriteScopeViolation.OutsideScope;
                    }

                    segments.RemoveAt(segments.Count - 1);
                    continue;
            }

            if (!IsValidSegment(segment))
            {
                return WriteScopeViolation.InvalidSegment;
            }

            segments.Add(segment);
        }

        normalized = segments.Count == 0
            ? unified[..3]
            : string.Concat(unified.AsSpan(0, 3), string.Join('\\', segments));
        return WriteScopeViolation.None;
    }

    public static bool IsSameOrUnder(string normalizedPath, string normalizedRoot)
    {
        if (normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.Length > normalizedRoot.Length
            && normalizedPath[normalizedRoot.Length] == '\\'
            && normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidSegment(string segment)
    {
        // Win32 silently strips trailing dots and spaces, which makes such names ambiguous.
        if (segment.EndsWith('.') || segment.EndsWith(' '))
        {
            return false;
        }

        if (segment.AsSpan().ContainsAny(InvalidSegmentChars) || segment.Any(char.IsControl))
        {
            return false;
        }

        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        return !ReservedDeviceNames.Contains(stem);
    }
}
