using System.Reflection;
using ImageForge.Core.Contracts;

namespace ImageForge.Engine;

public sealed class EngineService(Action requestShutdown) : IEngineService
{
    private static readonly Guid InstanceId = Guid.NewGuid();

    public Task<EngineInfo> GetInfoAsync(CancellationToken ct)
    {
        var version = typeof(EngineService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return Task.FromResult(new EngineInfo(EngineProtocol.Version, version, InstanceId, Environment.ProcessId, []));
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        requestShutdown();
        return Task.CompletedTask;
    }
}
