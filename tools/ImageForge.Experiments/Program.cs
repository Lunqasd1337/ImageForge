using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using ImageForge.Core.Appx;
using ImageForge.Core.Changes;
using ImageForge.Core.Drivers;
using ImageForge.Core.Execution;
using ImageForge.Core.Features;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;
using ImageForge.Core.Registry.RegFile;
using ImageForge.Core.Safety;
using ImageForge.Core.Servicing;
using ImageForge.Core.Updates;
using ImageForge.Core.Workspace;
using ImageForge.Engine.Dism;
using ImageForge.Engine.Registry;
using ImageForge.Engine.Workspace;
using Microsoft.Dism;
using Microsoft.Win32;

// Manual M1 experiments (plan, section 16). Every command prints what it did and ends with RESULT: OK / FAIL.
var commands = new Dictionary<string, (string Usage, Func<string[], Task<bool>> Run)>(StringComparer.OrdinalIgnoreCase)
{
    ["version"] = ("version", _ => Task.FromResult(PrintVersion())),
    ["hash"] = ("hash <file>", a => Hash(a[0])),
    ["info"] = ("info <install.wim>", a => Task.FromResult(Info(a[0]))),
    ["wim-header"] = ("wim-header <file.wim>", a => Task.FromResult(WimHeader(a[0]))),
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
    ["reg-apply"] = ("reg-apply <mount-dir> <file.reg>", a => Task.FromResult(RegApply(a[0], a[1]))),
    ["reg-verify"] = ("reg-verify <mount-dir> <file.reg>", a => Task.FromResult(RegVerify(a[0], a[1]))),
    ["first-logon-export"] = ("first-logon-export <file.reg> <empty-out-dir>", a => Task.FromResult(FirstLogonExport(a[0], a[1]))),
    ["appx-list"] = ("appx-list <mount-dir>", a => AppxList(a[0])),
    ["appx-remove"] = ("appx-remove <mount-dir> <PackageName>", a => AppxRemove(a[0], a[1])),
    ["feature-list"] = ("feature-list <mount-dir>", a => FeatureList(a[0])),
    ["feature-info"] = ("feature-info <mount-dir> <FeatureName>", a => FeatureInfo(a[0], a[1])),
    ["feature-parents"] = ("feature-parents <mount-dir> <FeatureName>", a => FeatureParents(a[0], a[1])),
    ["feature-enable"] = ("feature-enable <mount-dir> <FeatureName> <source-dir|none>", a => FeatureChange(a[0], a[1], a[2])),
    ["feature-disable"] = ("feature-disable <mount-dir> <FeatureName>", a => FeatureChange(a[0], a[1], null)),
    ["capability-list"] = ("capability-list <mount-dir>", a => CapabilityList(a[0])),
    ["capability-info"] = ("capability-info <mount-dir> <CapabilityName>", a => CapabilityInfo(a[0], a[1])),
    ["capability-remove"] = ("capability-remove <mount-dir> <CapabilityName>", a => CapabilityRemove(a[0], a[1])),
    ["capability-add"] = ("capability-add <mount-dir> <CapabilityName> <fod-dir>", a => CapabilityAdd(a[0], a[1], a[2])),
    ["driver-scan"] = ("driver-scan <folder>", a => Task.FromResult(DriverScan(a[0]))),
    ["driver-list"] = ("driver-list <mount-dir> third-party|all", a => Task.FromResult(DriverList(a[0], a[1]))),
    ["driver-add"] = ("driver-add <mount-dir> <file.inf> signed-only|force-unsigned", a => Task.FromResult(DriverAdd(a[0], a[1], a[2]))),
    ["driver-remove"] = ("driver-remove <mount-dir> <oemN.inf> normal|allow-boot-critical", a => Task.FromResult(DriverRemove(a[0], a[1], a[2]))),
    ["update-scan"] = ("update-scan <folder>", a => Task.FromResult(UpdateScan(a[0]))),
    ["package-list"] = ("package-list <mount-dir>", a => Task.FromResult(PackageList(a[0]))),
    ["cleanup"] = ("cleanup <mount-dir> normal|resetbase", a => CleanupAsync(a[0], a[1])),
    ["update-add"] = ("update-add <mount-dir> <file.msu|file.cab|folder> api|cli", a => UpdateAddAsync(a[0], a[1], a[2])),
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
    Log($"Dism.exe output read as code page {DismCli.OutputEncoding.CodePage}");
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

// Reads the WIM header and XML without DISM: the boot index of boot.wim and the image names, for any WIM.
static bool WimHeader(string wim)
{
    using var stream = File.OpenRead(wim);
    var info = WimArchive.Read(stream);
    Log($"{Path.GetFullPath(wim)}: {stream.Length} bytes, part {info.PartNumber}/{info.TotalParts}, images {info.ImageCount}, boot index {info.BootIndex}");
    var index = 0;
    foreach (var image in info.Images)
    {
        Log($"  image {++index}: {image.Name ?? "-"} | {image.Description ?? "-"} | files {image.FileCount?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {image.TotalBytes?.ToString(CultureInfo.InvariantCulture) ?? "-"} bytes");
    }

    if (info.Problem is { } problem)
    {
        Log("PROBLEM: " + problem);
        return false;
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
        Log($"Preview: {RegistryPreviewText.Describe(p)} ({OfflineHivePaths.Resolve(scope, p.HiveFile)})");
    }

    Log($"PlanId {plan.PlanId}, fingerprint {plan.Fingerprint}");
    var runId = Guid.NewGuid();
    var executor = new RegistryPlanExecutor(new Win32OfflineHiveLoader(runId), hiveFile => OfflineHivePaths.Resolve(scope, hiveFile), new MountFileWriter(scope.Root));
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
    return value == ValidationMarker.M1.Value.Text && OwnedHivesLoaded() == 0;
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

static async Task<AppxInventory?> ReadAppxAsync(string mountDir)
{
    var arguments = DismCliArguments.GetProvisionedAppx(Path.GetFullPath(mountDir));
    Log($"{DismCli.TrustedPath} {string.Join(' ', arguments)}");
    var result = await DismCli.RunAsync(arguments, null, CancellationToken.None).ConfigureAwait(false);
    Log($"Dism.exe exit code {result.ExitCode}");
    if (result.ExitCode != 0)
    {
        Console.WriteLine(result.Output);
        return null;
    }

    var inventory = DismAppxListParser.Parse(result.Output);
    foreach (var problem in inventory.Problems)
    {
        Log("PROBLEM: " + problem);
    }

    return inventory.Problems.Count == 0 ? inventory : null;
}

static async Task<bool> AppxList(string mountDir)
{
    if (await ReadAppxAsync(mountDir).ConfigureAwait(false) is not { } inventory)
    {
        return false;
    }

    inventory = AppxManifestStore.Attach(Path.GetFullPath(mountDir), inventory);
    foreach (var package in inventory.Packages.OrderBy(p => p.Identity.Name, StringComparer.OrdinalIgnoreCase))
    {
        var id = package.Identity;
        Console.WriteLine($"  {id.Name,-50} {id.Version,-17} {id.Architecture,-8} {id.FamilyName}");
        if (package.Facts is { DependsOn: null })
        {
            Console.WriteLine("      bundle read, own dependencies unknown (removable unless others need it): " + package.FactsProblem);
        }
        else if (package.Facts is { } facts)
        {
            Console.WriteLine($"      {(facts.IsFramework ? "framework; " : string.Empty)}needs: {(facts.DependsOn!.Count == 0 ? "-" : string.Join(", ", facts.DependsOn))}");
        }
        else
        {
            Console.WriteLine("      DEPENDENCIES UNKNOWN: " + package.FactsProblem);
        }

        if (AppxWarnings.For(id) is { } warning)
        {
            Console.WriteLine("      WARNING: " + warning);
        }
    }

    var unknown = inventory.Packages.Count(p => p.Facts?.DependsOn is null);
    Log(inventory.InboxDependencies is { } inbox
        ? $"Inbox apps need: {(inbox.Count == 0 ? "-" : string.Join(", ", inbox))}"
        : "Inbox apps: UNKNOWN (a SystemApps manifest could not be read)");
    Log($"Provisioned packages: {inventory.Packages.Count}, dependencies unknown: {unknown}");
    return true;
}

// Removes one package named exactly as appx-list printed it, then lists again to confirm it is gone.
static async Task<bool> AppxRemove(string mountDir, string packageName)
{
    if (PackageIdentity.Parse(packageName) is not { } identity)
    {
        Log($"'{packageName}' is not a valid package full name.");
        return false;
    }

    if (await ReadAppxAsync(mountDir).ConfigureAwait(false) is not { } before)
    {
        return false;
    }

    if (!before.Packages.Any(p => p.Identity == identity))
    {
        Log($"'{packageName}' is not provisioned in this image; nothing was removed.");
        return false;
    }

    if (AppxWarnings.For(identity) is { } warning)
    {
        Log("WARNING: " + warning);
    }

    var arguments = DismCliArguments.RemoveProvisionedAppx(Path.GetFullPath(mountDir), identity);
    Log($"{DismCli.TrustedPath} {string.Join(' ', arguments)}");
    var result = await DismCli.RunAsync(arguments, line => Console.WriteLine("  " + line), CancellationToken.None).ConfigureAwait(false);
    Log($"Dism.exe exit code {result.ExitCode}");
    if (result.ExitCode != 0 || await ReadAppxAsync(mountDir).ConfigureAwait(false) is not { } after)
    {
        return false;
    }

    var gone = !after.Packages.Any(p => p.Identity == identity);
    Log($"Provisioned packages: {before.Packages.Count} -> {after.Packages.Count}; '{identity.Name}' {(gone ? "removed" : "STILL PRESENT")}");
    return gone && after.Packages.Count == before.Packages.Count - 1;
}

static async Task<(int ExitCode, string Output)> DismAsync(IReadOnlyList<string> arguments, bool echo)
{
    Log($"{DismCli.TrustedPath} {string.Join(' ', arguments)}");
    var result = await DismCli.RunAsync(arguments, echo ? line => Console.WriteLine("  " + line) : null, CancellationToken.None).ConfigureAwait(false);
    Log($"Dism.exe exit code {result.ExitCode}");
    return (result.ExitCode, result.Output);
}

static async Task<FeatureInventory?> ReadFeaturesAsync(string mountDir)
{
    var (exitCode, output) = await DismAsync(DismCliArguments.GetFeatures(Path.GetFullPath(mountDir)), echo: false).ConfigureAwait(false);
    if (exitCode != 0)
    {
        Console.WriteLine(output);
        return null;
    }

    var inventory = DismFeatureListParser.Parse(output);
    foreach (var problem in inventory.Problems)
    {
        Log("PROBLEM: " + problem);
    }

    return inventory.Problems.Count == 0 ? inventory : null;
}

static async Task<CapabilityInventory?> ReadCapabilitiesAsync(string mountDir)
{
    var (exitCode, output) = await DismAsync(DismCliArguments.GetCapabilities(Path.GetFullPath(mountDir)), echo: false).ConfigureAwait(false);
    if (exitCode != 0)
    {
        Console.WriteLine(output);
        return null;
    }

    var inventory = DismCapabilityListParser.Parse(output);
    foreach (var problem in inventory.Problems)
    {
        Log("PROBLEM: " + problem);
    }

    return inventory.Problems.Count == 0 ? inventory : null;
}

static async Task<bool> FeatureList(string mountDir)
{
    if (await ReadFeaturesAsync(mountDir).ConfigureAwait(false) is not { } inventory)
    {
        return false;
    }

    foreach (var feature in inventory.Features.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"  {feature.Name,-60} {feature.State,-27} {feature.RawState}");
    }

    var counts = inventory.Features.GroupBy(f => f.State).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
    Log($"Features: {inventory.Features.Count} ({string.Join(", ", counts)})");
    foreach (var unknown in inventory.Features.Where(f => f.State == FeatureState.Unknown).Select(f => f.RawState).Distinct())
    {
        Log("UNKNOWN STATE: " + unknown);
    }

    return true;
}

static async Task<bool> CapabilityList(string mountDir)
{
    if (await ReadCapabilitiesAsync(mountDir).ConfigureAwait(false) is not { } inventory)
    {
        return false;
    }

    foreach (var capability in inventory.Capabilities.OrderBy(c => c.Identity.FullName, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"  {capability.Identity.FullName,-70} {capability.State,-11} {capability.RawState}");
    }

    var counts = inventory.Capabilities.GroupBy(c => c.State).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
    Log($"Capabilities: {inventory.Capabilities.Count} ({string.Join(", ", counts)})");
    foreach (var unknown in inventory.Capabilities.Where(c => c.State == CapabilityState.Unknown).Select(c => c.RawState).Distinct())
    {
        Log("UNKNOWN STATE: " + unknown);
    }

    return true;
}

static async Task<bool> PrintInfo(IReadOnlyList<string> arguments)
{
    var (exitCode, output) = await DismAsync(arguments, echo: false).ConfigureAwait(false);
    if (exitCode != 0 || DismInfoParser.Parse(output) is not { } fields)
    {
        Console.WriteLine(output);
        return false;
    }

    foreach (var (key, value) in fields)
    {
        Console.WriteLine($"  {key,-24} {value}");
    }

    return true;
}

static Task<bool> FeatureInfo(string mountDir, string name)
{
    if (!WindowsFeature.IsValidName(name))
    {
        Log($"'{name}' is not a valid feature name.");
        return Task.FromResult(false);
    }

    return PrintInfo(DismCliArguments.GetFeatureInfo(Path.GetFullPath(mountDir), name));
}

static Task<bool> CapabilityInfo(string mountDir, string name)
{
    if (CapabilityIdentity.Parse(name) is not { } identity)
    {
        Log($"'{name}' is not a valid capability name.");
        return Task.FromResult(false);
    }

    return PrintInfo(DismCliArguments.GetCapabilityInfo(Path.GetFullPath(mountDir), identity));
}

static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadFeatureParents(string mountDir, FeatureInventory inventory)
{
    Log($"Reading the parents of {inventory.Features.Count} features (DismGetFeatureParent)...");
    var watch = Stopwatch.StartNew();
    using var dism = new DismImages();
    var (parents, _) = DismImages.GetFeatureDetails(Path.GetFullPath(mountDir), inventory.Features.Select(f => f.Name),
        (name, ex) => Log($"PROBLEM: parents of '{name}' not read: 0x{ex.HResult:x8} {ex.Message}"));
    Log($"Parents read: {parents.Count} of {inventory.Features.Count}; features with parents: {parents.Count(p => p.Value.Count > 0)}; {watch.Elapsed.TotalSeconds:F1} s");
    return parents;
}

static void PrintChange(string verb, FeatureChange change)
{
    foreach (var problem in change.Problems)
    {
        Log("PROBLEM: " + problem);
    }

    Log(change.Features.Count == 0 && change.Problems.Count == 0
        ? $"{verb}: nothing to change"
        : $"{verb}: {string.Join(", ", change.Features)}");
    if (change.Added.Count > 0)
    {
        Log($"{verb} along with it: {string.Join(", ", change.Added)}");
    }

    if (change.NeedsSource.Count > 0)
    {
        Log($"Needs a source: {string.Join(", ", change.NeedsSource)}");
    }
}

// Shows a feature's direct parents and what enabling or disabling it would change; changes nothing.
static async Task<bool> FeatureParents(string mountDir, string name)
{
    if (!WindowsFeature.IsValidName(name))
    {
        Log($"'{name}' is not a valid feature name.");
        return false;
    }

    if (await ReadFeaturesAsync(mountDir).ConfigureAwait(false) is not { } inventory)
    {
        return false;
    }

    var parents = ReadFeatureParents(mountDir, inventory);
    if (parents.TryGetValue(name, out var direct))
    {
        Log($"Direct parents of '{name}': {(direct.Count == 0 ? "-" : string.Join(", ", direct))}");
    }

    var enable = FeatureDependencies.Enable(inventory, parents, name);
    var disable = FeatureDependencies.Disable(inventory, parents, name);
    PrintChange("Enable", enable);
    PrintChange("Disable", disable);
    return parents.Count == inventory.Features.Count && enable.Problems.Count == 0 && disable.Problems.Count == 0;
}

// Enables (with its disabled parents, never from Windows Update) or disables (with its enabled dependents) one
// feature, then lists again to confirm every feature in the change has the new state.
static async Task<bool> FeatureChange(string mountDir, string name, string? source)
{
    var enable = source is not null;
    if (!WindowsFeature.IsValidName(name))
    {
        Log($"'{name}' is not a valid feature name.");
        return false;
    }

    if (await ReadFeaturesAsync(mountDir).ConfigureAwait(false) is not { } before)
    {
        return false;
    }

    var parents = ReadFeatureParents(mountDir, before);
    var change = enable ? FeatureDependencies.Enable(before, parents, name) : FeatureDependencies.Disable(before, parents, name);
    PrintChange(enable ? "Enable" : "Disable", change);
    if (change.Problems.Count > 0 || change.Features.Count == 0)
    {
        return change.Problems.Count == 0;
    }

    string? sourceDir = null;
    if (change.NeedsSource.Count > 0)
    {
        if (source == "none")
        {
            Log("REFUSED: a feature without payload needs a source folder; nothing was changed.");
            return false;
        }

        sourceDir = Path.GetFullPath(source!);
        if (!Directory.Exists(sourceDir))
        {
            Log($"REFUSED: source folder {sourceDir} does not exist; nothing was changed.");
            return false;
        }

        if (change.NeedsSource.Contains(FeatureSources.NetFx3, StringComparer.OrdinalIgnoreCase)
            && FeatureSources.NetFx3Problem(Directory.EnumerateFiles(sourceDir).Select(Path.GetFileName)!) is { } problem)
        {
            Log($"REFUSED: {sourceDir}: {problem} Nothing was changed.");
            return false;
        }
    }

    var mount = Path.GetFullPath(mountDir);
    var (exitCode, _) = await DismAsync(
        enable ? DismCliArguments.EnableFeatures(mount, change.Features, sourceDir) : DismCliArguments.DisableFeatures(mount, change.Features),
        echo: true).ConfigureAwait(false);
    if (exitCode != 0 || await ReadFeaturesAsync(mountDir).ConfigureAwait(false) is not { } after)
    {
        return false;
    }

    var ok = true;
    foreach (var feature in change.Features)
    {
        var was = before.Features.Single(f => f.Name == feature);
        var now = after.Features.Single(f => f.Name == feature);
        var expected = enable ? now.IsOn : now.IsOff;
        ok &= expected;
        Log($"Feature '{feature}': {was.RawState} -> {now.RawState}{(expected ? string.Empty : "  UNEXPECTED")}");
    }

    foreach (var other in FeatureDependencies.Compare(before, after).Where(c => !change.Features.Contains(c.Name)))
    {
        Log($"ALSO CHANGED by DISM: '{other.Name}': {other.Before ?? "(not listed)"} -> {other.After ?? "(not listed)"}");
    }

    return ok;
}

// Removes one capability named exactly as capability-list printed it, then lists again to confirm it is gone.
static async Task<bool> CapabilityRemove(string mountDir, string name)
{
    if (CapabilityIdentity.Parse(name) is not { } identity)
    {
        Log($"'{name}' is not a valid capability name.");
        return false;
    }

    if (await ReadCapabilitiesAsync(mountDir).ConfigureAwait(false) is not { } before)
    {
        return false;
    }

    if (before.Capabilities.FirstOrDefault(c => c.Identity == identity) is not { State: CapabilityState.Installed })
    {
        Log($"Capability '{name}' is not installed in this image; nothing was removed.");
        return false;
    }

    var (exitCode, _) = await DismAsync(DismCliArguments.RemoveCapability(Path.GetFullPath(mountDir), identity), echo: true).ConfigureAwait(false);
    if (exitCode != 0 || await ReadCapabilitiesAsync(mountDir).ConfigureAwait(false) is not { } after)
    {
        return false;
    }

    var now = after.Capabilities.FirstOrDefault(c => c.Identity == identity);
    Log($"Capability '{name}': Installed -> {now?.RawState ?? "(not listed)"}");
    return now is null or { State: CapabilityState.NotPresent or CapabilityState.Pending };
}

static async Task<bool> CapabilityAdd(string mountDir, string name, string source)
{
    if (CapabilityIdentity.Parse(name) is not { } identity)
    {
        Log($"'{name}' is not a valid capability name.");
        return false;
    }

    var fod = Path.GetFullPath(source);
    var metadata = Path.Combine(fod, FeatureSources.FodMetadataFolder);
    var problem = Directory.Exists(fod)
        ? FeatureSources.FodProblem(
            Directory.EnumerateFiles(fod).Select(Path.GetFileName).OfType<string>().ToList(),
            Directory.Exists(metadata) ? [.. Directory.EnumerateFiles(metadata).Select(Path.GetFileName).OfType<string>()] : null)
        : "The folder does not exist.";
    if (problem is not null)
    {
        Log($"REFUSED: {fod}: {problem}");
        return false;
    }

    Log($"Source {fod}: {Directory.EnumerateFiles(fod, "*.cab").Count()} package files, metadata present");
    if (await ReadCapabilitiesAsync(mountDir).ConfigureAwait(false) is not { } before)
    {
        return false;
    }

    if (before.Capabilities.FirstOrDefault(c => c.Identity == identity) is not { State: CapabilityState.NotPresent })
    {
        Log($"REFUSED: capability '{name}' is not listed as Not Present in this image; nothing was added.");
        return false;
    }

    var (exitCode, _) = await DismAsync(DismCliArguments.AddCapability(Path.GetFullPath(mountDir), identity, fod), echo: true).ConfigureAwait(false);
    if (exitCode != 0 || await ReadCapabilitiesAsync(mountDir).ConfigureAwait(false) is not { } after)
    {
        return false;
    }

    var now = after.Capabilities.FirstOrDefault(c => c.Identity == identity);
    Log($"Capability '{name}': Not Present -> {now?.RawState ?? "(not listed)"}");
    var was = before.Capabilities.ToDictionary(c => c.Identity.FullName, c => c.RawState, StringComparer.OrdinalIgnoreCase);
    foreach (var other in after.Capabilities.Where(c => c.Identity != identity && was.GetValueOrDefault(c.Identity.FullName) != c.RawState))
    {
        Log($"ALSO CHANGED by DISM: {other.Identity.FullName}: {was.GetValueOrDefault(other.Identity.FullName) ?? "(not listed)"} -> {other.RawState}");
    }

    return now is { State: CapabilityState.Installed or CapabilityState.Pending };
}

// Reads one INF from disk and checks its package next to it; paths in the result are relative to root.
static DriverPackageInfo AnalyzeInf(string root, string inf)
{
    var relative = Path.GetRelativePath(root, inf);
    return DriverPackage.Analyze(relative, InfText.Decode(File.ReadAllBytes(inf)), p => File.Exists(Path.Combine(root, p.Replace('\\', Path.DirectorySeparatorChar))));
}

static void PrintPackage(DriverPackageInfo package)
{
    var inf = package.Inf;
    Log(inf is null
        ? $"{package.InfPath}: not a driver INF"
        : $"{package.InfPath}: class {inf.Class ?? "-"} {inf.ClassGuid ?? "-"} | provider {inf.Provider ?? "-"} | {inf.DriverDate ?? "-"} {inf.DriverVersion ?? "-"} | " +
          $"catalog {inf.CatalogFile ?? "(none: UNSIGNED)"} | architectures {(inf.Architectures.Count == 0 ? "-" : string.Join(",", inf.Architectures))} | {package.Files.Count} files");
    foreach (var problem in package.Problems)
    {
        Log("  PROBLEM: " + problem);
    }
}

// Every *.inf under the folder, with the package checks of plan section 12; same INF content is reported once per group.
static bool DriverScan(string folder)
{
    var root = Path.GetFullPath(folder);
    if (!Directory.Exists(root))
    {
        Log($"{root} does not exist.");
        return false;
    }

    var infs = Directory.EnumerateFiles(root, "*.inf", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList();
    var packages = infs.Select(inf => AnalyzeInf(root, inf)).ToList();
    foreach (var package in packages)
    {
        PrintPackage(package);
    }

    foreach (var same in infs.GroupBy(inf => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inf)))).Where(g => g.Count() > 1))
    {
        Log($"SAME INF CONTENT ({same.Key[..12]}): {string.Join(", ", same.Select(inf => Path.GetRelativePath(root, inf)))}");
    }

    var drivers = packages.Count(p => p.Inf is not null);
    Log($"INF files: {infs.Count}; driver packages: {drivers}; ready for x64: {packages.Count(p => p.Inf is not null && p.Problems.Count == 0)}; " +
        $"unsigned: {packages.Count(p => p.IsUnsigned)}; with problems: {packages.Count(p => p.Problems.Count > 0)}");
    return infs.Count > 0;
}

