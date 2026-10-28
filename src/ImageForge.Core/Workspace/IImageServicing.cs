using ImageForge.Core.Appx;
using ImageForge.Core.Execution;
using ImageForge.Core.Planning;

namespace ImageForge.Core.Workspace;

/// <summary>Servicing operations the run pipeline needs; the engine implements them with DISM and offline hives.</summary>
public interface IImageServicing
{
    Task MountAsync(string imageFile, int index, string mountDir, CancellationToken ct);

    /// <summary>Mounts without the ability to commit; used to read inventories from the verified source copy.</summary>
    Task MountReadOnlyAsync(string imageFile, int index, string mountDir, CancellationToken ct);

    Task<AppxInventory> GetProvisionedAppxAsync(string mountDir, CancellationToken ct);

    Task RemoveProvisionedAppxAsync(string mountDir, PackageIdentity package, CancellationToken ct);

    Task UnmountAsync(string mountDir, bool commit, CancellationToken ct);

    Task<bool> IsMountedAsync(string mountDir, CancellationToken ct);

    Task ExportAsync(string sourceImage, int index, string destinationImage, CancellationToken ct);

    ExecutionReport ApplyRegistryPlan(Guid runId, ExecutionPlan plan, string mountDir, CancellationToken ct);

    /// <summary>Unloads hives this run left loaded; returns their load names. Throws if any stay loaded.</summary>
    IReadOnlyList<string> UnloadOwnedHives(Guid runId);
}
