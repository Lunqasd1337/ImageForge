using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using ImageForge.Core.Changes;
using ImageForge.Core.Execution;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;
using ImageForge.Core.Registry.RegFile;
using ImageForge.Core.Safety;
using ImageForge.Core.Servicing;
using ImageForge.Core.Workspace;
using ImageForge.Engine.Dism;
using ImageForge.Engine.Registry;
using ImageForge.Engine.Workspace;
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
    ["run"] = ("run <media-dir> <work-dir> <output-dir> none|after-mount|before-publish|pause-after-mount|pause-before-publish", a => RunPipelineAsync(a[0], a[1], a[2], a[3])),
    ["status"] = ("status <work-dir>", a => Task.FromResult(Status(a[0]))),
    ["recover"] = ("recover <work-dir>", a => RecoverAsync(a[0])),
    ["manifest"] = ("manifest <dir>", a => ManifestAsync(a[0])),
    ["reg-check"] = ("reg-check <file.reg> auto|utf16|utf8|ansi <ansi-code-page> <live-key-under-HKCU>", a => Task.FromResult(RegCheck(a[0], a[1], int.Parse(a[2], CultureInfo.InvariantCulture), a[3]))),
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
    if (!File.Exists(wim))
    {
        Log($"Image file not found: {Path.GetFullPath(wim)}");
        return false;
    }

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
    var full = Path.GetFullPath(mountDir).TrimEnd('\\');
    if (!DismImages.GetMountedImages().Any(m => string.Equals(m.MountPath.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase)))
    {
        Log($"Nothing is mounted at {full}");
        PrintMounts();
        return false;
    }

    var last = -1;
    DismImages.Unmount(full, commit: mode == "commit", p =>
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

// Experiment 4: the whole M1 run with an optional failure before publication.
static async Task<bool> RunPipelineAsync(string media, string work, string output, string mode)
{
    var failure = mode switch
    {
        "none" or "pause-after-mount" or "pause-before-publish" => InjectedFailure.None,
        "after-mount" => InjectedFailure.AfterMount,
        "before-publish" => InjectedFailure.BeforePublish,
        _ => (InjectedFailure?)null,
    };
    if (failure is null)
    {
        Log("Mode must be none, after-mount, before-publish, pause-after-mount or pause-before-publish.");
        return false;
    }

    var (source, workDir, outputDir) = (Path.GetFullPath(media), Path.GetFullPath(work), Path.GetFullPath(output));
    var problems = WorkspaceLocations.Validate(source, workDir, outputDir);
    foreach (var problem in problems)
    {
        Log($"LOCATION ERROR: {problem.Kind} {problem.Location} {problem.Other}");
    }

    if (problems.Count > 0)
    {
        return false;
    }

    var plan = new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid)
        .Plan(new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), [ValidationMarker.M1])).Plan!;
    foreach (var p in plan.Preview)
    {
        Log($"Preview: {p.SourcePath} -> offline {p.Hive} hive, key {p.SubKey}, {p.ValueName} = REG_SZ \"{p.NewValue.Data}\"");
    }

    var layout = WorkspaceLayout.Create(workDir);
    using var dism = new DismImages();
    var pipeline = new RunPipeline(layout, new DismImageServicing(dism, Log), Guid.NewGuid)
    {
        Log = Log,
        BeforeStep = (step, _) =>
        {
            if ((mode, step) is ("pause-before-publish", "publish") or ("pause-after-mount", "registry"))
            {
                Log($"PAUSED before step '{step}'.");
                Log("Now end this process in Task Manager (ImageForge.Experiments.exe -> End task). Press Enter only to continue normally.");
                Console.ReadLine();
            }

            return Task.CompletedTask;
        },
    };

    var result = await pipeline.RunAsync(new RunRequest(source, outputDir, 1, plan, failure.Value), CancellationToken.None).ConfigureAwait(false);
    Log($"Run {result.RunId:N}: {result.Status}" + (result.FailedStep is null ? string.Empty : $" at {result.FailedStep}: {result.Error}"));
    if (result.PublishedPath is not null)
    {
        Log($"Published: {result.PublishedPath}");
    }

    PrintState(layout);
    PrintMounts();
    var hives = OwnedHivesLoaded();
    var expected = failure == InjectedFailure.None ? RunStatus.Published : RunStatus.Failed;
    return result.Status == expected && hives == 0 && DismImages.GetMountedImages().Count == 0;
}

static bool Status(string work)
{
    var layout = WorkspaceLayout.Create(Path.GetFullPath(work));
    PrintState(layout);
    using var dism = new DismImages();
    PrintMounts();
    return OwnedHivesLoaded() == 0;
}

