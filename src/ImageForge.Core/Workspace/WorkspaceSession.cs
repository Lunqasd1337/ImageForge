using ImageForge.Core.Appx;
using ImageForge.Core.Changes;
using ImageForge.Core.Contracts;
using ImageForge.Core.Drivers;
using ImageForge.Core.Updates;
using ImageForge.Core.Features;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;
using ImageForge.Core.Registry.RegFile;

namespace ImageForge.Core.Workspace;

public sealed record OpenWorkspaceRequest(string SourceDir, string WorkDir, string OutputDir)
{
    /// <summary>The install.wim image to service; 0 lets the engine choose (Pro when the source has it).</summary>
    public int ImageIndex { get; init; }
}

/// <param name="Problems">Non-empty when the folders cannot be used; nothing is opened then.</param>
public sealed record WorkspaceSummary(
    Guid WorkspaceId,
    long Revision,
    TargetStatus Target,
    RunRecord? LastRun,
    bool RecoveryRequired,
    IReadOnlyList<LocationProblem> Problems)
{
    /// <summary>The editions of the source install.wim; empty when it could not be read without DISM.</summary>
    public IReadOnlyList<ImageEdition> Editions { get; init; } = [];

    /// <summary>The image this workspace services; the other editions are kept unchanged.</summary>
    public int ImageIndex { get; init; }

    /// <summary>A network or synced source or output: allowed, shown before the copy.</summary>
    public IReadOnlyList<LocationProblem> Warnings { get; init; } = [];
}

/// <param name="FileDiagnostics">Problems in the .reg file itself (decoding, syntax); the plan is not built then.</param>
/// <param name="FileEncoding">How the .reg file was decoded, for the window to show; null without a file.</param>
public sealed record PlanSummary(
    Guid PlanId,
    long WorkspaceRevision,
    string Fingerprint,
    IReadOnlyList<RegistryWritePreview> Preview,
    IReadOnlyList<PlanningDiagnostic> Diagnostics,
    IReadOnlyList<RegFileDiagnostic>? FileDiagnostics = null,
    string? FileEncoding = null)
{
    public IReadOnlyList<RegFileDiagnostic> FileDiagnostics { get; init; } = FileDiagnostics ?? [];

    /// <summary>Provisioned packages the plan removes, in order.</summary>
    public IReadOnlyList<AppxPackageView> AppxRemovals { get; init; } = [];

    /// <summary>Capabilities the plan removes, by name.</summary>
    public IReadOnlyList<string> CapabilityRemovals { get; init; } = [];

    /// <summary>Capabilities the plan adds, by name; DISM may add their dependencies too.</summary>
    public IReadOnlyList<string> CapabilityAdditions { get; init; } = [];

    /// <summary>The Features on Demand folder the additions come from; null when nothing is added.</summary>
    public string? FodSource { get; init; }

    /// <summary>Feature changes, with what is added by parents and dependents; null when there are none.</summary>
    public FeatureChangeView? FeatureChanges { get; init; }

    /// <summary>Driver removals and additions, with their warnings; null when there are none.</summary>
    public DriverPlan? Drivers { get; init; }

    /// <summary>Update packages to add, in order, with their warnings; null when there are none.</summary>
    public UpdatePlan? Updates { get; init; }

    /// <summary>.NET Framework cumulative updates, added after the cleanup; null when there are none.</summary>
    public UpdatePlan? DotNetUpdates { get; init; }

    /// <summary>The component store cleanup, with its warnings; null when not asked for.</summary>
    public CleanupPlan? Cleanup { get; init; }

    /// <summary>The packages that go into WinRE, in order, with their warnings; null when there are none.</summary>
    public WinRePlan? WinRe { get; init; }

    /// <summary>The packages that go into boot.wim, in order, with their warnings; null when there are none.</summary>
    public BootPlan? Boot { get; init; }
}

/// <param name="Disable">Turned off in this order (dependents first), before <paramref name="Enable"/>.</param>
/// <param name="Enable">Turned on in this order (parents first).</param>
/// <param name="Added">Not chosen by the user: parents and dependents the change needs.</param>
/// <param name="UsesMediaSxs">.NET Framework 3.5 comes from sources\sxs of the media.</param>
public sealed record FeatureChangeView(IReadOnlyList<string> Disable, IReadOnlyList<string> Enable, IReadOnlyList<string> Added, bool UsesMediaSxs)
{
    public bool RemovePayload { get; init; }
}

