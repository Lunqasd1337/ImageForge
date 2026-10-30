using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Dism;

namespace ImageForge.Engine.Dism;

public sealed record WimImageSummary(int Index, string Name, string EditionId, string Version, string Architecture, ulong SizeBytes);

/// <param name="PublishedName">The name inside the image (oemN.inf), which removal needs.</param>
public sealed record ImageDriver(
    string PublishedName,
    string OriginalFileName,
    bool InBox,
    bool BootCritical,
    string ClassName,
    string ProviderName,
    DateTime Date,
    string Version,
    string Signature,
    string CatalogFile);

/// <param name="Name">The package identity, such as Package_for_RollupFix~31bf3856ad364e35~amd64~~26300.1000.1.0.</param>
public sealed record ImagePackage(string Name, string State, string ReleaseType, DateTime InstallTime);

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
    /// Direct parents and the localized name of each named feature, through one offline session (DismGetFeatureParent;
    /// Dism.exe has no equivalent). A feature whose parents could not be read is left out of the parents and reported;
    /// a missing name is only left out.
    /// </summary>
    public static (IReadOnlyDictionary<string, IReadOnlyList<string>> Parents, IReadOnlyDictionary<string, string> DisplayNames) GetFeatureDetails(
        string mountPath, IEnumerable<string> featureNames, Action<string, DismException> onFailure)
    {
        ArgumentNullException.ThrowIfNull(featureNames);
        ArgumentNullException.ThrowIfNull(onFailure);
        var parents = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

            try
            {
                if (DismApi.GetFeatureInfo(session, name).DisplayName is { Length: > 0 } displayName)
                {
                    names[name] = displayName;
                }
            }
            catch (DismException)
            {
                // A name is only for showing; the feature stays usable without it.
            }
        }

        return (parents, names);
    }

    /// <summary>Localized names of the named capabilities; one that cannot be read is left out.</summary>
    public static IReadOnlyDictionary<string, string> GetCapabilityDisplayNames(string mountPath, IEnumerable<string> capabilityNames)
    {
        ArgumentNullException.ThrowIfNull(capabilityNames);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var session = DismApi.OpenOfflineSession(mountPath);
        foreach (var name in capabilityNames)
        {
            try
            {
                if (DismApi.GetCapabilityInfo(session, name).DisplayName is { Length: > 0 } displayName)
                {
                    names[name] = displayName;
                }
            }
            catch (DismException)
            {
                // A name is only for showing.
            }
        }

        return names;
    }

    /// <summary>The driver packages of an offline image (DismGetDrivers); inbox ones only when asked.</summary>
    public static IReadOnlyList<ImageDriver> GetDrivers(string mountPath, bool includeInbox)
    {
        using var session = DismApi.OpenOfflineSession(mountPath);
        return [.. DismApi.GetDrivers(session, includeInbox).Select(d => new ImageDriver(
            d.PublishedName,
            d.OriginalFileName,
            d.InBox,
            d.BootCritical,
            d.ClassName,
            d.ProviderName,
            d.Date,
            d.Version.ToString(),
            d.DriverSignature.ToString(),
            d.CatalogFile))];
    }

    /// <summary>Adds one INF package (DismAddDriver). ForceUnsigned stays off unless the user chose it with a warning.</summary>
    public static void AddDriver(string mountPath, string infPath, bool forceUnsigned)
    {
        using var session = DismApi.OpenOfflineSession(mountPath);
        DismApi.AddDriver(session, infPath, forceUnsigned);
    }

    /// <summary>Removes a third-party package by its published name, such as oem3.inf (DismRemoveDriver).</summary>
    public static void RemoveDriver(string mountPath, string publishedName)
    {
        using var session = DismApi.OpenOfflineSession(mountPath);
        DismApi.RemoveDriver(session, publishedName);
    }

    /// <summary>The servicing packages of an offline image (DismGetPackages): updates, language packs and FoD.</summary>
    public static IReadOnlyList<ImagePackage> GetPackages(string mountPath)
    {
        using var session = DismApi.OpenOfflineSession(mountPath);
        return [.. DismApi.GetPackages(session).Select(p => new ImagePackage(p.PackageName, p.PackageState.ToString(), p.ReleaseType.ToString(), p.InstallTime))];
    }

    /// <summary>Adds one .msu or .cab (DismAddPackage); DISM checks applicability and the signature.</summary>
    /// <param name="laterPhase">Called once when DISM reports Current past Total: the percentage stays at 100 from then on.</param>
    public static void AddPackage(string mountPath, string packagePath, Action<int>? progressPercent = null, Action? laterPhase = null)
    {
        using var session = DismApi.OpenOfflineSession(mountPath);
        var later = false;
        DismApi.AddPackage(session, packagePath, ignoreCheck: false, preventPending: false, p =>
        {
            if (!later && p.Total > 0 && p.Current > p.Total)
            {
                later = true;
                laterPhase?.Invoke();
            }

            progressPercent?.Invoke(Percent(p));
        });
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

    // DismAddPackage reports a later phase with Current past Total (2000% and more in S2-UPDATES-INVENTORY).
    private static int Percent(DismProgress progress) =>
        progress.Total == 0 ? 0 : (int)Math.Clamp(100L * progress.Current / progress.Total, 0, 100);
}
