using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ImageForge.Core.Workspace;

public sealed record ManifestEntry(string RelativePath, long Length, string Sha256);

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
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var entries = new List<ManifestEntry>();
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            entries.Add(new ManifestEntry(relative, stream.Length, Convert.ToHexStringLower(hash)));
        }

        entries.Sort((x, y) => PathComparer.Compare(x.RelativePath, y.RelativePath));
        return new SourceManifest(entries, ComputeFingerprint(entries));
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
