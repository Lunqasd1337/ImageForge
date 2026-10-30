using ImageForge.Core.Appx;
using ImageForge.Core.Changes;
using ImageForge.Core.Drivers;
using ImageForge.Core.Features;
using ImageForge.Core.Registry;
using ImageForge.Core.Updates;

namespace ImageForge.Core.Planning;

public sealed record PlanRequest(Guid WorkspaceId, long WorkspaceRevision, Guid ImageId, IReadOnlyList<ImageChange> Changes)
{
    /// <summary>The provisioned packages read from the image; Appx removals are planned only against it.</summary>
    public AppxInventory? Appx { get; init; }

    /// <summary>The features read from the image, with their parents; feature changes are planned only against it.</summary>
    public FeatureInventory? Features { get; init; }

    public CapabilityInventory? Capabilities { get; init; }

    /// <summary>The source media holds the .NET Framework 3.5 payload in sources\sxs (checked by the inspection).</summary>
    public bool MediaHasNetFx3 { get; init; }

    /// <summary>A Features on Demand folder that passed the check; null when none was given or it failed.</summary>
    public string? FodSource { get; init; }

    /// <summary>Why the given Features on Demand folder cannot be used; shown when an addition needs it.</summary>
    public string? FodSourceProblem { get; init; }

    /// <summary>The third-party drivers read from the image; removals are planned only against it.</summary>
    public DriverInventory? Drivers { get; init; }

    /// <summary>The scanned driver folder; additions are planned only from it.</summary>
    public DriverFolder? DriverSource { get; init; }

    /// <summary>The scanned update folder; updates are added only from it.</summary>
    public UpdateFolder? UpdateSource { get; init; }
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

    /// <summary>Feature changes, run after the Appx removals; null when there are none.</summary>
    public FeaturePlan? Features { get; init; }

    /// <summary>Capabilities to remove and add, before the feature changes; null when there are none.</summary>
    public CapabilityPlan? Capabilities { get; init; }

    /// <summary>Driver packages to remove and add, after the feature changes; null when there are none.</summary>
    public DriverPlan? Drivers { get; init; }

    /// <summary>Update packages to add, after the feature changes and before the drivers; null when there are none.</summary>
    public UpdatePlan? Updates { get; init; }

    /// <summary>The component store cleanup, after all other servicing and before the registry; null when not asked for.</summary>
    public CleanupPlan? Cleanup { get; init; }

    /// <summary>Winre.wim servicing, after the cleanup and before the registry; null when nothing goes into WinRE.</summary>
    public WinRePlan? WinRe { get; init; }
}

/// <param name="Disable">Passed to DISM in this order, dependents first; runs before <paramref name="Enable"/>.</param>
/// <param name="Enable">Passed to DISM in this order, parents first.</param>
/// <param name="Added">Features in either list that the user did not ask for, to show in the preview.</param>
/// <param name="UsesMediaSxs">An enabled feature has no payload in the image; it comes from sources\sxs of the media copy.</param>
/// <param name="Inventory">Fingerprint of the feature list the change was worked out from; the run checks it first.</param>
public sealed record FeaturePlan(IReadOnlyList<string> Disable, IReadOnlyList<string> Enable, IReadOnlyList<string> Added, bool UsesMediaSxs, string Inventory);

/// <param name="Remove">Removed first, in this order.</param>
/// <param name="Inventory">Fingerprint of the capability list the changes were checked against.</param>
public sealed record CapabilityPlan(IReadOnlyList<CapabilityIdentity> Remove, string Inventory)
{
    /// <summary>Added after the removals, one DISM call each, from <see cref="Source"/>.</summary>
    public IReadOnlyList<CapabilityIdentity> Add { get; init; } = [];

    /// <summary>The Features on Demand folder; set only when something is added.</summary>
    public string? Source { get; init; }
}

