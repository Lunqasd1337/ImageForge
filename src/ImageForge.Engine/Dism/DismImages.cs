using Microsoft.Dism;

namespace ImageForge.Engine.Dism;

public sealed record WimImageSummary(int Index, string Name, string EditionId, string Version, string Architecture, ulong SizeBytes);

public sealed record MountedImageSummary(string ImageFile, int Index, string MountPath, string Mode, string Status);

/// <summary>Thin, synchronous wrapper over the DISM API. One instance owns one DismApi initialization.</summary>
public sealed class DismImages : IDisposable
{
    private bool _disposed;

    public DismImages(string? logFilePath = null)
    {
        DismApi.Initialize(DismLogLevel.LogErrorsWarnings, logFilePath);
    }

    public static IReadOnlyList<WimImageSummary> GetImages(string imageFile) =>
        [.. DismApi.GetImageInfo(imageFile).Select(i => new WimImageSummary(
            i.ImageIndex,
            i.ImageName,
            i.EditionId,
            $"{i.ProductVersion} SP{i.SpLevel}",
            i.Architecture.ToString(),
            i.ImageSize))];

    public static IReadOnlyList<MountedImageSummary> GetMountedImages() =>
        [.. DismApi.GetMountedImages().Select(m => new MountedImageSummary(
            m.ImageFilePath, m.ImageIndex, m.MountPath, m.MountMode.ToString(), m.MountStatus.ToString()))];

    public static void Mount(string imageFile, int index, string mountPath, Action<int>? progressPercent = null) =>
        DismApi.MountImage(imageFile, mountPath, index, readOnly: false, DismMountImageOptions.None,
            progressPercent is null ? null : p => progressPercent(Percent(p)));

    public static void Unmount(string mountPath, bool commit, Action<int>? progressPercent = null) =>
        DismApi.UnmountImage(mountPath, commit,
            progressPercent is null ? null : p => progressPercent(Percent(p)));

    public void Dispose()
    {
        if (!_disposed)
        {
            DismApi.Shutdown();
            _disposed = true;
        }
    }

    private static int Percent(DismProgress progress) =>
        progress.Total == 0 ? 0 : (int)(100L * progress.Current / progress.Total);
}
