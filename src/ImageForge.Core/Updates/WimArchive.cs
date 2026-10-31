using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ImageForge.Core.Updates;

public sealed record WimArchiveImage(string? Name, string? Description, long? FileCount, long? TotalBytes);

/// <param name="Images">From the XML resource; empty when it could not be read, which is not a problem by itself.</param>
/// <param name="Problem">Why the file is not a whole single-part WIM; null when it is.</param>
public sealed record WimInfo(int PartNumber, int TotalParts, int ImageCount, IReadOnlyList<WimArchiveImage> Images, string? Problem)
{
    /// <summary>The image Windows Setup boots from (1-based); 0 when none is marked, as in an install.wim or an MSU.</summary>
    public int BootIndex { get; init; }
}

/// <summary>
/// Reads the header and the XML resource of a WIM file without DISM. Newer MSU updates are WIM files rather than cabinets
/// (S2-UPDATES-INVENTORY); the file list itself is in compressed metadata and is left to DISM.
/// </summary>
public static class WimArchive
{
    public static ReadOnlySpan<byte> Magic => "MSWIM\0\0\0"u8;

    private const int HeaderSize = 208;
    private const int MaxXmlBytes = 4 * 1024 * 1024;

    public static bool HasMagic(ReadOnlySpan<byte> start) => start.StartsWith(Magic);

    public static WimInfo Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderSize];
        stream.Position = 0;
        if (stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize)
        {
            return new(0, 0, 0, [], Messages.WimTruncated);
        }

        if (!HasMagic(header))
        {
            return new(0, 0, 0, [], Messages.WimNoSignature);
        }

        var span = header.AsSpan();
        int part = BitConverter.ToUInt16(span[40..]);
        int parts = BitConverter.ToUInt16(span[42..]);
        var count = (int)Math.Min(BitConverter.ToUInt32(span[44..]), int.MaxValue);
        if (parts != 1 || part != 1)
        {
            return new(part, parts, count, [], Messages.WimSplit(part, parts));
        }

        if (count == 0)
        {
            return new(part, parts, count, [], Messages.WimNoImages);
        }

        var xmlSize = (long)(BitConverter.ToUInt64(span[72..]) & 0x00FF_FFFF_FFFF_FFFF);
        var xmlOffset = BitConverter.ToInt64(span[80..]);
        return new(part, parts, count, ReadImages(stream, xmlOffset, xmlSize), null)
        {
            BootIndex = (int)Math.Min(BitConverter.ToUInt32(span[120..]), int.MaxValue),
        };
    }

    private static IReadOnlyList<WimArchiveImage> ReadImages(Stream stream, long offset, long size)
    {
        if (size is <= 0 or > MaxXmlBytes || offset < HeaderSize || offset + size > stream.Length)
        {
            return [];
        }

        var bytes = new byte[size];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        try
        {
            var document = XDocument.Parse(Encoding.Unicode.GetString(bytes).TrimStart('﻿'));
            return [.. document.Root?.Elements("IMAGE").Select(i => new WimArchiveImage(
                (string?)i.Element("NAME"),
                (string?)i.Element("DESCRIPTION"),
                Number(i.Element("FILECOUNT")),
                Number(i.Element("TOTALBYTES")))) ?? []];
        }
        catch (XmlException)
        {
            return [];
        }
    }

    private static long? Number(XElement? element) =>
        long.TryParse(element?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}
