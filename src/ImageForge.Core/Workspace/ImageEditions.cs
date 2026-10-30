using ImageForge.Core.Updates;

namespace ImageForge.Core.Workspace;

/// <summary>One image of the source install.wim, as its XML names it.</summary>
public sealed record ImageEdition(int Index, string Name);

/// <summary>
/// The editions of the source install.wim, read from the WIM header without DISM. One is serviced; the run keeps the
/// others unchanged (plan, section 5).
/// </summary>
public static class ImageEditions
{
    /// <summary>The editions in index order; empty when the file is missing or not a readable WIM.</summary>
    public static IReadOnlyList<ImageEdition> Read(string sourceDir)
    {
        ArgumentNullException.ThrowIfNull(sourceDir);
        var file = Path.Combine(sourceDir, "sources", "install.wim");
        try
        {
            using var stream = File.OpenRead(file);
            var wim = WimArchive.Read(stream);
            return wim.Problem is null && wim.Images.Count == wim.ImageCount
                ? [.. wim.Images.Select((image, i) => new ImageEdition(i + 1, image.Name ?? $"Образ {i + 1}"))]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The requested index when the source has it; otherwise Pro, the edition the fixture is checked on (owner decision),
    /// and the first image when there is no Pro.
    /// </summary>
    public static int Choose(IReadOnlyList<ImageEdition> editions, int requested)
    {
        ArgumentNullException.ThrowIfNull(editions);
        if (editions.Any(e => e.Index == requested))
        {
            return requested;
        }

        return editions.FirstOrDefault(e => e.Name.EndsWith(" Pro", StringComparison.OrdinalIgnoreCase))?.Index ?? 1;
    }
}
