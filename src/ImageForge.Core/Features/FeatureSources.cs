namespace ImageForge.Core.Features;

/// <summary>
/// Local sources for features whose payload was removed. ImageForge never falls back to Windows Update, so a source is
/// checked before it is used (plan, section 10).
/// </summary>
public static class FeatureSources
{
    public const string NetFx3 = "NetFx3";

    /// <summary>The media folder that holds the .NET Framework 3.5 payload.</summary>
    public static readonly string[] MediaSxsSegments = ["sources", "sxs"];

    /// <summary>
    /// Why the folder cannot serve as the NetFx3 source for an x64 image, or null when it can: it must hold the
    /// .NET Framework 3.5 on-demand package for amd64.
    /// </summary>
    public static string? NetFx3Problem(IEnumerable<string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        return fileNames.Any(IsNetFx3Package)
            ? null
            : "The folder has no microsoft-windows-netfx3-ondemand-package~...~amd64~~.cab.";
    }

    private static bool IsNetFx3Package(string fileName) =>
        fileName.StartsWith("microsoft-windows-netfx3-ondemand-package~", StringComparison.OrdinalIgnoreCase)
        && fileName.Contains("~amd64~", StringComparison.OrdinalIgnoreCase)
        && fileName.EndsWith(".cab", StringComparison.OrdinalIgnoreCase);
}
