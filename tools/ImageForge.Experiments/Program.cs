using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using ImageForge.Core.Changes;
using ImageForge.Core.Execution;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;
using ImageForge.Core.Safety;
using ImageForge.Core.Servicing;
using ImageForge.Engine.Dism;
using ImageForge.Engine.Registry;
using Microsoft.Win32;

// Manual M1 experiments (plan, section 16). Every command prints what it did and ends with RESULT: OK / FAIL.
var commands = new Dictionary<string, (string Usage, Func<string[], Task<bool>> Run)>(StringComparer.OrdinalIgnoreCase)
{
    ["version"] = ("version", _ => Task.FromResult(PrintVersion())),
    ["hash"] = ("hash <file>", a => Hash(a[0])),
    ["info"] = ("info <install.wim>", a => Task.FromResult(Info(a[0]))),
    ["mount"] = ("mount <install.wim> <index> <empty-mount-dir>", a => Task.FromResult(Mount(a[0], int.Parse(a[1], CultureInfo.InvariantCulture), a[2]))),
    ["write-marker"] = ("write-marker <mount-dir>", a => Task.FromResult(WriteMarker(a[0]))),
    ["read-marker"] = ("read-marker <mount-dir>", a => Task.FromResult(ReadMarker(a[0]))),
    ["host-check"] = ("host-check", _ => Task.FromResult(HostCheck())),
    ["mounts"] = ("mounts", _ => Task.FromResult(Mounts())),
    ["unmount"] = ("unmount <mount-dir> commit|discard", a => Task.FromResult(Unmount(a[0], a[1]))),
    ["export"] = ("export <source.wim> <index> <destination.wim>", a => Export(a[0], int.Parse(a[1], CultureInfo.InvariantCulture), a[2])),
};

if (args.Length == 0 || !commands.TryGetValue(args[0], out var command))
{
    Console.WriteLine("ImageForge.Experiments — run as administrator inside the test VM only.");
    foreach (var (usage, _) in commands.Values)
    {
        Console.WriteLine("  " + usage);
    }

    return 2;
}

var expectedArgs = command.Usage.Split(' ').Length - 1;
if (args.Length - 1 != expectedArgs)
{
    Console.WriteLine("Usage: " + command.Usage);
    return 2;
}

Log($"ImageForge.Experiments {BuildId()} :: {string.Join(' ', args)}");
bool ok;
try
{
    ok = await command.Run(args[1..]).ConfigureAwait(false);
}
#pragma warning disable CA1031 // A manual experiment reports every failure instead of crashing.
catch (Exception ex)
#pragma warning restore CA1031
{
    Log("ERROR: " + ex);
    ok = false;
}

Log(ok ? "RESULT: OK" : "RESULT: FAIL");
return ok ? 0 : 1;

static void Log(string line) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");

static string BuildId() =>
    (Assembly.GetEntryAssembly() ?? typeof(HiveLoadName).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

static bool PrintVersion()
{
    Log($"BuildId: {BuildId()}");
    Log($"OS: {Environment.OSVersion.VersionString}");
    Log($"Dism.exe: {DismCli.TrustedPath} {FileVersionInfo.GetVersionInfo(DismCli.TrustedPath).ProductVersion}");
    return true;
}

static async Task<bool> Hash(string file)
{
    var stream = File.OpenRead(file);
    await using (stream.ConfigureAwait(false))
    {
        var hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        Log($"SHA-256 {Convert.ToHexString(hash)}  {Path.GetFullPath(file)}  ({new FileInfo(file).Length} bytes)");
    }

    return true;
}

static bool Info(string wim)
{
    using var dism = new DismImages();
    foreach (var image in DismImages.GetImages(wim))
    {
        Log($"Index {image.Index}: {image.Name} | {image.EditionId} | {image.Version} | {image.Architecture} | {image.SizeBytes} bytes");
    }

    return true;
}

static bool Mount(string wim, int index, string mountDir)
{
    var full = Path.GetFullPath(mountDir);
    if (!Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any())
    {
        Log($"Mount directory must exist and be empty: {full}");
        return false;
    }

    using var dism = new DismImages();
    var last = -1;
    DismImages.Mount(wim, index, full, p =>
    {
        if (p / 10 != last / 10)
        {
            last = p;
            Log($"mount {p}%");
        }
    });
    Log($"Mounted {wim} index {index} at {full} (read-write)");
    PrintMounts();
    return true;
}

static bool WriteMarker(string mountDir)
{
    var scope = WriteScope.Create(Path.GetFullPath(mountDir));
    var planner = new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid);
    var result = planner.Plan(new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), [ValidationMarker.M1]));
    if (result.Plan is not { } plan)
    {
        foreach (var d in result.Diagnostics)
        {
            Log($"PLAN ERROR {d.Code}: {d.Message}");
        }

        return false;
    }

    foreach (var p in plan.Preview)
    {
        Log($"Preview: {p.SourcePath} -> offline {p.Hive} hive ({OfflineHivePaths.Resolve(scope, p.HiveFile)}), key {p.SubKey}, {p.ValueName} = REG_SZ \"{p.NewValue.Data}\"");
    }

    Log($"PlanId {plan.PlanId}, fingerprint {plan.Fingerprint}");
    var runId = Guid.NewGuid();
    var executor = new RegistryPlanExecutor(new Win32OfflineHiveLoader(runId), hiveFile => OfflineHivePaths.Resolve(scope, hiveFile));
    var report = executor.Execute(plan, CancellationToken.None);
    Log($"Execution: {report.Outcome}, steps {report.CompletedSteps}/{plan.Steps.Count}" +
        (report.Error is null ? string.Empty : $", failed step {report.FailedStepIndex}: {report.Error}"));
    if (report.RequiresRecovery)
    {
        Log($"RECOVERY REQUIRED: hives still loaded: {string.Join(", ", report.HivesLeftLoaded)} (run {runId:N})");
    }

    return report.Outcome == ExecutionOutcome.Completed && OwnedHivesLoaded() == 0;
}

