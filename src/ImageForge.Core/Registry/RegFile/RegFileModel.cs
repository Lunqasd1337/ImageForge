using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ImageForge.Core.Registry.RegFile;

/// <param name="Line">1-based line in the file; 0 when the problem is the file as a whole.</param>
public sealed record RegFileDiagnostic(string Code, int Line, string Message);

public static class RegFileCodes
{
    public const string EncodingAmbiguous = "regfile.encoding.ambiguous";
    public const string EncodingInvalid = "regfile.encoding.invalid";
    public const string EncodingUnsupported = "regfile.encoding.unsupported";
    public const string HeaderMissing = "regfile.header.missing";
    public const string KeyMalformed = "regfile.key.malformed";
    public const string ValueOutsideKey = "regfile.value.outside-key";
    public const string ValueMalformed = "regfile.value.malformed";
    public const string ValueTypeUnsupported = "regfile.value.type-unsupported";
    public const string LineUnrecognized = "regfile.line.unrecognized";
}

public enum RegFileFormat
{
    /// <summary>REGEDIT4: text is ANSI, and string types written as hex are ANSI bytes.</summary>
    Regedit4,

    /// <summary>Windows Registry Editor Version 5.00: string types written as hex are UTF-16LE bytes.</summary>
    Version5,
}

/// <summary>Registry value types REG_NONE..REG_QWORD (0..0xB), numbered as the Win32 constants.</summary>
public enum RegValueType : uint
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

/// <summary>A value exactly as it is written with RegSetValueEx: its type and raw bytes (strings as UTF-16LE).</summary>
public sealed record RegData(RegValueType Type, byte[] Bytes)
{
    public string TypeName => Type switch
    {
        RegValueType.None => "REG_NONE",
        RegValueType.Sz => "REG_SZ",
        RegValueType.ExpandSz => "REG_EXPAND_SZ",
        RegValueType.Binary => "REG_BINARY",
        RegValueType.Dword => "REG_DWORD",
        RegValueType.DwordBigEndian => "REG_DWORD_BIG_ENDIAN",
        RegValueType.Link => "REG_LINK",
        RegValueType.MultiSz => "REG_MULTI_SZ",
        RegValueType.ResourceList => "REG_RESOURCE_LIST",
        RegValueType.FullResourceDescriptor => "REG_FULL_RESOURCE_DESCRIPTOR",
        RegValueType.ResourceRequirementsList => "REG_RESOURCE_REQUIREMENTS_LIST",
        RegValueType.Qword => "REG_QWORD",
        _ => $"0x{(uint)Type:X}",
    };

    /// <summary>Readable form for previews; falls back to hex when the bytes do not fit the type.</summary>
    public string Describe() => $"{TypeName}: {DescribeData()}";

    private string DescribeData()
    {
        switch (Type)
        {
            case RegValueType.Sz or RegValueType.ExpandSz when Bytes.Length % 2 == 0:
                return Encoding.Unicode.GetString(Bytes).TrimEnd('\0');
            case RegValueType.MultiSz when Bytes.Length % 2 == 0:
                return string.Join(" | ", Encoding.Unicode.GetString(Bytes).TrimEnd('\0').Split('\0'));
            case RegValueType.Dword when Bytes.Length == 4:
                var dword = BinaryPrimitives.ReadUInt32LittleEndian(Bytes);
                return string.Create(CultureInfo.InvariantCulture, $"0x{dword:x8} ({dword})");
            case RegValueType.Qword when Bytes.Length == 8:
                var qword = BinaryPrimitives.ReadUInt64LittleEndian(Bytes);
                return string.Create(CultureInfo.InvariantCulture, $"0x{qword:x16} ({qword})");
            default:
                return Bytes.Length == 0 ? "(empty)" : Convert.ToHexString(Bytes);
        }
    }
}

/// <param name="Line">1-based line where the operation starts.</param>
public abstract record RegFileOperation(int Line, string KeyPath);

public sealed record CreateRegKey(int Line, string KeyPath) : RegFileOperation(Line, KeyPath);

public sealed record DeleteRegKey(int Line, string KeyPath) : RegFileOperation(Line, KeyPath);

/// <param name="Name">Empty string addresses the default value.</param>
public sealed record SetRegValue(int Line, string KeyPath, string Name, RegData Data) : RegFileOperation(Line, KeyPath);

/// <param name="Name">Empty string addresses the default value.</param>
public sealed record DeleteRegValue(int Line, string KeyPath, string Name) : RegFileOperation(Line, KeyPath);

/// <param name="Operations">In file order, including repeated writes and deletions; empty when any diagnostic exists.</param>
public sealed record RegFileParseResult(RegFileFormat? Format, IReadOnlyList<RegFileOperation> Operations, IReadOnlyList<RegFileDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.Count == 0;
}
