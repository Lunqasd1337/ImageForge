using System.IO.Pipes;
using ImageForge.Core.Contracts;
using ImageForge.Core.Hosting;
using ImageForge.Engine;
using ImageForge.Engine.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace ImageForge.Engine.Host;

/// <summary>
/// Serves exactly one connection from the verified UI process on a single-instance pipe.
/// Losing the UI or the pipe stops the host.
/// </summary>
internal sealed partial class EnginePipeServer(
    EngineLaunchArguments launch,
    ClientProcess client,
    IHostApplicationLifetime lifetime,
    ILogger<EnginePipeServer> logger) : BackgroundService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private const int PipeBufferSize = 64 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.Exited += (_, _) =>
        {
            LogClientExited(logger, client.ProcessId);
            lifetime.StopApplication();
        };

        try
        {
            await ServeAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            LogPipeFailed(logger, ex);
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    private async Task ServeAsync(CancellationToken stoppingToken)
    {
        await using var pipe = NamedPipeServerStreamAcl.Create(
            launch.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            PipeBufferSize,
            PipeBufferSize,
            EnginePipeSecurity.Create(client.User));

        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            connectTimeout.CancelAfter(ConnectTimeout);
            await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
        }

        if (!client.IsConnectedTo(pipe))
        {
            LogUnexpectedClient(logger);
            return;
        }

        var service = new EngineService(lifetime.StopApplication);
        var formatter = new SystemTextJsonFormatter();
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, formatter));
        rpc.AddLocalRpcTarget<IEngineService>(service, null);
        rpc.StartListening();
        LogConnected(logger, client.ProcessId);

        await rpc.Completion.WaitAsync(stoppingToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "UI process {Pid} connected")]
    private static partial void LogConnected(ILogger logger, int pid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UI process {Pid} exited; stopping engine")]
    private static partial void LogClientExited(ILogger logger, int pid);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pipe connection came from an unexpected process; refusing")]
    private static partial void LogUnexpectedClient(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Engine pipe failed")]
    private static partial void LogPipeFailed(ILogger logger, Exception exception);
}
