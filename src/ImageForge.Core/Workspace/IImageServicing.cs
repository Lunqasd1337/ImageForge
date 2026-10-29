using ImageForge.Core.Appx;
using ImageForge.Core.Execution;
using ImageForge.Core.Features;
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

    Task<FeatureInventory> GetFeaturesAsync(string mountDir, CancellationToken ct);

    /// <summary>Adds the parents and localized names of every listed feature (DISM API); used by the inspection.</summary>
    Task<FeatureInventory> ReadFeatureDetailsAsync(string mountDir, FeatureInventory features, CancellationToken ct);

    /// <param name="source">A local folder for features without payload, or null; never Windows Update.</param>
    Task EnableFeaturesAsync(string mountDir, IReadOnlyList<string> features, string? source, CancellationToken ct);

    Task DisableFeaturesAsync(string mountDir, IReadOnlyList<string> features, CancellationToken ct);

    Task<CapabilityInventory> GetCapabilitiesAsync(string mountDir, CancellationToken ct);

    /// <summary>Adds the localized names of the installed and the not present capabilities; used by the inspection.</summary>
    Task<CapabilityInventory> ReadCapabilityNamesAsync(string mountDir, CapabilityInventory capabilities, CancellationToken ct);

    Task RemoveCapabilityAsync(string mountDir, CapabilityIdentity capability, CancellationToken ct);

    /// <param name="source">A checked local Features on Demand folder; never Windows Update.</param>
    Task AddCapabilityAsync(string mountDir, CapabilityIdentity capability, string source, CancellationToken ct);

    Task UnmountAsync(string mountDir, bool commit, CancellationToken ct);

    Task<bool> IsMountedAsync(string mountDir, CancellationToken ct);

    Task ExportAsync(string sourceImage, int index, string destinationImage, CancellationToken ct);

    ExecutionReport ApplyRegistryPlan(Guid runId, ExecutionPlan plan, string mountDir, CancellationToken ct);

    /// <summary>Unloads hives this run left loaded; returns their load names. Throws if any stay loaded.</summary>
    IReadOnlyList<string> UnloadOwnedHives(Guid runId);
}
