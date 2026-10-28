using ImageForge.Core.Changes;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;

namespace ImageForge.Core.Execution;

public interface IOfflineHiveLoader
{
    ILoadedOfflineHive Load(OfflineHive hive, string hiveFilePath);

    bool HiveFileExists(string hiveFilePath);
}

public interface ILoadedOfflineHive
{
    OfflineHive Hive { get; }

    /// <summary>Writes the type and bytes as given, creating missing keys.</summary>
    void SetValue(string subKey, string valueName, RegistryValue value);

    /// <summary>A missing key or value is not an error.</summary>
    void DeleteValue(string subKey, string valueName);

    void CreateKey(string subKey);

    /// <summary>Deletes the key and its subtree; a missing key is not an error.</summary>
    void DeleteKeyTree(string subKey);

    /// <summary>SYSTEM\Select\Current, or null when the key or value is missing or not a REG_DWORD.</summary>
    uint? ReadSelectCurrent();

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

        // A missing hive file is not created or replaced by another hive; refuse the plan before anything is written.
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            if (plan.Steps[i] is not LoadOfflineHive load)
            {
                continue;
            }

            string path;
            try
            {
                path = resolveHiveFile(load.HiveFile);
            }
#pragma warning disable CA1031 // A path outside the write scope fails the plan like any other load failure.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return new ExecutionReport(ExecutionOutcome.Failed, 0, i, $"{ex.GetType().Name}: {ex.Message}", []);
            }

            if (!loader.HiveFileExists(path))
            {
                return new ExecutionReport(
                    ExecutionOutcome.Failed, 0, i, $"HiveFileMissing: the image has no {load.HiveFile} ({load.Hive}); nothing was written.", []);
            }
        }

        var loaded = new Dictionary<OfflineHive, ILoadedOfflineHive>();
        var controlSets = new Dictionary<OfflineHive, string>();
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
                    case OfflineKeyStep keyStep:
                        if (!loaded.TryGetValue(keyStep.Hive, out var target))
                        {
                            throw new InvalidOperationException($"Hive {keyStep.Hive} is not loaded.");
                        }

                        var subKey = keyStep.UnderCurrentControlSet ? UnderControlSet(target, keyStep.SubKey, controlSets) : keyStep.SubKey;
                        switch (keyStep)
                        {
                            case SetOfflineRegistryValue set:
                                target.SetValue(subKey, set.ValueName, set.Value);
                                break;
                            case DeleteOfflineRegistryValue delete:
                                target.DeleteValue(subKey, delete.ValueName);
                                break;
                            case CreateOfflineRegistryKey:
                                target.CreateKey(subKey);
                                break;
                            case DeleteOfflineRegistryKey when subKey.Length == 0:
                                throw new InvalidOperationException("Deleting the root of a hive is not allowed.");
                            case DeleteOfflineRegistryKey:
                                target.DeleteKeyTree(subKey);
                                break;
                            default:
                                throw new NotSupportedException($"Unknown plan step {step.GetType().Name}.");
                        }

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

    /// <summary>CurrentControlSet exists only in a running system; offline it is whatever Select\Current names.</summary>
    private static string UnderControlSet(ILoadedOfflineHive hive, string subKey, Dictionary<OfflineHive, string> cache)
    {
        if (!cache.TryGetValue(hive.Hive, out var controlSet))
        {
            controlSet = ControlSet.FromSelectCurrent(hive.ReadSelectCurrent())
                ?? throw new InvalidDataException(@"The image's SYSTEM hive has no valid Select\Current; CurrentControlSet cannot be resolved.");
            cache.Add(hive.Hive, controlSet);
        }

        return subKey.Length == 0 ? controlSet : controlSet + "\\" + subKey;
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
