using ImageForge.Core.Execution;
using ImageForge.Core.Registry;

namespace ImageForge.Core.Tests.Execution;

public sealed class HiveLoadNameTests
{
    [Fact]
    public void Names_are_unique_per_run_and_hive_and_marked_as_ours()
    {
        var run = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

        var name = HiveLoadName.Create(run, OfflineHive.Software);

        name.ShouldBe("ImageForge_0f1e2d3c4b5a69788796a5b4c3d2e1f0_SOFTWARE");
        HiveLoadName.IsOwned(name).ShouldBeTrue();
        HiveLoadName.Create(Guid.NewGuid(), OfflineHive.Software).ShouldNotBe(name);
    }

    [Theory]
    [InlineData("SOFTWARE")]
    [InlineData("ImageForge_SOFTWARE")]
    [InlineData("ImageForge_0f1e2d3c4b5a69788796a5b4c3d2e1f0_")]
    [InlineData("OFFLINE_SOFTWARE")]
    public void Foreign_names_are_not_ours(string name)
    {
        HiveLoadName.IsOwned(name).ShouldBeFalse();
    }
}
