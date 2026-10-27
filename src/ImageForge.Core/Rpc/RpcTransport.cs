using System.Buffers;
using System.Buffers.Binary;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace ImageForge.Core.Rpc;

/// <summary>Per-direction message size limits for the UI ↔ engine channel.</summary>
public sealed record RpcLimits(int MaxIncomingMessageBytes, int MaxOutgoingMessageBytes)
{
    private const int MiB = 1024 * 1024;

    /// <summary>The engine accepts small requests and may send larger inventories back.</summary>
    public static RpcLimits Engine { get; } = new(MaxIncomingMessageBytes: 1 * MiB, MaxOutgoingMessageBytes: 16 * MiB);

    public static RpcLimits Client { get; } = new(MaxIncomingMessageBytes: 16 * MiB, MaxOutgoingMessageBytes: 1 * MiB);
}

public sealed class RpcMessageTooLargeException(string message) : IOException(message);

/// <summary>
/// Frames each JSON-RPC message as a 4-byte big-endian length followed by the payload.
/// A declared length above the limit fails before the body is read, which drops the connection.
/// </summary>
public sealed class BoundedMessageHandler : MessageHandlerBase
{
    private const int HeaderSize = sizeof(int);

    private readonly Stream _stream;
    private readonly RpcLimits _limits;
    private readonly byte[] _readHeader = new byte[HeaderSize];
    private readonly byte[] _writeHeader = new byte[HeaderSize];

    public BoundedMessageHandler(Stream stream, IJsonRpcMessageFormatter formatter, RpcLimits limits)
        : base(formatter)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxIncomingMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxOutgoingMessageBytes);
        _stream = stream;
        _limits = limits;
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        var headerRead = await _stream.ReadAtLeastAsync(_readHeader, HeaderSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead < HeaderSize)
        {
            throw new EndOfStreamException("RPC stream ended inside a message header.");
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(_readHeader);
        if (length <= 0)
        {
            throw new InvalidDataException($"Invalid RPC message length {length}.");
        }

        if (length > _limits.MaxIncomingMessageBytes)
        {
            throw new RpcMessageTooLargeException($"Incoming RPC message of {length} bytes exceeds the {_limits.MaxIncomingMessageBytes}-byte limit.");
        }

        // A fresh buffer per message: the formatter may keep referencing it after Deserialize returns.
        var payload = new byte[length];
        await _stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return Formatter.Deserialize(new ReadOnlySequence<byte>(payload));
    }

    protected override async ValueTask WriteCoreAsync(JsonRpcMessage content, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Formatter.Serialize(buffer, content);
        if (buffer.WrittenCount > _limits.MaxOutgoingMessageBytes)
        {
            throw new RpcMessageTooLargeException($"Outgoing RPC message of {buffer.WrittenCount} bytes exceeds the {_limits.MaxOutgoingMessageBytes}-byte limit.");
        }

        BinaryPrimitives.WriteInt32BigEndian(_writeHeader, buffer.WrittenCount);
        await _stream.WriteAsync(_writeHeader, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    protected override ValueTask FlushAsync(CancellationToken cancellationToken) => new(_stream.FlushAsync(cancellationToken));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>The single place where both sides build their JSON-RPC connection, so framing and limits always match.</summary>
public static class EngineRpc
{
    public static JsonRpc Create(Stream stream, RpcLimits limits) =>
        new(new BoundedMessageHandler(stream, new SystemTextJsonFormatter(), limits));
}
