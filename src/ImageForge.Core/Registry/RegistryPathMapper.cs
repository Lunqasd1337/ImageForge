namespace ImageForge.Core.Registry;

public enum OfflineHive
{
    Software,
}

public enum RegistryMappingError
{
    None,
    MalformedPath,
    UnknownRoot,
    ForbiddenRoot,
    NotSupportedYet,
}

public sealed record OfflineRegistryLocation(OfflineHive Hive, string HiveFile, string SubKey);

public sealed record RegistryMappingResult(OfflineRegistryLocation? Location, RegistryMappingError Error)
{
    public bool IsSuccess => Location is not null;

    internal static RegistryMappingResult Success(OfflineRegistryLocation location) => new(location, RegistryMappingError.None);

    internal static RegistryMappingResult Fail(RegistryMappingError error) => new(null, error);
}

public interface IRegistryPathMapper
{
    RegistryMappingResult Map(string importPath);
}

/// <summary>
/// Maps an import path (HKLM\..., HKCU\...) to an offline hive file inside the mounted image.
/// M1 supports only HKLM\SOFTWARE; other allowed roots from the plan's table are reported as not supported yet.
/// There is never a fallback to the host registry.
/// </summary>
public sealed class RegistryPathMapper : IRegistryPathMapper
{
    public const int MaxKeyNameLength = 255;

    private const string SoftwareHiveFile = @"Windows\System32\config\SOFTWARE";

    public RegistryMappingResult Map(string importPath)
    {
        if (string.IsNullOrEmpty(importPath) || importPath.Contains('\0', StringComparison.Ordinal))
        {
            return RegistryMappingResult.Fail(RegistryMappingError.MalformedPath);
        }

        var segments = importPath.Split('\\');
        if (segments.Any(s => s.Length == 0 || s.Length > MaxKeyNameLength || s.Trim().Length != s.Length))
        {
            return RegistryMappingResult.Fail(RegistryMappingError.MalformedPath);
        }

        var root = CanonicalRoot(segments[0]);
        var subRoot = segments.Length > 1 ? segments[1].ToUpperInvariant() : null;

        return (root, subRoot) switch
        {
            ("HKLM", "SOFTWARE") => RegistryMappingResult.Success(
                new OfflineRegistryLocation(OfflineHive.Software, SoftwareHiveFile, string.Join('\\', segments[2..]))),
            ("HKLM", "SAM" or "SECURITY" or "HARDWARE") or ("HKCC", _) => RegistryMappingResult.Fail(RegistryMappingError.ForbiddenRoot),
            ("HKLM", "SYSTEM") or ("HKCU", _) or ("HKU", _) or ("HKCR", _) => RegistryMappingResult.Fail(RegistryMappingError.NotSupportedYet),
            _ => RegistryMappingResult.Fail(RegistryMappingError.UnknownRoot),
        };
    }

    private static string? CanonicalRoot(string root) => root.ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => "HKLM",
        "HKCU" or "HKEY_CURRENT_USER" => "HKCU",
        "HKU" or "HKEY_USERS" => "HKU",
        "HKCR" or "HKEY_CLASSES_ROOT" => "HKCR",
        "HKCC" or "HKEY_CURRENT_CONFIG" => "HKCC",
        _ => null,
    };
}
