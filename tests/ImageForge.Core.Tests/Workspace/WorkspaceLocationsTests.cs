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

public sealed class WorkspaceVolumesTests
{
    private static VolumeFacts? Volume(string root) => root switch
    {
        @"C:\" => new VolumeFacts(VolumeKind.Local, "NTFS"),
        @"E:\" => new VolumeFacts(VolumeKind.Removable, "exFAT"),
        @"N:\" => new VolumeFacts(VolumeKind.Network, "NTFS"),
        @"R:\" => new VolumeFacts(VolumeKind.Local, "ReFS"),
        _ => null,
    };

    private static bool Synced(string path) => path.StartsWith(@"C:\Users\nik\OneDrive", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<LocationProblem> Check(string source, string work, string output) =>
        WorkspaceLocations.CheckVolumes(source, work, output, Volume, Synced);

    [Fact]
    public void Local_ntfs_folders_pass()
    {
        Check(@"C:\IF\media", @"C:\IF\ws", @"C:\IF\out").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(@"N:\ws", LocationProblemKind.NetworkVolume)]
    [InlineData(@"E:\ws", LocationProblemKind.NotNtfs)]
    [InlineData(@"R:\ws", LocationProblemKind.NotNtfs)]
    [InlineData(@"C:\Users\nik\OneDrive\ws", LocationProblemKind.SyncedFolder)]
    public void The_work_folder_must_be_local_ntfs_and_not_synced(string work, LocationProblemKind kind)
    {
        var problem = Check(@"C:\IF\media", work, @"C:\IF\out").ShouldHaveSingleItem();

        problem.Kind.ShouldBe(kind);
        problem.Location.ShouldBe(WorkspaceLocations.WorkDir);
        problem.IsWarning.ShouldBeFalse();
    }

    [Fact]
    public void A_network_or_synced_source_or_output_is_a_warning()
    {
        var problems = Check(@"N:\media", @"C:\IF\ws", @"C:\Users\nik\OneDrive\out");

        problems.Select(p => (p.Kind, p.Location)).ShouldBe(
            [(LocationProblemKind.NetworkVolume, WorkspaceLocations.Source), (LocationProblemKind.SyncedFolder, WorkspaceLocations.OutputDir)]);
        problems.ShouldAllBe(p => p.IsWarning);
    }

    [Fact]
    public void An_unknown_volume_is_not_blocked()
    {
        Check(@"Z:\media", @"Z:\ws", @"Z:\out").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(@"C:\Users\nik\OneDrive\IF", true)]
    [InlineData(@"C:\Users\nik\OneDriveX\IF", false)]
    [InlineData(@"C:\IF", false)]
    [InlineData(@"D:\Dropbox\IF", true)]
    [InlineData(@"G:\My Drive\IF", true)]
    public void Synced_folders_are_known_by_their_roots_and_names(string path, bool synced)
    {
        WorkspaceLocations.IsSynced(path, [@"C:\Users\nik\OneDrive"]).ShouldBe(synced);
    }
}
