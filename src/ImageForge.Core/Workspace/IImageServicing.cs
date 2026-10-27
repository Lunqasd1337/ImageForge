using ImageForge.Core.Execution;
using ImageForge.Core.Planning;

namespace ImageForge.Core.Workspace;

/// <summary>Servicing operations the run pipeline needs; the engine implements them with DISM and offline hives.</summary>
public interface IImageServicing
{
    Task MountAsync(string imageFile, int index, string mountDir, CancellationToken ct);

    Task UnmountAsync(string mountDir, bool commit, CancellationToken ct);

    Task<bool> IsMountedAsync(string mountDir, CancellationToken ct);

    Task ExportAsync(string sourceImage, int index, string destinationImage, CancellationToken ct);

    ExecutionReport ApplyRegistryPlan(Guid runId, ExecutionPlan plan, string mountDir, CancellationToken ct);

    /// <summary>Unloads hives this run left loaded; returns their load names. Throws if any stay loaded.</summary>
    IReadOnlyList<string> UnloadOwnedHives(Guid runId);
}
