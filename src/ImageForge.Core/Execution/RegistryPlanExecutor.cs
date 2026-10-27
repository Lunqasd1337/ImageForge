using ImageForge.Core.Changes;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;

namespace ImageForge.Core.Execution;

public interface IOfflineHiveLoader
{
    ILoadedOfflineHive Load(OfflineHive hive, string hiveFilePath);
}

public interface ILoadedOfflineHive
{
    OfflineHive Hive { get; }

    void SetValue(string subKey, string valueName, RegistryValue value);

    void Unload();
}

public enum ExecutionOutcome
{
    Completed,
    Failed,
    Cancelled,
}

public sealed record ExecutionReport(
    ExecutionOutcome Outcome,
    int CompletedSteps,
    int? FailedStepIndex,
    string? Error,
    IReadOnlyList<OfflineHive> HivesLeftLoaded)
{
    public bool RequiresRecovery => HivesLeftLoaded.Count > 0;
}

/// <summary>
/// Runs the registry steps of a plan against offline hives. Every hive loaded here is unloaded before returning,
/// whatever happens; a hive that cannot be unloaded is reported so the workspace goes to RecoveryRequired.
/// </summary>
/// <param name="resolveHiveFile">Maps a hive file relative to the mount to a checked absolute path.</param>
public sealed class RegistryPlanExecutor(IOfflineHiveLoader loader, Func<string, string> resolveHiveFile)
{
    public ExecutionReport Execute(ExecutionPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var loaded = new Dictionary<OfflineHive, ILoadedOfflineHive>();
        var completed = 0;
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (step is not UnloadOfflineHive && ct.IsCancellationRequested)
            {
                return Finish(ExecutionOutcome.Cancelled, completed, null, null, loaded);
            }

            try
            {
                switch (step)
                {
                    case LoadOfflineHive load:
                        if (loaded.ContainsKey(load.Hive))
                        {
                            throw new InvalidOperationException($"Hive {load.Hive} is already loaded.");
                        }

                        loaded.Add(load.Hive, loader.Load(load.Hive, resolveHiveFile(load.HiveFile)));
                        break;
                    case SetOfflineRegistryValue set:
                        if (!loaded.TryGetValue(set.Hive, out var target))
                        {
                            throw new InvalidOperationException($"Hive {set.Hive} is not loaded.");
                        }

                        target.SetValue(set.SubKey, set.ValueName, set.Value);
                        break;
                    case UnloadOfflineHive unload:
                        if (!loaded.TryGetValue(unload.Hive, out var owned))
                        {
                            throw new InvalidOperationException($"Hive {unload.Hive} is not loaded.");
                        }

                        owned.Unload();
                        loaded.Remove(unload.Hive);
                        break;
                    default:
                        throw new NotSupportedException($"Unknown plan step {step.GetType().Name}.");
                }
            }
#pragma warning disable CA1031 // Any step failure must still release the hives this run loaded.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return Finish(ExecutionOutcome.Failed, completed, i, $"{ex.GetType().Name}: {ex.Message}", loaded);
            }

            completed++;
        }

        return Finish(ExecutionOutcome.Completed, completed, null, null, loaded);
    }

    private static ExecutionReport Finish(
        ExecutionOutcome outcome,
        int completed,
        int? failedStep,
        string? error,
        Dictionary<OfflineHive, ILoadedOfflineHive> loaded)
    {
        var leftLoaded = new List<OfflineHive>();
        foreach (var (hive, handle) in loaded)
        {
            try
            {
                handle.Unload();
            }
#pragma warning disable CA1031 // Report every hive that stays loaded instead of stopping at the first.
            catch (Exception)
#pragma warning restore CA1031
            {
                leftLoaded.Add(hive);
            }
        }

        return new ExecutionReport(outcome, completed, failedStep, error, leftLoaded);
    }
}
