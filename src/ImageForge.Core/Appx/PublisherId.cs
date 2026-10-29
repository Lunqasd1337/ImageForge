using System.Security.Cryptography;
using System.Text;

namespace ImageForge.Core.Appx;

/// <summary>
/// The 13-character publisher id of a package family: the first 64 bits of SHA-256 over the UTF-16LE publisher name,
/// padded to 65 bits and written in Crockford base32, lowercase.
/// </summary>
public static class PublisherId
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    public static string From(string publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        var bits = (UInt128)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(hash) << 1;
        var id = new char[13];
        for (var i = 12; i >= 0; i--)
        {
            id[i] = Alphabet[(int)(bits & 31)];
            bits >>= 5;
        }

        return new string(id);
    }
}
