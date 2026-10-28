using ImageForge.Core.Appx;

namespace ImageForge.Core.Workspace;

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

    public async Task<AppxInventory> InspectAsync(string sourceDir, Guid workspaceId, int index, CancellationToken ct)
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
            return await servicing.GetProvisionedAppxAsync(mountDir, ct).ConfigureAwait(false);
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
}
