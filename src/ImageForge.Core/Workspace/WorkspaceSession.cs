using ImageForge.Core.Appx;
using ImageForge.Core.Changes;
using ImageForge.Core.Contracts;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;
using ImageForge.Core.Registry.RegFile;

namespace ImageForge.Core.Workspace;

public sealed record OpenWorkspaceRequest(string SourceDir, string WorkDir, string OutputDir);

/// <param name="Problems">Non-empty when the folders cannot be used; nothing is opened then.</param>
public sealed record WorkspaceSummary(
    Guid WorkspaceId,
    long Revision,
    TargetStatus Target,
    RunRecord? LastRun,
    bool RecoveryRequired,
    IReadOnlyList<LocationProblem> Problems);

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
}

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
public sealed record InspectionSummary(long WorkspaceRevision, IReadOnlyList<AppxPackageView> Packages, IReadOnlyList<string> Problems);

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
    private readonly Func<string, IWorkspacePaths> _layoutFactory = layoutFactory ?? (dir => WorkspaceLayout.Create(dir));
    private readonly Func<string, string, string, IReadOnlyList<LocationProblem>> _validate = validateLocations ?? WorkspaceLocations.Validate;
    private readonly StartRequestLedger _ledger = new(newId);
    private readonly Dictionary<Guid, Task<RunOutcome>> _operations = [];
    private readonly Lock _gate = new();
    private (OpenWorkspaceRequest Request, IWorkspacePaths Paths, Guid WorkspaceId, Guid ImageId)? _open;
    private ExecutionPlan? _plan;
    private AppxInventory? _inventory;
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
            if (problems.Count > 0)
            {
                return new WorkspaceSummary(Guid.Empty, _revision, TargetStatus.Pending, null, false, problems);
            }

            var paths = _layoutFactory(request.WorkDir);
            var state = new StateStore<WorkspaceState>(paths.StateFile).Load();
            var workspaceId = state.State?.WorkspaceId ?? newId();
            _open = (request, paths, workspaceId, newId());
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
        AppxInventory? inventory = null;
        string? error = null;
        try
        {
            var inspection = new ImageInspection(open.Paths, servicingFactory(log)) { Log = log };
            var task = inspection.InspectAsync(open.Request.SourceDir, open.WorkspaceId, 1, ct);
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
            if (inventory is null || inventory.Problems.Count > 0)
            {
                return new InspectionSummary(_revision, [], inventory?.Problems ?? [error!]);
            }

            _inventory = inventory;
            return new InspectionSummary(
                _revision,
                [.. inventory.Packages.OrderBy(p => p.Identity.Name, StringComparer.OrdinalIgnoreCase).Select(p => View(p, inventory))],
                []);
        }
    }

    public PlanSummary Plan(PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<ImageChange> removals = [.. options.RemoveAppx.Select(f => new RemoveProvisionedAppx(f))];
        return options.RegFile is { } file ? PlanRegFile(file, removals)
            : removals.Count > 0 ? Plan(removals, null)
            : PlanM1();
    }

    /// <summary>Plans the M1 validation marker; used when nothing else is chosen.</summary>
    public PlanSummary PlanM1() => Plan([ValidationMarker.M1], null);

    /// <summary>Plans the changes of a .reg file. A file that cannot be read drops the previous plan.</summary>
    public PlanSummary PlanRegFile(RegFileInput file, IReadOnlyList<ImageChange>? alsoChanges = null)
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

        return Plan([.. changes, .. alsoChanges ?? []], encoding);
    }

    private PlanSummary Plan(IReadOnlyList<ImageChange> changes, string? fileEncoding)
    {
        lock (_gate)
        {
            ThrowIfBusy();
            var open = _open ?? throw new InvalidOperationException("No workspace is open.");
            var result = new PipelinePlanner(new RegistryPathMapper(), newId)
                .Plan(new PlanRequest(open.WorkspaceId, _revision, open.ImageId, changes) { Appx = _inventory });
            _plan = result.Plan;
            return result.Plan is { } plan
                ? new PlanSummary(plan.PlanId, plan.WorkspaceRevision, plan.Fingerprint, plan.Preview, [], null, fileEncoding)
                {
                    AppxRemovals = [.. plan.AppxRemovals.Select(r => View(_inventory!.Packages.Single(p => p.Identity == r.Package), _inventory))],
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
            var task = RunAsync(registration.OperationId, open.Request, open.Paths, _plan!, progress, ct);
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
            return new WorkspaceSummary(open.WorkspaceId, _revision, loaded.State?.Target ?? TargetStatus.Pending, lastRun, recovery, []);
        }
    }

    private async Task<RunOutcome> RunAsync(
        Guid operationId, OpenWorkspaceRequest open, IWorkspacePaths paths, ExecutionPlan plan, IProgress<string>? progress, CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            var log = Report(progress);
            var pipeline = new RunPipeline(paths, servicingFactory(log), newId) { Log = log };
            var result = await pipeline.RunAsync(new RunRequest(open.SourceDir, open.OutputDir, 1, plan, Failure), ct).ConfigureAwait(false);
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
        var blocked = package.Facts is null ? $"Зависимости не прочитаны ({package.FactsProblem}); удалить нельзя."
            : inventory.InboxDependencies?.Contains(family, StringComparer.OrdinalIgnoreCase) == true ? "Нужна встроенным приложениям Windows; удалить нельзя."
            : null;
        return new AppxPackageView(
            package.DisplayName, package.PackageName, family, package.Identity.Version.ToString(4), package.Identity.Architecture,
            AppxWarnings.For(package.Identity), blocked)
        {
            IsFramework = package.Facts?.IsFramework == true,
            NeededBy = [.. inventory.Packages
                .Where(p => p.Facts?.DependsOn.Contains(family, StringComparer.OrdinalIgnoreCase) == true)
                .Select(p => p.DisplayName)],
        };
    }

    private static Action<string> Report(IProgress<string>? progress) => line => progress?.Report(line);

    private static RunOutcome Rejected(StartPlanRejection rejection, Guid operationId = default) =>
        new(operationId, rejection, null, null, null, null);
}
