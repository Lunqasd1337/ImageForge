using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ImageForge.Core.Changes;

/// <summary>Registry value types REG_NONE..REG_QWORD (0..0xB), numbered as the Win32 constants.</summary>
public enum RegistryValueType : uint
{
    None = 0,
    Sz = 1,
    ExpandSz = 2,
    Binary = 3,
    Dword = 4,
    DwordBigEndian = 5,
    Link = 6,
    MultiSz = 7,
    ResourceList = 8,
    FullResourceDescriptor = 9,
    ResourceRequirementsList = 0xA,
    Qword = 0xB,
}

/// <summary>
/// A value exactly as RegSetValueEx writes it: its type and raw bytes (strings as nul-terminated UTF-16LE,
/// REG_EXPAND_SZ unexpanded). Equality compares the bytes.
/// </summary>
public sealed record RegistryValue(RegistryValueType Type, byte[] Bytes)
{
    public static RegistryValue Sz(string data) => new(RegistryValueType.Sz, Encoding.Unicode.GetBytes(data + "\0"));

    /// <summary>The string of a REG_SZ or REG_EXPAND_SZ value without its terminator; null for other types.</summary>
    public string? Text => Type is RegistryValueType.Sz or RegistryValueType.ExpandSz && Bytes.Length % 2 == 0
        ? TrimOneTerminator(Encoding.Unicode.GetString(Bytes))
        : null;

    public string TypeName => Type switch
    {
        RegistryValueType.None => "REG_NONE",
        RegistryValueType.Sz => "REG_SZ",
        RegistryValueType.ExpandSz => "REG_EXPAND_SZ",
        RegistryValueType.Binary => "REG_BINARY",
        RegistryValueType.Dword => "REG_DWORD",
        RegistryValueType.DwordBigEndian => "REG_DWORD_BIG_ENDIAN",
        RegistryValueType.Link => "REG_LINK",
        RegistryValueType.MultiSz => "REG_MULTI_SZ",
        RegistryValueType.ResourceList => "REG_RESOURCE_LIST",
        RegistryValueType.FullResourceDescriptor => "REG_FULL_RESOURCE_DESCRIPTOR",
        RegistryValueType.ResourceRequirementsList => "REG_RESOURCE_REQUIREMENTS_LIST",
        RegistryValueType.Qword => "REG_QWORD",
        _ => $"0x{(uint)Type:X}",
    };

    /// <summary>Readable form for previews; falls back to hex when the bytes do not fit the type.</summary>
    public string Describe() => $"{TypeName}: {DescribeData()}";

    public bool Equals(RegistryValue? other) =>
        other is not null && Type == other.Type && Bytes.AsSpan().SequenceEqual(other.Bytes);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Type);
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }

    private string DescribeData()
    {
        switch (Type)
        {
            case RegistryValueType.Sz or RegistryValueType.ExpandSz when Text is { } text:
                return text;
            case RegistryValueType.MultiSz when Bytes.Length % 2 == 0:
                return string.Join(" | ", Encoding.Unicode.GetString(Bytes).TrimEnd('\0').Split('\0'));
            case RegistryValueType.Dword when Bytes.Length == 4:
                var dword = BinaryPrimitives.ReadUInt32LittleEndian(Bytes);
                return string.Create(CultureInfo.InvariantCulture, $"0x{dword:x8} ({dword})");
            case RegistryValueType.Qword when Bytes.Length == 8:
                var qword = BinaryPrimitives.ReadUInt64LittleEndian(Bytes);
                return string.Create(CultureInfo.InvariantCulture, $"0x{qword:x16} ({qword})");
            default:
                return Bytes.Length == 0 ? "(empty)" : Convert.ToHexString(Bytes);
        }
    }

    private static string TrimOneTerminator(string text) => text.EndsWith('\0') ? text[..^1] : text;
}

/// <summary>A typed change requested for an offline image. Commands and arbitrary paths are not representable.</summary>
public abstract record ImageChange;

/// <param name="ValueName">Empty string addresses the default value.</param>
public sealed record SetRegistryValue(string KeyPath, string ValueName, RegistryValue Value) : ImageChange;

/// <param name="ValueName">Empty string addresses the default value.</param>
public sealed record DeleteRegistryValue(string KeyPath, string ValueName) : ImageChange;

public sealed record CreateRegistryKey(string KeyPath) : ImageChange;

/// <summary>Deletes the key and everything below it.</summary>
public sealed record DeleteRegistryKey(string KeyPath) : ImageChange;

/// <summary>Removes the provisioned package of this family; the exact package is resolved from the inventory.</summary>
/// <param name="FamilyName">Name_PublisherId, as <see cref="Appx.PackageIdentity.FamilyName"/>.</param>
public sealed record RemoveProvisionedAppx(string FamilyName) : ImageChange;

/// <summary>Turns the feature on, with every parent that is off; the planner works out and shows the parents.</summary>
public sealed record EnableWindowsFeature(string Name) : ImageChange;

/// <summary>Turns the feature off, with every feature that depends on it.</summary>
/// <param name="RemovePayload">Also deletes its files from the image (/Remove); off by default (plan, section 10).</param>
public sealed record DisableWindowsFeature(string Name, bool RemovePayload = false) : ImageChange;

/// <summary>Removes an installed capability (Feature on Demand), named as DISM lists it.</summary>
public sealed record RemoveCapability(string Name) : ImageChange;

/// <summary>Adds a capability listed as Not Present, from the Features on Demand folder given with the plan.</summary>
public sealed record AddCapability(string Name) : ImageChange;

/// <summary>Adds a driver package from the driver folder given with the plan.</summary>
/// <param name="InfPath">Relative to that folder, as its scan lists it.</param>
/// <param name="ForceUnsigned">Lets DISM add a package without a catalog; it may still not load in the installed system.</param>
public sealed record AddDriverPackage(string InfPath, bool ForceUnsigned = false) : ImageChange;

/// <summary>Removes a third-party driver by its name in the image (oemN.inf).</summary>
/// <param name="AllowBootCritical">The user accepted that removing a boot-critical driver can leave the image unbootable.</param>
public sealed record RemoveDriverPackage(string PublishedName, bool AllowBootCritical = false) : ImageChange;

/// <summary>Adds a local .msu or .cab update (DismAddPackage).</summary>
/// <param name="FileName">Relative to the scanned update folder.</param>
public sealed record AddUpdatePackage(string FileName) : ImageChange;

/// <summary>
/// Adds an update package to the Windows Recovery Environment inside the image (Windows\System32\Recovery\Winre.wim),
/// by its path relative to the scanned update folder: the cumulative update for its servicing stack, then the Safe OS
/// Dynamic Update.
/// </summary>
public sealed record AddWinReUpdatePackage(string FileName) : ImageChange;

/// <summary>
/// Adds an update package to every image of sources\boot.wim (Windows PE and Windows Setup), by its path relative to
/// the scanned update folder; the setup files of the Setup image then replace those of the media.
/// </summary>
public sealed record AddBootUpdatePackage(string FileName) : ImageChange;

/// <summary>Cleans up the component store (Dism.exe /Cleanup-Image /StartComponentCleanup) after the other servicing.</summary>
/// <param name="ResetBase">Also removes superseded component versions, so installed updates can no longer be removed.</param>
public sealed record CleanupComponentStore(bool ResetBase) : ImageChange;

public static class ValidationMarker
{
    /// <summary>The M1 test operation; intended for the validation fixture only.</summary>
    public static SetRegistryValue M1 { get; } =
        new(@"HKLM\SOFTWARE\ImageForge\Validation", "Milestone", RegistryValue.Sz("M1"));
}
