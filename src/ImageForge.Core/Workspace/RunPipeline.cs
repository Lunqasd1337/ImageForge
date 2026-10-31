using ImageForge.Core.Drivers;
using ImageForge.Core.Updates;
using ImageForge.Core.Execution;
using ImageForge.Core.Features;
using ImageForge.Core.Planning;

namespace ImageForge.Core.Workspace;

/// <summary>Deliberate failure points for the manual failure experiments; never set by the product UI.</summary>
public enum InjectedFailure
{
    None,
    AfterMount,
    BeforePublish,
}

public sealed record RunRequest(string SourceDir, string OutputDir, int SourceIndex, ExecutionPlan Plan, InjectedFailure Failure = InjectedFailure.None)
{
    /// <summary>How many images install.wim holds; all are exported in order, only <see cref="SourceIndex"/> is serviced.</summary>
    public int ImageCount { get; init; } = 1;
}

public sealed record RunResult(Guid RunId, RunStatus Status, string? FailedStep, string? Error, string? PublishedPath);

public sealed class InjectedFailureException(string step) : Exception($"Injected failure at '{step}'.");

public sealed class SourceChangedException(IReadOnlyList<ManifestDifference> differences)
    : IOException($"The source changed during the run: {string.Join(", ", differences.Select(d => $"{d.Change} {d.RelativePath}"))}.");

/// <summary>
/// M1 run: verified copy of the source → working image → mount → offline registry plan → commit → export →
/// assembled media in a staging sibling of the output → source re-check → publication.
/// Any failure releases owned resources, keeps the source and the previous result, and marks the target for rebuild.
/// </summary>
public sealed class RunPipeline(IWorkspacePaths paths, IImageServicing servicing, Func<Guid> newId)
{
    public const string InstallImage = "sources/install.wim";

    public const string BootImage = "sources/boot.wim";

    /// <summary>
    /// Setup files that Microsoft's media update recipe copies from the serviced Setup image of boot.wim into the media's
    /// sources; after the cumulative update they are newer there than on the media.
    /// </summary>
    public static readonly IReadOnlyList<string> SetupFiles = ["setup.exe", "setuphost.exe"];

    /// <summary>Where the recovery environment lives inside a mounted Windows image.</summary>
    public static readonly string WinReInImage = Path.Combine("Windows", "System32", "Recovery", "Winre.wim");

    // ERROR_MOD_NOT_FOUND: Microsoft documents it as expected when the cumulative update is added to WinRE.
    private const int ModuleNotFound = unchecked((int)0x8007007E);

    /// <summary>Called before each step starts; the experiment tool uses it to pause before publication.</summary>
    public Func<string, CancellationToken, Task>? BeforeStep { get; init; }

    public Action<string>? Log { get; init; }

