using System.Globalization;
using ImageForge.Core.Appx;
using ImageForge.Core.Features;
using ImageForge.Core.Safety;

namespace ImageForge.Core.Servicing;

/// <summary>Argument lists for Dism.exe. Values are passed as separate arguments, never through a shell.</summary>
public static class DismCliArguments
{
    public static IReadOnlyList<string> ExportImage(string sourceImageFile, int sourceIndex, string destinationImageFile)
    {
        var source = LocalPath(sourceImageFile, nameof(sourceImageFile));
        var destination = LocalPath(destinationImageFile, nameof(destinationImageFile));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceIndex);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Export source and destination must differ.", nameof(destinationImageFile));
        }

        List<string> arguments =
        [
            "/English",
            "/Export-Image",
            "/SourceImageFile:" + source,
            "/SourceIndex:" + sourceIndex.ToString(CultureInfo.InvariantCulture),
            "/DestinationImageFile:" + destination,
            "/Compress:max",
        ];

        // Integrity tables exist only in WIM files; an ESD source (official media) has none.
        if (!source.EndsWith(".esd", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("/CheckIntegrity");
        }

        return arguments;
    }

    public static IReadOnlyList<string> GetProvisionedAppx(string mountDirectory) =>
        ["/English", "/Image:" + LocalPath(mountDirectory, nameof(mountDirectory)), "/Get-ProvisionedAppxPackages"];

    public static IReadOnlyList<string> RemoveProvisionedAppx(string mountDirectory, PackageIdentity package)
    {
        ArgumentNullException.ThrowIfNull(package);

        // Re-validated here so that a hand-built identity cannot smuggle extra arguments into the name.
        if (PackageIdentity.Parse(package.FullName) != package)
        {
            throw new ArgumentException($"'{package.FullName}' is not a valid package full name.", nameof(package));
        }

        return
        [
            "/English",
            "/Image:" + LocalPath(mountDirectory, nameof(mountDirectory)),
            "/Remove-ProvisionedAppxPackage",
            "/PackageName:" + package.FullName,
        ];
    }

    public static IReadOnlyList<string> GetFeatures(string mountDirectory) =>
        ["/English", "/Image:" + LocalPath(mountDirectory, nameof(mountDirectory)), "/Get-Features"];

    public static IReadOnlyList<string> GetFeatureInfo(string mountDirectory, string featureName) =>
        ["/English", "/Image:" + LocalPath(mountDirectory, nameof(mountDirectory)), "/Get-FeatureInfo", "/FeatureName:" + FeatureName(featureName)];

    /// <summary>Without /All (parents are not added silently) and with /LimitAccess: never a download from Windows Update.</summary>
    public static IReadOnlyList<string> EnableFeature(string mountDirectory, string featureName) =>
        ["/English", "/Image:" + LocalPath(mountDirectory, nameof(mountDirectory)), "/Enable-Feature", "/FeatureName:" + FeatureName(featureName), "/LimitAccess"];

    /// <summary>Without /Remove: the payload stays in the image unless the user explicitly asks otherwise (plan, section 10).</summary>
    public static IReadOnlyList<string> DisableFeature(string mountDirectory, string featureName) =>
        ["/English", "/Image:" + LocalPath(mountDirectory, nameof(mountDirectory)), "/Disable-Feature", "/FeatureName:" + FeatureName(featureName)];

    public static IReadOnlyList<string> GetCapabilities(string mountDirectory) =>
        ["/English", "/Image:" + LocalPath(mountDirectory, nameof(mountDirectory)), "/Get-Capabilities"];

    public static IReadOnlyList<string> GetCapabilityInfo(string mountDirectory, CapabilityIdentity capability) =>
        ["/English", "/Image:" + LocalPath(mountDirectory, nameof(mountDirectory)), "/Get-CapabilityInfo", "/CapabilityName:" + CapabilityName(capability)];

    public static IReadOnlyList<string> RemoveCapability(string mountDirectory, CapabilityIdentity capability) =>
        ["/English", "/Image:" + LocalPath(mountDirectory, nameof(mountDirectory)), "/Remove-Capability", "/CapabilityName:" + CapabilityName(capability)];

    private static string FeatureName(string name) =>
        WindowsFeature.IsValidName(name) ? name : throw new ArgumentException($"'{name}' is not a valid feature name.", nameof(name));

    // Re-validated here so that a hand-built identity cannot smuggle extra arguments into the name.
    private static string CapabilityName(CapabilityIdentity capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        return CapabilityIdentity.Parse(capability.FullName) == capability
            ? capability.FullName
            : throw new ArgumentException($"'{capability.FullName}' is not a valid capability name.", nameof(capability));
    }

    private static string LocalPath(string path, string parameterName) =>
        WindowsPath.TryNormalize(path, out var normalized) == WriteScopeViolation.None
            ? normalized
            : throw new ArgumentException($"'{path}' is not an absolute local path.", parameterName);
}
