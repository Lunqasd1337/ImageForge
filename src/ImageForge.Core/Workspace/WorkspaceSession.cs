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
    private readonly Func<string, IWorkspacePaths> _layoutFactory = layoutFactory ?? (dir => WorkspaceLayout.Create(dir));
    private readonly Func<string, string, string, IReadOnlyList<LocationProblem>> _validate = validateLocations ?? WorkspaceLocations.Validate;
    private readonly StartRequestLedger _ledger = new(newId);
    private readonly Dictionary<Guid, Task<RunOutcome>> _operations = [];
    private readonly Lock _gate = new();
    private (OpenWorkspaceRequest Request, IWorkspacePaths Paths, Guid WorkspaceId, Guid ImageId)? _open;
    private ExecutionPlan? _plan;
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

    public PlanSummary Plan(PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.RegFile is { } file ? PlanRegFile(file) : PlanM1();
    }

    /// <summary>Plans the M1 validation marker; used when no .reg file is chosen.</summary>
    public PlanSummary PlanM1() => Plan([ValidationMarker.M1], null);

    /// <summary>Plans the changes of a .reg file. A file that cannot be read drops the previous plan.</summary>
    public PlanSummary PlanRegFile(RegFileInput file)
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

        return Plan(changes, encoding);
    }

    private PlanSummary Plan(IReadOnlyList<ImageChange> changes, string? fileEncoding)
    {
        lock (_gate)
        {
            ThrowIfBusy();
            var open = _open ?? throw new InvalidOperationException("No workspace is open.");
            var result = new PipelinePlanner(new RegistryPathMapper(), newId)
                .Plan(new PlanRequest(open.WorkspaceId, _revision, open.ImageId, changes));
            _plan = result.Plan;
            return result.Plan is { } plan
                ? new PlanSummary(plan.PlanId, plan.WorkspaceRevision, plan.Fingerprint, plan.Preview, [], null, fileEncoding)
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
                || lastRun?.Status is RunStatus.Running or RunStatus.RecoveryRequired;
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

    private static Action<string> Report(IProgress<string>? progress) => line => progress?.Report(line);

    private static RunOutcome Rejected(StartPlanRejection rejection, Guid operationId = default) =>
        new(operationId, rejection, null, null, null, null);
}
