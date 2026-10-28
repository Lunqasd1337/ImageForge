using ImageForge.Core.Changes;
using ImageForge.Core.Registry;

namespace ImageForge.Core.Planning;

public sealed record PlanRequest(Guid WorkspaceId, long WorkspaceRevision, Guid ImageId, IReadOnlyList<ImageChange> Changes);

public abstract record PlannedStep;

public sealed record LoadOfflineHive(OfflineHive Hive, string HiveFile) : PlannedStep;

/// <summary>A step that addresses a key inside a loaded hive.</summary>
/// <param name="UnderCurrentControlSet">SubKey is relative to the control set SYSTEM\Select\Current names; the
/// executor resolves it from the loaded hive and fails when it is missing.</param>
public abstract record OfflineKeyStep(OfflineHive Hive, string SubKey, bool UnderCurrentControlSet) : PlannedStep;

public sealed record SetOfflineRegistryValue(OfflineHive Hive, string SubKey, string ValueName, RegistryValue Value, bool UnderCurrentControlSet = false)
    : OfflineKeyStep(Hive, SubKey, UnderCurrentControlSet);

public sealed record DeleteOfflineRegistryValue(OfflineHive Hive, string SubKey, string ValueName, bool UnderCurrentControlSet = false)
    : OfflineKeyStep(Hive, SubKey, UnderCurrentControlSet);

public sealed record CreateOfflineRegistryKey(OfflineHive Hive, string SubKey, bool UnderCurrentControlSet = false)
    : OfflineKeyStep(Hive, SubKey, UnderCurrentControlSet);

/// <summary>Deletes the key and its whole subtree; a missing key is not an error.</summary>
public sealed record DeleteOfflineRegistryKey(OfflineHive Hive, string SubKey, bool UnderCurrentControlSet = false)
    : OfflineKeyStep(Hive, SubKey, UnderCurrentControlSet);

public sealed record UnloadOfflineHive(OfflineHive Hive) : PlannedStep;

public enum RegistryChangeKind
{
    SetValue,
    DeleteValue,
    CreateKey,
    DeleteKey,
}

/// <param name="SourcePath">The path as the user wrote it.</param>
/// <param name="ValueName">Null for key changes; empty for the default value.</param>
/// <param name="NewValue">Only for <see cref="RegistryChangeKind.SetValue"/>.</param>
/// <param name="ClassesRootPolicy">An HKCR path written to the machine classes by ImageForge policy.</param>
public sealed record RegistryWritePreview(
    string SourcePath,
    OfflineHive Hive,
    string HiveFile,
    string SubKey,
    string? ValueName,
    RegistryValue? NewValue,
    RegistryChangeKind Kind = RegistryChangeKind.SetValue,
    bool UnderCurrentControlSet = false,
    bool ClassesRootPolicy = false);

public sealed record ExecutionPlan(
    Guid PlanId,
    Guid WorkspaceId,
    long WorkspaceRevision,
    Guid ImageId,
    IReadOnlyList<PlannedStep> Steps,
    IReadOnlyList<RegistryWritePreview> Preview,
    string Fingerprint);

public sealed record PlanningDiagnostic(string Code, int ChangeIndex, string Message);

public sealed record PlanResult(ExecutionPlan? Plan, IReadOnlyList<PlanningDiagnostic> Diagnostics);

public static class PlanningCodes
{
    public const string EmptyChangeSet = "plan.empty";
    public const string UnsupportedChange = "change.unsupported";
    public const string RegistryPathMalformed = "registry.path.malformed";
    public const string RegistryRootUnknown = "registry.root.unknown";
    public const string RegistryRootForbidden = "registry.root.forbidden";
    public const string RegistryRootNotSupportedYet = "registry.root.not-supported-yet";
    public const string RegistryValueInvalid = "registry.value.invalid";
    public const string RegistryKeyDeleteRoot = "registry.key.delete-root";
}
