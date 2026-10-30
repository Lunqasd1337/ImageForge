using ImageForge.Core.Appx;
using ImageForge.Core.Drivers;
using ImageForge.Core.Features;
using ImageForge.Core.Updates;

namespace ImageForge.Core.Workspace;

/// <summary>What the inspection read from the image; plans are checked against it.</summary>
/// <param name="MediaHasNetFx3">sources\sxs of the media copy holds the .NET Framework 3.5 package (see <see cref="FeatureSources"/>).</param>
public sealed record ImageInventory(AppxInventory Appx, FeatureInventory Features, CapabilityInventory Capabilities, bool MediaHasNetFx3)
{
    /// <summary>The third-party drivers; empty with no problems when the inventory did not read them.</summary>
    public DriverInventory Drivers { get; init; } = new([], ["Список драйверов не прочитан."]);

    /// <summary>The servicing packages; the window shows the installed cumulative update and servicing stack.</summary>
    public PackageInventory Packages { get; init; } = new([], ["Список пакетов не прочитан."]);
}

/// <summary>
/// Reads the inventories of the source image: the source is copied and verified into base\ (reused by the next run),
/// and that copy is mounted read-only, so neither the source nor the copy can change. The mount is always discarded.
/// </summary>
public sealed class ImageInspection(IWorkspacePaths paths, IImageServicing servicing)
{
    public Action<string>? Log { get; init; }

    /// <summary>True when an inspection mount folder is left over; the workspace needs recovery then.</summary>
    public static bool LeftBehind(IWorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Directory.Exists(paths.InspectMount);
    }

    public async Task<ImageInventory> InspectAsync(string sourceDir, Guid workspaceId, int index, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Root);
        var store = new StateStore<WorkspaceState>(paths.StateFile);
        var state = RunPipeline.LoadForRun(store, workspaceId);
        if (LeftBehind(paths))
        {
            throw new WorkspaceRecoveryRequiredException("An earlier inspection left its mount behind; recover the workspace first.");
        }

        Log?.Invoke("source-check...");
        var source = await SourceManifest.ComputeAsync(sourceDir, ct).ConfigureAwait(false);
        if (!source.Entries.Any(e => string.Equals(e.RelativePath, RunPipeline.InstallImage, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(@"The source has no sources\install.wim; only a WIM source is supported.");
        }

        Log?.Invoke("base-copy...");
        await RunPipeline.PrepareBaseAsync(paths, sourceDir, source, state.SourceFingerprint, Log, ct).ConfigureAwait(false);
        store.Save(state with { SourceFingerprint = source.Fingerprint });

        var mountDir = paths.InspectMount;
        RunPipeline.PrepareEmptyDirectory(mountDir);
        var mounted = false;
        try
        {
            Log?.Invoke("mount read-only...");
            await servicing.MountReadOnlyAsync(Path.Combine(paths.Base, "sources", "install.wim"), index, mountDir, ct).ConfigureAwait(false);
            mounted = true;
            Log?.Invoke("appx-list...");
            var inventory = await servicing.GetProvisionedAppxAsync(mountDir, ct).ConfigureAwait(false);
            Log?.Invoke("appx-manifests...");
            inventory = AppxManifestStore.Attach(mountDir, inventory);
            Log?.Invoke("feature-list...");
            var features = await servicing.GetFeaturesAsync(mountDir, ct).ConfigureAwait(false);
            if (features.Problems.Count == 0)
            {
                Log?.Invoke("feature-parents...");
                features = await servicing.ReadFeatureDetailsAsync(mountDir, features, ct).ConfigureAwait(false);
            }

            Log?.Invoke("capability-list...");
            var capabilities = await servicing.GetCapabilitiesAsync(mountDir, ct).ConfigureAwait(false);
            if (capabilities.Problems.Count == 0)
            {
                capabilities = await servicing.ReadCapabilityNamesAsync(mountDir, capabilities, ct).ConfigureAwait(false);
            }

            Log?.Invoke("driver-list...");
            var drivers = await servicing.GetDriversAsync(mountDir, ct).ConfigureAwait(false);
            Log?.Invoke("package-list...");
            var packages = await servicing.GetPackagesAsync(mountDir, ct).ConfigureAwait(false);
            return new ImageInventory(inventory, features, capabilities, MediaHasNetFx3(paths)) { Drivers = drivers, Packages = packages };
        }
        finally
        {
            // Without a successful mount there may still be a partial one; ask rather than assume.
            if (mounted || await servicing.IsMountedAsync(mountDir, CancellationToken.None).ConfigureAwait(false))
            {
                Log?.Invoke("unmount discard...");
                await servicing.UnmountAsync(mountDir, commit: false, CancellationToken.None).ConfigureAwait(false);
            }

            RunPipeline.TryDeleteEmptyDirectory(mountDir);
        }
    }

    /// <summary>The media sxs folder inside the verified base copy; the run takes the NetFx3 payload from here.</summary>
    /// <summary>Why the folder cannot serve as the Features on Demand source, or null when it can (see <see cref="FeatureSources.FodProblem"/>).</summary>
    public static string? FodFolderProblem(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        try
        {
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            {
                return "папка не найдена (нужен полный путь)";
            }

            var metadata = Path.Combine(folder, FeatureSources.FodMetadataFolder);
            return FeatureSources.FodProblem(
                Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().ToList(),
                Directory.Exists(metadata) ? [.. Directory.EnumerateFiles(metadata).Select(Path.GetFileName).OfType<string>()] : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    public static string MediaSxs(IWorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine([paths.Base, .. FeatureSources.MediaSxsSegments]);
    }

    internal static bool MediaHasNetFx3(IWorkspacePaths paths)
    {
        var sxs = MediaSxs(paths);
        return Directory.Exists(sxs) && FeatureSources.NetFx3Problem(Directory.EnumerateFiles(sxs).Select(Path.GetFileName).OfType<string>()) is null;
    }
}
