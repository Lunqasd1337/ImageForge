using ImageForge.Core.Appx;
using ImageForge.Core.Changes;
using ImageForge.Core.Registry;

namespace ImageForge.Core.Planning;

public sealed record PlanRequest(Guid WorkspaceId, long WorkspaceRevision, Guid ImageId, IReadOnlyList<ImageChange> Changes)
{
    /// <summary>The provisioned packages read from the image; Appx removals are planned only against it.</summary>
    public AppxInventory? Appx { get; init; }
}

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

/// <summary>Writes a file inside the mounted image; the path is relative to the mount root.</summary>
public sealed record WriteImageFile(string RelativePath, byte[] Content) : PlannedStep;

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
/// <param name="FirstLogon">Not written offline: imported for every user at first logon (see <see cref="FirstLogonDelivery"/>).</param>
public sealed record RegistryWritePreview(
    string SourcePath,
    OfflineHive Hive,
    string HiveFile,
    string SubKey,
    string? ValueName,
    RegistryValue? NewValue,
    RegistryChangeKind Kind = RegistryChangeKind.SetValue,
    bool UnderCurrentControlSet = false,
    bool ClassesRootPolicy = false,
    bool FirstLogon = false);

public sealed record ExecutionPlan(
    Guid PlanId,
    Guid WorkspaceId,
    long WorkspaceRevision,
    Guid ImageId,
    IReadOnlyList<PlannedStep> Steps,
    IReadOnlyList<RegistryWritePreview> Preview,
    string Fingerprint)
{
    /// <summary>Provisioned packages to remove, in order, before the registry steps run.</summary>
    public IReadOnlyList<AppxRemoval> AppxRemovals { get; init; } = [];

    /// <summary>Fingerprint of the inventory the removals were resolved from; the run checks it before removing.</summary>
    public string? AppxInventory { get; init; }
}

/// <param name="Warning">Shown before the removal is confirmed; null when none is known.</param>
public sealed record AppxRemoval(PackageIdentity Package, string DisplayName, string? Warning);

public sealed record PlanningDiagnostic(string Code, int ChangeIndex, string Message);

public sealed record PlanResult(ExecutionPlan? Plan, IReadOnlyList<PlanningDiagnostic> Diagnostics);

public static class PlanningCodes
{
    public const string EmptyChangeSet = "plan.empty";
    public const string UnsupportedChange = "change.unsupported";
    public const string RegistryPathMalformed = "registry.path.malformed";
    public const string RegistryRootUnknown = "registry.root.unknown";
    public const string RegistryRootForbidden = "registry.root.forbidden";
    public const string AppxInventoryMissing = "appx.inventory.missing";
    public const string AppxNotProvisioned = "appx.not-provisioned";
    public const string AppxFrameworkBlocked = "appx.framework.blocked";
    public const string RegistryRootNotSupportedYet = "registry.root.not-supported-yet";
    public const string RegistryValueInvalid = "registry.value.invalid";
    public const string RegistryKeyDeleteRoot = "registry.key.delete-root";
    public const string FirstLogonUnrepresentable = "registry.first-logon.unrepresentable";
}