static bool ReadMarker(string mountDir)
{
    var scope = WriteScope.Create(Path.GetFullPath(mountDir));
    var location = new RegistryPathMapper().Map(ValidationMarker.M1.KeyPath).Location!;
    var hive = (Win32OfflineHiveLoader.LoadedHive)new Win32OfflineHiveLoader(Guid.NewGuid())
        .Load(location.Hive, OfflineHivePaths.Resolve(scope, location.HiveFile));
    string? value;
    try
    {
        value = hive.ReadString(location.SubKey, ValidationMarker.M1.ValueName);
    }
    finally
    {
        hive.Unload();
    }

    Log($"Offline {ValidationMarker.M1.KeyPath}\\{ValidationMarker.M1.ValueName} = {(value is null ? "<absent>" : $"\"{value}\"")}");
    return value == ValidationMarker.M1.Value.Data && OwnedHivesLoaded() == 0;
}

static bool HostCheck()
{
    using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ImageForge\Validation");
    var value = key?.GetValue(ValidationMarker.M1.ValueName);
    Log($"Host HKLM\\SOFTWARE\\ImageForge\\Validation\\Milestone = {(value is null ? "<absent>" : $"\"{value}\"")} (expected on the servicing VM: <absent>)");
    return value is null & OwnedHivesLoaded() == 0;
}

static int OwnedHivesLoaded()
{
    var owned = Registry.LocalMachine.GetSubKeyNames().Where(HiveLoadName.IsOwned).ToList();
    Log(owned.Count == 0 ? "Loaded ImageForge hives: none" : $"Loaded ImageForge hives: {string.Join(", ", owned)}");
    return owned.Count;
}

static bool Mounts()
{
    using var dism = new DismImages();
    PrintMounts();
    return true;
}

// Requires an active DismImages session; DismApi must not be initialized twice.
static void PrintMounts()
{
    var mounts = DismImages.GetMountedImages();
    if (mounts.Count == 0)
    {
        Log("DISM mounted images: none");
    }

    foreach (var m in mounts)
    {
        Log($"DISM mount: {m.MountPath} <- {m.ImageFile} index {m.Index} ({m.Mode}, {m.Status})");
    }
}

static bool Unmount(string mountDir, string mode)
{
    if (mode is not ("commit" or "discard"))
    {
        Log("Mode must be commit or discard.");
        return false;
    }

    using var dism = new DismImages();
    var last = -1;
    DismImages.Unmount(Path.GetFullPath(mountDir), commit: mode == "commit", p =>
    {
        if (p / 10 != last / 10)
        {
            last = p;
            Log($"unmount {p}%");
        }
    });
    Log($"Unmounted {mountDir} ({mode})");
    PrintMounts();
    return true;
}

static async Task<bool> Export(string source, int index, string destination)
{
    var arguments = DismCliArguments.ExportImage(Path.GetFullPath(source), index, Path.GetFullPath(destination));
    Log($"{DismCli.TrustedPath} {string.Join(' ', arguments)}");
    var result = await DismCli.RunAsync(arguments, line => Console.WriteLine("  " + line), CancellationToken.None).ConfigureAwait(false);
    Log($"Dism.exe exit code {result.ExitCode}");
    return result.ExitCode == 0;
}
