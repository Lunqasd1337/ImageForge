namespace ImageForge.Core.Workspace;

public enum RecoveryOutcome
{
    NothingToDo,
    Recovered,
    StillRequired,
}

public sealed record RecoveryResult(RecoveryOutcome Outcome, IReadOnlyList<string> Actions, IReadOnlyList<string> Problems);

/// <summary>
/// Releases what an interrupted run owned: its hives, its mount, its staging or moved-aside result and its run folder.
/// Only resources named by the workspace state are touched; foreign mounts are never cleaned up.
/// </summary>
public sealed class RunRecovery(IWorkspacePaths paths, IImageServicing servicing)
{
    public async Task<RecoveryResult> RecoverAsync(CancellationToken ct)
    {
        var store = new StateStore<WorkspaceState>(paths.StateFile);
        var loaded = store.Load();
        if (loaded.Source == StateSource.Unusable)
        {
            return new RecoveryResult(RecoveryOutcome.StillRequired, [], ["state.json and its backup are damaged; manual inspection is needed."]);
        }

        var state = loaded.State;
        if (state?.LastRun is not { Status: RunStatus.Running or RunStatus.RecoveryRequired } run)
        {
            return new RecoveryResult(RecoveryOutcome.NothingToDo, [], []);
        }

        var actions = new List<string>();
        var problems = new List<string>();

        // Hive files live inside the mount, so hives are unloaded before the image is discarded.
        Try(problems, "unload hives", () =>
        {
            foreach (var name in servicing.UnloadOwnedHives(run.RunId))
            {
                actions.Add($"unloaded hive {name}");
            }
        });

        var mountDir = paths.Mount(run.ImageId);
        try
        {
            if (await servicing.IsMountedAsync(mountDir, ct).ConfigureAwait(false))
            {
                await servicing.UnmountAsync(mountDir, commit: false, ct).ConfigureAwait(false);
                actions.Add($"discarded mount {mountDir}");
            }

            RunPipeline.TryDeleteEmptyDirectory(mountDir);
        }
#pragma warning disable CA1031 // Report the problem and keep the workspace in RecoveryRequired.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            problems.Add($"discard mount: {ex.Message}");
        }

        Try(problems, "publication", () =>
        {
            var action = ResultPublisher.Recover(PublicationPaths.For(run.OutputDir, run.RunId));
            if (action == PublicationRecoveryAction.RecoveryRequired)
            {
                throw new IOException("Output, previous result and staging all exist; choose manually.");
            }

            if (action != PublicationRecoveryAction.None)
            {
                actions.Add($"publication: {action}");
            }
        });

        Try(problems, "run folder", () =>
        {
            var runRoot = paths.Run(run.RunId).Root;
            if (Directory.Exists(runRoot))
            {
                RunPipeline.TryDeleteDirectory(runRoot, throwOnError: true);
                actions.Add($"deleted {runRoot}");
            }
        });

        var recovered = problems.Count == 0;
        store.Save(state with
        {
            Target = TargetStatus.NeedsRebuild,
            LastRun = run with
            {
                Status = recovered ? RunStatus.Failed : RunStatus.RecoveryRequired,
                FailedStep = run.FailedStep ?? RunJournal.Read(paths.Journal(run.RunId)).LastOpenStep,
                Error = recovered ? run.Error ?? "Interrupted; recovered." : string.Join("; ", problems),
            },
        });

        return new RecoveryResult(recovered ? RecoveryOutcome.Recovered : RecoveryOutcome.StillRequired, actions, problems);
    }

    private static void Try(List<string> problems, string name, Action action)
    {
        try
        {
            action();
        }
#pragma warning disable CA1031 // Collect every problem; one failure must not skip the rest.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            problems.Add($"{name}: {ex.Message}");
        }
    }
}
