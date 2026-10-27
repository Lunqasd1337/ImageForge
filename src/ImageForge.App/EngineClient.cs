using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using ImageForge.Core.Contracts;
using ImageForge.Core.Hosting;
using StreamJsonRpc;

namespace ImageForge.App;

public sealed class EngineLaunchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Starts the elevated engine from the trusted install path and connects to it over its private pipe.</summary>
public sealed partial class EngineClient : IAsyncDisposable
{
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly NamedPipeClientStream _pipe;
    private readonly JsonRpc _rpc;

    private EngineClient(Process process, NamedPipeClientStream pipe, JsonRpc rpc, IEngineService service, EngineInfo info)
    {
        _process = process;
        _pipe = pipe;
        _rpc = rpc;
        Service = service;
        Info = info;
    }

    public IEngineService Service { get; }

    public EngineInfo Info { get; }

    public static async Task<EngineClient> StartAsync(CancellationToken ct)
    {
        var hostPath = Path.Combine(AppContext.BaseDirectory, "engine", "ImageForge.Engine.Host.exe");
        if (!File.Exists(hostPath))
        {
            throw new EngineLaunchException($"Не найден движок: {hostPath}");
        }

        using var self = Process.GetCurrentProcess();
        var launch = EngineLaunchArguments.CreateFor(self.Id, self.StartTime.ToUniversalTime().ToFileTimeUtc());
        var startInfo = new ProcessStartInfo(hostPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            // Every argument is validated to contain no spaces or quotes.
            Arguments = string.Join(' ', launch.ToArgumentList()),
        };

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new EngineLaunchException("Движок не запустился.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new EngineLaunchException("Запуск движка отменён в окне UAC.", ex);
        }

        var pipe = new NamedPipeClientStream(".", launch.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(ConnectTimeout);
                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            }

            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != (uint)process.Id)
            {
                throw new EngineLaunchException("Канал движка принадлежит другому процессу.");
            }

            var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, new SystemTextJsonFormatter()));
            var service = rpc.Attach<IEngineService>();
            rpc.StartListening();

            var info = await service.GetInfoAsync(ct).ConfigureAwait(false);
            if (ProtocolHandshake.Check(info) != HandshakeResult.Compatible || info.ProcessId != process.Id)
            {
                rpc.Dispose();
                throw new EngineLaunchException($"Несовместимый движок: протокол {info.ProtocolVersion}.");
            }

            return new EngineClient(process, pipe, rpc, service, info);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            process.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Service.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException or IOException)
        {
            // The engine also stops by itself when this process or the pipe goes away.
        }

        _rpc.Dispose();
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
}
