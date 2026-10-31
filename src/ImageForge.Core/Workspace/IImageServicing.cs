using ImageForge.Core.Appx;
using ImageForge.Core.Drivers;
using ImageForge.Core.Execution;
using ImageForge.Core.Features;
using ImageForge.Core.Planning;
using ImageForge.Core.Updates;

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

    Task DisableFeaturesAsync(string mountDir, IReadOnlyList<string> features, bool removePayload, CancellationToken ct);

    Task<CapabilityInventory> GetCapabilitiesAsync(string mountDir, CancellationToken ct);

    /// <summary>Adds the localized names of the installed and the not present capabilities; used by the inspection.</summary>
    Task<CapabilityInventory> ReadCapabilityNamesAsync(string mountDir, CapabilityInventory capabilities, CancellationToken ct);

    Task RemoveCapabilityAsync(string mountDir, CapabilityIdentity capability, CancellationToken ct);

    /// <param name="source">A checked local Features on Demand folder; never Windows Update.</param>
    Task AddCapabilityAsync(string mountDir, CapabilityIdentity capability, string source, CancellationToken ct);

    /// <summary>The third-party drivers of the image (DismGetDrivers without inbox drivers).</summary>
    Task<DriverInventory> GetDriversAsync(string mountDir, CancellationToken ct);

    /// <param name="infPath">The full path of the INF in the checked driver folder.</param>
    Task AddDriverAsync(string mountDir, string infPath, bool forceUnsigned, CancellationToken ct);

    Task RemoveDriverAsync(string mountDir, string publishedName, CancellationToken ct);

    /// <summary>The servicing packages of the image (DismGetPackages).</summary>
    Task<PackageInventory> GetPackagesAsync(string mountDir, CancellationToken ct);

    /// <param name="packagePath">The full path of a .msu or .cab in the checked update folder (DismAddPackage).</param>
    Task AddPackageAsync(string mountDir, string packagePath, CancellationToken ct);

    /// <summary>Dism.exe /Cleanup-Image /StartComponentCleanup, with /ResetBase when asked; not cancelled once started.</summary>
    Task CleanupComponentStoreAsync(string mountDir, bool resetBase, CancellationToken ct);

    Task UnmountAsync(string mountDir, bool commit, CancellationToken ct);

    Task<bool> IsMountedAsync(string mountDir, CancellationToken ct);

    /// <param name="bootable">Marks the exported Windows PE image as the boot image of the destination.</param>
    Task ExportAsync(string sourceImage, int index, string destinationImage, bool bootable, CancellationToken ct);

    ExecutionReport ApplyRegistryPlan(Guid runId, ExecutionPlan plan, string mountDir, CancellationToken ct);

    /// <summary>Unloads hives this run left loaded; returns their load names. Throws if any stay loaded.</summary>
    IReadOnlyList<string> UnloadOwnedHives(Guid runId);
}
