using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ImageForge.Core.Workspace;

/// <param name="LastWriteUtc">Recorded for the metadata re-check; not part of the content comparison or the fingerprint.</param>
public sealed record ManifestEntry(string RelativePath, long Length, string Sha256, DateTime LastWriteUtc = default);

public enum ManifestChange
{
    Added,
    Removed,
    Changed,
}

public sealed record ManifestDifference(ManifestChange Change, string RelativePath);

/// <summary>Size and SHA-256 of every file under a folder, used to prove the source did not change during a run.</summary>
public sealed record SourceManifest(IReadOnlyList<ManifestEntry> Entries, string Fingerprint)
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static async Task<SourceManifest> ComputeAsync(string root, CancellationToken ct)
    {
        var entries = new List<ManifestEntry>();
        foreach (var (file, relative) in Files(root))
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(await HashFileAsync(file, relative, ct).ConfigureAwait(false));
        }

        return FromEntries(entries);
    }

    public static SourceManifest FromEntries(IEnumerable<ManifestEntry> entries)
    {
        var sorted = entries.ToList();
        sorted.Sort((x, y) => PathComparer.Compare(x.RelativePath, y.RelativePath));
        return new SourceManifest(sorted, ComputeFingerprint(sorted));
    }

    /// <summary>
    /// Re-checks a folder against a manifest without reading every file: size and last write time for all files,
    /// full SHA-256 only for <paramref name="hashPaths"/>.
    /// </summary>
    public static async Task<IReadOnlyList<ManifestDifference>> CheckMetadataAsync(
        string root, SourceManifest expected, IReadOnlyCollection<string> hashPaths, CancellationToken ct)
    {
        var hash = new HashSet<string>(hashPaths, PathComparer);
        var actual = new List<ManifestEntry>();
        foreach (var (file, relative) in Files(root))
        {
            ct.ThrowIfCancellationRequested();
            if (hash.Contains(relative))
            {
                actual.Add(await HashFileAsync(file, relative, ct).ConfigureAwait(false));
                continue;
            }

            var info = new FileInfo(file);
            actual.Add(new ManifestEntry(relative, info.Length, string.Empty, info.LastWriteTimeUtc));
        }

        var before = expected.Entries.ToDictionary(e => e.RelativePath, PathComparer);
        var differences = Compare(expected, new SourceManifest(actual, string.Empty))
            .Where(d => d.Change != ManifestChange.Changed || hash.Contains(d.RelativePath))
            .ToList();
        differences.AddRange(actual
            .Where(e => !hash.Contains(e.RelativePath)
                && before.TryGetValue(e.RelativePath, out var old)
                && (old.Length != e.Length || old.LastWriteUtc != e.LastWriteUtc))
            .Select(e => new ManifestDifference(ManifestChange.Changed, e.RelativePath)));

        differences.Sort((x, y) => PathComparer.Compare(x.RelativePath, y.RelativePath));
        return differences;
    }

    private static IEnumerable<(string File, string Relative)> Files(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        return Directory.EnumerateFiles(root, "*", options)
            .Select(file => (file, Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')));
    }

    private static async Task<ManifestEntry> HashFileAsync(string file, string relative, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return new ManifestEntry(relative, stream.Length, Convert.ToHexStringLower(hash), File.GetLastWriteTimeUtc(file));
    }

    public static IReadOnlyList<ManifestDifference> Compare(SourceManifest expected, SourceManifest actual)
    {
        var before = expected.Entries.ToDictionary(e => e.RelativePath, PathComparer);
        var after = actual.Entries.ToDictionary(e => e.RelativePath, PathComparer);
        var differences = new List<ManifestDifference>();

        foreach (var entry in actual.Entries)
        {
            if (!before.TryGetValue(entry.RelativePath, out var old))
            {
                differences.Add(new ManifestDifference(ManifestChange.Added, entry.RelativePath));
            }
            else if (old.Length != entry.Length || !string.Equals(old.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                differences.Add(new ManifestDifference(ManifestChange.Changed, entry.RelativePath));
            }
        }

        differences.AddRange(expected.Entries
            .Where(e => !after.ContainsKey(e.RelativePath))
            .Select(e => new ManifestDifference(ManifestChange.Removed, e.RelativePath)));

        differences.Sort((x, y) => PathComparer.Compare(x.RelativePath, y.RelativePath));
        return differences;
    }

    private static string ComputeFingerprint(IEnumerable<ManifestEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            var path = entry.RelativePath.ToUpperInvariant();
            builder.Append(CultureInfo.InvariantCulture, $"{path.Length}:{path}|{entry.Length}|{entry.Sha256}\n");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
