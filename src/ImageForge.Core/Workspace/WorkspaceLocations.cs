using ImageForge.Core.Safety;

namespace ImageForge.Core.Workspace;

public enum LocationProblemKind
{
    NotLocalPath,
    Same,
    Nested,
}

/// <summary>A problem with the chosen folders. For <see cref="LocationProblemKind.Nested"/>, <see cref="Location"/> lies inside <see cref="Other"/>.</summary>
public sealed record LocationProblem(LocationProblemKind Kind, string Location, string? Other);

/// <summary>The source, work folder and output folder must be local drive paths that neither match nor contain one another.</summary>
public static class WorkspaceLocations
{
    public const string Source = "Source";
    public const string WorkDir = "WorkDir";
    public const string OutputDir = "OutputDir";

    public static IReadOnlyList<LocationProblem> Validate(string source, string workDir, string outputDir)
    {
        var problems = new List<LocationProblem>();
        var normalized = new List<(string Name, string Path)>();

        foreach (var (name, path) in new[] { (Source, source), (WorkDir, workDir), (OutputDir, outputDir) })
        {
            if (WindowsPath.TryNormalize(path, out var full) == WriteScopeViolation.None)
            {
                normalized.Add((name, full));
            }
            else
            {
                problems.Add(new LocationProblem(LocationProblemKind.NotLocalPath, name, null));
            }
        }

        for (var i = 0; i < normalized.Count; i++)
        {
            for (var j = i + 1; j < normalized.Count; j++)
            {
                var a = normalized[i];
                var b = normalized[j];
                var bInA = WindowsPath.IsSameOrUnder(b.Path, a.Path);
                var aInB = WindowsPath.IsSameOrUnder(a.Path, b.Path);
                if (aInB && bInA)
                {
                    problems.Add(new LocationProblem(LocationProblemKind.Same, a.Name, b.Name));
                }
                else if (bInA)
                {
                    problems.Add(new LocationProblem(LocationProblemKind.Nested, b.Name, a.Name));
                }
                else if (aInB)
                {
                    problems.Add(new LocationProblem(LocationProblemKind.Nested, a.Name, b.Name));
                }
            }
        }

        return problems;
    }
}
