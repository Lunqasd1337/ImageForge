using ImageForge.Core.Safety;
using ImageForge.Core.Tests.Workspace;

namespace ImageForge.Core.Tests.Safety;

public sealed class MountFileWriterTests
{
    [Fact]
    public void Writes_the_file_and_creates_missing_folders()
    {
        using var mount = new TempDirectory();

        new MountFileWriter(mount.Path).Write(@"ProgramData\ImageForge\FirstLogon\a.reg", [1, 2, 3]);

        File.ReadAllBytes(mount.Combine("ProgramData", "ImageForge", "FirstLogon", "a.reg")).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void Replaces_an_existing_regular_file()
    {
        using var mount = new TempDirectory();
        mount.WriteFile("ProgramData/a.reg", "old");

        new MountFileWriter(mount.Path).Write(@"ProgramData\a.reg", [9]);

        File.ReadAllBytes(mount.Combine("ProgramData", "a.reg")).ShouldBe(new byte[] { 9 });
    }

    [Theory]
    [InlineData(@"..\a.reg")]
    [InlineData(@"ProgramData\..\..\a.reg")]
    [InlineData(@"ProgramData\.\a.reg")]
    [InlineData(@"\ProgramData\a.reg")]
    [InlineData(@"C:\a.reg")]
    [InlineData(@"ProgramData\\a.reg")]
    [InlineData(@"ProgramData\a.reg:stream")]
    [InlineData("")]
    public void Refuses_a_path_that_is_not_a_plain_relative_path(string relative)
    {
        using var mount = new TempDirectory();

        Should.Throw<UnauthorizedAccessException>(() => new MountFileWriter(mount.Path).Write(relative, [1]));
    }

    [Fact]
    public void Refuses_to_write_through_a_linked_folder()
    {
        using var mount = new TempDirectory();
        using var outside = new TempDirectory();
        Directory.CreateSymbolicLink(mount.Combine("ProgramData"), outside.Path);

        Should.Throw<UnauthorizedAccessException>(() => new MountFileWriter(mount.Path).Write(@"ProgramData\a.reg", [1]));

        Directory.EnumerateFileSystemEntries(outside.Path).ShouldBeEmpty();
    }

    [Fact]
    public void Refuses_to_replace_a_linked_file()
    {
        using var mount = new TempDirectory();
        using var outside = new TempDirectory();
        var target = outside.WriteFile("t.reg", "keep");
        Directory.CreateDirectory(mount.Combine("ProgramData"));
        File.CreateSymbolicLink(mount.Combine("ProgramData", "a.reg"), target);

        Should.Throw<UnauthorizedAccessException>(() => new MountFileWriter(mount.Path).Write(@"ProgramData\a.reg", [1]));

        File.ReadAllText(target).ShouldBe("keep");
    }
}
