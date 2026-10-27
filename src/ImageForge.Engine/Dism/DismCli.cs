using System.Diagnostics;
using System.Text;

namespace ImageForge.Engine.Dism;

public sealed record CliResult(int ExitCode, string Output);

/// <summary>
/// Runs Dism.exe from the trusted System32 path with an explicit argument list (no cmd.exe or PowerShell).
/// </summary>
public static class DismCli
{
    public static string TrustedPath => Path.Combine(Environment.SystemDirectory, "Dism.exe");

    public static async Task<CliResult> RunAsync(IReadOnlyList<string> arguments, Action<string>? onLine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var startInfo = new ProcessStartInfo(TrustedPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        void Collect(object? _, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(e.Data);
            }

            onLine?.Invoke(e.Data);
        }

        process.OutputDataReceived += Collect;
        process.ErrorDataReceived += Collect;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Dism servicing is not killed on cancellation: a hard kill is not a safe stop (plan, section 5).
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return new CliResult(process.ExitCode, output.ToString());
    }
}
