using System.Buffers;
using ImageForge.Core.Execution;

namespace ImageForge.Core.Safety;

/// <summary>
/// Writes files below a mount root. The path must be plain and relative, and no folder or file on the way may be a
/// reparse point: an offline image has junctions with absolute targets (C:\Users, ...) that would lead to the host.
/// </summary>
public sealed class MountFileWriter(string mountRoot) : IImageFileWriter
{
    private static readonly SearchValues<char> Forbidden = SearchValues.Create(":/*?\"<>|\0");

    public void Write(string relativePath, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(content);
        var segments = relativePath.Split('\\');
        if (relativePath.Length == 0 || segments.Any(s => s.Length == 0 || s is "." or ".." || s.AsSpan().ContainsAny(Forbidden) || s.Trim().Length != s.Length))
        {
            throw new UnauthorizedAccessException($"'{relativePath}' is not a plain path inside the mount.");
        }

        var current = mountRoot;
        foreach (var folder in segments[..^1])
        {
            current = Path.Combine(current, folder);
            var info = new DirectoryInfo(current);
            if (info.Exists || info.LinkTarget is not null)
            {
                RefuseLink(info, relativePath);
            }
            else
            {
                info.Create();
                RefuseLink(new DirectoryInfo(current), relativePath);
            }
        }

        var file = new FileInfo(Path.Combine(current, segments[^1]));
        if (file.Exists || file.LinkTarget is not null)
        {
            RefuseLink(file, relativePath);
        }

        File.WriteAllBytes(file.FullName, content);
    }

    private static void RefuseLink(FileSystemInfo info, string relativePath)
    {
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException($"'{info.FullName}' is a link or reparse point; writing '{relativePath}' through it is not allowed.");
        }
    }
}