/// <param name="State">As DISM prints it.</param>
/// <param name="IsOn">On now or after first boot; the window shows it as the checkbox state.</param>
/// <param name="BlockedReason">Why the state cannot be changed; null when it can.</param>
public sealed record FeatureView(string Name, string DisplayName, string State, bool IsOn, string? BlockedReason)
{
    /// <summary>Its files are not in the image; turning it on takes them from sources\sxs of the media.</summary>
    public bool FromMedia { get; init; }
}

/// <param name="Name">As DISM lists it, for example MathRecognizer~~~~0.0.1.0.</param>
public sealed record CapabilityView(string Name, string DisplayName, string State);

/// <param name="Warning">Shown before removal; null when none is known.</param>
/// <param name="BlockedReason">Why the package cannot be removed at all; null when it can (alone or with its dependents).</param>
public sealed record AppxPackageView(
    string DisplayName, string PackageName, string FamilyName, string Version, string Architecture, string? Warning, string? BlockedReason)
{
    public bool IsFramework { get; init; }

    /// <summary>Provisioned packages that depend on this one; removing it needs them removed too.</summary>
    public IReadOnlyList<string> NeededBy { get; init; } = [];
}

/// <param name="Problems">Why the inventory could not be read; the packages are empty then.</param>
public sealed record InspectionSummary(long WorkspaceRevision, IReadOnlyList<AppxPackageView> Packages, IReadOnlyList<string> Problems)
{
    public IReadOnlyList<FeatureView> Features { get; init; } = [];

    /// <summary>Why the feature list cannot be used; the features are empty then.</summary>
    public IReadOnlyList<string> FeatureProblems { get; init; } = [];

    /// <summary>Installed capabilities, which can be removed.</summary>
    public IReadOnlyList<CapabilityView> Capabilities { get; init; } = [];

    /// <summary>Capabilities listed as Not Present, which can be added from a Features on Demand folder.</summary>
    public IReadOnlyList<CapabilityView> AvailableCapabilities { get; init; } = [];

    public IReadOnlyList<string> CapabilityProblems { get; init; } = [];

    /// <summary>Third-party drivers of the image, which can be removed.</summary>
    public IReadOnlyList<InstalledDriver> Drivers { get; init; } = [];

    public IReadOnlyList<string> DriverProblems { get; init; } = [];

    /// <summary>The version of the installed cumulative update (Package_for_RollupFix); null when none or not read.</summary>
    public string? Rollup { get; init; }

    /// <summary>The version of the installed servicing stack; null when none or not read.</summary>
    public string? ServicingStack { get; init; }

    /// <summary>How many servicing packages the image lists; 0 when not read.</summary>
    public int PackageCount { get; init; }

    public IReadOnlyList<string> PackageProblems { get; init; } = [];

    /// <summary>Packages with an unfinished action (Install Pending and the like).</summary>
    public IReadOnlyList<string> PendingPackages { get; init; } = [];
}

public sealed record RunOutcome(
    Guid OperationId,
    StartPlanRejection Rejection,
    RunStatus? Status,
    string? FailedStep,
    string? Error,
    string? PublishedPath);

