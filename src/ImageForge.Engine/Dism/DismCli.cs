using System.Diagnostics;
using System.Text;
using Windows.Win32;

namespace ImageForge.Engine.Dism;

public sealed record CliResult(int ExitCode, string Output);

/// <summary>
/// Runs Dism.exe from the trusted System32 path with an explicit argument list (no cmd.exe or PowerShell).
/// </summary>
public static class DismCli
{
    public static string TrustedPath => Path.Combine(Environment.SystemDirectory, "Dism.exe");

    /// <summary>
    /// Dism.exe writes redirected output in the OEM code page (866 on a Russian system, found in the VM): read as UTF-8,
    /// localized descriptions turned into '?'. ASCII, which the parsers read, is the same in every OEM code page.
    /// </summary>
    public static Encoding OutputEncoding { get; } =
        CodePagesEncodingProvider.Instance.GetEncoding((int)PInvoke.GetOEMCP()) ?? Encoding.UTF8;

    public static Task<CliResult> RunAsync(IReadOnlyList<string> arguments, Action<string>? onLine, CancellationToken ct) =>
        RunToolAsync(TrustedPath, arguments, onLine, ct);

    /// <summary>Runs another System32 tool the same way, such as expand.exe.</summary>
    public static async Task<CliResult> RunToolAsync(string path, IReadOnlyList<string> arguments, Action<string>? onLine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = OutputEncoding,
            StandardErrorEncoding = OutputEncoding,
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
