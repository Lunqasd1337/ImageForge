using System.Security.Cryptography;

namespace ImageForge.Core.Updates;

/// <param name="Sha256">Of the file as it was scanned; the run checks it again before adding. Null when it could not be read.</param>
public sealed record ScannedUpdate(UpdatePackageInfo Package, string? Sha256);

/// <summary>A folder of local update packages chosen by the user, searched for .msu and .cab files recursively (plan, section 11).</summary>
/// <param name="Packages">Every .msu and .cab found, sorted by path; paths use '\' and are relative to <paramref name="Folder"/>.</param>
/// <param name="Problem">Why the folder cannot be used; null when it can.</param>
public sealed record UpdateFolder(string Folder, IReadOnlyList<ScannedUpdate> Packages, string? Problem)
{
    public static UpdateFolder Scan(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        try
        {
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            {
                return new(folder, [], "папка не найдена (нужен полный путь)");
            }

            var packages = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".msu" or ".cab")
                .Select(f => Path.GetRelativePath(folder, f).Replace('/', '\\'))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(path => ScanFile(folder, path))
                .ToList();
            return new(folder, packages, packages.Count == 0 ? "в папке нет файлов .msu и .cab" : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(folder, [], ex.Message);
        }
    }

    /// <summary>The SHA-256 of the file as it is now; null when it is gone or cannot be read.</summary>
    public static string? CurrentHash(string folder, string fileName)
    {
        try
        {
            using var stream = File.OpenRead(Full(folder, fileName));
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string Full(string folder, string relative) => Path.Combine(folder, relative.Replace('\\', Path.DirectorySeparatorChar));

    /// <summary>Reads and hashes one package of the folder; <paramref name="path"/> is relative to it.</summary>
    public static ScannedUpdate ScanFile(string folder, string path)
    {
        using var stream = File.OpenRead(Full(folder, path));
        var start = new byte[8];
        var isWim = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false) == start.Length && WimArchive.HasMagic(start);
        stream.Position = 0;
        var package = isWim ? UpdatePackage.Analyze(path, WimArchive.Read(stream)) : UpdatePackage.Analyze(path, Cabinet.Read(stream));
        stream.Position = 0;
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (package.Sha1FromName is { } expected)
        {
            stream.Position = 0;
#pragma warning disable CA5350 // Catalog names carry SHA-1; it only catches a bad download, trust comes from SHA-256 and DISM.
            var actual = Convert.ToHexStringLower(SHA1.HashData(stream));
#pragma warning restore CA5350
            if (actual != expected)
            {
                package = package with { Problems = [.. package.Problems, $"SHA-1 файла {actual} не совпадает с указанным в имени: файл повреждён или скачан не полностью"] };
            }
        }

        return new(package, sha256);
    }
}
