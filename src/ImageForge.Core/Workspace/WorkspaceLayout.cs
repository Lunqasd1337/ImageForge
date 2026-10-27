using ImageForge.Core.Safety;

namespace ImageForge.Core.Workspace;

/// <summary>Folders of one run: the working image, finished images and the media tree assembled for publication.</summary>
public sealed record RunLayout(string Root, string Working, string Completed, string Assemble);

/// <summary>
/// Fixed layout of a work folder. Every path is built under <see cref="Scope"/>,
/// so nothing the engine writes for the workspace can land outside it.
/// </summary>
public sealed class WorkspaceLayout
{
    private WorkspaceLayout(WriteScope scope) => Scope = scope;

    public WriteScope Scope { get; }

    public string Root => Scope.Root;

    /// <summary>Copy of the source media; never modified after the source check.</summary>
    public string Base => Under("base");

    public string StateFile => Under("state.json");

    public string Logs => Under("logs");

    public string Scratch => Under("scratch");

    public static WorkspaceLayout Create(string workDir) => new(WriteScope.Create(workDir));

    public string Mount(Guid imageId) => Under("mount", imageId.ToString("N"));

    public string Journal(Guid runId) => Under("journal", runId.ToString("N") + ".jsonl");

    public RunLayout Run(Guid runId)
    {
        var root = Under("runs", runId.ToString("N"));
        return new RunLayout(root, root + @"\working", root + @"\completed", root + @"\assemble");
    }

    private string Under(params string[] segments)
    {
        var path = Root + @"\" + string.Join('\\', segments);
        var check = Scope.Check(path);
        return check.IsAllowed ? check.NormalizedPath! : throw new InvalidOperationException($"Workspace path escaped the work folder: {check.Violation}.");
    }
}
