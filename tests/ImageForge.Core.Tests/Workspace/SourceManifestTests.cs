using ImageForge.Core.Workspace;

namespace ImageForge.Core.Tests.Workspace;

public sealed class SourceManifestTests
{
    [Fact]
    public async Task Same_tree_produces_no_differences()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("sources/install.wim", "wim");
        dir.WriteFile("setup.exe", "exe");

        var a = await SourceManifest.ComputeAsync(dir.Path, TestContext.Current.CancellationToken);
        var b = await SourceManifest.ComputeAsync(dir.Path, TestContext.Current.CancellationToken);

        a.Entries.Select(e => e.RelativePath).ShouldBe(["setup.exe", "sources/install.wim"]);
        SourceManifest.Compare(a, b).ShouldBeEmpty();
        a.Fingerprint.ShouldBe(b.Fingerprint);
    }

    [Fact]
    public async Task Changed_added_and_removed_files_are_reported()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("sources/install.wim", "wim");
        dir.WriteFile("setup.exe", "exe");
        var before = await SourceManifest.ComputeAsync(dir.Path, TestContext.Current.CancellationToken);

        dir.WriteFile("sources/install.wim", "WIM");
        File.Delete(dir.Combine("setup.exe"));
        dir.WriteFile("autorun.inf", "x");
        var after = await SourceManifest.ComputeAsync(dir.Path, TestContext.Current.CancellationToken);

        SourceManifest.Compare(before, after).ShouldBe(
        [
            new ManifestDifference(ManifestChange.Added, "autorun.inf"),
            new ManifestDifference(ManifestChange.Removed, "setup.exe"),
            new ManifestDifference(ManifestChange.Changed, "sources/install.wim"),
        ]);
        after.Fingerprint.ShouldNotBe(before.Fingerprint);
    }

    [Fact]
    public async Task Last_write_time_is_not_part_of_the_fingerprint()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("setup.exe", "exe");
        var before = await SourceManifest.ComputeAsync(dir.Path, TestContext.Current.CancellationToken);

        File.SetLastWriteTimeUtc(dir.Combine("setup.exe"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var after = await SourceManifest.ComputeAsync(dir.Path, TestContext.Current.CancellationToken);

        after.Fingerprint.ShouldBe(before.Fingerprint);
        SourceManifest.Compare(before, after).ShouldBeEmpty();
    }

    [Fact]
    public async Task Metadata_check_reports_size_time_added_and_removed_files()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("a.txt", "a");
        dir.WriteFile("b.txt", "b");
        dir.WriteFile("c.txt", "c");
        var expected = await SourceManifest.ComputeAsync(dir.Path, TestContext.Current.CancellationToken);

        dir.WriteFile("a.txt", "longer");
        File.SetLastWriteTimeUtc(dir.Combine("b.txt"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.Delete(dir.Combine("c.txt"));
        dir.WriteFile("d.txt", "d");

        var differences = await SourceManifest.CheckMetadataAsync(dir.Path, expected, [], TestContext.Current.CancellationToken);

        differences.ShouldBe(
        [
            new ManifestDifference(ManifestChange.Changed, "a.txt"),
            new ManifestDifference(ManifestChange.Changed, "b.txt"),
            new ManifestDifference(ManifestChange.Removed, "c.txt"),
            new ManifestDifference(ManifestChange.Added, "d.txt"),
        ]);
    }

    [Fact]
    public async Task Metadata_check_hashes_only_the_listed_files()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("setup.exe", "exe");
        dir.WriteFile("sources/install.wim", "wim");
        var expected = await SourceManifest.ComputeAsync(dir.Path, TestContext.Current.CancellationToken);

        // Same size and time, different content: only a hashed file can show it.
        Rewrite(dir.Combine("setup.exe"), "EXE");
        Rewrite(dir.Combine("sources", "install.wim"), "WIM");

        var differences = await SourceManifest.CheckMetadataAsync(dir.Path, expected, ["sources/install.wim"], TestContext.Current.CancellationToken);

        differences.ShouldBe([new ManifestDifference(ManifestChange.Changed, "sources/install.wim")]);
    }

    internal static void Rewrite(string path, string content)
    {
        var time = File.GetLastWriteTimeUtc(path);
        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, time);
        File.SetAttributes(path, attributes);
    }
}
