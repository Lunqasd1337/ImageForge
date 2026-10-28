namespace ImageForge.Core.Registry;

public enum OfflineHive
{
    Software,
    System,

    /// <summary>config\DEFAULT, the system profile (HKU\.DEFAULT).</summary>
    Default,

    /// <summary>Users\Default\NTUSER.DAT, the template for future users.</summary>
    DefaultUser,

    /// <summary>Users\Default\...\UsrClass.dat, HKCU\Software\Classes of future users.</summary>
    DefaultUserClasses,
}

public enum RegistryMappingError
{
    None,
    MalformedPath,
    UnknownRoot,
    ForbiddenRoot,
    NotSupportedYet,
}

/// <param name="SubKey">Path inside the hive. When <paramref name="UnderCurrentControlSet"/> is set, it is relative to
/// the control set that SYSTEM\Select\Current names, which only the mounted image can tell.</param>
/// <param name="ClassesRootPolicy">The path came from HKCR and is written to the machine classes by ImageForge policy.</param>
public sealed record OfflineRegistryLocation(
    OfflineHive Hive,
    string HiveFile,
    string SubKey,
    bool UnderCurrentControlSet = false,
    bool ClassesRootPolicy = false);

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
/// Maps an import path (HKLM\..., HKCU\...) to an offline hive file inside the mounted image, by the plan's table;
/// the most specific rule wins. SAM, SECURITY, HARDWARE, HKCC and anything else not listed is blocked. There is never
/// a fallback to the host registry.
/// </summary>
public sealed class RegistryPathMapper : IRegistryPathMapper
{
    public const int MaxKeyNameLength = 255;

    public const string SoftwareHiveFile = @"Windows\System32\config\SOFTWARE";
    public const string SystemHiveFile = @"Windows\System32\config\SYSTEM";
    public const string DefaultHiveFile = @"Windows\System32\config\DEFAULT";
    public const string DefaultUserHiveFile = @"Users\Default\NTUSER.DAT";
    public const string DefaultUserClassesHiveFile = @"Users\Default\AppData\Local\Microsoft\Windows\UsrClass.dat";

    public static string HiveFile(OfflineHive hive) => hive switch
    {
        OfflineHive.Software => SoftwareHiveFile,
        OfflineHive.System => SystemHiveFile,
        OfflineHive.Default => DefaultHiveFile,
        OfflineHive.DefaultUser => DefaultUserHiveFile,
        OfflineHive.DefaultUserClasses => DefaultUserClassesHiveFile,
        _ => throw new ArgumentOutOfRangeException(nameof(hive)),
    };

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
        if (root is null)
        {
            return RegistryMappingResult.Fail(RegistryMappingError.UnknownRoot);
        }

        var path = segments[1..];
        return root switch
        {
            "HKLM" => MapMachine(path),
            "HKCU" when StartsWith(path, "Software", "Classes") => In(OfflineHive.DefaultUserClasses, path[2..]),
            "HKCU" => In(OfflineHive.DefaultUser, path),
            "HKU" when StartsWith(path, ".DEFAULT") => In(OfflineHive.Default, path[1..]),
            "HKU" => RegistryMappingResult.Fail(RegistryMappingError.ForbiddenRoot),
            "HKCR" => RegistryMappingResult.Success(new OfflineRegistryLocation(
                OfflineHive.Software, SoftwareHiveFile, Join(["Classes", .. path]), ClassesRootPolicy: true)),
            _ => RegistryMappingResult.Fail(RegistryMappingError.ForbiddenRoot),
        };
    }

    private static RegistryMappingResult MapMachine(string[] path)
    {
        if (StartsWith(path, "SOFTWARE"))
        {
            return In(OfflineHive.Software, path[1..]);
        }

        if (StartsWith(path, "SYSTEM", "CurrentControlSet"))
        {
            return RegistryMappingResult.Success(new OfflineRegistryLocation(
                OfflineHive.System, SystemHiveFile, Join(path[2..]), UnderCurrentControlSet: true));
        }

        if (StartsWith(path, "SYSTEM"))
        {
            return In(OfflineHive.System, path[1..]);
        }

        return path.Length == 0
            ? RegistryMappingResult.Fail(RegistryMappingError.UnknownRoot)
            : RegistryMappingResult.Fail(RegistryMappingError.ForbiddenRoot);
    }

    private static RegistryMappingResult In(OfflineHive hive, string[] rest) =>
        RegistryMappingResult.Success(new OfflineRegistryLocation(hive, HiveFile(hive), Join(rest)));

    private static bool StartsWith(string[] path, params string[] prefix) =>
        path.Length >= prefix.Length && prefix.Select((p, i) => string.Equals(path[i], p, StringComparison.OrdinalIgnoreCase)).All(x => x);

    private static string Join(string[] segments) => string.Join('\\', segments);

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

/// <summary>Resolves CurrentControlSet from SYSTEM\Select\Current. A missing or invalid value is never guessed.</summary>
public static class ControlSet
{
    /// <returns>"ControlSet00N", or null when the image does not say which control set is current.</returns>
    public static string? FromSelectCurrent(uint? current) =>
        current is >= 1 and <= 999 ? $"ControlSet{current.Value:000}" : null;
}
