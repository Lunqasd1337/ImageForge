using System.Globalization;
using System.Text;

namespace ImageForge.Core.Registry.RegFile;

public interface IRegFileParser
{
    /// <param name="ansiCodePage">Used for REGEDIT4 string types written as hex, which are ANSI bytes.</param>
    RegFileParseResult Parse(string text, int ansiCodePage);
}

/// <summary>
/// Syntax of REGEDIT4 and Windows Registry Editor Version 5.00 files, on already decoded text. Every problem is
/// reported with its line, and any problem blocks the whole file. Key paths are not mapped here.
/// </summary>
public sealed class RegFileParser : IRegFileParser
{
    private const string HeaderV5 = "Windows Registry Editor Version 5.00";
    private const string HeaderV4 = "REGEDIT4";

    public RegFileParseResult Parse(string text, int ansiCodePage)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var diagnostics = new List<RegFileDiagnostic>();
        var operations = new List<RegFileOperation>();

        var index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0)
        {
            index++;
        }

        RegFileFormat? format = index < lines.Length ? lines[index].Trim() switch
        {
            HeaderV5 => RegFileFormat.Version5,
            HeaderV4 => RegFileFormat.Regedit4,
            _ => null,
        } : null;
        if (format is null)
        {
            diagnostics.Add(new RegFileDiagnostic(RegFileCodes.HeaderMissing, Math.Min(index, lines.Length - 1) + 1, $"The file must start with \"{HeaderV5}\" or \"{HeaderV4}\"."));
            return new RegFileParseResult(null, [], diagnostics);
        }

        string? currentKey = null;
        for (index++; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index].TrimStart();
            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                currentKey = ParseKey(line, lineNumber, operations, diagnostics);
                continue;
            }

            if (line[0] is not ('@' or '"'))
            {
                diagnostics.Add(new RegFileDiagnostic(RegFileCodes.LineUnrecognized, lineNumber, "Expected a [key], a value or a ; comment."));
                continue;
            }

            if (currentKey is null)
            {
                diagnostics.Add(new RegFileDiagnostic(RegFileCodes.ValueOutsideKey, lineNumber, "A value must follow a [key] line."));
                continue;
            }

            try
            {
                operations.Add(ParseValue(lines, ref index, currentKey, format.Value, ansiCodePage));
            }
            catch (RegSyntaxException ex)
            {
                diagnostics.Add(new RegFileDiagnostic(ex.Code, lineNumber, ex.Message));
            }
        }

        return new RegFileParseResult(format, diagnostics.Count == 0 ? operations : [], diagnostics);
    }

    private static string? ParseKey(string line, int lineNumber, List<RegFileOperation> operations, List<RegFileDiagnostic> diagnostics)
    {
        var trimmed = line.TrimEnd();
        if (trimmed[^1] != ']')
        {
            diagnostics.Add(new RegFileDiagnostic(RegFileCodes.KeyMalformed, lineNumber, "A key line must be [path] or [-path] with nothing after it."));
            return null;
        }

        var inner = trimmed[1..^1];
        var delete = inner.StartsWith('-');
        var path = delete ? inner[1..] : inner;
        if (path.Length == 0)
        {
            diagnostics.Add(new RegFileDiagnostic(RegFileCodes.KeyMalformed, lineNumber, "The key path is empty."));
            return null;
        }

        operations.Add(delete ? new DeleteRegKey(lineNumber, path) : new CreateRegKey(lineNumber, path));
        return delete ? null : path;
    }

    private static RegFileOperation ParseValue(string[] lines, ref int index, string key, RegFileFormat format, int ansiCodePage)
    {
        var lineNumber = index + 1;
        var line = lines[index].TrimStart();
        var pos = 0;
        string name;
        if (line[0] == '@')
        {
            name = string.Empty;
            pos = 1;
        }
        else
        {
            name = ReadQuoted(line, ref pos);
        }

        SkipSpaces(line, ref pos);
        Expect(pos < line.Length && line[pos] == '=', "Expected = after the value name.");
        pos++;
        SkipSpaces(line, ref pos);
        Expect(pos < line.Length, "The value has no data.");

        var rest = line[pos..];
        if (rest[0] == '-')
        {
            pos++;
            ExpectEnd(line, pos);
            return new DeleteRegValue(lineNumber, key, name);
        }

        if (rest[0] == '"')
        {
            var data = ReadQuoted(line, ref pos);
            ExpectEnd(line, pos);
            return new SetRegValue(lineNumber, key, name, new RegData(RegValueType.Sz, Utf16Z(data)));
        }

        if (rest.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
        {
            var digits = StripComment(rest["dword:".Length..]).Trim();
            Expect(digits.Length is >= 1 and <= 8 && digits.All(char.IsAsciiHexDigit), "dword: needs 1 to 8 hex digits.");
            var value = uint.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            return new SetRegValue(lineNumber, key, name, new RegData(RegValueType.Dword, BitConverter.GetBytes(value)));
        }

        if (rest.StartsWith("hex", StringComparison.OrdinalIgnoreCase))
        {
            var (type, afterColon) = ReadHexType(rest);
            var bytes = ReadHexBytes(lines, ref index, afterColon);
            if (format == RegFileFormat.Regedit4 && type is RegValueType.Sz or RegValueType.ExpandSz or RegValueType.MultiSz)
            {
                bytes = AnsiToUtf16(bytes, ansiCodePage);
            }

            return new SetRegValue(lineNumber, key, name, new RegData(type, bytes));
        }

        throw new RegSyntaxException(RegFileCodes.ValueMalformed, "Expected \"text\", dword:, hex:, hex(type): or - as the value data.");
    }

    private static (RegValueType Type, string AfterColon) ReadHexType(string rest)
    {
        var pos = 3;
        uint type = (uint)RegValueType.Binary;
        if (pos < rest.Length && rest[pos] == '(')
        {
            var close = rest.IndexOf(')', pos);
            Expect(close > pos + 1, "hex( needs a type and a closing ).");
            var digits = rest[(pos + 1)..close];
            Expect(digits.Length <= 8 && digits.All(char.IsAsciiHexDigit), "The hex type must be hex digits.");
            type = uint.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            if (type > (uint)RegValueType.Qword)
            {
                throw new RegSyntaxException(RegFileCodes.ValueTypeUnsupported, $"Value type 0x{type:X} is not supported; only 0 to b are.");
            }

            pos = close + 1;
        }

        Expect(pos < rest.Length && rest[pos] == ':', "Expected : after hex.");
        return ((RegValueType)type, rest[(pos + 1)..]);
    }

    private static byte[] ReadHexBytes(string[] lines, ref int index, string first)
    {
        var text = new StringBuilder();
        var part = StripComment(first).Trim();
        while (part.EndsWith('\\'))
        {
            text.Append(part, 0, part.Length - 1);
            index++;
            Expect(index < lines.Length && lines[index].Trim().Length > 0, "The hex data continues past the end of the file.");
            part = StripComment(lines[index]).Trim();
        }

        text.Append(part);
        var all = text.ToString();
        if (all.Trim().Length == 0)
        {
            return [];
        }

        var tokens = all.Split(',');
        var bytes = new byte[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i].Trim();
            Expect(token.Length is 1 or 2 && token.All(char.IsAsciiHexDigit), $"\"{token}\" is not a hex byte.");
            bytes[i] = byte.Parse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    private static byte[] AnsiToUtf16(byte[] bytes, int codePage)
    {
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            ?? throw new RegSyntaxException(RegFileCodes.ValueMalformed, $"Code page {codePage} is not available.");
        try
        {
            return Encoding.Unicode.GetBytes(encoding.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            throw new RegSyntaxException(RegFileCodes.ValueMalformed, $"The string bytes are not valid in code page {codePage}.");
        }
    }

    private static string ReadQuoted(string line, ref int pos)
    {
        Expect(line[pos] == '"', "Expected a quoted string.");
        var builder = new StringBuilder();
        for (pos++; pos < line.Length; pos++)
        {
            var c = line[pos];
            if (c == '"')
            {
                pos++;
                return builder.ToString();
            }

            if (c == '\\')
            {
                pos++;
                Expect(pos < line.Length && line[pos] is '\\' or '"', "Only \\\\ and \\\" escapes are allowed in a string.");
                builder.Append(line[pos]);
                continue;
            }

            builder.Append(c);
        }

        throw new RegSyntaxException(RegFileCodes.ValueMalformed, "The string has no closing quote.");
    }

    private static void ExpectEnd(string line, int pos)
    {
        var tail = line[pos..].TrimStart();
        Expect(tail.Length == 0 || tail[0] == ';', "Unexpected text after the value.");
    }

    private static string StripComment(string text)
    {
        var semicolon = text.IndexOf(';', StringComparison.Ordinal);
        return semicolon < 0 ? text : text[..semicolon];
    }

    private static void SkipSpaces(string line, ref int pos)
    {
        while (pos < line.Length && line[pos] is ' ' or '\t')
        {
            pos++;
        }
    }

    private static byte[] Utf16Z(string text) => Encoding.Unicode.GetBytes(text + "\0");

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new RegSyntaxException(RegFileCodes.ValueMalformed, message);
        }
    }

    private sealed class RegSyntaxException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