static async Task<bool> RecoverAsync(string work)
{
    var layout = WorkspaceLayout.Create(Path.GetFullPath(work));
    using var dism = new DismImages();
    var result = await new RunRecovery(layout, new DismImageServicing(dism, Log)).RecoverAsync(CancellationToken.None).ConfigureAwait(false);
    foreach (var action in result.Actions)
    {
        Log("recovery: " + action);
    }

    foreach (var problem in result.Problems)
    {
        Log("RECOVERY PROBLEM: " + problem);
    }

    Log($"Recovery: {result.Outcome}");
    PrintState(layout);
    PrintMounts();
    return result.Outcome != RecoveryOutcome.StillRequired & OwnedHivesLoaded() == 0;
}

static async Task<bool> ManifestAsync(string dir)
{
    var manifest = await SourceManifest.ComputeAsync(Path.GetFullPath(dir), CancellationToken.None).ConfigureAwait(false);
    Log($"{manifest.Entries.Count} files, fingerprint {manifest.Fingerprint.ToUpperInvariant()}  {Path.GetFullPath(dir)}");
    return true;
}

// Parses a .reg file and compares every value under HKEY_CURRENT_USER\<live-key> with the live registry (read only),
// so real regedit exports serve as reference fixtures.
static bool RegCheck(string file, string encodingName, int codePage, string liveKey)
{
    var encoding = Enum.Parse<RegFileEncoding>(encodingName, ignoreCase: true);
    var text = new RegFileLoader().Decode(File.ReadAllBytes(file), encoding, codePage);
    Log($"decoded as {text.Encoding?.ToString() ?? "-"}{(text.CodePage is { } cp ? $" (code page {cp})" : string.Empty)}");
    var parsed = text.Text is null ? null : new RegFileParser().Parse(text.Text, codePage);
    foreach (var d in text.Diagnostics.Concat(parsed?.Diagnostics ?? []))
    {
        Log($"line {d.Line}: {d.Code}: {d.Message}");
    }

    if (parsed is not { IsValid: true })
    {
        return false;
    }

    Log($"format {parsed.Format}, {parsed.Operations.Count} operations");
    const string prefix = "HKEY_CURRENT_USER\\";
    var ok = true;
    var compared = 0;
    foreach (var op in parsed.Operations)
    {
        var name = op is SetRegValue v ? (v.Name.Length == 0 ? "@" : $"\"{v.Name}\"") : string.Empty;
        Log($"  line {op.Line}: {op.GetType().Name} [{op.KeyPath}] {name} {(op is SetRegValue set ? set.Data.Describe() : string.Empty)}");
        if (op is not SetRegValue value || !value.KeyPath.StartsWith(prefix + liveKey, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        using var key = Registry.CurrentUser.OpenSubKey(value.KeyPath[prefix.Length..]);
        var live = key?.GetValue(value.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var same = live is not null && Matches(value.Data, live);
        compared++;
        ok &= same;
        Log($"    live {(same ? "matches" : $"DIFFERS: {Show(live)}")}");
    }

    Log($"{compared} values compared with HKEY_CURRENT_USER\\{liveKey}");
    return ok && compared > 0;

    static bool Matches(RegData data, object live)
    {
        var text = data.Bytes.Length % 2 == 0 ? System.Text.Encoding.Unicode.GetString(data.Bytes) : null;
        return (data.Type, live) switch
        {
            (RegValueType.Sz or RegValueType.ExpandSz, string s) => text?.TrimEnd('\0') == s,
            (RegValueType.MultiSz, string[] items) => text is not null && text.TrimEnd('\0').Split('\0').Where(x => x.Length > 0).SequenceEqual(items),
            (RegValueType.Dword, int i) => data.Bytes.Length == 4 && BitConverter.ToInt32(data.Bytes) == i,
            (RegValueType.Qword, long l) => data.Bytes.Length == 8 && BitConverter.ToInt64(data.Bytes) == l,
            (_, byte[] bytes) => bytes.AsSpan().SequenceEqual(data.Bytes),
            _ => false,
        };
    }

    static string Show(object? live) => live switch
    {
        null => "(missing)",
        byte[] b => Convert.ToHexString(b),
        string[] items => string.Join(" | ", items),
        _ => Convert.ToString(live, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}

static void PrintState(WorkspaceLayout layout)
{
    var loaded = new StateStore<WorkspaceState>(layout.StateFile).Load();
    Log($"state.json ({loaded.Source}): target {loaded.State?.Target}, last run {loaded.State?.LastRun?.RunId:N} {loaded.State?.LastRun?.Status}" +
        (loaded.State?.LastRun?.FailedStep is { } step ? $" at {step}" : string.Empty));
    if (loaded.State?.LastRun is { } run)
    {
        var journal = RunJournal.Read(layout.Journal(run.RunId));
        Log($"journal: {journal.Entries.Count} entries, corrupt {journal.IsCorrupt}, open step {journal.LastOpenStep ?? "none"}");
        foreach (var entry in journal.Entries.TakeLast(4))
        {
            Log($"  #{entry.Sequence} {entry.Kind} {entry.Step} {entry.Detail}");
        }
    }
}
