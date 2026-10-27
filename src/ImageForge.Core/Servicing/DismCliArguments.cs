using System.Globalization;
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

    private static string LocalPath(string path, string parameterName) =>
        WindowsPath.TryNormalize(path, out var normalized) == WriteScopeViolation.None
            ? normalized
            : throw new ArgumentException($"'{path}' is not an absolute local path.", parameterName);
}
