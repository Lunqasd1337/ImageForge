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

public sealed record RunRequest(string SourceDir, string OutputDir, int SourceIndex, ExecutionPlan Plan, InjectedFailure Failure = InjectedFailure.None);

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
            if (request.Plan.AppxRemovals.Count > 0)
            {
                await context.Step("appx", () => RemoveAppxAsync(request.Plan, mountDir, ct), ct).ConfigureAwait(false);
            }

            if (request.Plan.Capabilities is { } capabilities)
            {
                await context.Step("capabilities", () => RemoveCapabilitiesAsync(capabilities, mountDir, ct), ct).ConfigureAwait(false);
            }

            if (request.Plan.Features is { } features)
            {
                await context.Step("features", () => ChangeFeaturesAsync(features, mountDir, ct), ct).ConfigureAwait(false);
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
                await servicing.ExportAsync(working, request.SourceIndex, completed, ct).ConfigureAwait(false);
                var exported = await SourceManifest.ComputeAsync(run.Completed, ct).ConfigureAwait(false);
                return exported.Entries.Single() with { RelativePath = InstallImage };
            }, ct).ConfigureAwait(false);
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
                var staged = (await TreeCopy.CopyAsync(paths.Base, publication.Staging, [InstallImage], ct).ConfigureAwait(false)).ToList();
                Directory.CreateDirectory(Path.Combine(publication.Staging, "sources"));
                var image = await TreeCopy.CopyFileAsync(completed, Path.Combine(publication.Staging, "sources", "install.wim"), ct).ConfigureAwait(false);
                staged.Add(image with { RelativePath = InstallImage });

                var expected = baseManifest.Entries
                    .Select(e => string.Equals(e.RelativePath, InstallImage, StringComparison.OrdinalIgnoreCase) ? completedEntry : e)
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

    private async Task<bool> RemoveCapabilitiesAsync(CapabilityPlan plan, string mountDir, CancellationToken ct)
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

        var after = await servicing.GetCapabilitiesAsync(mountDir, ct).ConfigureAwait(false);
        var left = plan.Remove.Where(r => after.Capabilities.Any(c => c.Identity == r && c.State == CapabilityState.Installed)).Select(r => r.FullName).ToList();
        return after.Problems.Count == 0 && left.Count == 0
            ? true
            : throw new InvalidDataException(left.Count > 0
                ? $"Still installed after removal: {string.Join(", ", left)}."
                : $"The capability list cannot be read after removal: {string.Join("; ", after.Problems)}");
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
            Log?.Invoke($"disable {string.Join(", ", plan.Disable)}");
            await servicing.DisableFeaturesAsync(mountDir, plan.Disable, ct).ConfigureAwait(false);
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