static IReadOnlyList<ImageDriver> ReadDrivers(string mountDir, bool includeInbox)
{
    var watch = Stopwatch.StartNew();
    using var dism = new DismImages();
    var drivers = DismImages.GetDrivers(Path.GetFullPath(mountDir), includeInbox);
    Log($"Drivers read (DismGetDrivers, inbox {(includeInbox ? "included" : "excluded")}): {drivers.Count}; {watch.Elapsed.TotalSeconds:F1} s");
    return drivers;
}

static void PrintDriver(ImageDriver d) =>
    Log($"{d.PublishedName} | {d.OriginalFileName} | {d.ClassName} | {d.ProviderName} | {d.Date:yyyy-MM-dd} {d.Version} | {d.Signature}" +
        $"{(d.InBox ? " | inbox" : string.Empty)}{(d.BootCritical ? " | BOOT-CRITICAL" : string.Empty)}");

static bool DriverList(string mountDir, string scope)
{
    if (scope is not ("third-party" or "all"))
    {
        Log("Scope must be third-party or all.");
        return false;
    }

    var drivers = ReadDrivers(mountDir, scope == "all");
    foreach (var driver in drivers.OrderBy(d => d.InBox).ThenBy(d => d.PublishedName, StringComparer.OrdinalIgnoreCase))
    {
        PrintDriver(driver);
    }

    Log($"Third-party: {drivers.Count(d => !d.InBox)}; inbox: {drivers.Count(d => d.InBox)}; boot-critical: {drivers.Count(d => d.BootCritical)}");
    return true;
}

