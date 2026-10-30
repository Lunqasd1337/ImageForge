namespace ImageForge.Core.Updates;

/// <param name="Name">The package identity, such as Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9457.1.0.</param>
/// <param name="State">As DISM reports it: Installed, Staged, Superseded, InstallPending and so on.</param>
public sealed record InstalledPackage(string Name, string State, string ReleaseType)
{
    /// <summary>The version after the last '~', such as 26100.9457.1.0.</summary>
    public string Version => Name[(Name.LastIndexOf('~') + 1)..];

    public bool IsInstalled => State.Equals("Installed", StringComparison.OrdinalIgnoreCase);
}

/// <param name="Added">Names that were not in the image before, sorted.</param>
/// <param name="Changed">Packages whose state changed, as "name: before -> after".</param>
/// <param name="Superseded">How many packages became Superseded.</param>
public sealed record PackageChange(IReadOnlyList<string> Added, IReadOnlyList<string> Changed, int Superseded)
{
    public bool IsEmpty => Added.Count == 0 && Changed.Count == 0;
}

/// <summary>The servicing packages of an image (DismGetPackages): updates, language packs and Features on Demand.</summary>
public sealed record PackageInventory(IReadOnlyList<InstalledPackage> Packages, IReadOnlyList<string> Problems)
{
    /// <summary>The version of the installed cumulative update (Package_for_RollupFix); null when none is installed.</summary>
    public string? Rollup => Latest("Package_for_RollupFix~");

    /// <summary>The version of the installed servicing stack (Package_for_ServicingStack_…); null when none is installed.</summary>
    public string? ServicingStack => Latest("Package_for_ServicingStack");

    public static PackageChange Compare(PackageInventory before, PackageInventory after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var was = before.Packages.ToDictionary(p => p.Name, p => p.State, StringComparer.OrdinalIgnoreCase);
        var added = after.Packages.Where(p => !was.ContainsKey(p.Name)).Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var changed = after.Packages
            .Where(p => was.TryGetValue(p.Name, out var state) && !string.Equals(state, p.State, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new(
            added,
            [.. changed.Select(p => $"{p.Name}: {was[p.Name]} -> {p.State}").Order(StringComparer.Ordinal)],
            changed.Count(p => p.State.Equals("Superseded", StringComparison.OrdinalIgnoreCase)));
    }

    private string? Latest(string prefix) => Packages
        .Where(p => p.IsInstalled && p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .Select(p => p.Version)
        .OrderByDescending(v => System.Version.TryParse(v, out var parsed) ? parsed : new Version())
        .FirstOrDefault();
}
