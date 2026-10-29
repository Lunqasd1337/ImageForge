using System.Reflection;
using System.Runtime.ExceptionServices;
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

    public static void Mount(string imageFile, int index, string mountPath, Action<int>? progressPercent = null, bool readOnly = false) =>
        DismApi.MountImage(imageFile, mountPath, index, readOnly, DismMountImageOptions.None,
            progressPercent is null ? null : p => progressPercent(Percent(p)));

    public static void Unmount(string mountPath, bool commit, Action<int>? progressPercent = null) =>
        DismApi.UnmountImage(mountPath, commit,
            progressPercent is null ? null : p => progressPercent(Percent(p)));

    /// <summary>
    /// Direct parents of each named feature, through one offline session (DismGetFeatureParent; Dism.exe has no
    /// equivalent). A feature whose parents could not be read is left out and reported.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> GetFeatureParents(
        string mountPath, IEnumerable<string> featureNames, Action<string, DismException> onFailure)
    {
        ArgumentNullException.ThrowIfNull(featureNames);
        ArgumentNullException.ThrowIfNull(onFailure);
        var parents = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        using var session = DismApi.OpenOfflineSession(mountPath);
        foreach (var name in featureNames)
        {
            try
            {
                parents[name] = [.. GetFeatureParent(session, name)
                    .Select(f => f.FeatureName)
                    .Where(parent => !string.Equals(parent, name, StringComparison.OrdinalIgnoreCase))];
            }
            catch (DismException ex)
            {
                onFailure(name, ex);
            }
        }

        return parents;
    }

    // ManagedDism exposes DismGetFeatureParent only by package name or path; a feature of the foundation package (all
    // features that /Get-Features lists) needs DismPackageNone, which only its private overload passes.
    private static readonly MethodInfo FeatureParentMethod =
        typeof(DismApi).GetMethod("GetFeatureParent", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(DismApi), "GetFeatureParent");

    private static readonly object PackageNone = Enum.ToObject(FeatureParentMethod.GetParameters()[3].ParameterType, 0);

    private static DismFeatureCollection GetFeatureParent(DismSession session, string featureName)
    {
        try
        {
            return (DismFeatureCollection)FeatureParentMethod.Invoke(null, [session, featureName, null, PackageNone])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

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
