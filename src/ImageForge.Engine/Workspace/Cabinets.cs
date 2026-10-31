using ImageForge.Engine.Dism;

namespace ImageForge.Engine.Workspace;

/// <summary>Expands a cabinet with expand.exe from System32, as Microsoft's media dynamic update sample does.</summary>
public static class Cabinets
{
    public static string TrustedPath => Path.Combine(Environment.SystemDirectory, "expand.exe");

    public static async Task ExpandAsync(string cabinet, string folder, Action<string>? log, CancellationToken ct)
    {
        log?.Invoke($"expand.exe {cabinet} -F:* {folder}");
        var result = await DismCli.RunToolAsync(TrustedPath, [cabinet, "-F:*", folder], null, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException($"expand.exe failed with exit code {result.ExitCode}: {result.Output.Trim()}");
        }
    }
}
