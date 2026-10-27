using ImageForge.Core.Hosting;

namespace ImageForge.Core.Tests.Hosting;

public sealed class EngineLaunchArgumentsTests
{
    private const string Pipe = "ImageForge.Engine.0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e";

    [Fact]
    public void Round_trips_through_the_command_line()
    {
        var original = new EngineLaunchArguments(Pipe, 4242, 133_700_000_000_000_000);

        var parsed = EngineLaunchArguments.Parse(original.ToArgumentList());

        parsed.ShouldBe(original);
    }

    [Fact]
    public void Creates_a_random_pipe_name_per_launch()
    {
        var a = EngineLaunchArguments.CreateFor(1, 10);
        var b = EngineLaunchArguments.CreateFor(1, 10);

        a.PipeName.ShouldNotBe(b.PipeName);
        EngineLaunchArguments.IsValidPipeName(a.PipeName).ShouldBeTrue();
    }

    [Theory]
    [InlineData("ImageForge.Engine.short")]
    [InlineData(@"ImageForge.Engine.0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e\..\x")]
    [InlineData("Other.0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e")]
    [InlineData("ImageForge.Engine.0B1C2D3E4F5A6B7C8D9E0F1A2B3C4D5E")]
    [InlineData("")]
    public void Rejects_unexpected_pipe_names(string pipe)
    {
        EngineLaunchArguments.IsValidPipeName(pipe).ShouldBeFalse();
    }

    [Theory]
    [InlineData("--pipe", "ImageForge.Engine.0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e", "--client-pid", "0", "--client-start", "1")]
    [InlineData("--pipe", "ImageForge.Engine.0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e", "--client-pid", "12", "--client-start", "-1")]
    [InlineData("--pipe", "ImageForge.Engine.0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e", "--client-pid", "x", "--client-start", "1")]
    [InlineData("--pipe", "bad", "--client-pid", "12", "--client-start", "1")]
    [InlineData("--pipe", "ImageForge.Engine.0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e", "--client-pid", "12")]
    [InlineData("--pipe", "ImageForge.Engine.0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e", "--client-pid", "12", "--client-start", "1", "--extra", "1")]
    [InlineData("--pipe", "ImageForge.Engine.0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e", "--pipe", "ImageForge.Engine.0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e", "--client-pid", "12", "--client-start", "1")]
    public void Rejects_malformed_arguments(params string[] args)
    {
        Should.Throw<FormatException>(() => EngineLaunchArguments.Parse(args));
    }
}
