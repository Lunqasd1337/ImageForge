using System.Text.RegularExpressions;

namespace ImageForge.Core.Updates;

/// <param name="Kb">The KB number, when the name has one.</param>
/// <param name="Architecture">x64, arm64 or x86, when the name has one; amd64 is reported as x64.</param>
/// <param name="Sha1">The lowercase SHA-1 that Catalog file names carry, as a check of the download.</param>
public sealed record UpdateFileName(int? Kb, string? Architecture, string? Sha1)
{
    public static UpdateFileName Parse(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        var kb = Regex.Match(name, @"(?i)(?<![a-z])kb(\d{6,8})(?!\d)");
        var arch = Regex.Match(name, @"(?i)-(x64|amd64|arm64|x86)(?=[_.\-])");
        var sha1 = Regex.Match(name, @"(?i)_([0-9a-f]{40})\.(msu|cab)$");
        return new(
            kb.Success ? int.Parse(kb.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null,
            arch.Success ? (arch.Groups[1].Value.Equals("amd64", StringComparison.OrdinalIgnoreCase) ? "x64" : arch.Groups[1].Value.ToLowerInvariant()) : null,
            sha1.Success ? sha1.Groups[1].Value.ToLowerInvariant() : null);
    }
}

public enum UpdateKind
{
    Unknown,
    Msu,
    Cab,
}

/// <param name="FileName">Relative to the scanned folder.</param>
/// <param name="Payloads">For an MSU, the packages DISM installs from it: SSU and KB cabinets, WIM and PSF files.</param>
/// <param name="HasServicingStack">The file is or carries a servicing stack update.</param>
/// <param name="Problems">Why the file cannot be added as it is; empty when it can.</param>
public sealed record UpdatePackageInfo(
    string FileName,
    UpdateKind Kind,
    int? Kb,
    string? Architecture,
    string? Sha1FromName,
    IReadOnlyList<CabinetEntry> Contents,
    IReadOnlyList<string> Payloads,
    bool HasServicingStack,
    IReadOnlyList<string> Problems);

/// <summary>
/// Previews a local update package before DISM sees it (plan, section 11): its kind, KB and architecture from the name, and
/// what the cabinet holds. DISM still checks the signature and whether the package applies.
/// </summary>
public static class UpdatePackage
{
    public static UpdatePackageInfo Analyze(string fileName, CabinetListing listing)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(listing);
        var name = UpdateFileName.Parse(fileName);
        var leaf = Path.GetFileName(fileName.Replace('\\', '/'));
        var kind = Path.GetExtension(leaf).ToLowerInvariant() switch
        {
            ".msu" => UpdateKind.Msu,
            ".cab" => UpdateKind.Cab,
            _ => UpdateKind.Unknown,
        };
        var problems = new List<string>();
        if (kind == UpdateKind.Unknown)
        {
            problems.Add("это не .msu и не .cab");
        }

        if (name.Architecture is { } arch && arch != "x64")
        {
            problems.Add($"пакет для {arch}, а образ x64");
        }

        if (listing.Problem is { } problem)
        {
            problems.Add(problem);
        }

        IReadOnlyList<string> payloads = [];
        var servicingStack = IsServicingStack(leaf);
        if (listing.Problem is null && kind == UpdateKind.Msu)
        {
            payloads = [.. listing.Entries.Select(e => e.Name).Where(IsPayload)];
            servicingStack |= payloads.Any(IsServicingStack);
            if (payloads.Count == 0)
            {
                problems.Add("в MSU нет пакетов для установки (SSU, KB .cab, .wim или .psf)");
            }
        }
        else if (listing.Problem is null && kind == UpdateKind.Cab
            && !listing.Entries.Any(e => e.Name.Equals("update.mum", StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add("в CAB нет update.mum: это не пакет обслуживания Windows");
        }

        return new(fileName, kind, name.Kb, name.Architecture, name.Sha1, listing.Entries, payloads, servicingStack, problems);
    }

    private static bool IsServicingStack(string name) => name.StartsWith("SSU-", StringComparison.OrdinalIgnoreCase);

    private static bool IsPayload(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension is ".cab" or ".wim" or ".psf"
            && (IsServicingStack(name) || UpdateFileName.Parse(name).Kb is not null);
    }
}
