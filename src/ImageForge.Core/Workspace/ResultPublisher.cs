namespace ImageForge.Core.Workspace;

public enum PublicationRecoveryAction
{
    None,

    /// <summary>The switch never happened; the unfinished staging folder is deleted.</summary>
    DiscardStaging,

    /// <summary>The old result was moved aside but the new one never took its place; the old result goes back.</summary>
    RestorePrevious,

    /// <summary>The new result is in place; only the old copy is left to delete.</summary>
    DeletePrevious,

    /// <summary>An unexpected combination; nothing is touched automatically.</summary>
    RecoveryRequired,
}

public static class PublicationRecovery
{
    /// <summary>Decides how to finish an interrupted publication. A staging folder is never promoted to the result.</summary>
    public static PublicationRecoveryAction Decide(bool outputExists, bool previousExists, bool stagingExists) =>
        (outputExists, previousExists, stagingExists) switch
        {
            (_, false, false) => PublicationRecoveryAction.None,
            (_, false, true) => PublicationRecoveryAction.DiscardStaging,
            (false, true, _) => PublicationRecoveryAction.RestorePrevious,
            (true, true, false) => PublicationRecoveryAction.DeletePrevious,
            (true, true, true) => PublicationRecoveryAction.RecoveryRequired,
        };
}

/// <summary>Output folder plus its hidden siblings used during publication; all three are on the same volume so moves are renames.</summary>
public sealed record PublicationPaths(string Output, string Staging, string Previous)
{
    public static PublicationPaths For(string outputDir, Guid runId)
    {
        var output = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(outputDir));
        var parent = System.IO.Path.GetDirectoryName(output);
        var name = System.IO.Path.GetFileName(output);
        if (parent is null || name.Length == 0)
        {
            throw new ArgumentException("The output folder cannot be a drive root.", nameof(outputDir));
        }

        var prefix = $".{name}.imageforge-{runId:N}";
        return new PublicationPaths(
            output,
            System.IO.Path.Combine(parent, prefix + ".staging"),
            System.IO.Path.Combine(parent, prefix + ".previous"));
    }
}

/// <summary>Swaps a fully built staging folder into the output folder.</summary>
public static class ResultPublisher
{
    /// <param name="journal">Called after each completed move so a crash can be recovered with <see cref="Recover"/>.</param>
    public static void Publish(PublicationPaths paths, Action<string> journal)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(journal);
        if (!Directory.Exists(paths.Staging))
        {
            throw new DirectoryNotFoundException($"Staging folder '{paths.Staging}' does not exist.");
        }

        if (Directory.Exists(paths.Previous))
        {
            throw new IOException($"'{paths.Previous}' is left from an interrupted publication; recover first.");
        }

        var hadPrevious = Directory.Exists(paths.Output);
        if (hadPrevious)
        {
            Directory.Move(paths.Output, paths.Previous);
            journal("previous-moved");
        }

        Directory.Move(paths.Staging, paths.Output);
        journal("switched");

        if (hadPrevious)
        {
            Directory.Delete(paths.Previous, recursive: true);
            journal("previous-deleted");
        }
    }

    public static PublicationRecoveryAction Recover(PublicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var action = PublicationRecovery.Decide(Directory.Exists(paths.Output), Directory.Exists(paths.Previous), Directory.Exists(paths.Staging));
        switch (action)
        {
            case PublicationRecoveryAction.DiscardStaging:
                Directory.Delete(paths.Staging, recursive: true);
                break;
            case PublicationRecoveryAction.RestorePrevious:
                Directory.Move(paths.Previous, paths.Output);
                if (Directory.Exists(paths.Staging))
                {
                    Directory.Delete(paths.Staging, recursive: true);
                }

                break;
            case PublicationRecoveryAction.DeletePrevious:
                Directory.Delete(paths.Previous, recursive: true);
                break;
        }

        return action;
    }
}
