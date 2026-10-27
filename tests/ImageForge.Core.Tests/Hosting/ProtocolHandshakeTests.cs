using ImageForge.Core.Contracts;

namespace ImageForge.Core.Tests.Hosting;

public sealed class ProtocolHandshakeTests
{
    private static EngineInfo Info(int protocol) =>
        new(protocol, "1.0.0", Guid.NewGuid(), 100, ["registry.offline.sz"]);

    [Fact]
    public void Accepts_an_engine_with_the_same_protocol_version()
    {
        ProtocolHandshake.Check(Info(EngineProtocol.Version)).ShouldBe(HandshakeResult.Compatible);
    }

    [Theory]
    [InlineData(EngineProtocol.Version - 1)]
    [InlineData(EngineProtocol.Version + 1)]
    public void Rejects_other_protocol_versions(int version)
    {
        ProtocolHandshake.Check(Info(version)).ShouldBe(HandshakeResult.IncompatibleProtocol);
    }
}
