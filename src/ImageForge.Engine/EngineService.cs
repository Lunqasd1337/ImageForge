using System.Reflection;
using ImageForge.Core.Contracts;
using ImageForge.Core.Planning;
using ImageForge.Core.Workspace;
using ImageForge.Engine.Dism;
using ImageForge.Engine.Workspace;

namespace ImageForge.Engine;

public sealed class EngineService : IEngineService, IDisposable
{
    private static readonly Guid InstanceId = Guid.NewGuid();
    private readonly Action _requestShutdown;
    private readonly Action<string> _log;
    private readonly WorkspaceSession _session;
    private readonly Lock _dismGate = new();
    private readonly CancellationTokenSource _stopping = new();
    private DismImages? _dism;

    public EngineService(Action requestShutdown, Action<string>? log = null)
    {
        _requestShutdown = requestShutdown;
        _log = log ?? (_ => { });
        _session = new WorkspaceSession(line => new DismImageServicing(Dism(), line), Guid.NewGuid) {
            FreeSpace = DiskSpace.FreeBytes,
            Volumes = LocalVolumes.Facts,
            IsSynced = LocalVolumes.IsSynced,
        };
    }

    public Task<EngineInfo> GetInfoAsync(CancellationToken ct)
    {
        var version = typeof(EngineService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return Task.FromResult(new EngineInfo(EngineProtocol.Version, version, InstanceId, Environment.ProcessId, ["workspace", "m1-run", "reg-file", "appx", "features", "drivers", "updates", "cleanup", "editions", "winre", "boot", "recover"]));
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _requestShutdown();
        return Task.CompletedTask;
    }

    public Task<WorkspaceSummary> OpenWorkspaceAsync(OpenWorkspaceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        _log($"open workspace: source {request.SourceDir}, work {request.WorkDir}, output {request.OutputDir}, image {(request.ImageIndex == 0 ? "default" : request.ImageIndex)}");
        var summary = _session.Open(request);
        var edition = summary.Editions.FirstOrDefault(e => e.Index == summary.ImageIndex);
        _log(summary.Editions.Count == 0
            ? $"editions: install.wim not readable, servicing image {summary.ImageIndex}"
            : $"editions: {summary.Editions.Count}, servicing {summary.ImageIndex} {edition?.Name}, others kept unchanged");
        return Task.FromResult(summary);
    }

    public Task<WorkspaceSummary> GetWorkspaceAsync(CancellationToken ct) => Task.FromResult(_session.Summary());

    public Task<PlanSummary> PlanAsync(PlanOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        _log(options.RegFile is { } file
            ? $"plan .reg {file.FileName} ({file.Content?.Length ?? 0} bytes, {file.Encoding}, code page {file.AnsiCodePage})"
            : options.RemoveAppx.Count + options.RemoveCapabilities.Count + options.AddCapabilities.Count + options.EnableFeatures.Count + options.DisableFeatures.Count
                + options.AddDrivers.Count + options.WinReDrivers.Count + options.BootDrivers.Count + options.RemoveDrivers.Count + options.AddUpdates.Count + options.WinReUpdates.Count + options.BootUpdates.Count > 0 || options.CleanupComponents
                ? "plan without .reg" : "plan M1 marker");
        foreach (var family in options.RemoveAppx)
        {
            _log($"plan remove appx {family}");
        }

        foreach (var capability in options.RemoveCapabilities)
        {
            _log($"plan remove capability {capability}");
        }

        foreach (var capability in options.AddCapabilities)
        {
            _log($"plan add capability {capability} from {options.FodSource ?? "(no folder)"}");
        }

        foreach (var feature in options.DisableFeatures)
        {
            _log($"plan disable feature {feature}");
        }

        foreach (var feature in options.EnableFeatures)
        {
            _log($"plan enable feature {feature}");
        }

        foreach (var driver in options.RemoveDrivers)
        {
            _log($"plan remove driver {driver}{(options.AllowBootCritical ? " (boot-critical allowed)" : string.Empty)}");
        }

        foreach (var driver in options.AddDrivers)
        {
            _log($"plan add driver {driver} from {options.DriverFolder ?? "(no folder)"}{(options.ForceUnsigned ? " (ForceUnsigned allowed)" : string.Empty)}");
        }

        foreach (var driver in options.WinReDrivers)
        {
            _log($"plan add WinRE driver {driver} from {options.DriverFolder ?? "(no folder)"}{(options.ForceUnsigned ? " (ForceUnsigned allowed)" : string.Empty)}");
        }

        foreach (var driver in options.BootDrivers)
        {
            _log($"plan add boot.wim driver {driver} from {options.DriverFolder ?? "(no folder)"}{(options.ForceUnsigned ? " (ForceUnsigned allowed)" : string.Empty)}");
        }

        foreach (var update in options.AddUpdates)
        {
            _log($"plan add update {update} from {options.UpdateFolder ?? "(no folder)"}");
        }

        foreach (var update in options.WinReUpdates)
        {
            _log($"plan add WinRE update {update} from {options.UpdateFolder ?? "(no folder)"}");
        }

        foreach (var update in options.BootUpdates)
        {
            _log($"plan add boot.wim update {update} from {options.UpdateFolder ?? "(no folder)"}");
        }

        if (options.CleanupComponents)
        {
            _log($"plan cleanup component store{(options.ResetBase ? " (ResetBase)" : string.Empty)}");
        }

        var plan = _session.Plan(options);
        _log($"plan {plan.PlanId} revision {plan.WorkspaceRevision} fingerprint {plan.Fingerprint}, " +
            $"{plan.Preview.Count} changes, {plan.Diagnostics.Count + plan.FileDiagnostics.Count} problems");
        foreach (var warning in (plan.Drivers?.Warnings ?? []).Concat(plan.Updates?.Warnings ?? []).Concat(plan.DotNetUpdates?.Warnings ?? []).Concat(plan.Cleanup?.Warnings ?? []).Concat(plan.WinRe?.Warnings ?? []).Concat(plan.Boot?.Warnings ?? []))
        {
            _log($"plan warning: {warning}");
        }
        return Task.FromResult(plan);
    }

    public async Task<RunOutcome> StartAsync(StartPlanRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        _log($"start plan {request.PlanId} revision {request.ExpectedWorkspaceRevision} request {request.ClientRequestId}");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        var outcome = await _session.StartAsync(request, Tee(progress), linked.Token).ConfigureAwait(false);
        _log($"run {outcome.OperationId}: rejection {outcome.Rejection}, status {outcome.Status}, step {outcome.FailedStep}, error {outcome.Error}");
        return outcome;
    }

    public async Task<InspectionSummary> InspectAsync(IProgress<string>? progress, CancellationToken ct)
    {
        _log("inspect image");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        var result = await _session.InspectAsync(Tee(progress), linked.Token).ConfigureAwait(false);
        _log($"inspection revision {result.WorkspaceRevision}: {result.Packages.Count} packages, {result.Features.Count} features, " +
            $"{result.Capabilities.Count} installed and {result.AvailableCapabilities.Count} available capabilities, {result.Drivers.Count} third-party drivers, " +
            $"{result.PackageCount} servicing packages (rollup {result.Rollup ?? "none"}, servicing stack {result.ServicingStack ?? "none"}), " +
            $"problems: {string.Join("; ", [.. result.Problems, .. result.FeatureProblems, .. result.CapabilityProblems, .. result.DriverProblems, .. result.PackageProblems])}");
        return result;
    }

    public async Task<RecoveryResult> RecoverAsync(IProgress<string>? progress, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        var result = await _session.RecoverAsync(Tee(progress), linked.Token).ConfigureAwait(false);
        _log($"recovery {result.Outcome}: {string.Join("; ", result.Actions.Concat(result.Problems))}");
        return result;
    }

    /// <summary>
    /// Stops safely when the UI is gone: the run in progress is cancelled at the next step boundary, its failure
    /// path discards the mount, and only then may the process exit.
    /// </summary>
    public async Task DrainAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _session.WhenIdleAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_dismGate)
        {
            try
            {
                _dism?.Dispose();
            }
#pragma warning disable CA1031 // Shutdown must not crash the host; the log keeps the reason.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log($"DismApi shutdown failed: {ex.Message}");
            }

            _dism = null;
        }

        _session.Dispose();
        _stopping.Dispose();
    }

    // DismApi may be initialized only once per process, so one session serves the whole engine lifetime.
    private DismImages Dism()
    {
        lock (_dismGate)
        {
            return _dism ??= new DismImages();
        }
    }

    private Progress Tee(IProgress<string>? remote) => new(line =>
    {
        _log(line);
        remote?.Report(line);
    });

    private sealed class Progress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
