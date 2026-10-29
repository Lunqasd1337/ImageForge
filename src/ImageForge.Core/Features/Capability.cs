using System.Buffers;
using System.Globalization;

namespace ImageForge.Core.Features;

/// <summary>
/// A capability (Feature on Demand) name, Name~Architecture~Resource~Language~Version; DISM prints empty parts as
/// nothing between the tildes ("OpenSSH.Client~~~~0.0.1.0", "VBSCRIPT~~~~"). Parsing is strict, because the name goes
/// to Dism.exe as /CapabilityName:.
/// </summary>
public sealed record CapabilityIdentity(string FullName, string Name, string Architecture, string Resource, string Language, string Version)
{
    private static readonly SearchValues<char> NameChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.-_");

    private static readonly SearchValues<char> TagChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-");

    public static CapabilityIdentity? Parse(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        var parts = fullName.Split('~');
        if (parts.Length != 5)
        {
            return null;
        }

        var (name, architecture, resource, language, version) = (parts[0], parts[1], parts[2], parts[3], parts[4]);
        if (name.Length is 0 or > 128 || !char.IsAsciiLetterOrDigit(name[0]) || name.AsSpan().ContainsAnyExcept(NameChars)
            || !IsTag(architecture) || !IsTag(resource) || !IsTag(language) || !IsVersion(version))
        {
            return null;
        }

        return new CapabilityIdentity(fullName, name, architecture, resource, language, version);
    }

    private static bool IsTag(string tag) => tag.Length <= 32 && !tag.AsSpan().ContainsAnyExcept(TagChars);

    private static bool IsVersion(string version)
    {
        if (version.Length == 0)
        {
            return true;
        }

        var parts = version.Split('.');
        return parts.Length <= 4 && parts.All(p => p.Length is > 0 and <= 5
            && ushort.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }
}

public enum CapabilityState
{
    /// <summary>Any other state (Staged, Superseded, ...); shown as DISM printed it, and never changed by a plan.</summary>
    Unknown,
    Installed,
    NotPresent,

    /// <summary>Install Pending or Uninstall Pending: the change finishes on first boot.</summary>
    Pending,
}

/// <param name="RawState">The state exactly as DISM printed it.</param>
public sealed record Capability(CapabilityIdentity Identity, CapabilityState State, string RawState)
{
    internal static CapabilityState ParseState(string state) => state switch
    {
        "Installed" => CapabilityState.Installed,
        "Not Present" => CapabilityState.NotPresent,
        "Install Pending" or "Uninstall Pending" => CapabilityState.Pending,
        _ => CapabilityState.Unknown,
    };
}

public sealed record CapabilityInventory(IReadOnlyList<Capability> Capabilities, IReadOnlyList<string> Problems);

/// <summary>Reads <c>Dism /English /Image:... /Get-Capabilities</c> ("Capability Identity" and "State" blocks).</summary>
public static class DismCapabilityListParser
{
    public static CapabilityInventory Parse(string output)
    {
        var (blocks, problems) = DismBlocks.Read(output, ["Capability Identity", "State"]);
        var capabilities = new List<Capability>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in blocks)
        {
            if (!block.TryGetValue("Capability Identity", out var name))
            {
                problems.Add("A capability block has no Capability Identity.");
            }
            else if (CapabilityIdentity.Parse(name) is not { } identity)
            {
                problems.Add($"'{name}' is not a valid capability name.");
            }
            else if (!block.TryGetValue("State", out var state))
            {
                problems.Add($"Capability '{name}' has no State.");
            }
            else if (!seen.Add(name))
            {
                problems.Add($"Capability '{name}' is listed twice.");
            }
            else
            {
                capabilities.Add(new Capability(identity, Capability.ParseState(state), state));
            }
        }

        return new CapabilityInventory(capabilities, problems);
    }
}
