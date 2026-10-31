using System.Security.Cryptography;
using System.Text;

namespace ImageForge.Core.Drivers;

/// <param name="ContentHash">SHA-256 over the INF, its catalog and its files, by their paths relative to the INF; the
/// same package in two folders has the same hash. Null when a file of the package is missing.</param>
public sealed record ScannedDriver(DriverPackageInfo Package, string? ContentHash);

/// <summary>A folder of driver packages chosen by the user, searched for INF files recursively (plan, section 12).</summary>
/// <param name="Packages">Every INF found, sorted by path; paths use '\' and are relative to <paramref name="Folder"/>.</param>
/// <param name="Problem">Why the folder cannot be used; null when it can.</param>
public sealed record DriverFolder(string Folder, IReadOnlyList<ScannedDriver> Packages, string? Problem)
{
    public static DriverFolder Scan(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        try
        {
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            {
                return new(folder, [], Messages.FolderNotFound);
            }

            var packages = Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories)
                .Select(inf => Relative(folder, inf))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                {
                    var package = DriverPackage.Analyze(path, InfText.Decode(File.ReadAllBytes(Full(folder, path))), p => File.Exists(Full(folder, p)));
                    return new ScannedDriver(package, ContentHash(folder, package));
                })
                .ToList();
            return new(folder, packages, packages.Count == 0 ? Messages.DriverFolderEmpty : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(folder, [], ex.Message);
        }
    }

    /// <summary>Hashes the package as it is on disk now; null when a file is missing. The run checks it before adding.</summary>
    public static string? ContentHash(string folder, DriverPackageInfo package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var infFolder = InfFolder(package);
        var lines = new List<string>();
        foreach (var file in PackageFiles(package))
        {
            var full = Full(folder, file);
            if (!File.Exists(full))
            {
                return null;
            }

            var name = file.StartsWith(infFolder, StringComparison.OrdinalIgnoreCase) ? file[infFolder.Length..] : file;
            lines.Add($"{name.ToUpperInvariant()}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full)))}");
        }

        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    /// <summary>Reads the INF again and hashes its package as it is now; null when it can no longer be read whole.</summary>
    public static string? CurrentHash(string folder, string infPath)
    {
        try
        {
            var full = Full(folder, infPath);
            return File.Exists(full)
                ? ContentHash(folder, DriverPackage.Analyze(infPath, InfText.Decode(File.ReadAllBytes(full)), p => File.Exists(Full(folder, p))))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The INF, its catalog and the files it copies, relative to the scanned folder: what DISM reads to add the package.</summary>
    public static IReadOnlyList<string> PackageFiles(DriverPackageInfo package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var files = new List<string> { package.InfPath };
        if (package.Inf?.CatalogFile is { } catalog)
        {
            files.Add(InfFolder(package) + catalog);
        }

        files.AddRange(package.Files);
        return [.. files.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Reads the INF as it is now; null when it is gone or cannot be read.</summary>
    public static DriverPackageInfo? CurrentPackage(string folder, string infPath)
    {
        try
        {
            var full = Full(folder, infPath);
            return File.Exists(full) ? DriverPackage.Analyze(infPath, InfText.Decode(File.ReadAllBytes(full)), p => File.Exists(Full(folder, p))) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string InfFolder(DriverPackageInfo package)
    {
        var slash = package.InfPath.LastIndexOf('\\');
        return slash < 0 ? string.Empty : package.InfPath[..(slash + 1)];
    }

    /// <summary>The full path of a package path relative to the folder.</summary>
    public static string Full(string folder, string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        return Path.Combine(folder, relative.Replace('\\', Path.DirectorySeparatorChar));
    }

    private static string Relative(string folder, string path) => Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '\\');
}

/// <param name="PublishedName">The name in the image, oemN.inf; removal uses it.</param>
/// <param name="Signature">As DISM reports it: Signed, Unsigned or Unknown.</param>
public sealed record InstalledDriver(
    string PublishedName,
    string OriginalFileName,
    string ClassName,
    string Provider,
    string Version,
    DateTime Date,
    bool BootCritical,
    string Signature)
{
    /// <summary>The INF file name without the DriverStore folder DISM reports it in.</summary>
    public string InfName => OriginalFileName[(OriginalFileName.LastIndexOfAny(['\\', '/']) + 1)..];
}

/// <summary>The third-party drivers of an image (DismGetDrivers without inbox drivers).</summary>
public sealed record DriverInventory(IReadOnlyList<InstalledDriver> Drivers, IReadOnlyList<string> Problems)
{
    /// <summary>SHA-256 over the sorted published names, INF names and versions; a run compares it with its own mount.</summary>
    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', Drivers.Select(d => $"{d.PublishedName.ToUpperInvariant()}={d.InfName.ToUpperInvariant()} {d.Version}").Order(StringComparer.Ordinal)))));
}