/// <summary>
/// Engine-side state of the one workspace the UI works with: open → plan → start exactly the shown plan → recover.
/// The revision changes on every open, run and recovery, so a plan shown before any of them can no longer start.
/// </summary>
public sealed class WorkspaceSession(
    Func<Action<string>, IImageServicing> servicingFactory,
    Func<Guid> newId,
    Func<string, IWorkspacePaths>? layoutFactory = null,
    Func<string, string, string, IReadOnlyList<LocationProblem>>? validateLocations = null)
{
    /// <summary>Free bytes of a volume by its root; null when unknown. The engine reads the disk; tests leave it unknown.</summary>
    public Func<string, long?> FreeSpace { get; init; } = _ => null;

    /// <summary>The kind and file system of a volume by its root; null when unknown. The engine reads the disk.</summary>
    public Func<string, VolumeFacts?> Volumes { get; init; } = _ => null;

    /// <summary>Whether a folder is synced by OneDrive or a similar client. The engine knows the user's sync roots.</summary>
    public Func<string, bool> IsSynced { get; init; } = _ => false;

    private IReadOnlyList<LocationProblem> _warnings = [];

    private readonly Func<string, IWorkspacePaths> _layoutFactory = layoutFactory ?? (dir => WorkspaceLayout.Create(dir));
    private readonly Func<string, string, string, IReadOnlyList<LocationProblem>> _validate = validateLocations ?? WorkspaceLocations.Validate;
    private readonly StartRequestLedger _ledger = new(newId);
    private readonly Dictionary<Guid, Task<RunOutcome>> _operations = [];
    private readonly Lock _gate = new();
    private (OpenWorkspaceRequest Request, IWorkspacePaths Paths, Guid WorkspaceId, Guid ImageId)? _open;
    private IReadOnlyList<ImageEdition> _editions = [];
    private ExecutionPlan? _plan;
    private ImageInventory? _inventory;
    private long _revision;
    private bool _busy;
    private Task _current = Task.CompletedTask;

    /// <summary>Test hook for the manual failure experiments; the product UI never sets it.</summary>
    public InjectedFailure Failure { get; set; }

    public WorkspaceSummary Open(OpenWorkspaceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ThrowIfBusy();
            _open = null;
            _plan = null;
            _inventory = null;
            _revision++;

            var problems = _validate(request.SourceDir, request.WorkDir, request.OutputDir);
            var volumes = problems.Count > 0 ? [] : WorkspaceLocations.CheckVolumes(request.SourceDir, request.WorkDir, request.OutputDir, Volumes, IsSynced);
            problems = [.. problems, .. volumes.Where(p => !p.IsWarning)];
            _warnings = [.. volumes.Where(p => p.IsWarning)];
            if (problems.Count > 0)
            {
                return new WorkspaceSummary(Guid.Empty, _revision, TargetStatus.Pending, null, false, problems);
            }

            var paths = _layoutFactory(request.WorkDir);
            var state = new StateStore<WorkspaceState>(paths.StateFile).Load();
            var workspaceId = state.State?.WorkspaceId ?? newId();
            _editions = ImageEditions.Read(request.SourceDir);
            _open = (request with { ImageIndex = ImageEditions.Choose(_editions, request.ImageIndex) }, paths, workspaceId, newId());
            return Summary();
        }
    }

    /// <summary>
    /// Reads the provisioned packages of the image into the session; the plans that follow resolve removals against it.
    /// A failure is returned as problems and leaves no inventory.
    /// </summary>
    public async Task<InspectionSummary> InspectAsync(IProgress<string>? progress, CancellationToken ct)
    {
        (OpenWorkspaceRequest Request, IWorkspacePaths Paths, Guid WorkspaceId, Guid ImageId) open;
        lock (_gate)
        {
            ThrowIfBusy();
            open = _open ?? throw new InvalidOperationException("No workspace is open.");
            _busy = true;
            _inventory = null;
            _plan = null;
        }

        var log = Report(progress);
        ImageInventory? inventory = null;
        string? error = null;
        try
        {
            var inspection = new ImageInspection(open.Paths, servicingFactory(log)) { Log = log, FreeSpace = FreeSpace };
            var task = inspection.InspectAsync(open.Request.SourceDir, open.WorkspaceId, open.Request.ImageIndex, ct);
            lock (_gate)
            {
                _current = task;
            }

            inventory = await task.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The window shows why the inventory is missing; the engine keeps running.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            error = ex.Message;
            log($"inspection failed: {ex.Message}");
        }

        lock (_gate)
        {
            _busy = false;
            if (inventory is null)
            {
                return new InspectionSummary(_revision, [], [error!]);
            }

            _inventory = inventory;
            var appx = inventory.Appx;
            var features = inventory.Features;
            var capabilities = inventory.Capabilities;
            var featureProblems = features.Problems.Count > 0 ? features.Problems
                : features.Parents is null ? [Messages.FeatureParentsNotRead]
                : [];
            return new InspectionSummary(
                _revision,
                appx.Problems.Count > 0 ? [] : [.. appx.Packages.OrderBy(p => p.Identity.Name, StringComparer.OrdinalIgnoreCase).Select(p => View(p, appx))],
                appx.Problems)
            {
                Features = featureProblems.Count > 0 ? [] : [.. features.Features
                    .Select(f => View(f, features, inventory.MediaHasNetFx3))
                    .OrderBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase)],
                FeatureProblems = featureProblems,
                Capabilities = CapabilityViews(capabilities, CapabilityState.Installed),
                AvailableCapabilities = CapabilityViews(capabilities, CapabilityState.NotPresent),
                CapabilityProblems = capabilities.Problems,
                Drivers = inventory.Drivers.Problems.Count > 0 ? [] : [.. inventory.Drivers.Drivers.OrderBy(d => d.PublishedName, StringComparer.OrdinalIgnoreCase)],
                DriverProblems = inventory.Drivers.Problems,
                Rollup = inventory.Packages.Rollup,
                ServicingStack = inventory.Packages.ServicingStack,
                PackageCount = inventory.Packages.Packages.Count,
                PackageProblems = inventory.Packages.Problems,
                PendingPackages = inventory.Packages.Pending,
            };
        }
    }

    public PlanSummary Plan(PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<ImageChange> removals =
        [
            .. options.RemoveAppx.Select(f => new RemoveProvisionedAppx(f)),
            .. options.RemoveCapabilities.Select(c => new RemoveCapability(c)),
            .. options.AddCapabilities.Select(c => new AddCapability(c)),
            .. options.DisableFeatures.Select(f => new DisableWindowsFeature(f, options.RemoveFeaturePayload)),
            .. options.EnableFeatures.Select(f => new EnableWindowsFeature(f)),
            .. options.RemoveDrivers.Select(d => new RemoveDriverPackage(d, options.AllowBootCritical)),
            .. options.AddDrivers.Select(d => new AddDriverPackage(d, options.ForceUnsigned)),
            .. options.AddUpdates.Select(u => new AddUpdatePackage(u)),
            .. options.CleanupComponents ? [new CleanupComponentStore(options.ResetBase)] : Array.Empty<ImageChange>(),
            .. options.WinReUpdates.Select(u => new AddWinReUpdatePackage(u)),
            .. options.BootUpdates.Select(u => new AddBootUpdatePackage(u)),
        ];
        // The folder is checked only when something is added; it is read here, outside the lock.
        var fod = options.AddCapabilities.Count > 0 && !string.IsNullOrWhiteSpace(options.FodSource) ? options.FodSource.Trim() : null;
        var source = fod is null ? null : new FodChoice(fod, ImageInspection.FodFolderProblem(fod));
        if (options.AddDrivers.Count > 0)
        {
            // Scanned again here, so the hashes in the plan are those of the files as they are now.
            source = (source ?? new FodChoice(null, null)) with
            {
                Drivers = string.IsNullOrWhiteSpace(options.DriverFolder) ? null : DriverFolder.Scan(options.DriverFolder.Trim()),
            };
        }

        if (options.AddUpdates.Count + options.WinReUpdates.Count + options.BootUpdates.Count > 0)
        {
            // Hashed again here (a few seconds per GB), so the plan holds the files as they are now.
            source = (source ?? new FodChoice(null, null)) with
            {
                Updates = string.IsNullOrWhiteSpace(options.UpdateFolder) ? null : UpdateFolder.Scan(options.UpdateFolder.Trim()),
            };
        }

        return options.RegFile is { } file ? PlanRegFile(file, removals, source)
            : removals.Count > 0 ? Plan(removals, null, source)
            : PlanM1();
    }

    /// <summary>Plans the M1 validation marker; used when nothing else is chosen.</summary>
    public PlanSummary PlanM1() => Plan([ValidationMarker.M1], null);

    /// <summary>Plans the changes of a .reg file. A file that cannot be read drops the previous plan.</summary>
    public PlanSummary PlanRegFile(RegFileInput file, IReadOnlyList<ImageChange>? alsoChanges = null) => PlanRegFile(file, alsoChanges, null);

    private PlanSummary PlanRegFile(RegFileInput file, IReadOnlyList<ImageChange>? alsoChanges, FodChoice? fod)
    {
        ArgumentNullException.ThrowIfNull(file);
        var import = RegFileImport.Read(file);
        var encoding = import.Encoding switch
        {
            RegFileEncodingKind.Utf16 => "UTF-16",
            RegFileEncodingKind.Utf8 => "UTF-8",
            RegFileEncodingKind.Ansi => $"ANSI {import.CodePage}",
            _ => null,
        };
        if (import.Changes is not { } changes)
        {
            lock (_gate)
            {
                ThrowIfBusy();
                _ = _open ?? throw new InvalidOperationException("No workspace is open.");
                _plan = null;
                return new PlanSummary(Guid.Empty, _revision, string.Empty, [], [], import.Diagnostics, encoding);
            }
        }

        return Plan([.. changes, .. alsoChanges ?? []], encoding, fod);
    }

    private PlanSummary Plan(IReadOnlyList<ImageChange> changes, string? fileEncoding, FodChoice? fod = null)
    {
        lock (_gate)
        {
            ThrowIfBusy();
            var open = _open ?? throw new InvalidOperationException("No workspace is open.");
            var result = new PipelinePlanner(new RegistryPathMapper(), newId)
                .Plan(new PlanRequest(open.WorkspaceId, _revision, open.ImageId, changes)
                {
                    Appx = _inventory?.Appx,
                    Features = _inventory?.Features,
                    Capabilities = _inventory?.Capabilities,
                    MediaHasNetFx3 = _inventory?.MediaHasNetFx3 == true,
                    FodSource = fod is { Folder: { } folder, Problem: null } ? folder : null,
                    FodSourceProblem = fod?.Problem,
                    Drivers = _inventory?.Drivers,
                    DriverSource = fod?.Drivers,
                    UpdateSource = fod?.Updates,
                    Packages = _inventory?.Packages,
                });
            _plan = result.Plan;
            return result.Plan is { } plan
                ? new PlanSummary(plan.PlanId, plan.WorkspaceRevision, plan.Fingerprint, plan.Preview, [], null, fileEncoding)
                {
                    AppxRemovals = [.. plan.AppxRemovals.Select(r => View(_inventory!.Appx.Packages.Single(p => p.Identity == r.Package), _inventory.Appx))],
                    CapabilityRemovals = [.. plan.Capabilities?.Remove.Select(c => c.FullName) ?? []],
                    CapabilityAdditions = [.. plan.Capabilities?.Add.Select(c => c.FullName) ?? []],
                    FodSource = plan.Capabilities?.Source,
                    FeatureChanges = plan.Features is { } f ? new FeatureChangeView(f.Disable, f.Enable, f.Added, f.UsesMediaSxs) { RemovePayload = f.RemovePayload } : null,
                    Drivers = plan.Drivers,
                    Updates = plan.Updates,
                    DotNetUpdates = plan.DotNetUpdates,
                    Cleanup = plan.Cleanup,
                    WinRe = plan.WinRe,
                    Boot = plan.Boot,
                }
                : new PlanSummary(Guid.Empty, _revision, string.Empty, [], result.Diagnostics, null, fileEncoding);
        }
    }

    public Task<RunOutcome> StartAsync(StartPlanRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_open is not { } open)
            {
                return Task.FromResult(Rejected(StartPlanRejection.NotOpen));
            }

            var registration = _ledger.Register(request.ClientRequestId, request.PlanId);
            switch (registration.Kind)
            {
                case StartRegistrationKind.Duplicate:
                    return _operations[registration.OperationId];
                case StartRegistrationKind.Conflict:
                    return Task.FromResult(Rejected(StartPlanRejection.RequestConflict));
            }

            var rejection = _busy ? StartPlanRejection.Busy : PlanStartValidator.Validate(_plan, request, _revision);
            if (rejection != StartPlanRejection.None)
            {
                var rejected = Task.FromResult(Rejected(rejection, registration.OperationId));
                _operations[registration.OperationId] = rejected;
                return rejected;
            }

            _busy = true;
            var task = RunAsync(registration.OperationId, open.Request, Math.Max(_editions.Count, 1), open.Paths, _plan!, progress, ct);
            _operations[registration.OperationId] = task;
            _current = task;
            return task;
        }
    }

    public async Task<RecoveryResult> RecoverAsync(IProgress<string>? progress, CancellationToken ct)
    {
        IWorkspacePaths paths;
        lock (_gate)
        {
            ThrowIfBusy();
            paths = (_open ?? throw new InvalidOperationException("No workspace is open.")).Paths;
            _busy = true;
        }

        try
        {
            var recovery = new RunRecovery(paths, servicingFactory(Report(progress))).RecoverAsync(ct);
            lock (_gate)
            {
                _current = recovery;
            }

            return await recovery.ConfigureAwait(false);
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>Completes when the run or recovery in progress (if any) has finished, including its cleanup.</summary>
    public Task WhenIdleAsync()
    {
        Task current;
        lock (_gate)
        {
            current = _current;
        }

        return current.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public WorkspaceSummary Summary()
    {
        lock (_gate)
        {
            if (_open is not { } open)
            {
                return new WorkspaceSummary(Guid.Empty, _revision, TargetStatus.Pending, null, false, []);
            }

            var loaded = new StateStore<WorkspaceState>(open.Paths.StateFile).Load();
            var lastRun = loaded.State?.LastRun;
            var recovery = loaded.Source == StateSource.Unusable
                || lastRun?.Status is RunStatus.Running or RunStatus.RecoveryRequired
                || ImageInspection.LeftBehind(open.Paths);
            return new WorkspaceSummary(open.WorkspaceId, _revision, loaded.State?.Target ?? TargetStatus.Pending, lastRun, recovery, [])
            {
                Editions = _editions,
                ImageIndex = open.Request.ImageIndex,
                Warnings = _warnings,
            };
        }
    }

    private async Task<RunOutcome> RunAsync(
        Guid operationId, OpenWorkspaceRequest open, int imageCount, IWorkspacePaths paths, ExecutionPlan plan, IProgress<string>? progress, CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            var log = Report(progress);
            var pipeline = new RunPipeline(paths, servicingFactory(log), newId) { Log = log, FreeSpace = FreeSpace };
            var result = await pipeline.RunAsync(new RunRequest(open.SourceDir, open.OutputDir, open.ImageIndex, plan, Failure) { ImageCount = imageCount }, ct).ConfigureAwait(false);
            return new RunOutcome(operationId, StartPlanRejection.None, result.Status, result.FailedStep, result.Error, result.PublishedPath);
        }
        catch (WorkspaceRecoveryRequiredException ex)
        {
            return new RunOutcome(operationId, StartPlanRejection.None, RunStatus.RecoveryRequired, null, ex.Message, null);
        }
        finally
        {
            Finish();
        }
    }

    private void Finish()
    {
        lock (_gate)
        {
            _busy = false;
            _plan = null;
            _revision++;
        }
    }

    private void ThrowIfBusy()
    {
        if (_busy)
        {
            throw new InvalidOperationException("A run is in progress.");
        }
    }

    private static AppxPackageView View(ProvisionedAppxPackage package, AppxInventory inventory)
    {
        var family = package.Identity.FamilyName;
        var blocked = package.Facts is null ? Messages.AppxFactsNotRead(package.FactsProblem)
            : inventory.InboxDependencies?.Contains(family, StringComparer.OrdinalIgnoreCase) == true ? Messages.AppxNeededByInbox
            : null;
        return new AppxPackageView(
            package.DisplayName, package.PackageName, family, package.Identity.Version.ToString(4), package.Identity.Architecture,
            AppxWarnings.For(package.Identity), blocked)
        {
            IsFramework = package.Facts?.IsFramework == true,
            NeededBy = [.. inventory.Packages
                .Where(p => p.Facts?.DependsOn?.Contains(family, StringComparer.OrdinalIgnoreCase) == true)
                .Select(p => p.DisplayName)],
        };
    }

    private static List<CapabilityView> CapabilityViews(CapabilityInventory capabilities, CapabilityState state) =>
        capabilities.Problems.Count > 0 ? [] : [.. capabilities.Capabilities
            .Where(c => c.State == state)
            .Select(c => new CapabilityView(c.Identity.FullName, capabilities.DisplayNames.GetValueOrDefault(c.Identity.FullName, c.Identity.Name), c.RawState))
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)];

    private static FeatureView View(WindowsFeature feature, FeatureInventory inventory, bool mediaHasNetFx3)
    {
        var fromMedia = feature.State == FeatureState.DisabledWithPayloadRemoved;
        var blocked = feature.State == FeatureState.Unknown ? Messages.FeatureStateUnsupported(feature.RawState)
            : fromMedia && !(mediaHasNetFx3 && string.Equals(feature.Name, FeatureSources.NetFx3, StringComparison.OrdinalIgnoreCase))
                ? Messages.FeatureNoSource
            : null;
        return new FeatureView(feature.Name, inventory.DisplayNames.GetValueOrDefault(feature.Name, feature.Name), feature.RawState, feature.IsOn, blocked)
        {
            FromMedia = fromMedia && blocked is null,
        };
    }

    private static Action<string> Report(IProgress<string>? progress) => line => progress?.Report(line);

    private static RunOutcome Rejected(StartPlanRejection rejection, Guid operationId = default) =>
        new(operationId, rejection, null, null, null, null);
}

/// <summary>The folders the plan takes files from, checked before the lock is taken.</summary>
/// <param name="Folder">The Features on Demand folder; null when no capability is added.</param>
/// <param name="Problem">Why that folder cannot be used; null when it passed the check.</param>
internal sealed record FodChoice(string? Folder, string? Problem)
{
    /// <summary>The scanned driver folder; null when no driver is added or no folder was given.</summary>
    public DriverFolder? Drivers { get; init; }

    /// <summary>The scanned update folder; null when no update is added or no folder was given.</summary>
    public UpdateFolder? Updates { get; init; }
}
