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

    /// <summary>The subfolder of a Features on Demand folder that describes its packages to DISM.</summary>
    public const string FodMetadataFolder = "metadata";

    private const string FodMetadataFile = "FoDMetadata_Client.cab";

    /// <summary>
    /// Why the folder cannot serve as a Features on Demand source, or null when it can: the LanguagesAndOptionalFeatures
    /// folder of the matching ISO, with package .cab files and a metadata subfolder holding FoDMetadata_Client.cab.
    /// Whether it matches the build of the image is known only to DISM, which then fails the addition.
    /// </summary>
    /// <param name="fileNames">Files directly in the folder.</param>
    /// <param name="metadataFileNames">Files in its metadata subfolder, or null when there is none.</param>
    public static string? FodProblem(IEnumerable<string> fileNames, IEnumerable<string>? metadataFileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        return metadataFileNames is null ? $"The folder has no {FodMetadataFolder} subfolder; use LanguagesAndOptionalFeatures of the Features on Demand ISO."
            : !metadataFileNames.Contains(FodMetadataFile, StringComparer.OrdinalIgnoreCase) ? $"The {FodMetadataFolder} subfolder has no {FodMetadataFile}."
            : !fileNames.Any(f => f.EndsWith(".cab", StringComparison.OrdinalIgnoreCase)) ? "The folder holds no package .cab files."
            : null;
    }

    private static bool IsNetFx3Package(string fileName) =>
        fileName.StartsWith("microsoft-windows-netfx3-ondemand-package~", StringComparison.OrdinalIgnoreCase)
        && fileName.Contains("~amd64~", StringComparison.OrdinalIgnoreCase)
        && fileName.EndsWith(".cab", StringComparison.OrdinalIgnoreCase);
}
