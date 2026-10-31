using ImageForge.Core.Appx;
using ImageForge.Core.Drivers;
using ImageForge.Core.Execution;
using ImageForge.Core.Features;
using ImageForge.Core.Planning;
using ImageForge.Core.Safety;
using ImageForge.Core.Servicing;
using ImageForge.Core.Updates;
using ImageForge.Core.Workspace;
using ImageForge.Engine.Dism;
using ImageForge.Engine.Registry;
using Microsoft.Dism;

namespace ImageForge.Engine.Workspace;

/// <summary>Run-pipeline servicing over the DISM API, Dism.exe export and offline hives. Needs an active <see cref="DismImages"/> session.</summary>
public sealed class DismImageServicing(DismImages session, Action<string>? log = null) : IImageServicing
{
    private readonly DismImages _session = session ?? throw new ArgumentNullException(nameof(session));

    public Task MountAsync(string imageFile, int index, string mountDir, CancellationToken ct) =>
        Task.Run(() => DismImages.Mount(imageFile, index, mountDir, Progress("mount")), CancellationToken.None);

    public Task MountReadOnlyAsync(string imageFile, int index, string mountDir, CancellationToken ct) =>
        Task.Run(() => DismImages.Mount(imageFile, index, mountDir, Progress("mount read-only"), readOnly: true), CancellationToken.None);

    public async Task<AppxInventory> GetProvisionedAppxAsync(string mountDir, CancellationToken ct)
    {
        var result = await RunCliAsync(DismCliArguments.GetProvisionedAppx(mountDir), "/Get-ProvisionedAppxPackages").ConfigureAwait(false);
        return DismAppxListParser.Parse(result.Output);
    }

    public Task RemoveProvisionedAppxAsync(string mountDir, PackageIdentity package, CancellationToken ct) =>
        RunCliAsync(DismCliArguments.RemoveProvisionedAppx(mountDir, package), "/Remove-ProvisionedAppxPackage");

    public async Task<FeatureInventory> GetFeaturesAsync(string mountDir, CancellationToken ct)
    {
        var result = await RunCliAsync(DismCliArguments.GetFeatures(mountDir), "/Get-Features").ConfigureAwait(false);
        return DismFeatureListParser.Parse(result.Output);
    }

    public Task<FeatureInventory> ReadFeatureDetailsAsync(string mountDir, FeatureInventory features, CancellationToken ct) => Task.Run(() =>
    {
        GC.KeepAlive(_session);
        var failed = new List<string>();
        var (parents, names) = DismImages.GetFeatureDetails(mountDir, features.Features.Select(f => f.Name), (name, ex) =>
        {
            failed.Add(name);
            log?.Invoke($"parents of {name} not read: 0x{ex.HResult:x8} {ex.Message}");
        });
        log?.Invoke($"feature parents read: {parents.Count} of {features.Features.Count}");

        // A partial map would hide dependents; the planner refuses feature changes without it.
        return features with { Parents = failed.Count == 0 ? parents : null, DisplayNames = names };
    }, CancellationToken.None);

    public Task EnableFeaturesAsync(string mountDir, IReadOnlyList<string> features, string? source, CancellationToken ct) =>
        RunCliAsync(DismCliArguments.EnableFeatures(mountDir, features, source), "/Enable-Feature");

    public Task DisableFeaturesAsync(string mountDir, IReadOnlyList<string> features, bool removePayload, CancellationToken ct) =>
        RunCliAsync(DismCliArguments.DisableFeatures(mountDir, features, removePayload), "/Disable-Feature");

    public async Task<CapabilityInventory> GetCapabilitiesAsync(string mountDir, CancellationToken ct)
    {
        var result = await RunCliAsync(DismCliArguments.GetCapabilities(mountDir), "/Get-Capabilities").ConfigureAwait(false);
        return DismCapabilityListParser.Parse(result.Output);
    }

    public Task<CapabilityInventory> ReadCapabilityNamesAsync(string mountDir, CapabilityInventory capabilities, CancellationToken ct) => Task.Run(() =>
    {
        GC.KeepAlive(_session);
        var shown = capabilities.Capabilities.Where(c => c.State is CapabilityState.Installed or CapabilityState.NotPresent).Select(c => c.Identity.FullName);
        return capabilities with { DisplayNames = DismImages.GetCapabilityDisplayNames(mountDir, shown) };
    }, CancellationToken.None);

    public Task RemoveCapabilityAsync(string mountDir, CapabilityIdentity capability, CancellationToken ct) =>
        RunCliAsync(DismCliArguments.RemoveCapability(mountDir, capability), "/Remove-Capability");

    public Task AddCapabilityAsync(string mountDir, CapabilityIdentity capability, string source, CancellationToken ct) =>
        RunCliAsync(DismCliArguments.AddCapability(mountDir, capability, source), "/Add-Capability");

