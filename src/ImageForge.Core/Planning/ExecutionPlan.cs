using ImageForge.Core.Changes;
using ImageForge.Core.Registry;

namespace ImageForge.Core.Planning;

public sealed record PlanRequest(Guid WorkspaceId, long WorkspaceRevision, Guid ImageId, IReadOnlyList<ImageChange> Changes);

public abstract record PlannedStep;

public sealed record LoadOfflineHive(OfflineHive Hive, string HiveFile) : PlannedStep;

public sealed record SetOfflineRegistryValue(OfflineHive Hive, string SubKey, string ValueName, RegistryValue Value) : PlannedStep;

public sealed record UnloadOfflineHive(OfflineHive Hive) : PlannedStep;

public sealed record RegistryWritePreview(
    string SourcePath,
    OfflineHive Hive,
    string HiveFile,
    string SubKey,
    string ValueName,
    RegistryValue NewValue);

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
}
