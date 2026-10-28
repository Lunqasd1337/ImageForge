using ImageForge.Core.Planning;
using ImageForge.Core.Registry.RegFile;
using ImageForge.Core.Workspace;
using StreamJsonRpc;

namespace ImageForge.Core.Contracts;

public static class EngineProtocol
{
    public const int Version = 4;
}

public sealed record EngineInfo(int ProtocolVersion, string EngineVersion, Guid InstanceId, int ProcessId, IReadOnlyList<string> Capabilities);

public enum HandshakeResult
{
    Compatible,
    IncompatibleProtocol,
}

public static class ProtocolHandshake
{
    public static HandshakeResult Check(EngineInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info.ProtocolVersion == EngineProtocol.Version ? HandshakeResult.Compatible : HandshakeResult.IncompatibleProtocol;
    }
}

/// <summary>RPC surface of the elevated engine. Only a closed set of typed operations is exposed.</summary>
[JsonRpcContract]
public partial interface IEngineService
{
    Task<EngineInfo> GetInfoAsync(CancellationToken ct);

    Task ShutdownAsync(CancellationToken ct);

    /// <summary>Validates the three folders and opens the workspace in the work folder; a new revision starts.</summary>
    Task<WorkspaceSummary> OpenWorkspaceAsync(OpenWorkspaceRequest request, CancellationToken ct);

    Task<WorkspaceSummary> GetWorkspaceAsync(CancellationToken ct);

    /// <summary>
    /// Plans the open workspace: the changes of the chosen .reg file, or the M1 marker without one. Only the latest
    /// plan can be started; a file that cannot be read leaves no plan.
    /// </summary>
    Task<PlanSummary> PlanAsync(PlanOptions options, CancellationToken ct);

    /// <summary>
    /// Copies and verifies the source if needed, mounts the copy read-only and reads its provisioned Appx packages.
    /// Removals are planned only against the latest inventory of the open workspace.
    /// </summary>
    Task<InspectionSummary> InspectAsync(IProgress<string>? progress, CancellationToken ct);

    /// <summary>Runs the shown plan to the end and returns its outcome; progress lines stream while it runs.</summary>
    Task<RunOutcome> StartAsync(StartPlanRequest request, IProgress<string>? progress, CancellationToken ct);

    Task<RecoveryResult> RecoverAsync(IProgress<string>? progress, CancellationToken ct);
}

/// <param name="RegFile">The .reg file to apply.</param>
/// <param name="RemoveAppx">Family names of provisioned packages to remove, resolved against the latest inventory.</param>
/// <remarks>With neither, the M1 validation marker is planned.</remarks>
public sealed record PlanOptions(RegFileInput? RegFile, IReadOnlyList<string>? RemoveAppx = null)
{
    public IReadOnlyList<string> RemoveAppx { get; init; } = RemoveAppx ?? [];
}

public enum EngineState
{
    Idle,
    Inspecting,
    Ready,
    Planning,
    Applying,
    Cancelling,
    Finalizing,
    Completed,
    Failed,
    RecoveryRequired,
}

/// <param name="Code">Stable machine-readable code.</param>
/// <param name="NativeCode">Original HRESULT or process exit code, if any.</param>
public sealed record EngineError(string Code, string? Target, string? Stage, int? NativeCode, IReadOnlyList<string> AvailableActions);
