using ImageForge.Core.Workspace;

namespace ImageForge.Core.Tests.Workspace;

public sealed class WorkspaceLocationsTests
{
    [Fact]
    public void Separate_local_folders_are_accepted()
    {
        WorkspaceLocations.Validate(@"D:\Media\Win11", @"E:\IFWork", @"D:\Out\Win11-custom").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(@"D:\Media", @"D:\Media\work", @"E:\Out", LocationProblemKind.Nested, "WorkDir", "Source")]
    [InlineData(@"D:\Media", @"E:\Work", @"D:\Media\out", LocationProblemKind.Nested, "OutputDir", "Source")]
    [InlineData(@"D:\Work\media", @"D:\Work", @"E:\Out", LocationProblemKind.Nested, "Source", "WorkDir")]
    [InlineData(@"D:\Media", @"E:\Work", @"E:\Work\out", LocationProblemKind.Nested, "OutputDir", "WorkDir")]
    [InlineData(@"D:\Out\media", @"E:\Work", @"D:\Out", LocationProblemKind.Nested, "Source", "OutputDir")]
    [InlineData(@"D:\Media", @"d:\media", @"E:\Out", LocationProblemKind.Same, "Source", "WorkDir")]
    public void Overlapping_folders_are_rejected(string source, string work, string output, LocationProblemKind kind, string first, string second)
    {
        var problems = WorkspaceLocations.Validate(source, work, output);

        problems.ShouldContain(new LocationProblem(kind, first, second));
    }

    [Fact]
    public void Lookalike_prefixes_do_not_count_as_nesting()
    {
        WorkspaceLocations.Validate(@"D:\Media", @"D:\Media2", @"D:\MediaOut").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(@"\\server\share\media")]
    [InlineData(@"media")]
    [InlineData(@"\\?\D:\media")]
    public void Non_local_paths_are_rejected(string work)
    {
        WorkspaceLocations.Validate(@"D:\Media", work, @"E:\Out")
            .ShouldContain(new LocationProblem(LocationProblemKind.NotLocalPath, "WorkDir", null));
    }
}
