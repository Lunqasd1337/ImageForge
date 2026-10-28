using System.Text;

namespace ImageForge.Core.Registry.RegFile;

/// <summary>How the user wants a .reg file decoded. <see cref="Auto"/> trusts only a BOM or plain ASCII.</summary>
public enum RegFileEncoding
{
    Auto,
    Utf16,
    Utf8,
    Ansi,
}

public enum RegFileEncodingKind
{
    Utf16,
    Utf8,
    Ansi,
}

/// <param name="Text">Null when decoding is blocked; <paramref name="Diagnostics"/> then says why.</param>
/// <param name="CodePage">The ANSI code page used, shown to the user so they can change it; null for Unicode.</param>
public sealed record RegFileText(string? Text, RegFileEncodingKind? Encoding, int? CodePage, IReadOnlyList<RegFileDiagnostic> Diagnostics);

public interface IRegFileLoader
{
    RegFileText Decode(ReadOnlySpan<byte> bytes, RegFileEncoding encoding, int ansiCodePage);
}

/// <summary>
/// Turns the bytes of a .reg file into text before parsing. A BOM decides UTF-16LE or UTF-8; without one, plain
/// ASCII is unambiguous and anything else must not be guessed: text that is valid UTF-8 or contains zero bytes is
/// blocked until the user chooses, otherwise the shown ANSI code page is used. Invalid bytes always block.
/// </summary>
public sealed class RegFileLoader : IRegFileLoader
{
    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BeBom = [0xFE, 0xFF];
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public RegFileText Decode(ReadOnlySpan<byte> bytes, RegFileEncoding encoding, int ansiCodePage)
    {
        if (bytes.StartsWith(Utf16BeBom))
        {
            return Blocked(RegFileCodes.EncodingUnsupported, "UTF-16 big-endian .reg files are not supported.");
        }

        var kind = encoding switch
        {
            RegFileEncoding.Utf16 => RegFileEncodingKind.Utf16,
            RegFileEncoding.Utf8 => RegFileEncodingKind.Utf8,
            RegFileEncoding.Ansi => RegFileEncodingKind.Ansi,
            _ => Detect(bytes),
        };
        if (kind is null)
        {
            return Blocked(RegFileCodes.EncodingAmbiguous, "The file has no byte order mark and its encoding cannot be told apart; choose UTF-16, UTF-8 or the ANSI code page.");
        }

        var body = kind switch
        {
            RegFileEncodingKind.Utf16 when bytes.StartsWith(Utf16LeBom) => bytes[Utf16LeBom.Length..],
            RegFileEncodingKind.Utf8 when bytes.StartsWith(Utf8Bom) => bytes[Utf8Bom.Length..],
            _ => bytes,
        };

        Encoding decoder;
        switch (kind)
        {
            case RegFileEncodingKind.Utf16:
                if (body.Length % 2 != 0)
                {
                    return Blocked(RegFileCodes.EncodingInvalid, "The UTF-16 data has an odd number of bytes; the file is truncated.");
                }

                decoder = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
                break;
            case RegFileEncodingKind.Utf8:
                decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                break;
            default:
                var ansi = StrictCodePage(ansiCodePage);
                if (ansi is null)
                {
                    return Blocked(RegFileCodes.EncodingUnsupported, $"Code page {ansiCodePage} is not available.");
                }

                decoder = ansi;
                break;
        }

        try
        {
            return new RegFileText(decoder.GetString(body), kind, kind == RegFileEncodingKind.Ansi ? ansiCodePage : null, []);
        }
        catch (DecoderFallbackException ex)
        {
            return Blocked(RegFileCodes.EncodingInvalid, $"The file is not valid {kind}{(kind == RegFileEncodingKind.Ansi ? $" (code page {ansiCodePage})" : string.Empty)}: {ex.Message}");
        }
    }

    private static RegFileEncodingKind? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Utf16LeBom))
        {
            return RegFileEncodingKind.Utf16;
        }

        if (bytes.StartsWith(Utf8Bom))
        {
            return RegFileEncodingKind.Utf8;
        }

        if (bytes.Contains((byte)0))
        {
            return null;
        }

        if (!bytes.ContainsAnyExceptInRange((byte)0x01, (byte)0x7F))
        {
            return RegFileEncodingKind.Ansi;
        }

        return System.Text.Unicode.Utf8.IsValid(bytes) ? null : RegFileEncodingKind.Ansi;
    }

    private static Encoding? StrictCodePage(int codePage)
    {
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                ?? Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static RegFileText Blocked(string code, string message) => new(null, null, null, [new RegFileDiagnostic(code, 0, message)]);
}
