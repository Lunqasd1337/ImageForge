using ImageForge.Core.Workspace;

namespace ImageForge.Core.Tests.Workspace;

public sealed class WorkspaceLayoutTests
{
    private static readonly Guid RunId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid ImageId = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    [Fact]
    public void Paths_are_fixed_folders_under_the_work_dir()
    {
        var layout = WorkspaceLayout.Create(@"E:\IFWork\");

        layout.Root.ShouldBe(@"E:\IFWork");
        layout.Base.ShouldBe(@"E:\IFWork\base");
        layout.StateFile.ShouldBe(@"E:\IFWork\state.json");
        layout.Logs.ShouldBe(@"E:\IFWork\logs");
        layout.Scratch.ShouldBe(@"E:\IFWork\scratch");
        layout.Mount(ImageId).ShouldBe(@"E:\IFWork\mount\bbbbbbbb000040008000000000000002");
        layout.Journal(RunId).ShouldBe(@"E:\IFWork\journal\aaaaaaaa000040008000000000000001.jsonl");

        var run = layout.Run(RunId);
        run.Root.ShouldBe(@"E:\IFWork\runs\aaaaaaaa000040008000000000000001");
        run.Working.ShouldBe(run.Root + @"\working");
        run.Completed.ShouldBe(run.Root + @"\completed");
        run.Assemble.ShouldBe(run.Root + @"\assemble");
    }

    [Fact]
    public void Every_path_stays_inside_the_write_scope()
    {
        var layout = WorkspaceLayout.Create(@"E:\IFWork");
        var run = layout.Run(RunId);

        foreach (var path in new[] { layout.Base, layout.StateFile, layout.Logs, layout.Scratch, layout.Mount(ImageId), layout.Journal(RunId), run.Working, run.Completed, run.Assemble })
        {
            layout.Scope.Check(path).IsAllowed.ShouldBeTrue(path);
        }
    }

    [Theory]
    [InlineData(@"E:\")]
    [InlineData(@"\\server\share\work")]
    [InlineData(@"work")]
    public void Unusable_work_dirs_are_rejected(string workDir)
    {
        Should.Throw<ArgumentException>(() => WorkspaceLayout.Create(workDir));
    }
}
