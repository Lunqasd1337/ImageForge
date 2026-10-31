using ImageForge.Core.Workspace;
using Shouldly;
using Xunit;

namespace ImageForge.Core.Tests.Workspace;

public sealed class DiskSpaceTests
{
    private const long GiB = 1L << 30;

    private static readonly SpaceInputs Typical = new(SourceBytes: 7 * GiB, BaseCopyPresent: false, InstallWimBytes: 6 * GiB, ImageExpandedBytes: 18 * GiB)
    {
        UpdateBytes = 4 * GiB,
    };

    [Fact]
    public void The_peak_counts_the_base_copy_the_working_copy_the_mount_the_updates_and_the_export()
    {
        var estimate = DiskSpace.Estimate(Typical);

        estimate.WorkBytes.ShouldBe((7 + 6 + 18 + 3 * 4 + 6) * GiB + DiskSpace.Reserve);
        estimate.OutputBytes.ShouldBe(7 * GiB + DiskSpace.Reserve);
        DiskSpace.Estimate(Typical with { BaseCopyPresent = true }).WorkBytes.ShouldBe(estimate.WorkBytes - 7 * GiB);
    }

    [Fact]
    public void WinRe_and_boot_wim_add_their_working_copies()
    {
        var plain = DiskSpace.Estimate(Typical).WorkBytes;

        DiskSpace.Estimate(Typical with { ServicesWinRe = true }).WorkBytes.ShouldBe(plain + DiskSpace.WinReAllowance);
        DiskSpace.Estimate(Typical with { BootWimBytes = GiB }).WorkBytes.ShouldBe(plain + 2 * GiB);
    }

    [Fact]
    public void An_unknown_expanded_size_takes_a_safe_default()
    {
        DiskSpace.Estimate(Typical with { ImageExpandedBytes = null }).WorkBytes
            .ShouldBe(DiskSpace.Estimate(Typical with { ImageExpandedBytes = DiskSpace.DefaultExpandedBytes }).WorkBytes);
    }

    [Fact]
    public void Separate_volumes_are_checked_separately_and_one_volume_against_the_sum()
    {
        var estimate = new SpaceEstimate(30 * GiB, 8 * GiB);

        DiskSpace.Check(estimate, @"C:\IF\ws", @"D:\out", root => root == @"C:\" ? 31 * GiB : 9 * GiB).ShouldBeEmpty();
        DiskSpace.Check(estimate, @"C:\IF\ws", @"C:\IF\out", _ => 31 * GiB).ShouldHaveSingleItem().ShouldContain("38.0 GB");

        var problems = DiskSpace.Check(estimate, @"C:\IF\ws", @"D:\out", root => root == @"C:\" ? 29 * GiB : 7 * GiB);
        problems.Count.ShouldBe(2);
        problems[0].ShouldContain(@"C:\");
        problems[0].ShouldContain("30.0 GB");
        problems[0].ShouldContain("29.0 GB");
    }

    [Fact]
    public void A_volume_whose_free_space_cannot_be_read_is_not_blocked()
    {
        DiskSpace.Check(new SpaceEstimate(30 * GiB, 8 * GiB), @"C:\IF\ws", @"D:\out", _ => null).ShouldBeEmpty();
    }
}
