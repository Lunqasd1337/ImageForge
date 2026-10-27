using ImageForge.Core.Execution;
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
        var state = LoadForRun(store);

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

            var baseManifest = await context.Step("base-copy", () => PrepareBaseAsync(request.SourceDir, source, state, ct), ct).ConfigureAwait(false);
            state = state with { SourceFingerprint = source.Fingerprint };
            store.Save(state);

            var working = Path.Combine(run.Working, "install.wim");
            await context.Step("working-copy", () =>
            {
                Directory.CreateDirectory(run.Working);
                TreeCopy.CopyFile(Path.Combine(paths.Base, "sources", "install.wim"), working);
                return Task.FromResult(true);
            }, ct).ConfigureAwait(false);

            await context.Step("mount", async () =>
            {
                PrepareEmptyDirectory(mountDir);
                mountAttempted = true;
                await servicing.MountAsync(working, request.SourceIndex, mountDir, ct).ConfigureAwait(false);
                mounted = true;
                return true;
            }, ct).ConfigureAwait(false);
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
                TreeCopy.Copy(paths.Base, publication.Staging, [InstallImage], ct);
                TreeCopy.CopyFile(completed, Path.Combine(publication.Staging, "sources", "install.wim"));

                var expected = baseManifest.Entries
                    .Select(e => string.Equals(e.RelativePath, InstallImage, StringComparison.OrdinalIgnoreCase) ? completedEntry : e)
                    .ToList();
                var staged = await SourceManifest.ComputeAsync(publication.Staging, ct).ConfigureAwait(false);
                var differences = SourceManifest.Compare(new SourceManifest(expected, string.Empty), staged);
                return differences.Count == 0
                    ? true
                    : throw new IOException($"Assembled result does not match: {string.Join(", ", differences.Select(d => $"{d.Change} {d.RelativePath}"))}.");
            }, ct).ConfigureAwait(false);

            await context.Step("source-recheck", async () =>
            {
                var again = await SourceManifest.ComputeAsync(request.SourceDir, ct).ConfigureAwait(false);
                var differences = SourceManifest.Compare(source, again);
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

    private static WorkspaceState LoadForRun(StateStore<WorkspaceState> store)
    {
        var loaded = store.Load();
        if (loaded.Source == StateSource.Unusable)
        {
            throw new WorkspaceRecoveryRequiredException("state.json and its backup are damaged.");
        }

        var state = loaded.State ?? new WorkspaceState(Guid.NewGuid(), null, TargetStatus.Pending, null);
        return state.LastRun?.Status is RunStatus.Running or RunStatus.RecoveryRequired
            ? throw new WorkspaceRecoveryRequiredException($"Run {state.LastRun.RunId:N} did not finish ({state.LastRun.Status}); recover the workspace first.")
            : state;
    }

    private async Task<SourceManifest> PrepareBaseAsync(string sourceDir, SourceManifest source, WorkspaceState state, CancellationToken ct)
    {
        if (state.SourceFingerprint == source.Fingerprint && Directory.Exists(paths.Base))
        {
            var existing = await SourceManifest.ComputeAsync(paths.Base, ct).ConfigureAwait(false);
            if (existing.Fingerprint == source.Fingerprint)
            {
                Log?.Invoke("base copy reused");
                return existing;
            }
        }

        TryDeleteDirectory(paths.BasePartial, throwOnError: true);
        TreeCopy.Copy(sourceDir, paths.BasePartial, null, ct);
        var copy = await SourceManifest.ComputeAsync(paths.BasePartial, ct).ConfigureAwait(false);
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

            journal.Append(JournalEventKind.StepStarted, name, null);
            log?.Invoke($"{name}...");
            var result = await body().ConfigureAwait(false);
            journal.Append(JournalEventKind.StepCompleted, name, null);
            return result;
        }
    }
}
