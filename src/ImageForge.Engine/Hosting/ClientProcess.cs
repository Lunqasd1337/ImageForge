using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Security;

namespace ImageForge.Engine.Hosting;

/// <summary>
/// The UI process the engine was started for. Holds its process handle for the engine's lifetime so the PID
/// cannot be reused, and exposes its token user as the pipe initiator.
/// </summary>
public sealed class ClientProcess : IDisposable
{
    private readonly Process _process;

    private ClientProcess(Process process, SecurityIdentifier user)
    {
        _process = process;
        User = user;
    }

    public int ProcessId => _process.Id;

    public SecurityIdentifier User { get; }

    public bool HasExited => _process.HasExited;

    public event EventHandler? Exited
    {
        add => _process.Exited += value;
        remove => _process.Exited -= value;
    }

    /// <summary>Opens the expected UI process and verifies it by PID and start time.</summary>
    public static ClientProcess Open(int processId, long startTimeUtcFileTime)
    {
        var process = Process.GetProcessById(processId);
        try
        {
            if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != startTimeUtcFileTime)
            {
                throw new InvalidOperationException("Client process start time does not match; the PID may have been reused.");
            }

            process.EnableRaisingEvents = true;
            return new ClientProcess(process, ReadTokenUser(process));
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>Checks that the process connected to the pipe is the expected UI process.</summary>
    public bool IsConnectedTo(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid))
        {
            throw new Win32Exception();
        }

        return clientPid == (uint)_process.Id;
    }

    public void Dispose() => _process.Dispose();

    private static SecurityIdentifier ReadTokenUser(Process process)
    {
        if (!PInvoke.OpenProcessToken(process.SafeHandle, TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
        {
            throw new Win32Exception();
        }

        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            return identity.User ?? throw new InvalidOperationException("Client process token has no user SID.");
        }
    }
}