    public Task<DriverInventory> GetDriversAsync(string mountDir, CancellationToken ct) => Task.Run(() =>
    {
        GC.KeepAlive(_session);
        try
        {
            var drivers = DismImages.GetDrivers(mountDir, includeInbox: false);
            log?.Invoke($"third-party drivers: {drivers.Count}");
            return new DriverInventory([.. drivers.Select(d => new InstalledDriver(
                d.PublishedName, d.OriginalFileName, d.ClassName, d.ProviderName, d.Version, d.Date, d.BootCritical, d.Signature))], []);
        }
        catch (DismException ex)
        {
            // The window shows why drivers cannot be changed; the rest of the inspection stays usable.
            return new DriverInventory([], [$"DismGetDrivers 0x{ex.HResult:x8}: {ex.Message}"]);
        }
    }, CancellationToken.None);

    public Task AddDriverAsync(string mountDir, string infPath, bool forceUnsigned, CancellationToken ct) => Task.Run(() =>
    {
        GC.KeepAlive(_session);
        log?.Invoke($"DismAddDriver {infPath}{(forceUnsigned ? " ForceUnsigned" : string.Empty)}");
        DismImages.AddDriver(mountDir, infPath, forceUnsigned);
    }, CancellationToken.None);

    public Task RemoveDriverAsync(string mountDir, string publishedName, CancellationToken ct) => Task.Run(() =>
    {
        GC.KeepAlive(_session);
        log?.Invoke($"DismRemoveDriver {publishedName}");
        DismImages.RemoveDriver(mountDir, publishedName);
    }, CancellationToken.None);

    public Task<PackageInventory> GetPackagesAsync(string mountDir, CancellationToken ct) => Task.Run(() =>
    {
        GC.KeepAlive(_session);
        try
        {
            var packages = DismImages.GetPackages(mountDir);
            log?.Invoke($"servicing packages: {packages.Count}");
            return new PackageInventory([.. packages.Select(p => new InstalledPackage(p.Name, p.State, p.ReleaseType))], []);
        }
        catch (DismException ex)
        {
            // The window shows why updates cannot be checked; the rest of the inspection stays usable.
            return new PackageInventory([], [$"DismGetPackages 0x{ex.HResult:x8}: {ex.Message}"]);
        }
    }, CancellationToken.None);

    // An update is not cancelled once DISM starts it (10-15 minutes for a WIM-format LCU, S2-UPDATES-INVENTORY).
    public Task AddPackageAsync(string mountDir, string packagePath, CancellationToken ct) => Task.Run(() =>
    {
        GC.KeepAlive(_session);
        log?.Invoke($"DismAddPackage {packagePath}");
        DismImages.AddPackage(mountDir, packagePath, Progress("add-package"),
            () => log?.Invoke("add-package: DISM applies the packages; it reports no progress for this phase (about 7 minutes for a cumulative update)"));
    }, CancellationToken.None);

    public Task CleanupComponentStoreAsync(string mountDir, bool resetBase, CancellationToken ct) =>
        RunCliAsync(DismCliArguments.CleanupComponentStore(mountDir, resetBase), "/Cleanup-Image /StartComponentCleanup");

    public Task UnmountAsync(string mountDir, bool commit, CancellationToken ct) =>
        Task.Run(() => DismImages.Unmount(mountDir, commit, Progress(commit ? "commit" : "discard")), CancellationToken.None);

    public Task<bool> IsMountedAsync(string mountDir, CancellationToken ct)
    {
        GC.KeepAlive(_session);
        var full = Path.GetFullPath(mountDir).TrimEnd('\\');
        return Task.FromResult(DismImages.GetMountedImages()
            .Any(m => string.Equals(m.MountPath.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase)));
    }

    public Task ExportAsync(string sourceImage, int index, string destinationImage, bool bootable, CancellationToken ct) =>
        RunCliAsync(DismCliArguments.ExportImage(sourceImage, index, destinationImage, bootable), "/Export-Image");

    // Servicing CLI calls are not cancelled once started (plan, section 7); cancellation takes effect between steps.
    private async Task<CliResult> RunCliAsync(IReadOnlyList<string> arguments, string operation)
    {
        log?.Invoke($"{DismCli.TrustedPath} {string.Join(' ', arguments)}");
        var result = await DismCli.RunAsync(arguments, null, CancellationToken.None).ConfigureAwait(false);
        return result.ExitCode == 0
            ? result
            : throw new IOException($"Dism.exe {operation} exit code {result.ExitCode}: {Tail(result.Output)}");
    }

    public ExecutionReport ApplyRegistryPlan(Guid runId, ExecutionPlan plan, string mountDir, CancellationToken ct)
    {
        var scope = WriteScope.Create(mountDir);
        return new RegistryPlanExecutor(new Win32OfflineHiveLoader(runId), hiveFile => OfflineHivePaths.Resolve(scope, hiveFile), new MountFileWriter(scope.Root))
            .Execute(plan, ct);
    }

    public IReadOnlyList<string> UnloadOwnedHives(Guid runId) => Win32OfflineHiveLoader.UnloadOwned(runId);

    private Action<int>? Progress(string operation)
    {
        if (log is null)
        {
            return null;
        }

        var last = -1;
        return p =>
        {
            if (p / 25 != last / 25)
            {
                last = p;
                log($"{operation} {p}%");
            }
        };
    }

    private static string Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(4));
    }
}
