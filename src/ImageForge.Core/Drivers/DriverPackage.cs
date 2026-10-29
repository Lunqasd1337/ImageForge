namespace ImageForge.Core.Drivers;

/// <param name="Architectures">From the [Manufacturer] decorations, such as "amd64" for NTamd64.10.0.</param>
/// <param name="CatalogFile">Null for a package without a catalog, which DISM adds only with ForceUnsigned.</param>
public sealed record DriverInf(
    string? Class,
    string? ClassGuid,
    string? Provider,
    string? DriverDate,
    string? DriverVersion,
    string? CatalogFile,
    IReadOnlyList<string> Architectures);

/// <param name="InfPath">Relative to the scanned folder.</param>
/// <param name="Inf">Null when the file is not an NT driver INF.</param>
/// <param name="Files">The x64 files the INF copies, relative to the scanned folder.</param>
/// <param name="Problems">Why the package cannot be added as it is; empty for a complete x64 package.</param>
public sealed record DriverPackageInfo(string InfPath, DriverInf? Inf, IReadOnlyList<string> Files, IReadOnlyList<string> Problems)
{
    public bool IsUnsigned => Inf is { CatalogFile: null };
}

/// <summary>
/// Previews a driver package before DISM sees it (plan, section 12): the INF facts, the x64 decoration, and whether the
/// catalog and every file listed in [SourceDisksFiles] are next to the INF.
/// </summary>
public static class DriverPackage
{
    private const string Arch = "amd64";

    /// <param name="exists">Whether a path relative to the scanned folder exists.</param>
    public static DriverPackageInfo Analyze(string infRelativePath, string text, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(infRelativePath);
        ArgumentNullException.ThrowIfNull(exists);
        var document = InfParser.Parse(text);
        var signature = document.Value("Version", "Signature");
        if (signature is null)
        {
            return new(infRelativePath, null, [], ["Нет раздела [Version] с подписью: это не INF драйвера."]);
        }

        if (!signature.Equals("$WINDOWS NT$", StringComparison.OrdinalIgnoreCase) && !signature.Equals("$Chicago$", StringComparison.OrdinalIgnoreCase))
        {
            return new(infRelativePath, null, [], [$"Подпись {signature} не относится к драйверам Windows NT."]);
        }

        var architectures = document.Lines("Manufacturer")
            .SelectMany(l => l.Values.Skip(1))
            .Select(Architecture)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var driverVer = document.Value("Version", "DriverVer");
        var versionParts = document.Lines("Version").FirstOrDefault(l => string.Equals(l.Key, "DriverVer", StringComparison.OrdinalIgnoreCase))?.Values;
        var catalog = document.Value("Version", "CatalogFile.NTamd64") ?? document.Value("Version", "CatalogFile");
        var inf = new DriverInf(
            document.Value("Version", "Class"),
            document.Value("Version", "ClassGuid"),
            document.Value("Version", "Provider"),
            driverVer,
            versionParts is { Count: > 1 } ? versionParts[1] : null,
            string.IsNullOrWhiteSpace(catalog) ? null : catalog,
            architectures);

        var folder = Folder(infRelativePath);
        var problems = new List<string>();
        if (!architectures.Contains(Arch, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add("В [Manufacturer] нет раздела для x64 (NTamd64).");
        }

        if (inf.CatalogFile is { } cat && !exists(Combine(folder, cat)))
        {
            problems.Add($"Нет файла каталога {cat}.");
        }

        var files = Files(document, folder);
        problems.AddRange(files.Where(f => !exists(f)).Select(f => $"Нет файла {f}."));
        return new(infRelativePath, inf, files, problems);
    }

    // "NTamd64.10.0" -> "amd64"; "NTx86" -> "x86"; a bare "NT" or a target without NT is not an architecture.
    private static string? Architecture(string decoration)
    {
        if (!decoration.StartsWith("NT", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = decoration[2..].Split('.')[0];
        return name.Length == 0 ? null : name.ToLowerInvariant();
    }

    private static List<string> Files(InfDocument document, string folder)
    {
        // Disk paths: the architecture-specific section first, then the generic one.
        var disks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in new[] { $"SourceDisksNames.{Arch}", "SourceDisksNames" })
        {
            foreach (var line in document.Lines(section).Where(l => l.Key is not null))
            {
                disks.TryAdd(line.Key!, line.Values.Count > 3 ? line.Values[3].Trim('\\') : string.Empty);
            }
        }

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in new[] { $"SourceDisksFiles.{Arch}", "SourceDisksFiles" })
        {
            foreach (var line in document.Lines(section).Where(l => l.Key is { Length: > 0 }))
            {
                if (!seen.Add(line.Key!))
                {
                    continue;
                }

                var disk = disks.GetValueOrDefault(line.Values[0], string.Empty);
                var subdir = line.Values.Count > 1 ? line.Values[1].Trim('\\') : string.Empty;
                files.Add(Combine(Combine(Combine(folder, disk), subdir), line.Key!));
            }
        }

        return files;
    }

    private static string Folder(string relativePath)
    {
        var slash = relativePath.LastIndexOfAny(['\\', '/']);
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    private static string Combine(string left, string right) =>
        left.Length == 0 ? right : right.Length == 0 ? left : $"{left}\\{right}";
}
