using System.Text;

namespace ImageForge.Core.Updates;

public sealed record CabinetEntry(string Name, uint Size);

/// <param name="Problem">Why the file table could not be read; null when <paramref name="Entries"/> is the whole table.</param>
public sealed record CabinetListing(IReadOnlyList<CabinetEntry> Entries, string? Problem);

/// <summary>
/// Reads the file table of a Microsoft cabinet (MSU and CAB updates are cabinets) without unpacking anything: the header,
/// then each CFFILE entry; the reserve that holds the signature is skipped by the offset of the table.
/// </summary>
public static class Cabinet
{
    private const ushort PrevCabinet = 0x0001;
    private const ushort NextCabinet = 0x0002;
    private const ushort NameIsUtf8 = 0x80;

    public static CabinetListing Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using var r = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
            if (stream.Length < 36 || !r.ReadBytes(4).AsSpan().SequenceEqual("MSCF"u8))
            {
                return new([], "нет подписи MSCF: это не файл cabinet");
            }

            r.ReadUInt32();
            var size = r.ReadUInt32();
            r.ReadUInt32();
            var filesOffset = r.ReadUInt32();
            r.ReadUInt32();
            r.ReadUInt16();
            r.ReadUInt16();
            var count = r.ReadUInt16();
            var flags = r.ReadUInt16();
            if (size > stream.Length)
            {
                return new([], $"файл обрезан: в заголовке {size} байт, на диске {stream.Length}");
            }

            if ((flags & (PrevCabinet | NextCabinet)) != 0)
            {
                return new([], "многотомный cabinet не поддерживается");
            }

            stream.Position = filesOffset;
            var entries = new List<CabinetEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var fileSize = r.ReadUInt32();
                r.ReadBytes(10);
                var attributes = r.ReadUInt16();
                entries.Add(new(Name(r, (attributes & NameIsUtf8) != 0), fileSize));
            }

            return new(entries, null);
        }
        catch (EndOfStreamException)
        {
            return new([], "файл обрезан: таблица файлов не дочитана");
        }
    }

    private static string Name(BinaryReader r, bool utf8)
    {
        var bytes = new List<byte>();
        for (var b = r.ReadByte(); b != 0; b = r.ReadByte())
        {
            if (bytes.Count == 256)
            {
                throw new EndOfStreamException();
            }

            bytes.Add(b);
        }

        return (utf8 ? Encoding.UTF8 : Encoding.Latin1).GetString([.. bytes]);
    }
}