/// <param name="Remove">Removed first, in this order.</param>
/// <param name="Add">Added after the removals, one DISM call each, from <paramref name="Source"/>.</param>
/// <param name="Inventory">Fingerprint of the driver list the removals were checked against.</param>
/// <param name="Warnings">Shown in the preview: unsigned packages, boot-critical removals, packages DISM may already hold.</param>
public sealed record DriverPlan(
    IReadOnlyList<DriverRemoval> Remove, IReadOnlyList<DriverAddition> Add, string? Source, string Inventory, IReadOnlyList<string> Warnings);

public sealed record DriverRemoval(string PublishedName, string Description, bool BootCritical);

/// <param name="InfPath">Relative to the driver folder.</param>
/// <param name="ContentHash">The run adds the package only while its files still have this hash.</param>
/// <param name="SameContent">Other requested paths with the same content; added once.</param>
public sealed record DriverAddition(string InfPath, string ContentHash, bool ForceUnsigned, string Description, IReadOnlyList<string> SameContent);

/// <param name="Add">One DISM call each, in this order: servicing stack packages first.</param>
/// <param name="Source">The update folder the files are in.</param>
/// <param name="Warnings">Shown in the preview.</param>
public sealed record UpdatePlan(IReadOnlyList<UpdateAddition> Add, string Source, IReadOnlyList<string> Warnings);

/// <param name="ResetBase">Superseded component versions are removed too; updates in the image can no longer be uninstalled.</param>
/// <param name="Warnings">Shown in the preview.</param>
public sealed record CleanupPlan(bool ResetBase, IReadOnlyList<string> Warnings);

/// <summary>
/// Servicing of Winre.wim inside the image: <paramref name="Add"/> in order (cumulative updates first, which bring only
/// the servicing stack there), cleanup with /ResetBase, commit and export, then the file goes back into the image.
/// </summary>
public sealed record WinRePlan(IReadOnlyList<UpdateAddition> Add, string Source, IReadOnlyList<string> Warnings);

/// <param name="FileName">Relative to the update folder.</param>
/// <param name="Sha256">The run adds the file only while it still has this hash.</param>
/// <param name="SameContent">Other requested files with the same content; added once.</param>
public sealed record UpdateAddition(string FileName, string Sha256, string Description, IReadOnlyList<string> SameContent);

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
    public const string AppxDependenciesUnknown = "appx.dependencies.unknown";
    public const string AppxRequiredByKept = "appx.required-by-kept";
    public const string AppxRequiredByInbox = "appx.required-by-inbox";
    public const string AppxDependencyCycle = "appx.dependency-cycle";
    public const string FeatureInventoryMissing = "feature.inventory.missing";
    public const string FeatureBlocked = "feature.blocked";
    public const string FeatureConflict = "feature.conflict";
    public const string FeatureSourceMissing = "feature.source.missing";
    public const string CapabilityInventoryMissing = "capability.inventory.missing";
    public const string CapabilityNotInstalled = "capability.not-installed";
    public const string CapabilityNotAvailable = "capability.not-available";
    public const string CapabilitySourceMissing = "capability.source.missing";
    public const string DriverInventoryMissing = "driver.inventory.missing";
    public const string DriverNotInstalled = "driver.not-installed";
    public const string DriverBootCritical = "driver.boot-critical";
    public const string DriverSourceMissing = "driver.source.missing";
    public const string DriverPackageInvalid = "driver.package.invalid";
    public const string DriverUnsigned = "driver.unsigned";
    public const string UpdateSourceMissing = "update.source.missing";
    public const string UpdatePackageInvalid = "update.package.invalid";
    public const string RegistryRootNotSupportedYet = "registry.root.not-supported-yet";
    public const string RegistryValueInvalid = "registry.value.invalid";
    public const string RegistryKeyDeleteRoot = "registry.key.delete-root";
    public const string FirstLogonUnrepresentable = "registry.first-logon.unrepresentable";
}
