using System.Security.Cryptography;

namespace ImageForge.Core.Workspace;

/// <summary>
/// Copies a folder tree file by file and hashes the bytes as they are copied, so a copy is verified without being
/// read a second time. Reparse points are not followed; copies are made writable and keep the source's last write time.
/// </summary>
public static class TreeCopy
{
    private const int BufferSize = 1 << 20;

    /// <param name="exclude">Relative paths with '/' separators that are not copied, compared ignoring case.</param>
    /// <returns>Size and SHA-256 of every copied file, as read from the source.</returns>
    public static async Task<IReadOnlyList<ManifestEntry>> CopyAsync(string sourceRoot, string destinationRoot, IReadOnlyCollection<string>? exclude, CancellationToken ct)
    {
        var skip = new HashSet<string>(exclude ?? [], StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        Directory.CreateDirectory(destinationRoot);
        var copied = new List<ManifestEntry>();
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, file);
            var key = relative.Replace(Path.DirectorySeparatorChar, '/');
            if (skip.Contains(key))
            {
                continue;
            }

            var target = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            copied.Add(await CopyFileAsync(file, target, ct).ConfigureAwait(false) with { RelativePath = key });
        }

        return copied;
    }

    /// <summary>
    /// Copies one file without overwriting, clears the read-only attribute media files usually carry, and returns
    /// the size and SHA-256 of the bytes copied. <see cref="ManifestEntry.RelativePath"/> is the file name.
    /// </summary>
    public static async Task<ManifestEntry> CopyFileAsync(string source, string destination, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            output.SetLength(input.Length);
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                length += read;
            }

            output.SetLength(length);
        }

        var lastWrite = File.GetLastWriteTimeUtc(source);
        File.SetLastWriteTimeUtc(destination, lastWrite);
        File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
        return new ManifestEntry(Path.GetFileName(destination), length, Convert.ToHexStringLower(hash.GetHashAndReset()), lastWrite);
    }
}
