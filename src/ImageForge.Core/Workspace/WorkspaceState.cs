namespace ImageForge.Core.Workspace;

public enum TargetStatus
{
    Pending,
    InProgress,
    Completed,

    /// <summary>A run stopped before the target was confirmed; the next run recreates it from the source copy.</summary>
    NeedsRebuild,
}

public enum RunStatus
{
    Running,
    Published,
    Failed,

    /// <summary>Owned resources could not be released automatically; nothing else may run until recovery succeeds.</summary>
    RecoveryRequired,
}

public sealed record RunRecord(Guid RunId, RunStatus Status, string OutputDir, Guid ImageId, string? FailedStep, string? Error);

/// <summary>Contents of state.json.</summary>
/// <param name="SourceFingerprint">Fingerprint of the source the verified copy in base\ was made from; null until one is made.</param>
public sealed record WorkspaceState(Guid WorkspaceId, string? SourceFingerprint, TargetStatus Target, RunRecord? LastRun);

public sealed class WorkspaceRecoveryRequiredException(string message) : InvalidOperationException(message);
