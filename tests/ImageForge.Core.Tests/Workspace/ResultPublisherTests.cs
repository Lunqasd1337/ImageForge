using ImageForge.Core.Workspace;

namespace ImageForge.Core.Tests.Workspace;

public sealed class ResultPublisherTests
{
    private static readonly Guid RunId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");

    [Theory]
    [InlineData(false, false, false, PublicationRecoveryAction.None)]
    [InlineData(true, false, false, PublicationRecoveryAction.None)]
    [InlineData(true, false, true, PublicationRecoveryAction.DiscardStaging)]
    [InlineData(false, false, true, PublicationRecoveryAction.DiscardStaging)]
    [InlineData(false, true, false, PublicationRecoveryAction.RestorePrevious)]
    [InlineData(false, true, true, PublicationRecoveryAction.RestorePrevious)]
    [InlineData(true, true, false, PublicationRecoveryAction.DeletePrevious)]
    [InlineData(true, true, true, PublicationRecoveryAction.RecoveryRequired)]
    public void Recovery_decision_never_promotes_staging(bool output, bool previous, bool staging, PublicationRecoveryAction expected)
    {
        PublicationRecovery.Decide(output, previous, staging).ShouldBe(expected);
    }

    [Fact]
    public void Staging_and_previous_folders_are_siblings_of_the_output()
    {
        using var dir = new TempDirectory();
        var output = dir.Combine("out", "Win11");

        var paths = PublicationPaths.For(output, RunId);

        Path.GetDirectoryName(paths.Staging).ShouldBe(dir.Combine("out"));
        Path.GetDirectoryName(paths.Previous).ShouldBe(dir.Combine("out"));
        Path.GetFileName(paths.Staging).ShouldBe(".Win11.imageforge-aaaaaaaa000040008000000000000001.staging");
        Path.GetFileName(paths.Previous).ShouldBe(".Win11.imageforge-aaaaaaaa000040008000000000000001.previous");
    }

    [Fact]
    public void Publishing_replaces_the_previous_result_and_cleans_up()
    {
        using var dir = new TempDirectory();
        var output = dir.Combine("out", "Win11");
        dir.WriteFile("out/Win11/old.txt", "old");
        var paths = PublicationPaths.For(output, RunId);
        Directory.CreateDirectory(paths.Staging);
        File.WriteAllText(Path.Combine(paths.Staging, "new.txt"), "new");
        var events = new List<string>();

        ResultPublisher.Publish(paths, step => events.Add(step));

        File.Exists(Path.Combine(output, "new.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(output, "old.txt")).ShouldBeFalse();
        Directory.Exists(paths.Staging).ShouldBeFalse();
        Directory.Exists(paths.Previous).ShouldBeFalse();
        events.ShouldBe(["previous-moved", "switched", "previous-deleted"]);
    }

    [Fact]
    public void First_publication_has_no_previous_result()
    {
        using var dir = new TempDirectory();
        var output = dir.Combine("out", "Win11");
        Directory.CreateDirectory(dir.Combine("out"));
        var paths = PublicationPaths.For(output, RunId);
        Directory.CreateDirectory(paths.Staging);
        var events = new List<string>();

        ResultPublisher.Publish(paths, step => events.Add(step));

        Directory.Exists(output).ShouldBeTrue();
        events.ShouldBe(["switched"]);
    }

    [Fact]
    public void Recovery_after_a_crash_between_moves_restores_the_previous_result()
    {
        using var dir = new TempDirectory();
        var output = dir.Combine("out", "Win11");
        var paths = PublicationPaths.For(output, RunId);
        dir.WriteFile("out/.Win11.imageforge-aaaaaaaa000040008000000000000001.previous/old.txt", "old");
        Directory.CreateDirectory(paths.Staging);

        var action = ResultPublisher.Recover(paths);

        action.ShouldBe(PublicationRecoveryAction.RestorePrevious);
        File.ReadAllText(Path.Combine(output, "old.txt")).ShouldBe("old");
        Directory.Exists(paths.Staging).ShouldBeFalse();
        Directory.Exists(paths.Previous).ShouldBeFalse();
    }

    [Fact]
    public void Publishing_without_staging_is_refused()
    {
        using var dir = new TempDirectory();
        var paths = PublicationPaths.For(dir.Combine("out", "Win11"), RunId);

        Should.Throw<DirectoryNotFoundException>(() => ResultPublisher.Publish(paths, _ => { }));
    }
}
