using System.Text;

namespace ImageForge.Core.Drivers;

/// <param name="Key">Null for a line without '='.</param>
/// <param name="Values">Comma-separated, unquoted, with %strings% substituted.</param>
public sealed record InfLine(string? Key, IReadOnlyList<string> Values, int LineNumber);

/// <summary>A parsed INF file: sections of lines, looked up without regard to case (plan, section 12).</summary>
public sealed class InfDocument
{
    private readonly Dictionary<string, List<InfLine>> _sections;

    internal InfDocument(Dictionary<string, List<InfLine>> sections) => _sections = sections;

    public bool HasSection(string section) => _sections.ContainsKey(section);

    public IReadOnlyList<InfLine> Lines(string section) => _sections.TryGetValue(section, out var lines) ? lines : [];

    /// <summary>The first value of the first line with this key, or null.</summary>
    public string? Value(string section, string key) =>
        Lines(section).FirstOrDefault(l => string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase))?.Values[0];
}

/// <summary>
/// Reads the INF syntax: [sections], key = value lists, ';' comments outside quotes, '\' line continuation, "" inside
/// quotes, and %key% replaced from [Strings] (or a localized [Strings.xxxx] when [Strings] lacks it). Only for the
/// preview; DISM checks the package itself.
/// </summary>
public static class InfParser
{
    public static InfDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var raw = new Dictionary<string, List<(string? Key, List<string> Values, int Line)>>(StringComparer.OrdinalIgnoreCase);
        List<(string? Key, List<string> Values, int Line)>? current = null;
        foreach (var (line, number) in LogicalLines(text))
        {
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!raw.TryGetValue(name, out current))
                {
                    current = [];
                    raw[name] = current;
                }
            }
            else if (current is not null)
            {
                var (key, values) = SplitLine(line);
                current.Add((key, values, number));
            }
        }

        // [Strings] wins; a localized [Strings.xxxx] only fills keys it lacks.
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in raw.OrderBy(s => string.Equals(s.Key, "Strings", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Where(s => string.Equals(s.Key, "Strings", StringComparison.OrdinalIgnoreCase) || s.Key.StartsWith("Strings.", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var (key, values, _) in section.Value)
            {
                if (key is not null)
                {
                    strings.TryAdd(key, string.Join(",", values));
                }
            }
        }

        var sections = raw.ToDictionary(
            s => s.Key,
            s => s.Value.Select(l => new InfLine(
                l.Key is null ? null : Substitute(l.Key, strings),
                [.. l.Values.Select(v => Substitute(v, strings))],
                l.Line)).ToList(),
            StringComparer.OrdinalIgnoreCase);
        return new InfDocument(sections);
    }

    // Joins '\'-continued lines and drops comments and blank lines; the number is that of the first physical line.
    private static IEnumerable<(string Line, int Number)> LogicalLines(string text)
    {
        var builder = new StringBuilder();
        var start = 0;
        var number = 0;
        foreach (var physical in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            number++;
            var line = StripComment(physical).Trim();
            if (builder.Length == 0)
            {
                start = number;
            }

            if (line.EndsWith('\\'))
            {
                builder.Append(line.AsSpan(0, line.Length - 1)).Append(' ');
                continue;
            }

            builder.Append(line);
            var whole = builder.ToString().Trim();
            builder.Clear();
            if (whole.Length > 0)
            {
                yield return (whole, start);
            }
        }

        if (builder.Length > 0)
        {
            yield return (builder.ToString().Trim(), start);
        }
    }

    private static string StripComment(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ';' && !quoted)
            {
                return line[..i];
            }
        }

        return line;
    }

    private static (string? Key, List<string> Values) SplitLine(string line)
    {
        var parts = new List<string>();
        var part = new StringBuilder();
        string? key = null;
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    part.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (!quoted && c == '=' && key is null && parts.Count == 0)
            {
                key = part.ToString().Trim();
                part.Clear();
            }
            else if (!quoted && c == ',')
            {
                parts.Add(part.ToString().Trim());
                part.Clear();
            }
            else
            {
                part.Append(c);
            }
        }

        parts.Add(part.ToString().Trim());
        return (key, parts);
    }

    private static string Substitute(string value, Dictionary<string, string> strings)
    {
        if (!value.Contains('%', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var end = value[i] == '%' ? value.IndexOf('%', i + 1) : -1;
            if (end < 0)
            {
                builder.Append(value[i]);
                continue;
            }

            var token = value[(i + 1)..end];
            builder.Append(token.Length == 0 ? "%" : strings.TryGetValue(token, out var text) ? Substitute(text, strings) : value[i..(end + 1)]);
            i = end;
        }

        return builder.ToString();
    }
}

public static class InfText
{
    /// <summary>INF files are UTF-16 LE (with or without BOM), UTF-8 with BOM, or ANSI; ANSI is read as Latin-1.</summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes is [0xFF, 0xFE, ..])
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        // Text without a BOM whose odd bytes are all zero is UTF-16 LE of ASCII characters.
        var utf16 = bytes.Length >= 2 && bytes.Length % 2 == 0 && Enumerable.Range(0, Math.Min(bytes.Length / 2, 256)).All(i => bytes[(2 * i) + 1] == 0);
        return utf16 ? Encoding.Unicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
    }
}
