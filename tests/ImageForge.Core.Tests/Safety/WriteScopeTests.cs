using ImageForge.Core.Safety;

namespace ImageForge.Core.Tests.Safety;

public sealed class WriteScopeTests
{
    private const string Root = @"D:\Work\mount\3f2a";

    private static WriteScope Scope() => WriteScope.Create(Root);

    [Theory]
    [InlineData(@"D:\Work\mount\3f2a\Windows\System32\config\SOFTWARE", @"D:\Work\mount\3f2a\Windows\System32\config\SOFTWARE")]
    [InlineData(@"d:\work\MOUNT\3F2A\Windows", @"d:\work\MOUNT\3F2A\Windows")]
    [InlineData(@"D:/Work/mount/3f2a/Windows", @"D:\Work\mount\3f2a\Windows")]
    [InlineData(@"D:\Work\mount\3f2a\.\Windows\.\System32", @"D:\Work\mount\3f2a\Windows\System32")]
    [InlineData(@"D:\Work\mount\3f2a\Windows\..\Users", @"D:\Work\mount\3f2a\Users")]
    [InlineData(@"D:\Work\mount\3f2a\\Windows\", @"D:\Work\mount\3f2a\Windows")]
    [InlineData(@"D:\Work\mount\3f2a", @"D:\Work\mount\3f2a")]
    public void Allows_paths_inside_the_root(string candidate, string expected)
    {
        var result = Scope().Check(candidate);

        result.IsAllowed.ShouldBeTrue();
        result.Violation.ShouldBe(WriteScopeViolation.None);
        result.NormalizedPath.ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"D:\Work\mount\3f2a\..\other")]
    [InlineData(@"D:\Work\mount\3f2a\Windows\..\..\3f2b")]
    [InlineData(@"D:\Work\mount\3f2ab\Windows")]
    [InlineData(@"D:\Work\mount")]
    [InlineData(@"C:\Windows\System32\config\SOFTWARE")]
    [InlineData(@"D:\..\..\Work\mount\3f2a\..\..\..\Windows")]
    public void Rejects_paths_outside_the_root(string candidate)
    {
        var result = Scope().Check(candidate);

        result.IsAllowed.ShouldBeFalse();
        result.Violation.ShouldBe(WriteScopeViolation.OutsideScope);
        result.NormalizedPath.ShouldBeNull();
    }

    [Theory]
    [InlineData(@"Windows\System32")]
    [InlineData(@"\Work\mount\3f2a\Windows")]
    [InlineData(@"D:Work\mount\3f2a")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_relative_paths(string candidate)
    {
        Scope().Check(candidate).Violation.ShouldBe(WriteScopeViolation.NotAbsolute);
    }

    [Theory]
    [InlineData(@"\\?\D:\Work\mount\3f2a\Windows")]
    [InlineData(@"\\.\D:\Work\mount\3f2a\Windows")]
    [InlineData(@"\\server\share\mount\3f2a")]
    [InlineData(@"//?/D:/Work/mount/3f2a")]
    public void Rejects_device_and_network_paths(string candidate)
    {
        Scope().Check(candidate).Violation.ShouldBe(WriteScopeViolation.UnsupportedPathForm);
    }

    [Theory]
    [InlineData(@"D:\Work\mount\3f2a\Windows\file.txt:stream")]
    [InlineData(@"D:\Work\mount\3f2a\Windows\a*b")]
    [InlineData(@"D:\Work\mount\3f2a\Windows\a?b")]
    [InlineData(@"D:\Work\mount\3f2a\Windows\a""b")]
    [InlineData(@"D:\Work\mount\3f2a\Windows\a<b")]
    [InlineData(@"D:\Work\mount\3f2a\Windows\a|b")]
    [InlineData("D:\\Work\\mount\\3f2a\\Windows\\a\u0001b")]
    [InlineData(@"D:\Work\mount\3f2a\Windows.")]
    [InlineData(@"D:\Work\mount\3f2a\Windows ")]
    [InlineData(@"D:\Work\mount\3f2a\...")]
    public void Rejects_invalid_or_ambiguous_segments(string candidate)
    {
        Scope().Check(candidate).Violation.ShouldBe(WriteScopeViolation.InvalidSegment);
    }

    [Theory]
    [InlineData(@"D:\Work\mount\3f2a\CON")]
    [InlineData(@"D:\Work\mount\3f2a\nul.txt")]
    [InlineData(@"D:\Work\mount\3f2a\Windows\com1")]
    [InlineData(@"D:\Work\mount\3f2a\LPT9.log")]
    public void Rejects_reserved_device_names(string candidate)
    {
        Scope().Check(candidate).Violation.ShouldBe(WriteScopeViolation.InvalidSegment);
    }

    [Theory]
    [InlineData(@"Work\mount")]
    [InlineData(@"\\?\D:\Work")]
    [InlineData(@"D:\Work\..\..")]
    [InlineData(@"D:\")]
    public void Create_rejects_unusable_roots(string root)
    {
        Should.Throw<ArgumentException>(() => WriteScope.Create(root));
    }

    [Fact]
    public void Root_is_normalized_on_create()
    {
        WriteScope.Create(@"D:/Work/mount/3f2a/").Root.ShouldBe(Root);
    }
}
