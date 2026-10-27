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
}
