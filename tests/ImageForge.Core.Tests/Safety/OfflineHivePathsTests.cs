using ImageForge.Core.Safety;

namespace ImageForge.Core.Tests.Safety;

public sealed class OfflineHivePathsTests
{
    private static readonly WriteScope Mount = WriteScope.Create(@"D:\Work\mount\img");

    [Fact]
    public void Resolves_the_hive_file_inside_the_mount()
    {
        OfflineHivePaths.Resolve(Mount, @"Windows\System32\config\SOFTWARE")
            .ShouldBe(@"D:\Work\mount\img\Windows\System32\config\SOFTWARE");
    }

    [Theory]
    [InlineData(@"..\..\..\Windows\System32\config\SOFTWARE")]
    [InlineData(@"C:\Windows\System32\config\SOFTWARE")]
    [InlineData(@"Windows\System32\config\SOFTWARE:stream")]
    public void Refuses_anything_that_leaves_the_mount(string hiveFile)
    {
        Should.Throw<UnauthorizedAccessException>(() => OfflineHivePaths.Resolve(Mount, hiveFile));
    }
}