    public async Task<RunResult> RunAsync(RunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(paths.Root);
        var store = new StateStore<WorkspaceState>(paths.StateFile);
        var state = LoadForRun(store, request.Plan.WorkspaceId);
        if (ImageInspection.LeftBehind(paths))
        {
            throw new WorkspaceRecoveryRequiredException("An earlier inspection left its mount behind; recover the workspace first.");
        }

        var runId = newId();
        var imageId = request.Plan.ImageId;
        var run = paths.Run(runId);
        var mountDir = paths.Mount(imageId);
        var publication = PublicationPaths.For(request.OutputDir, runId);
        state = state with
        {
            Target = TargetStatus.InProgress,
            LastRun = new RunRecord(runId, RunStatus.Running, publication.Output, imageId, null, null),
        };
        store.Save(state);

        using var journal = RunJournal.Open(paths.Journal(runId), runId);
        var context = new StepContext(journal, BeforeStep, Log);
        var mountAttempted = false;
        var mounted = false;
        ExecutionReport? registry = null;

        try
        {
            var source = await context.Step("source-check", () => SourceManifest.ComputeAsync(request.SourceDir, ct), ct).ConfigureAwait(false);
            if (!source.Entries.Any(e => string.Equals(e.RelativePath, InstallImage, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(@"The source has no sources\install.wim; M1 supports only a WIM source.");
            }

            var baseManifest = await context.Step("base-copy", () => PrepareBaseAsync(paths, request.SourceDir, source, state.SourceFingerprint, Log, ct), ct).ConfigureAwait(false);
            state = state with { SourceFingerprint = source.Fingerprint };
            store.Save(state);

            var working = Path.Combine(run.Working, "install.wim");
            await context.Step("working-copy", async () =>
            {
                Directory.CreateDirectory(run.Working);
                var copied = await TreeCopy.CopyFileAsync(Path.Combine(paths.Base, "sources", "install.wim"), working, ct).ConfigureAwait(false);
                var expected = source.Entries.Single(e => string.Equals(e.RelativePath, InstallImage, StringComparison.OrdinalIgnoreCase));
                if (copied.Length != expected.Length || !string.Equals(copied.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    // The base copy was reused on metadata alone; damaged content must not be reused again.
                    TryDeleteDirectory(paths.Base);
                    throw new IOException($@"The base copy of {InstallImage} does not match the source; it was deleted and will be copied again.");
                }

                return true;
            }, ct).ConfigureAwait(false);

            await context.Step("mount", async () =>
            {
                PrepareEmptyDirectory(mountDir);
                mountAttempted = true;
                await servicing.MountAsync(working, request.SourceIndex, mountDir, ct).ConfigureAwait(false);
                mounted = true;
                return true;
            }, ct).ConfigureAwait(false);
            if (request.Plan.Capabilities is { } capabilities)
            {
                await context.Step("capabilities", () => ChangeCapabilitiesAsync(capabilities, mountDir, ct), ct).ConfigureAwait(false);
            }

            if (request.Plan.Features is { } features)
            {
                await context.Step("features", () => ChangeFeaturesAsync(features, mountDir, ct), ct).ConfigureAwait(false);
            }

            if (request.Plan.Updates is { } updates)
            {
                await context.Step("updates", () => AddUpdatesAsync(updates, mountDir, ct), ct).ConfigureAwait(false);
            }

            if (request.Plan.Drivers is { } drivers)
            {
                await context.Step("drivers", () => ChangeDriversAsync(drivers, mountDir, ct), ct).ConfigureAwait(false);
            }

            // Plan, section 11 (install): final LCU, then drivers and removals, cleanup, then .NET.
            if (request.Plan.AppxRemovals.Count > 0)
            {
                await context.Step("appx", () => RemoveAppxAsync(request.Plan, mountDir, ct), ct).ConfigureAwait(false);
            }

            if (request.Plan.Cleanup is { } cleanup)
            {
                await context.Step("cleanup", () => CleanupAsync(cleanup, mountDir, ct), ct).ConfigureAwait(false);
            }

            if (request.Plan.DotNetUpdates is { } dotNet)
            {
                await context.Step("dotnet", () => AddUpdatesAsync(dotNet, mountDir, ct), ct).ConfigureAwait(false);
            }

            if (request.Plan.WinRe is { } winRe)
            {
                await context.Step("winre", () => ServiceWinReAsync(winRe, mountDir, run, ct), ct).ConfigureAwait(false);
            }

            await context.Step("registry", () =>
            {
                Inject(request, InjectedFailure.AfterMount, "registry");
                registry = servicing.ApplyRegistryPlan(runId, request.Plan, mountDir, ct);
                return registry.Outcome == ExecutionOutcome.Completed
                    ? Task.FromResult(true)
                    : throw new InvalidOperationException($"Registry plan {registry.Outcome} at step {registry.FailedStepIndex}: {registry.Error}");
            }, ct).ConfigureAwait(false);

            await context.Step("unmount-commit", async () =>
            {
                await servicing.UnmountAsync(mountDir, commit: true, ct).ConfigureAwait(false);
                mounted = false;
                mountAttempted = false;
                TryDeleteEmptyDirectory(mountDir);
                return true;
            }, ct).ConfigureAwait(false);

            var completed = Path.Combine(run.Completed, "install.wim");
            var completedEntry = await context.Step("export", async () =>
            {
                Directory.CreateDirectory(run.Completed);

                // DISM appends to an existing destination, so a repeated step must not find a partial file.
                File.Delete(completed);

                // Every edition goes into the result in its order; the others are exported from the working copy unchanged.
                for (var index = 1; index <= request.ImageCount; index++)
                {
                    if (request.ImageCount > 1)
                    {
                        Log?.Invoke($"export image {index} of {request.ImageCount}{(index == request.SourceIndex ? " (serviced)" : string.Empty)}");
                    }

                    await servicing.ExportAsync(working, index, completed, bootable: false, ct).ConfigureAwait(false);
                }

                var exported = await SourceManifest.ComputeAsync(run.Completed, ct).ConfigureAwait(false);
                return exported.Entries.Single() with { RelativePath = InstallImage };
            }, ct).ConfigureAwait(false);

            // Media files other than install.wim that the run replaced, as a tree under the run folder.
            var replacedMedia = Path.Combine(run.Root, "media");
            IReadOnlyList<ManifestEntry> replaced = [];
            if (request.Plan.Boot is { } boot)
            {
                replaced = await context.Step("boot", () => ServiceBootAsync(boot, replacedMedia, run, ct), ct).ConfigureAwait(false);
            }

            state = state with { Target = TargetStatus.Completed };
            store.Save(state);

            await context.Step("assemble", async () =>
            {
                if (Directory.Exists(publication.Staging))
                {
                    throw new IOException($"Staging folder '{publication.Staging}' already exists.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(publication.Staging)!);
                // Hashing while copying checks every staged file against the source manifest, which also covers a
                // base copy that was reused on metadata alone.
                var staged = (await TreeCopy.CopyAsync(paths.Base, publication.Staging, [InstallImage, .. replaced.Select(r => r.RelativePath)], ct).ConfigureAwait(false)).ToList();
                Directory.CreateDirectory(Path.Combine(publication.Staging, "sources"));
                var image = await TreeCopy.CopyFileAsync(completed, Path.Combine(publication.Staging, "sources", "install.wim"), ct).ConfigureAwait(false);
                staged.Add(image with { RelativePath = InstallImage });
                foreach (var entry in replaced)
                {
                    var file = await TreeCopy.CopyFileAsync(Path.Combine(replacedMedia, entry.RelativePath), Path.Combine(publication.Staging, entry.RelativePath), ct).ConfigureAwait(false);
                    staged.Add(file with { RelativePath = entry.RelativePath });
                }

                var replacements = replaced.Append(completedEntry).ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
                var expected = baseManifest.Entries
                    .Select(e => replacements.GetValueOrDefault(e.RelativePath) ?? e)
                    .Concat(replaced.Where(r => !baseManifest.Entries.Any(e => string.Equals(e.RelativePath, r.RelativePath, StringComparison.OrdinalIgnoreCase))))
                    .ToList();
                var differences = SourceManifest.Compare(new SourceManifest(expected, string.Empty), new SourceManifest(staged, string.Empty));
                return differences.Count == 0
                    ? true
                    : throw new IOException($"Assembled result does not match: {string.Join(", ", differences.Select(d => $"{d.Change} {d.RelativePath}"))}.");
            }, ct).ConfigureAwait(false);

            await context.Step("source-recheck", async () =>
            {
                // Owner decision (28.10.2026): size and time for every file, full hash only for install.wim.
                var differences = await SourceManifest.CheckMetadataAsync(request.SourceDir, source, [InstallImage], ct).ConfigureAwait(false);
                return differences.Count == 0 ? true : throw new SourceChangedException(differences);
            }, ct).ConfigureAwait(false);

            await context.Step("publish", () =>
            {
                Inject(request, InjectedFailure.BeforePublish, "publish");
                ResultPublisher.Publish(publication, move => journal.Append(JournalEventKind.StepProgress, "publish", move));
                return Task.FromResult(true);
            }, ct).ConfigureAwait(false);

            TryDeleteDirectory(run.Root);
            state = state with { LastRun = state.LastRun! with { Status = RunStatus.Published } };
            store.Save(state);
            return new RunResult(runId, RunStatus.Published, null, null, publication.Output);
        }
#pragma warning disable CA1031 // Every failure must release owned resources and be recorded, whatever its type.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var failedStep = context.CurrentStep;
            journal.Append(JournalEventKind.StepFailed, failedStep ?? "run", $"{ex.GetType().Name}: {ex.Message}");
            Log?.Invoke($"FAILED at {failedStep}: {ex.Message}");

            var problems = new List<string>();
            if (registry?.RequiresRecovery == true)
            {
                problems.Add($"hives left loaded: {string.Join(", ", registry.HivesLeftLoaded)}");
            }

            if (request.Plan.Boot is not null && await IsMountedSafeAsync(paths.BootMount).ConfigureAwait(false))
            {
                await Release(problems, "unmount-boot-discard", async () =>
                {
                    await servicing.UnmountAsync(paths.BootMount, commit: false, CancellationToken.None).ConfigureAwait(false);
                    TryDeleteEmptyDirectory(paths.BootMount);
                }).ConfigureAwait(false);
            }

            if (request.Plan.WinRe is not null && await IsMountedSafeAsync(paths.WinReMount).ConfigureAwait(false))
            {
                await Release(problems, "unmount-winre-discard", async () =>
                {
                    await servicing.UnmountAsync(paths.WinReMount, commit: false, CancellationToken.None).ConfigureAwait(false);
                    TryDeleteEmptyDirectory(paths.WinReMount);
                }).ConfigureAwait(false);
            }

            if (mounted || (mountAttempted && await IsMountedSafeAsync(mountDir).ConfigureAwait(false)))
            {
                await Release(problems, "unmount-discard", async () =>
                {
                    await servicing.UnmountAsync(mountDir, commit: false, CancellationToken.None).ConfigureAwait(false);
                    TryDeleteEmptyDirectory(mountDir);
                }).ConfigureAwait(false);
            }

            await Release(problems, "publication-recovery", () =>
            {
                var action = ResultPublisher.Recover(publication);
                Log?.Invoke($"publication recovery: {action}");
                return action == PublicationRecoveryAction.RecoveryRequired
                    ? throw new IOException("Output, previous result and staging all exist.")
                    : Task.CompletedTask;
            }).ConfigureAwait(false);

            await Release(problems, "delete-run-folder", () =>
            {
                TryDeleteDirectory(run.Root, throwOnError: true);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            var status = problems.Count == 0 ? RunStatus.Failed : RunStatus.RecoveryRequired;
            var error = problems.Count == 0 ? ex.Message : $"{ex.Message} Cleanup problems: {string.Join("; ", problems)}";
            state = state with
            {
                Target = TargetStatus.NeedsRebuild,
                LastRun = state.LastRun! with { Status = status, FailedStep = failedStep, Error = error },
            };
            store.Save(state);
            return new RunResult(runId, status, failedStep, error, null);
        }
    }

    // The removals were resolved from an inventory of the verified copy; the image mounted here must list exactly the
    // same packages, or the plan no longer describes it.
    private async Task<bool> RemoveAppxAsync(ExecutionPlan plan, string mountDir, CancellationToken ct)
    {
        var before = await servicing.GetProvisionedAppxAsync(mountDir, ct).ConfigureAwait(false);
        if (before.Problems.Count > 0)
        {
            throw new InvalidDataException($"The provisioned package list cannot be read: {string.Join("; ", before.Problems)}");
        }

        if (before.Fingerprint != plan.AppxInventory)
        {
            throw new InvalidDataException("The provisioned packages changed since the preview; plan again.");
        }

        foreach (var removal in plan.AppxRemovals)
        {
            Log?.Invoke($"remove {removal.Package.FullName}");
            await servicing.RemoveProvisionedAppxAsync(mountDir, removal.Package, ct).ConfigureAwait(false);
        }

        var after = await servicing.GetProvisionedAppxAsync(mountDir, ct).ConfigureAwait(false);
        var left = plan.AppxRemovals.Where(r => after.Packages.Any(p => p.Identity == r.Package)).Select(r => r.Package.FullName).ToList();
        return after.Problems.Count == 0 && left.Count == 0
            ? true
            : throw new InvalidDataException(left.Count > 0
                ? $"Still provisioned after removal: {string.Join(", ", left)}."
                : $"The provisioned package list cannot be read after removal: {string.Join("; ", after.Problems)}");
    }

    // DISM adds what a capability needs (Language.Speech brings Language.Basic and TextToSpeech) without saying so in
    // advance, so the whole list is compared afterwards and logged, as with features.
    private async Task<bool> ChangeCapabilitiesAsync(CapabilityPlan plan, string mountDir, CancellationToken ct)
    {
        var before = await servicing.GetCapabilitiesAsync(mountDir, ct).ConfigureAwait(false);
        if (before.Problems.Count > 0 || before.Fingerprint != plan.Inventory)
        {
            throw new InvalidDataException(before.Problems.Count > 0
                ? $"The capability list cannot be read: {string.Join("; ", before.Problems)}"
                : "The capabilities changed since the preview; plan again.");
        }

        foreach (var capability in plan.Remove)
        {
            Log?.Invoke($"remove capability {capability.FullName}");
            await servicing.RemoveCapabilityAsync(mountDir, capability, ct).ConfigureAwait(false);
        }

        if (plan.Add.Count > 0)
        {
            var source = plan.Source ?? throw new InvalidDataException("The plan adds capabilities without a Features on Demand folder.");
            if (ImageInspection.FodFolderProblem(source) is { } problem)
            {
                throw new InvalidDataException($"The Features on Demand folder {source} can no longer be used: {problem}");
            }

            foreach (var capability in plan.Add)
            {
                Log?.Invoke($"add capability {capability.FullName} from {source}");
                await servicing.AddCapabilityAsync(mountDir, capability, source, ct).ConfigureAwait(false);
            }
        }

        var after = await servicing.GetCapabilitiesAsync(mountDir, ct).ConfigureAwait(false);
        if (after.Problems.Count > 0)
        {
            throw new InvalidDataException($"The capability list cannot be read after the change: {string.Join("; ", after.Problems)}");
        }

        var was = before.Capabilities.ToDictionary(c => c.Identity.FullName, c => c.RawState, StringComparer.OrdinalIgnoreCase);
        foreach (var capability in after.Capabilities.Where(c => was.GetValueOrDefault(c.Identity.FullName) != c.RawState))
        {
            var requested = plan.Remove.Contains(capability.Identity) || plan.Add.Contains(capability.Identity);
            Log?.Invoke($"{(requested ? "capability" : "also changed by DISM")} {capability.Identity.FullName}: {was.GetValueOrDefault(capability.Identity.FullName) ?? "-"} -> {capability.RawState}");
        }

        var wrong = plan.Remove.Where(r => after.Capabilities.Any(c => c.Identity == r && c.State == CapabilityState.Installed))
            .Concat(plan.Add.Where(a => !after.Capabilities.Any(c => c.Identity == a && c.State is CapabilityState.Installed or CapabilityState.Pending)))
            .Select(c => c.FullName)
            .ToList();
        return wrong.Count == 0 ? true : throw new InvalidDataException($"Capabilities not in the planned state: {string.Join(", ", wrong)}.");
    }

    // A file is added only while it still has the hash the preview showed; DISM checks its signature and applicability.
    // The package list is read after each file, so the log shows what each one brought. DISM adds a package it already
    // holds without any change, so that is logged, not failed.
    private async Task<bool> AddUpdatesAsync(UpdatePlan plan, string mountDir, CancellationToken ct)
    {
        var changed = plan.Add.Where(a => UpdateFolder.CurrentHash(plan.Source, a.FileName) != a.Sha256).Select(a => a.FileName).ToList();
        if (changed.Count > 0)
        {
            throw new InvalidDataException($"Update packages changed or went missing since the preview: {string.Join(", ", changed)}.");
        }

        var current = await ReadPackagesAsync(mountDir, ct).ConfigureAwait(false);
        Log?.Invoke($"packages: {current.Packages.Count}; rollup {current.Rollup ?? "-"}, servicing stack {current.ServicingStack ?? "-"}");
        foreach (var addition in plan.Add)
        {
            Log?.Invoke($"add update {addition.Description}");
            await servicing.AddPackageAsync(mountDir, UpdateFolder.Full(plan.Source, addition.FileName), ct).ConfigureAwait(false);
            var next = await ReadPackagesAsync(mountDir, ct).ConfigureAwait(false);
            var change = PackageInventory.Compare(current, next);
            Log?.Invoke(change.IsEmpty
                ? $"update {addition.FileName}: no change in the image (DISM already holds this package)"
                : $"update {addition.FileName}: {change.Added.Count} new, {change.Superseded} superseded; rollup {next.Rollup ?? "-"}, servicing stack {next.ServicingStack ?? "-"}");
            foreach (var name in change.Added.Where(n => n.StartsWith("Package_for_", StringComparison.OrdinalIgnoreCase)))
            {
                Log?.Invoke("  new " + name);
            }

            current = next;
        }

        return true;
    }

    // The recipe from S2-TARGETS-INVENTORY: Winre.wim is taken out of the mounted image, serviced in its own mount
    // (packages, cleanup with /ResetBase, commit), exported to drop what the commit left behind, and put back.
    private async Task<bool> ServiceWinReAsync(WinRePlan plan, string mountDir, RunLayout run, CancellationToken ct)
    {
        var changed = plan.Add.Where(a => UpdateFolder.CurrentHash(plan.Source, a.FileName) != a.Sha256).Select(a => a.FileName).ToList();
        if (changed.Count > 0)
        {
            throw new InvalidDataException($"Update packages changed or went missing since the preview: {string.Join(", ", changed)}.");
        }

        var inImage = Path.Combine(mountDir, WinReInImage);
        if (!File.Exists(inImage))
        {
            throw new InvalidDataException($@"The image has no {WinReInImage}; WinRE cannot be serviced.");
        }

        var folder = Path.Combine(run.Root, "winre");
        var copy = Path.Combine(folder, "winre.wim");
        var exported = Path.Combine(folder, "winre-export.wim");
        Directory.CreateDirectory(folder);
        File.Copy(inImage, copy, overwrite: true);
        File.SetAttributes(copy, FileAttributes.Normal);
        var before = new FileInfo(copy).Length;
        Log?.Invoke($"winre: {WinReInImage}, {Megabytes(before)} MB");

        var winReMount = paths.WinReMount;
        PrepareEmptyDirectory(winReMount);
        await servicing.MountAsync(copy, 1, winReMount, ct).ConfigureAwait(false);
        var current = await ReadPackagesAsync(winReMount, ct).ConfigureAwait(false);
        Log?.Invoke($"winre packages: {current.Packages.Count}; servicing stack {current.ServicingStack ?? "-"}");
        foreach (var addition in plan.Add)
        {
            Log?.Invoke($"winre add update {addition.Description}");
            try
            {
                await servicing.AddPackageAsync(winReMount, UpdateFolder.Full(plan.Source, addition.FileName), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex.HResult == ModuleNotFound && addition.FileName.EndsWith(".msu", StringComparison.OrdinalIgnoreCase))
            {
                Log?.Invoke($"winre update {addition.FileName}: 0x8007007E, expected for a cumulative update in WinRE; continuing");
            }

            var next = await ReadPackagesAsync(winReMount, ct).ConfigureAwait(false);
            var change = PackageInventory.Compare(current, next);
            Log?.Invoke(change.IsEmpty
                ? $"winre update {addition.FileName}: no change"
                : $"winre update {addition.FileName}: {change.Added.Count} new, {change.Superseded} superseded; servicing stack {next.ServicingStack ?? "-"}");
            foreach (var name in change.Added.Where(n => n.StartsWith("Package_for_", StringComparison.OrdinalIgnoreCase)))
            {
                Log?.Invoke("  new " + name);
            }

            current = next;
        }

        Log?.Invoke("winre cleanup component store (ResetBase)");
        await servicing.CleanupComponentStoreAsync(winReMount, resetBase: true, ct).ConfigureAwait(false);
        var cleaned = await ReadPackagesAsync(winReMount, ct).ConfigureAwait(false);
        Log?.Invoke($"winre cleanup: packages {current.Packages.Count} -> {cleaned.Packages.Count}");
        await servicing.UnmountAsync(winReMount, commit: true, ct).ConfigureAwait(false);
        TryDeleteEmptyDirectory(winReMount);

        File.Delete(exported);
        await servicing.ExportAsync(copy, 1, exported, bootable: false, ct).ConfigureAwait(false);
        var committed = new FileInfo(copy).Length;
        var after = new FileInfo(exported).Length;

        // The file in the image is replaced as a whole; a WOF-compressed original is a reparse point that is deleted first.
        File.SetAttributes(inImage, FileAttributes.Normal);
        File.Delete(inImage);
        File.Copy(exported, inImage);
        File.SetAttributes(inImage, FileAttributes.Archive);
        Log?.Invoke($"winre: {Megabytes(before)} MB -> commit {Megabytes(committed)} MB -> export {Megabytes(after)} MB, put back into the image");
        TryDeleteDirectory(folder);
        return true;
    }

    private static long Megabytes(long bytes) => bytes / (1024 * 1024);

    // The recipe from S2-TARGETS-INVENTORY: every image of a boot.wim copy gets the packages, a cleanup with /ResetBase
    // and a commit; the setup files of the boot image (Windows Setup) are taken for the media; then all images are
    // exported in order and the boot image stays the bootable one. Returns the media files to replace, under mediaDir.
    private async Task<IReadOnlyList<ManifestEntry>> ServiceBootAsync(BootPlan plan, string mediaDir, RunLayout run, CancellationToken ct)
    {
        var changed = plan.Add.Where(a => UpdateFolder.CurrentHash(plan.Source, a.FileName) != a.Sha256).Select(a => a.FileName).ToList();
        if (changed.Count > 0)
        {
            throw new InvalidDataException($"Update packages changed or went missing since the preview: {string.Join(", ", changed)}.");
        }

        var source = Path.Combine(paths.Base, "sources", "boot.wim");
        if (!File.Exists(source))
        {
            throw new InvalidDataException(@"The source has no sources\boot.wim; it cannot be serviced.");
        }

        var info = ReadWim(source);
        var bootIndex = info.BootIndex > 0 ? info.BootIndex : info.ImageCount;
        Log?.Invoke($"boot.wim: {info.ImageCount} images, boot index {info.BootIndex}, {Megabytes(new FileInfo(source).Length)} MB");

        var folder = Path.Combine(run.Root, "boot");
        var working = Path.Combine(folder, "boot.wim");
        TryDeleteDirectory(mediaDir);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(mediaDir, "sources"));
        File.Copy(source, working, overwrite: true);
        File.SetAttributes(working, FileAttributes.Normal);

        var mount = paths.BootMount;
        for (var index = 1; index <= info.ImageCount; index++)
        {
            Log?.Invoke($"boot image {index} of {info.ImageCount}: {info.Images.ElementAtOrDefault(index - 1)?.Name ?? "-"}");
            PrepareEmptyDirectory(mount);
            await servicing.MountAsync(working, index, mount, ct).ConfigureAwait(false);
            var current = await ReadPackagesAsync(mount, ct).ConfigureAwait(false);
            Log?.Invoke($"boot packages: {current.Packages.Count}; rollup {current.Rollup ?? "-"}, servicing stack {current.ServicingStack ?? "-"}");
            foreach (var addition in plan.Add)
            {
                Log?.Invoke($"boot add update {addition.Description}");
                await servicing.AddPackageAsync(mount, UpdateFolder.Full(plan.Source, addition.FileName), ct).ConfigureAwait(false);
                var next = await ReadPackagesAsync(mount, ct).ConfigureAwait(false);
                var change = PackageInventory.Compare(current, next);
                Log?.Invoke(change.IsEmpty
                    ? $"boot update {addition.FileName}: no change"
                    : $"boot update {addition.FileName}: {change.Added.Count} new, {change.Superseded} superseded; rollup {next.Rollup ?? "-"}, servicing stack {next.ServicingStack ?? "-"}");
                current = next;
            }

            Log?.Invoke("boot cleanup component store (ResetBase)");
            await servicing.CleanupComponentStoreAsync(mount, resetBase: true, ct).ConfigureAwait(false);
            var cleaned = await ReadPackagesAsync(mount, ct).ConfigureAwait(false);
            Log?.Invoke($"boot cleanup: packages {current.Packages.Count} -> {cleaned.Packages.Count}");
            if (index == bootIndex)
            {
                foreach (var name in SetupFiles)
                {
                    var serviced = Path.Combine(mount, "sources", name);
                    if (File.Exists(serviced))
                    {
                        var media = Path.Combine(paths.Base, "sources", name);
                        File.Copy(serviced, Path.Combine(mediaDir, "sources", name), overwrite: true);
                        Log?.Invoke($"setup file {name}: media {FileVersion(media)} -> boot.wim {FileVersion(serviced)}");
                    }
                }
            }

            await servicing.UnmountAsync(mount, commit: true, ct).ConfigureAwait(false);
            TryDeleteEmptyDirectory(mount);
        }

        var exported = Path.Combine(mediaDir, "sources", "boot.wim");
        File.Delete(exported);
        for (var index = 1; index <= info.ImageCount; index++)
        {
            Log?.Invoke($"export boot image {index} of {info.ImageCount}{(index == bootIndex ? " (bootable)" : string.Empty)}");
            await servicing.ExportAsync(working, index, exported, bootable: index == bootIndex, ct).ConfigureAwait(false);
        }

        var result = ReadWim(exported);
        if (result.ImageCount != info.ImageCount || (info.BootIndex > 0 && result.BootIndex != info.BootIndex))
        {
            throw new InvalidDataException(
                $"The exported boot.wim has {result.ImageCount} images and boot index {result.BootIndex}; expected {info.ImageCount} and {info.BootIndex}.");
        }

        Log?.Invoke($"boot.wim: {Megabytes(new FileInfo(source).Length)} MB -> {Megabytes(new FileInfo(exported).Length)} MB, boot index {result.BootIndex}");
        TryDeleteDirectory(folder);
        var replaced = await SourceManifest.ComputeAsync(mediaDir, ct).ConfigureAwait(false);
        return replaced.Entries;
    }

    private static WimInfo ReadWim(string file)
    {
        using var stream = File.OpenRead(file);
        var info = WimArchive.Read(stream);
        return info.Problem is null ? info : throw new InvalidDataException($"{file}: {info.Problem}");
    }

    private static string FileVersion(string file) =>
        File.Exists(file) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(file).FileVersion ?? "?" : "missing";

    // The package list before and after shows what the cleanup removed; superseded packages are what it can take away.
    private async Task<bool> CleanupAsync(CleanupPlan plan, string mountDir, CancellationToken ct)
    {
        var before = await ReadPackagesAsync(mountDir, ct).ConfigureAwait(false);
        Log?.Invoke($"packages: {before.Packages.Count}, superseded {Superseded(before)}");
        Log?.Invoke(plan.ResetBase ? "cleanup component store (ResetBase)" : "cleanup component store");
        await servicing.CleanupComponentStoreAsync(mountDir, plan.ResetBase, ct).ConfigureAwait(false);
        var after = await ReadPackagesAsync(mountDir, ct).ConfigureAwait(false);
        Log?.Invoke($"cleanup: packages {before.Packages.Count} -> {after.Packages.Count}, superseded {Superseded(before)} -> {Superseded(after)}; " +
            $"rollup {after.Rollup ?? "-"}, servicing stack {after.ServicingStack ?? "-"}");
        return true;
    }

    private static int Superseded(PackageInventory packages) =>
        packages.Packages.Count(p => string.Equals(p.State, "Superseded", StringComparison.OrdinalIgnoreCase));

    private async Task<PackageInventory> ReadPackagesAsync(string mountDir, CancellationToken ct)
    {
        var packages = await servicing.GetPackagesAsync(mountDir, ct).ConfigureAwait(false);
        return packages.Problems.Count == 0
            ? packages
            : throw new InvalidDataException($"The package list cannot be read: {string.Join("; ", packages.Problems)}");
    }

    // Removals name drivers of the image the preview read, so the list must be the same. A package is added only while
    // its files still have the hash the preview showed. DISM adds a package it already holds without a new name, so an
    // addition without one is logged, not failed.
    private async Task<bool> ChangeDriversAsync(DriverPlan plan, string mountDir, CancellationToken ct)
    {
        // DISM may give a new package the oemN.inf name of one just removed, so a driver is the name with its store path.
        static string Key(InstalledDriver d) => $"{d.PublishedName}|{d.OriginalFileName}";

        var before = await servicing.GetDriversAsync(mountDir, ct).ConfigureAwait(false);
        if (before.Problems.Count > 0 || before.Fingerprint != plan.Inventory)
        {
            throw new InvalidDataException(before.Problems.Count > 0
                ? $"The driver list cannot be read: {string.Join("; ", before.Problems)}"
                : "The drivers of the image changed since the preview; plan again.");
        }

        foreach (var removal in plan.Remove)
        {
            Log?.Invoke($"remove driver {removal.Description}{(removal.BootCritical ? " (boot-critical)" : string.Empty)}");
            await servicing.RemoveDriverAsync(mountDir, removal.PublishedName, ct).ConfigureAwait(false);
        }

        if (plan.Add.Count > 0)
        {
            var source = plan.Source ?? throw new InvalidDataException("The plan adds drivers without a driver folder.");
            var changed = plan.Add.Where(a => DriverFolder.CurrentHash(source, a.InfPath) != a.ContentHash).Select(a => a.InfPath).ToList();
            if (changed.Count > 0)
            {
                throw new InvalidDataException($"Driver packages changed or went missing since the preview: {string.Join(", ", changed)}.");
            }

            // The list is read after each addition, so every package gets exactly the names it brought.
            var current = before;
            foreach (var addition in plan.Add)
            {
                Log?.Invoke($"add driver {addition.InfPath}{(addition.ForceUnsigned ? " (ForceUnsigned)" : string.Empty)}");
                await servicing.AddDriverAsync(mountDir, DriverFolder.Full(source, addition.InfPath), addition.ForceUnsigned, ct).ConfigureAwait(false);
                var next = await servicing.GetDriversAsync(mountDir, ct).ConfigureAwait(false);
                var known = current.Drivers.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var published = next.Drivers.Where(d => !known.Contains(Key(d))).Select(d => $"{d.PublishedName} ({d.Signature})").ToList();
                Log?.Invoke(published.Count > 0
                    ? $"driver {addition.InfPath} -> {string.Join(", ", published)}"
                    : $"driver {addition.InfPath}: no new name in the image (DISM already holds this package)");
                current = next;
            }
        }

        var after = await servicing.GetDriversAsync(mountDir, ct).ConfigureAwait(false);
        if (after.Problems.Count > 0)
        {
            throw new InvalidDataException($"The driver list cannot be read after the change: {string.Join("; ", after.Problems)}");
        }

        var removed = before.Drivers.Where(d => plan.Remove.Any(r => string.Equals(r.PublishedName, d.PublishedName, StringComparison.OrdinalIgnoreCase))).ToList();
        var left = removed.Where(r => after.Drivers.Any(d => string.Equals(Key(d), Key(r), StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.PublishedName)
            .ToList();
        return left.Count == 0 ? true : throw new InvalidDataException($"Drivers still in the image after removal: {string.Join(", ", left)}.");
    }

    // The change was worked out from the features of the verified copy; this mount must list the same states. DISM
    // may turn on more than it was asked (default sub-features), so the whole list is compared afterwards and logged.
    private async Task<bool> ChangeFeaturesAsync(FeaturePlan plan, string mountDir, CancellationToken ct)
    {
        var before = await servicing.GetFeaturesAsync(mountDir, ct).ConfigureAwait(false);
        if (before.Problems.Count > 0 || before.Fingerprint != plan.Inventory)
        {
            throw new InvalidDataException(before.Problems.Count > 0
                ? $"The feature list cannot be read: {string.Join("; ", before.Problems)}"
                : "The features changed since the preview; plan again.");
        }

        if (plan.Disable.Count > 0)
        {
            Log?.Invoke($"disable {string.Join(", ", plan.Disable)}{(plan.RemovePayload ? " with /Remove" : string.Empty)}");
            await servicing.DisableFeaturesAsync(mountDir, plan.Disable, plan.RemovePayload, ct).ConfigureAwait(false);
        }

        if (plan.Enable.Count > 0)
        {
            string? source = null;
            if (plan.UsesMediaSxs)
            {
                source = ImageInspection.MediaSxs(paths);
                if (!ImageInspection.MediaHasNetFx3(paths))
                {
                    throw new InvalidDataException($"{source} no longer holds the .NET Framework 3.5 package.");
                }
            }

            Log?.Invoke($"enable {string.Join(", ", plan.Enable)}");
            await servicing.EnableFeaturesAsync(mountDir, plan.Enable, source, ct).ConfigureAwait(false);
        }

        var after = await servicing.GetFeaturesAsync(mountDir, ct).ConfigureAwait(false);
        if (after.Problems.Count > 0)
        {
            throw new InvalidDataException($"The feature list cannot be read after the change: {string.Join("; ", after.Problems)}");
        }

        var requested = plan.Disable.Concat(plan.Enable).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var change in FeatureDependencies.Compare(before, after))
        {
            Log?.Invoke($"{(requested.Contains(change.Name) ? "feature" : "also changed by DISM")} {change.Name}: {change.Before ?? "-"} -> {change.After ?? "-"}");
        }

        var wrong = plan.Enable.Where(f => after.Features.FirstOrDefault(a => a.Name == f)?.IsOn != true)
            .Concat(plan.Disable.Where(f => after.Features.FirstOrDefault(a => a.Name == f)?.IsOff != true))
            .ToList();
        return wrong.Count == 0 ? true : throw new InvalidDataException($"Features not in the planned state: {string.Join(", ", wrong)}.");
    }

    internal static WorkspaceState LoadForRun(StateStore<WorkspaceState> store, Guid workspaceId)
    {
        var loaded = store.Load();
        if (loaded.Source == StateSource.Unusable)
        {
            throw new WorkspaceRecoveryRequiredException("state.json and its backup are damaged.");
        }

        var state = loaded.State ?? new WorkspaceState(workspaceId, null, TargetStatus.Pending, null);
        return state.LastRun?.Status is RunStatus.Running or RunStatus.RecoveryRequired
            ? throw new WorkspaceRecoveryRequiredException($"Run {state.LastRun.RunId:N} did not finish ({state.LastRun.Status}); recover the workspace first.")
            : state;
    }

    internal static async Task<SourceManifest> PrepareBaseAsync(
        IWorkspacePaths paths, string sourceDir, SourceManifest source, string? baseFingerprint, Action<string>? log, CancellationToken ct)
    {
        // A reused base is checked by size and time here; its content is verified when it is copied (working-copy
        // for install.wim, assemble for the rest).
        if (baseFingerprint == source.Fingerprint && Directory.Exists(paths.Base)
            && (await SourceManifest.CheckMetadataAsync(paths.Base, source, [], ct).ConfigureAwait(false)).Count == 0)
        {
            log?.Invoke("base copy reused");
            return source;
        }

        TryDeleteDirectory(paths.BasePartial, throwOnError: true);
        var copy = SourceManifest.FromEntries(await TreeCopy.CopyAsync(sourceDir, paths.BasePartial, null, ct).ConfigureAwait(false));
        var differences = SourceManifest.Compare(source, copy);
        if (differences.Count > 0)
        {
            throw new SourceChangedException(differences);
        }

        TryDeleteDirectory(paths.Base, throwOnError: true);
        Directory.Move(paths.BasePartial, paths.Base);
        return copy;
    }

    private async Task<bool> IsMountedSafeAsync(string mountDir)
    {
        try
        {
            return await servicing.IsMountedAsync(mountDir, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Unknown mount state: assume mounted so discard is attempted.
        catch (Exception)
#pragma warning restore CA1031
        {
            return true;
        }
    }

    private async Task Release(List<string> problems, string name, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Collect every cleanup problem; one failure must not skip the rest.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            problems.Add($"{name}: {ex.Message}");
            Log?.Invoke($"cleanup {name} failed: {ex.Message}");
        }
    }

    private static void Inject(RunRequest request, InjectedFailure point, string step)
    {
        if (request.Failure == point)
        {
            throw new InjectedFailureException(step);
        }
    }

    internal static void PrepareEmptyDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new IOException($"Mount folder '{path}' is not empty.");
        }
    }

    internal static void TryDeleteEmptyDirectory(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    internal static void TryDeleteDirectory(string path, bool throwOnError = false)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException) when (!throwOnError)
        {
        }
        catch (UnauthorizedAccessException) when (!throwOnError)
        {
        }
    }

    private sealed class StepContext(RunJournal journal, Func<string, CancellationToken, Task>? beforeStep, Action<string>? log)
    {
        public string? CurrentStep { get; private set; }

        public async Task<T> Step<T>(string name, Func<Task<T>> body, CancellationToken ct)
        {
            CurrentStep = name;
            if (beforeStep is not null)
            {
                await beforeStep(name, ct).ConfigureAwait(false);
            }

            // A servicing call itself is never interrupted; cancellation takes effect between steps, so the
            // failure path can still release the mount.
            ct.ThrowIfCancellationRequested();
            journal.Append(JournalEventKind.StepStarted, name, null);
            log?.Invoke($"{name}...");
            var result = await body().ConfigureAwait(false);
            journal.Append(JournalEventKind.StepCompleted, name, null);
            return result;
        }
    }
}
