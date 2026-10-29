using System.Buffers;

namespace ImageForge.Core.Features;

public enum FeatureState
{
    /// <summary>A state this version does not know; shown as DISM printed it, and never changed by a plan.</summary>
    Unknown,
    Enabled,

    /// <summary>Off, with its payload in the image: it can be enabled without a source.</summary>
    Disabled,

    /// <summary>Off, and enabling it needs a source (sources\sxs for NetFx3).</summary>
    DisabledWithPayloadRemoved,

    /// <summary>Enable Pending or Disable Pending: the change finishes on first boot.</summary>
    Pending,
}

/// <param name="RawState">The state exactly as DISM printed it.</param>
public sealed record WindowsFeature(string Name, FeatureState State, string RawState)
{
    private static readonly SearchValues<char> NameChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.-_");

    /// <summary>
    /// Letters, digits, '.', '-' and '_', starting with a letter or digit; the name goes to Dism.exe as
    /// /FeatureName:, so nothing else may get through.
    /// </summary>
    public static bool IsValidName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length is > 0 and <= 128 && char.IsAsciiLetterOrDigit(name[0]) && !name.AsSpan().ContainsAnyExcept(NameChars);
    }

    internal static FeatureState ParseState(string state) => state switch
    {
        "Enabled" => FeatureState.Enabled,
        "Disabled" => FeatureState.Disabled,
        "Disabled with Payload Removed" => FeatureState.DisabledWithPayloadRemoved,
        "Enable Pending" or "Disable Pending" => FeatureState.Pending,
        _ => FeatureState.Unknown,
    };
}

public sealed record FeatureInventory(IReadOnlyList<WindowsFeature> Features, IReadOnlyList<string> Problems);

/// <summary>Reads <c>Dism /English /Image:... /Get-Features</c> (list format: "Feature Name" and "State" blocks).</summary>
public static class DismFeatureListParser
{
    public static FeatureInventory Parse(string output)
    {
        var (blocks, problems) = DismBlocks.Read(output, ["Feature Name", "State"]);
        var features = new List<WindowsFeature>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in blocks)
        {
            if (!block.TryGetValue("Feature Name", out var name))
            {
                problems.Add("A feature block has no Feature Name.");
            }
            else if (!WindowsFeature.IsValidName(name))
            {
                problems.Add($"'{name}' is not a valid feature name.");
            }
            else if (!block.TryGetValue("State", out var state))
            {
                problems.Add($"Feature '{name}' has no State.");
            }
            else if (!seen.Add(name))
            {
                problems.Add($"Feature '{name}' is listed twice.");
            }
            else
            {
                features.Add(new WindowsFeature(name, WindowsFeature.ParseState(state), state));
            }
        }

        return new FeatureInventory(features, problems);
    }
}
