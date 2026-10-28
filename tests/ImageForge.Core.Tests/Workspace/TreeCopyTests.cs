using ImageForge.Core.Workspace;

namespace ImageForge.Core.Tests.Workspace;

public sealed class TreeCopyTests
{
    [Fact]
    public async Task Copy_returns_the_hashes_of_the_copied_bytes_and_keeps_times()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("src/setup.exe", "setup");
        dir.WriteFile("src/sources/boot.wim", "BOOT");
        dir.WriteFile("src/sources/install.wim", "WIM");
        var readOnly = dir.Combine("src", "sources", "boot.wim");
        File.SetLastWriteTimeUtc(readOnly, new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        var copied = await TreeCopy.CopyAsync(dir.Combine("src"), dir.Combine("dst"), ["sources/install.wim"], TestContext.Current.CancellationToken);

        var source = await SourceManifest.ComputeAsync(dir.Combine("src"), TestContext.Current.CancellationToken);
        var expected = source.Entries.Where(e => e.RelativePath != "sources/install.wim").ToList();
        SourceManifest.Compare(new SourceManifest(expected, string.Empty), SourceManifest.FromEntries(copied)).ShouldBeEmpty();
        File.Exists(dir.Combine("dst", "sources", "install.wim")).ShouldBeFalse();

        var target = dir.Combine("dst", "sources", "boot.wim");
        File.ReadAllText(target).ShouldBe("BOOT");
        File.GetLastWriteTimeUtc(target).ShouldBe(File.GetLastWriteTimeUtc(readOnly));
        File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly).ShouldBeFalse();
        (await SourceManifest.CheckMetadataAsync(dir.Combine("dst"), SourceManifest.FromEntries(expected), [], TestContext.Current.CancellationToken)).ShouldBeEmpty();
        File.SetAttributes(readOnly, FileAttributes.Normal);
    }

    [Fact]
    public async Task Copy_does_not_overwrite()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("a.txt", "new");
        dir.WriteFile("b.txt", "old");

        await Should.ThrowAsync<IOException>(() => TreeCopy.CopyFileAsync(dir.Combine("a.txt"), dir.Combine("b.txt"), TestContext.Current.CancellationToken));

        File.ReadAllText(dir.Combine("b.txt")).ShouldBe("old");
    }
}
