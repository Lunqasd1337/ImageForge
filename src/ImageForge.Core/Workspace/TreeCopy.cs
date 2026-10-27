namespace ImageForge.Core.Workspace;

/// <summary>Copies a folder tree file by file. Reparse points are not followed; copies are made writable.</summary>
public static class TreeCopy
{
    /// <param name="exclude">Relative paths with '/' separators that are not copied, compared ignoring case.</param>
    public static void Copy(string sourceRoot, string destinationRoot, IReadOnlyCollection<string>? exclude, CancellationToken ct)
    {
        var skip = new HashSet<string>(exclude ?? [], StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        Directory.CreateDirectory(destinationRoot);
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, file);
            if (skip.Contains(relative.Replace(Path.DirectorySeparatorChar, '/')))
            {
                continue;
            }

            var target = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyFile(file, target);
        }
    }

    /// <summary>Copies one file without overwriting and clears the read-only attribute media files usually carry.</summary>
    public static void CopyFile(string source, string destination)
    {
        File.Copy(source, destination, overwrite: false);
        File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
    }
}
