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
        _session = new WorkspaceSession(line => new DismImageServicing(Dism(), line), Guid.NewGuid);
    }

    public Task<EngineInfo> GetInfoAsync(CancellationToken ct)
    {
        var version = typeof(EngineService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return Task.FromResult(new EngineInfo(EngineProtocol.Version, version, InstanceId, Environment.ProcessId, ["workspace", "m1-run", "reg-file", "recover"]));
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _requestShutdown();
        return Task.CompletedTask;
    }

    public Task<WorkspaceSummary> OpenWorkspaceAsync(OpenWorkspaceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        _log($"open workspace: source {request.SourceDir}, work {request.WorkDir}, output {request.OutputDir}");
        return Task.FromResult(_session.Open(request));
    }

    public Task<WorkspaceSummary> GetWorkspaceAsync(CancellationToken ct) => Task.FromResult(_session.Summary());

    public Task<PlanSummary> PlanAsync(PlanOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        _log(options.RegFile is { } file
            ? $"plan .reg {file.FileName} ({file.Content?.Length ?? 0} bytes, {file.Encoding}, code page {file.AnsiCodePage})"
            : "plan M1 marker");
        var plan = _session.Plan(options);
        _log($"plan {plan.PlanId} revision {plan.WorkspaceRevision} fingerprint {plan.Fingerprint}, " +
            $"{plan.Preview.Count} changes, {plan.Diagnostics.Count + plan.FileDiagnostics.Count} problems");
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
