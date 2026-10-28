using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ImageForge.Core.Appx;

/// <param name="IsFramework">The manifest marks the package as a framework other packages build on.</param>
/// <param name="DependsOn">Family names of the packages it needs, from its manifest.</param>
public sealed record AppxPackageFacts(bool IsFramework, IReadOnlyList<string> DependsOn);

public sealed record ProvisionedAppxPackage(string DisplayName, PackageIdentity Identity)
{
    public string PackageName => Identity.FullName;

    /// <summary>What its manifest says; null until read, or when it could not be read (see <see cref="FactsProblem"/>).</summary>
    public AppxPackageFacts? Facts { get; init; }

    public string? FactsProblem { get; init; }
}

/// <param name="Problems">Anything that makes the list untrustworthy; a caller must not plan removals from it.</param>
public sealed record AppxInventory(IReadOnlyList<ProvisionedAppxPackage> Packages, IReadOnlyList<string> Problems)
{
    /// <summary>
    /// Family names the inbox apps (Windows\SystemApps) depend on; they cannot be removed, so neither can these.
    /// Null when not read or when an inbox manifest could not be read.
    /// </summary>
    public IReadOnlyCollection<string>? InboxDependencies { get; init; }

    /// <summary>SHA-256 over the sorted full names; a run compares it with the list it reads from its own mount.</summary>
    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', Packages.Select(p => p.PackageName).Order(StringComparer.Ordinal)))));
}

/// <summary>
/// Reads the output of <c>Dism /English /Image:... /Get-ProvisionedAppxPackages</c>: blocks of "Key : Value" lines
/// separated by blank lines. Each block must name a valid package full name that agrees with its other fields.
/// </summary>
public static class DismAppxListParser
{
    private const string Success = "The operation completed successfully.";

    public static AppxInventory Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var packages = new List<ProvisionedAppxPackage>();
        var problems = new List<string>();
        var block = new Dictionary<string, string>(StringComparer.Ordinal);
        var succeeded = false;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (line == Success)
            {
                succeeded = true;
                continue;
            }

            var separator = line.IndexOf(" : ", StringComparison.Ordinal);
            if (separator > 0 && IsBlockKey(line[..separator]))
            {
                if (!block.TryAdd(line[..separator], line[(separator + 3)..].Trim()))
                {
                    problems.Add($"Repeated field '{line[..separator]}' in one package block.");
                }
            }
        }

        Flush();
        if (!succeeded)
        {
            problems.Add($"DISM output has no '{Success}' line.");
        }

        return new AppxInventory(packages, problems);

        void Flush()
        {
            if (block.Count == 0)
            {
                return;
            }

            if (Check(block) is { } problem)
            {
                problems.Add(problem);
            }
            else
            {
                packages.Add(new ProvisionedAppxPackage(block["DisplayName"], PackageIdentity.Parse(block["PackageName"])!));
            }

            block.Clear();
        }
    }

    // Only the block fields; the header lines ("Version: 10.0...", "Image Version: ...") use ": " without a leading space.
    private static bool IsBlockKey(string key) =>
        key is "DisplayName" or "Version" or "Architecture" or "ResourceId" or "PackageName" or "Regions";

    private static string? Check(Dictionary<string, string> block)
    {
        if (!block.TryGetValue("PackageName", out var name))
        {
            return "A package block has no PackageName.";
        }

        if (PackageIdentity.Parse(name) is not { } identity)
        {
            return $"'{name}' is not a valid package full name.";
        }

        if (!block.TryGetValue("DisplayName", out var display) || display != identity.Name)
        {
            return $"DisplayName does not match '{name}'.";
        }

        if (block.TryGetValue("Version", out var version) && version != identity.Version.ToString(4))
        {
            return $"Version {version} does not match '{name}'.";
        }

        if (block.TryGetValue("Architecture", out var architecture) && architecture != identity.Architecture)
        {
            return $"Architecture {architecture} does not match '{name}'.";
        }

        if (block.TryGetValue("ResourceId", out var resource) && PackageIdentity.NormalizeResourceId(resource) != identity.ResourceId)
        {
            return string.Create(CultureInfo.InvariantCulture, $"ResourceId {resource} does not match '{name}'.");
        }

        return null;
    }
}
