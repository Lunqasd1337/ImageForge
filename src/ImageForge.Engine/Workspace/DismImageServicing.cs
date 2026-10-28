using ImageForge.Core.Execution;
using ImageForge.Core.Planning;
using ImageForge.Core.Safety;
using ImageForge.Core.Servicing;
using ImageForge.Core.Workspace;
using ImageForge.Engine.Dism;
using ImageForge.Engine.Registry;

namespace ImageForge.Engine.Workspace;

/// <summary>Run-pipeline servicing over the DISM API, Dism.exe export and offline hives. Needs an active <see cref="DismImages"/> session.</summary>
public sealed class DismImageServicing(DismImages session, Action<string>? log = null) : IImageServicing
{
    private readonly DismImages _session = session ?? throw new ArgumentNullException(nameof(session));

    public Task MountAsync(string imageFile, int index, string mountDir, CancellationToken ct) =>
        Task.Run(() => DismImages.Mount(imageFile, index, mountDir, Progress("mount")), CancellationToken.None);

    public Task UnmountAsync(string mountDir, bool commit, CancellationToken ct) =>
        Task.Run(() => DismImages.Unmount(mountDir, commit, Progress(commit ? "commit" : "discard")), CancellationToken.None);

    public Task<bool> IsMountedAsync(string mountDir, CancellationToken ct)
    {
        GC.KeepAlive(_session);
        var full = Path.GetFullPath(mountDir).TrimEnd('\\');
        return Task.FromResult(DismImages.GetMountedImages()
            .Any(m => string.Equals(m.MountPath.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task ExportAsync(string sourceImage, int index, string destinationImage, CancellationToken ct)
    {
        var arguments = DismCliArguments.ExportImage(sourceImage, index, destinationImage);
        log?.Invoke($"{DismCli.TrustedPath} {string.Join(' ', arguments)}");
        var result = await DismCli.RunAsync(arguments, null, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException($"Dism.exe /Export-Image exit code {result.ExitCode}: {Tail(result.Output)}");
        }
    }

    public ExecutionReport ApplyRegistryPlan(Guid runId, ExecutionPlan plan, string mountDir, CancellationToken ct)
    {
        var scope = WriteScope.Create(mountDir);
        return new RegistryPlanExecutor(new Win32OfflineHiveLoader(runId), hiveFile => OfflineHivePaths.Resolve(scope, hiveFile), new MountFileWriter(scope.Root))
            .Execute(plan, ct);
    }

    public IReadOnlyList<string> UnloadOwnedHives(Guid runId) => Win32OfflineHiveLoader.UnloadOwned(runId);

    private Action<int>? Progress(string operation)
    {
        if (log is null)
        {
            return null;
        }

        var last = -1;
        return p =>
        {
            if (p / 25 != last / 25)
            {
                last = p;
                log($"{operation} {p}%");
            }
        };
    }

    private static string Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(4));
    }
}
