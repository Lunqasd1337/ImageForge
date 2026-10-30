using ImageForge.Core.Servicing;

namespace ImageForge.Core.Tests.Servicing;

public sealed class DismCliArgumentsTests
{
    [Fact]
    public void Export_passes_each_value_as_a_separate_argument()
    {
        var args = DismCliArguments.ExportImage(@"D:\Work\base\sources\install.wim", 1, @"D:\Work\runs\r1\assemble\install.wim");

        args.ShouldBe(
        [
            "/English",
            "/Export-Image",
            @"/SourceImageFile:D:\Work\base\sources\install.wim",
            "/SourceIndex:1",
            @"/DestinationImageFile:D:\Work\runs\r1\assemble\install.wim",
            "/Compress:max",
            "/CheckIntegrity",
        ]);
    }

    [Fact]
    public void Export_from_esd_skips_the_integrity_check()
    {
        var args = DismCliArguments.ExportImage(@"D:\media\sources\install.esd", 6, @"D:\fixture\install.wim");

        args.ShouldNotContain("/CheckIntegrity");
        args.ShouldContain("/Compress:max");
    }

    [Fact]
    public void A_bootable_export_marks_the_image_bootable()
    {
        var args = DismCliArguments.ExportImage(@"D:\Work\runs\r1\boot\boot.wim", 2, @"D:\Work\runs\r1\media\sources\boot.wim", bootable: true);

        args[^1].ShouldBe("/Bootable");
        DismCliArguments.ExportImage(@"D:\in\boot.wim", 1, @"D:\out\boot.wim").ShouldNotContain("/Bootable");
    }

    [Theory]
    [InlineData(@"install.wim", @"D:\out\install.wim", 1)]
    [InlineData(@"D:\in\install.wim", @"\\server\share\install.wim", 1)]
    [InlineData(@"D:\in\in""stall.wim", @"D:\out\install.wim", 1)]
    [InlineData(@"D:\in\install.wim", @"D:\out\install.wim", 0)]
    [InlineData(@"D:\in\install.wim", @"d:\IN\install.wim", 1)]
    public void Export_rejects_unsafe_input(string source, string destination, int index)
    {
        Should.Throw<ArgumentException>(() => DismCliArguments.ExportImage(source, index, destination));
    }
}