// Checks the package first, adds it, and shows which published names appeared.
static bool DriverAdd(string mountDir, string infFile, string mode)
{
    if (mode is not ("signed-only" or "force-unsigned"))
    {
        Log("Mode must be signed-only or force-unsigned.");
        return false;
    }

    var inf = Path.GetFullPath(infFile);
    if (!File.Exists(inf))
    {
        Log($"{inf} does not exist.");
        return false;
    }

    var package = AnalyzeInf(Path.GetDirectoryName(inf)!, inf);
    PrintPackage(package);
    if (package.Inf is null || package.Problems.Count > 0)
    {
        Log("REFUSED: the package has problems; nothing was added.");
        return false;
    }

    if (package.IsUnsigned && mode != "force-unsigned")
    {
        Log("REFUSED: the package has no catalog; add it only with force-unsigned.");
        return false;
    }

    if (mode == "force-unsigned")
    {
        Log("WARNING: ForceUnsigned is on; DISM will not check the driver signature.");
    }

    var before = ReadDrivers(mountDir, includeInbox: false).Select(d => d.PublishedName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var watch = Stopwatch.StartNew();
    using (new DismImages())
    {
        DismImages.AddDriver(Path.GetFullPath(mountDir), inf, mode == "force-unsigned");
    }

    Log($"DismAddDriver finished in {watch.Elapsed.TotalSeconds:F1} s");
    var added = ReadDrivers(mountDir, includeInbox: false).Where(d => !before.Contains(d.PublishedName)).ToList();
    foreach (var driver in added)
    {
        Log("ADDED:");
        PrintDriver(driver);
    }

    if (added.Count == 0)
    {
        Log("No new published name appeared (DISM may treat the package as already present).");
    }

    return added.Count > 0;
}

static bool DriverRemove(string mountDir, string publishedName, string mode)
{
    if (mode is not ("normal" or "allow-boot-critical"))
    {
        Log("Mode must be normal or allow-boot-critical.");
        return false;
    }

    if (ReadDrivers(mountDir, includeInbox: false).FirstOrDefault(d => string.Equals(d.PublishedName, publishedName, StringComparison.OrdinalIgnoreCase)) is not { } driver)
    {
        Log($"REFUSED: '{publishedName}' is not a third-party driver of this image; nothing was removed.");
        return false;
    }

    PrintDriver(driver);
    if (driver.BootCritical && mode != "allow-boot-critical")
    {
        Log("REFUSED: the driver is boot-critical; removing it can leave the image unbootable. Use allow-boot-critical to remove it anyway.");
        return false;
    }

    if (driver.BootCritical)
    {
        Log("WARNING: removing a boot-critical driver.");
    }

    using (new DismImages())
    {
        DismImages.RemoveDriver(Path.GetFullPath(mountDir), driver.PublishedName);
    }

    var still = ReadDrivers(mountDir, includeInbox: false).Any(d => string.Equals(d.PublishedName, publishedName, StringComparison.OrdinalIgnoreCase));
    Log($"'{publishedName}': {(still ? "STILL LISTED" : "removed")}");
    return !still;
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
        Log($"Preview: {RegistryPreviewText.Describe(p)}");
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
    var parsed = text.Text is null ? null : new RegFileParser().Parse(text.Text);
    foreach (var d in text.Diagnostics.Concat(parsed?.Diagnostics ?? []))
    {
        Log($"line {d.Line}: {d.Code}: {d.Message}");
    }

    if (parsed is not { IsValid: true })
    {
        return false;
    }

    Log($"{parsed.Operations.Count} operations");
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

    static bool Matches(RegistryValue data, object live)
    {
        var text = data.Bytes.Length % 2 == 0 ? System.Text.Encoding.Unicode.GetString(data.Bytes) : null;
        return (data.Type, live) switch
        {
            (RegistryValueType.Sz or RegistryValueType.ExpandSz, string s) => text?.TrimEnd('\0') == s,
            (RegistryValueType.MultiSz, string[] items) => text is not null && text.TrimEnd('\0').Split('\0').Where(x => x.Length > 0).SequenceEqual(items),
            (RegistryValueType.Dword, int i) => data.Bytes.Length == 4 && BitConverter.ToInt32(data.Bytes) == i,
            (RegistryValueType.Qword, long l) => data.Bytes.Length == 8 && BitConverter.ToInt64(data.Bytes) == l,
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

static ExecutionPlan? PlanRegFile(string file)
{
    var text = new RegFileLoader().Decode(File.ReadAllBytes(file), RegFileEncoding.Auto, 1251);
    var parsed = text.Text is null ? null : new RegFileParser().Parse(text.Text);
    foreach (var d in text.Diagnostics.Concat(parsed?.Diagnostics ?? []))
    {
        Log($"line {d.Line}: {d.Code}: {d.Message}");
    }

    if (parsed is not { IsValid: true })
    {
        return null;
    }

    var result = new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid)
        .Plan(new PlanRequest(Guid.NewGuid(), 1, Guid.NewGuid(), RegFileChanges.From(parsed)));
    foreach (var d in result.Diagnostics)
    {
        Log($"PLAN ERROR change {d.ChangeIndex}: {d.Code}: {d.Message}");
    }

    foreach (var p in result.Plan?.Preview ?? [])
    {
        Log(RegistryPreviewText.Describe(p));
    }

    return result.Plan;
}

// Applies a .reg file to the offline hives of a mounted image, as a run would (no commit; unmount separately).
static bool RegApply(string mountDir, string file)
{
    if (PlanRegFile(file) is not { } plan)
    {
        return false;
    }

    var scope = WriteScope.Create(Path.GetFullPath(mountDir));
    var runId = Guid.NewGuid();
    var report = new RegistryPlanExecutor(new Win32OfflineHiveLoader(runId), hiveFile => OfflineHivePaths.Resolve(scope, hiveFile), new MountFileWriter(scope.Root))
        .Execute(plan, CancellationToken.None);
    Log($"Execution: {report.Outcome}, steps {report.CompletedSteps}/{plan.Steps.Count}" +
        (report.Error is null ? string.Empty : $", failed step {report.FailedStepIndex}: {report.Error}"));
    if (report.RequiresRecovery)
    {
        Log($"RECOVERY REQUIRED: hives still loaded: {string.Join(", ", report.HivesLeftLoaded)} (run {runId:N})");
    }

    return report.Outcome == ExecutionOutcome.Completed && OwnedHivesLoaded() == 0;
}

// Writes what a run would put into the image for HKCU\Software\Classes: the per-user file and the Active Setup
// registration as register.reg (HKLM), so the first-logon import can be tried on a live VM. Nothing is installed here.
static bool FirstLogonExport(string file, string outDir)
{
    if (PlanRegFile(file) is not { } plan)
    {
        return false;
    }

    if (plan.Steps.OfType<WriteImageFile>().SingleOrDefault() is not { } userFile)
    {
        Log(@"The file has no HKCU\Software\Classes changes; nothing to export.");
        return false;
    }

    if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
    {
        Log($"{outDir} is not empty.");
        return false;
    }

    Directory.CreateDirectory(outDir);
    var registration = plan.Preview.Where(p => p.SubKey.StartsWith(FirstLogonDelivery.ActiveSetupComponents + "\\", StringComparison.Ordinal)).ToList();
    File.WriteAllBytes(Path.Combine(outDir, Path.GetFileName(userFile.RelativePath)), userFile.Content);
    File.WriteAllBytes(Path.Combine(outDir, "register.reg"), RegFileWriter.Write(@"HKEY_LOCAL_MACHINE\SOFTWARE", registration));
    Log($"User file: {Path.GetFileName(userFile.RelativePath)} -> %SystemDrive%\\{userFile.RelativePath}");
    Log($"Active Setup: HKLM\\SOFTWARE\\{registration[0].SubKey}");
    return true;
}

// Reads the offline hives back and checks the final effect of every change in the file, in file order.
static bool RegVerify(string mountDir, string file)
{
    if (PlanRegFile(file) is not { } plan)
    {
        return false;
    }

    var scope = WriteScope.Create(Path.GetFullPath(mountDir));
    var loader = new Win32OfflineHiveLoader(Guid.NewGuid());
    var ok = true;
    foreach (var written in plan.Steps.OfType<WriteImageFile>())
    {
        var path = Path.Combine(scope.Root, written.RelativePath);
        var same = File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(written.Content);
        ok &= same;
        Log($"  file {written.RelativePath}: {(File.Exists(path) ? "present" : "absent")} {(same ? "OK" : "MISMATCH")}");
    }

    foreach (var group in plan.Preview.Where(p => !p.FirstLogon).GroupBy(p => p.Hive))
    {
        var hive = (Win32OfflineHiveLoader.LoadedHive)loader.Load(group.Key, OfflineHivePaths.Resolve(scope, group.First().HiveFile));
        try
        {
            var controlSet = group.Any(p => p.UnderCurrentControlSet) ? ControlSet.FromSelectCurrent(hive.ReadSelectCurrent()) : null;
            string Resolve(RegistryWritePreview p) => p.UnderCurrentControlSet ? (p.SubKey.Length == 0 ? controlSet! : $"{controlSet}\\{p.SubKey}") : p.SubKey;
            static bool Under(string key, string root) => key.Equals(root, StringComparison.OrdinalIgnoreCase) || key.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

            // Final expectation per key and value: later changes override earlier ones; a deleted key drops what was under it.
            var keys = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<(string Key, string Name), RegistryValue?>();
            foreach (var p in group)
            {
                var key = Resolve(p);
                switch (p.Kind)
                {
                    case RegistryChangeKind.DeleteKey:
                        foreach (var k in keys.Keys.Where(k => Under(k, key)).ToList()) { keys.Remove(k); }
                        foreach (var v in values.Keys.Where(v => Under(v.Key, key)).ToList()) { values.Remove(v); }
                        keys[key] = false;
                        break;
                    case RegistryChangeKind.CreateKey:
                        keys[key] = true;
                        break;
                    case RegistryChangeKind.SetValue:
                        keys[key] = true;
                        values[(key, p.ValueName!)] = p.NewValue;
                        break;
                    case RegistryChangeKind.DeleteValue:
                        values[(key, p.ValueName!)] = null;
                        break;
                }
            }

            foreach (var (key, present) in keys)
            {
                var actual = hive.KeyExists(key);
                ok &= actual == present;
                Log($"  {group.Key}\\{key}: key {(actual ? "present" : "absent")} {(actual == present ? "OK" : "MISMATCH")}");
            }

            foreach (var ((key, name), expected) in values)
            {
                var actual = hive.ReadValue(key, name);
                var same = expected is null ? actual is null : expected.Equals(actual);
                ok &= same;
                Log($"  {group.Key}\\{key} [{(name.Length == 0 ? "@" : name)}]: {(actual is null ? "absent" : actual.Describe())} {(same ? "OK" : "MISMATCH, expected " + (expected?.Describe() ?? "absent"))}");
            }
        }
        finally
        {
            hive.Unload();
        }
    }

    return ok && OwnedHivesLoaded() == 0;
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

static void PrintUpdate(ScannedUpdate scanned)
{
    var p = scanned.Package;
    Log($"{p.FileName}: {p.Kind} | KB {p.Kb?.ToString(CultureInfo.InvariantCulture) ?? "-"} | arch {p.Architecture ?? "-"} | " +
        $"SHA-1 in name {(p.Sha1FromName is null ? "none" : "present")} | SHA-256 {scanned.Sha256?[..16] ?? "-"} | " +
        $"{p.Container}{(p.Container == UpdateContainer.Cabinet ? $" {p.Contents.Count} entries" : string.Empty)}{(p.HasServicingStack ? " | SSU" : string.Empty)}");
    if (p.Wim is { } wim)
    {
        Log($"  WIM part {wim.PartNumber}/{wim.TotalParts}, images {wim.ImageCount}; the file list is left to DISM");
        foreach (var image in wim.Images)
        {
            Log($"  image {image.Name ?? "-"} | {image.Description ?? "-"} | files {image.FileCount?.ToString(CultureInfo.InvariantCulture) ?? "-"} | " +
                $"{image.TotalBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "-"} bytes");
        }
    }

    foreach (var entry in p.Contents)
    {
        Log($"  {entry.Name} ({entry.Size:N0} bytes){(p.Payloads.Contains(entry.Name) ? " <- payload" : string.Empty)}");
    }

    foreach (var problem in p.Problems)
    {
        Log("  PROBLEM: " + problem);
    }
}

// Every .msu and .cab under the folder with the checks of plan section 11; nothing is unpacked and DISM is not called.
static bool UpdateScan(string folder)
{
    var watch = Stopwatch.StartNew();
    var scanned = UpdateFolder.Scan(Path.GetFullPath(folder));
    if (scanned.Problem is not null)
    {
        Log($"{scanned.Folder}: {scanned.Problem}");
        return false;
    }

    foreach (var package in scanned.Packages)
    {
        PrintUpdate(package);
    }

    Log($"Packages: {scanned.Packages.Count}; MSU: {scanned.Packages.Count(p => p.Package.Kind == UpdateKind.Msu)}; " +
        $"CAB: {scanned.Packages.Count(p => p.Package.Kind == UpdateKind.Cab)}; with problems: {scanned.Packages.Count(p => p.Package.Problems.Count > 0)}; " +
        $"{watch.Elapsed.TotalSeconds:F1} s");
    return true;
}

static async Task<bool> CleanupAsync(string mountDir, string mode)
{
    if (mode is not ("normal" or "resetbase"))
    {
        Log("Mode must be normal or resetbase.");
        return false;
    }

    var before = ReadPackages(mountDir);
    var watch = Stopwatch.StartNew();
    var (exitCode, _) = await DismAsync(DismCliArguments.CleanupComponentStore(Path.GetFullPath(mountDir), mode == "resetbase"), echo: true).ConfigureAwait(false);
    Log($"Dism.exe exit code {exitCode}; cleanup took {watch.Elapsed.TotalSeconds:F1} s");
    var after = ReadPackages(mountDir);
    static int Superseded(IReadOnlyList<ImagePackage> packages) => packages.Count(p => p.State == "Superseded");
    Log($"packages {before.Count} -> {after.Count}, superseded {Superseded(before)} -> {Superseded(after)}");
    return exitCode == 0;
}

static IReadOnlyList<ImagePackage> ReadPackages(string mountDir)
{
    var watch = Stopwatch.StartNew();
    using var dism = new DismImages();
    var packages = DismImages.GetPackages(Path.GetFullPath(mountDir));
    Log($"Packages read (DismGetPackages): {packages.Count}; {watch.Elapsed.TotalSeconds:F1} s");
    return packages;
}

static void PrintImagePackage(ImagePackage p) => Log($"{p.Name} | {p.State} | {p.ReleaseType} | {p.InstallTime:yyyy-MM-dd HH:mm}");

static bool PackageList(string mountDir)
{
    var packages = ReadPackages(mountDir);
    foreach (var package in packages.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
    {
        PrintImagePackage(package);
    }

    foreach (var group in packages.GroupBy(p => p.ReleaseType).OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        Log($"{group.Key}: {group.Count()}");
    }

    foreach (var state in packages.GroupBy(p => p.State).OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        Log($"state {state.Key}: {state.Count()}");
    }

    return true;
}

// Checks the file first, adds it through the API or Dism.exe, and shows which packages appeared or changed state.
static async Task<bool> UpdateAddAsync(string mountDir, string path, string mode)
{
    if (mode is not ("api" or "cli"))
    {
        Log("Mode must be api or cli.");
        return false;
    }

    var target = Path.GetFullPath(path);
    if (Directory.Exists(target))
    {
        if (mode != "cli")
        {
            Log("REFUSED: a folder of packages is passed only to Dism.exe (cli).");
            return false;
        }

        var folder = UpdateFolder.Scan(target);
        foreach (var package in folder.Packages)
        {
            PrintUpdate(package);
        }

        if (folder.Problem is not null || folder.Packages.Any(p => p.Package.Problems.Count > 0))
        {
            Log($"REFUSED: {folder.Problem ?? "a package in the folder has problems"}; nothing was added.");
            return false;
        }
    }
    else if (File.Exists(target))
    {
        var scanned = UpdateFolder.ScanFile(Path.GetDirectoryName(target)!, Path.GetFileName(target));
        PrintUpdate(scanned);
        if (scanned.Package.Problems.Count > 0)
        {
            Log("REFUSED: the package has problems; nothing was added.");
            return false;
        }
    }
    else
    {
        Log($"{target} does not exist.");
        return false;
    }

    var before = ReadPackages(mountDir).ToDictionary(p => p.Name, p => p.State, StringComparer.OrdinalIgnoreCase);
    var watch = Stopwatch.StartNew();
    var succeeded = true;
    if (mode == "api")
    {
        try
        {
            using (new DismImages())
            {
                var last = -1;
                DismImages.AddPackage(Path.GetFullPath(mountDir), target, percent =>
                {
                    if (percent / 10 != last / 10)
                    {
                        last = percent;
                        Log($"add-package {percent}%");
                    }
                });
            }
        }
        catch (DismException ex)
        {
            Log($"DismAddPackage failed: {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
            succeeded = false;
        }
    }
    else
    {
        var (exitCode, _) = await DismAsync(DismCliArguments.AddPackage(Path.GetFullPath(mountDir), target), echo: true).ConfigureAwait(false);
        succeeded = exitCode is 0 or 3010;
        if (exitCode == 3010)
        {
            Log("Dism.exe reports 3010: a restart is required (expected offline; the package is pending until first boot).");
        }
    }

    Log($"Add finished in {watch.Elapsed.TotalSeconds:F1} s");
    var after = ReadPackages(mountDir);
    var changed = 0;
    foreach (var package in after.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
    {
        if (!before.TryGetValue(package.Name, out var was))
        {
            Log("NEW: " + package.Name + " | " + package.State + " | " + package.ReleaseType);
            changed++;
        }
        else if (was != package.State)
        {
            Log($"STATE: {package.Name}: {was} -> {package.State}");
            changed++;
        }
    }

    foreach (var gone in before.Keys.Except(after.Select(p => p.Name), StringComparer.OrdinalIgnoreCase))
    {
        Log("GONE: " + gone);
        changed++;
    }

    Log($"Packages before: {before.Count}; after: {after.Count}; new or changed: {changed}");
    return succeeded;
}
