using ImageForge.Core.Workspace;

namespace ImageForge.Core.Tests.Workspace;

public sealed class ImageEditionsTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    // The editions of the 26H2 fixture, in its order (S2-TARGETS-INVENTORY).
    internal const string FixtureXml = "<WIM>" +
        "<IMAGE INDEX=\"1\"><NAME>Windows 11 Education</NAME></IMAGE>" +
        "<IMAGE INDEX=\"2\"><NAME>Windows 11 Enterprise</NAME></IMAGE>" +
        "<IMAGE INDEX=\"3\"><NAME>Windows 11 Pro</NAME></IMAGE>" +
        "<IMAGE INDEX=\"4\"><NAME>Windows 11 Pro Education</NAME></IMAGE>" +
        "<IMAGE INDEX=\"5\"><NAME>Windows 11 Pro for Workstations</NAME></IMAGE></WIM>";

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void The_editions_are_read_from_the_install_wim_of_the_media()
    {
        _dir.WriteFile("media/sources/readme.txt", "x");
        File.WriteAllBytes(_dir.Combine("media/sources/install.wim"), Updates.WimArchiveTests.BuildWim(FixtureXml, images: 5));

        var editions = ImageEditions.Read(_dir.Combine("media"));

        editions.Select(e => e.Index).ShouldBe([1, 2, 3, 4, 5]);
        editions[2].Name.ShouldBe("Windows 11 Pro");
    }

    [Fact]
    public void A_file_that_is_not_a_wim_has_no_editions()
    {
        _dir.WriteFile("media/sources/install.wim", "WIM");

        ImageEditions.Read(_dir.Combine("media")).ShouldBeEmpty();
        ImageEditions.Read(_dir.Combine("none")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(5, 5)]
    [InlineData(9, 3)]
    public void Pro_is_chosen_unless_another_edition_of_the_source_is_asked_for(int requested, int chosen)
    {
        IReadOnlyList<ImageEdition> editions =
            [new(1, "Windows 11 Education"), new(2, "Windows 11 Enterprise"), new(3, "Windows 11 Pro"), new(4, "Windows 11 Pro Education"), new(5, "Windows 11 Pro for Workstations")];

        ImageEditions.Choose(editions, requested).ShouldBe(chosen);
    }

    [Fact]
    public void Without_pro_or_editions_the_first_image_is_chosen()
    {
        ImageEditions.Choose([new(1, "Windows 11 Home"), new(2, "Windows 11 Education")], 0).ShouldBe(1);
        ImageEditions.Choose([], 0).ShouldBe(1);
        ImageEditions.Choose([], 4).ShouldBe(1);
    }
}
