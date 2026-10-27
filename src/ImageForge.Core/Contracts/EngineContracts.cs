using StreamJsonRpc;

namespace ImageForge.Core.Contracts;

public static class EngineProtocol
{
    public const int Version = 1;
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
