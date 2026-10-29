namespace ImageForge.Core.Features;

/// <summary>"Key : Value" blocks of DISM /English list output, separated by blank lines.</summary>
internal static class DismBlocks
{
    internal const string Success = "The operation completed successfully.";

    /// <param name="keys">The block fields; other "x : y" lines ("Features listing for package : ...") are skipped.</param>
    public static (List<Dictionary<string, string>> Blocks, List<string> Problems) Read(string output, string[] keys)
    {
        ArgumentNullException.ThrowIfNull(output);
        var blocks = new List<Dictionary<string, string>>();
        var problems = new List<string>();
        var block = new Dictionary<string, string>(StringComparer.Ordinal);
        var succeeded = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                Flush();
            }
            else if (line == Success)
            {
                succeeded = true;
            }
            else if (line.IndexOf(" : ", StringComparison.Ordinal) is var separator and > 0 && keys.Contains(line[..separator]))
            {
                if (!block.TryAdd(line[..separator], line[(separator + 3)..].Trim()))
                {
                    problems.Add($"Repeated field '{line[..separator]}' in one block.");
                }
            }
        }

        Flush();
        if (!succeeded)
        {
            problems.Add($"DISM output has no '{Success}' line.");
        }

        return (blocks, problems);

        void Flush()
        {
            if (block.Count > 0)
            {
                blocks.Add(block);
                block = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
    }
}

/// <summary>
/// The "Key : Value" lines of <c>/Get-FeatureInfo</c> or <c>/Get-CapabilityInfo</c>, for showing to the user; the
/// first value of a repeated key wins. Null when DISM did not report success.
/// </summary>
public static class DismInfoParser
{
    public static IReadOnlyDictionary<string, string>? Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var succeeded = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line == DismBlocks.Success)
            {
                succeeded = true;
            }
            else if (line.IndexOf(" : ", StringComparison.Ordinal) is var separator and > 0)
            {
                fields.TryAdd(line[..separator], line[(separator + 3)..].Trim());
            }
        }

        return succeeded ? fields : null;
    }
}
