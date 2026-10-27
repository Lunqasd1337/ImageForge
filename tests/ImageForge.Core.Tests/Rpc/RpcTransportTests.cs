using System.Buffers.Binary;
using ImageForge.Core.Rpc;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace ImageForge.Core.Tests.Rpc;

/// <summary>
/// Early transport test (plan, section 13): typed calls, IProgress and CancellationToken over StreamJsonRpc,
/// and the message size limits, on an in-memory duplex stream.
/// </summary>
public sealed partial class RpcTransportTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TestService _service = new();
    private JsonRpc? _server;
    private JsonRpc? _client;

    [JsonRpcContract]
    public partial interface ITestService
    {
        Task<string> EchoAsync(string text, CancellationToken ct);

        Task<int> CountAsync(int count, IProgress<int> progress, CancellationToken ct);

        Task WaitForCancellationAsync(CancellationToken ct);
    }

    private ITestService Connect(RpcLimits serverLimits, RpcLimits clientLimits)
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        _server = EngineRpc.Create(serverStream, serverLimits);
        _server.AddLocalRpcTarget<ITestService>(_service, null);
        _server.StartListening();

        _client = EngineRpc.Create(clientStream, clientLimits);
        var proxy = _client.Attach<ITestService>();
        _client.StartListening();
        return proxy;
    }

    private ITestService Connect() => Connect(RpcLimits.Engine, RpcLimits.Client);

    [Fact]
    public async Task Typed_call_round_trips()
    {
        var proxy = Connect();

        (await proxy.EchoAsync("Привет, M1", TestContext.Current.CancellationToken)).ShouldBe("Привет, M1");
    }

    [Fact]
    public async Task Progress_reports_reach_the_client_in_order()
    {
        var proxy = Connect();
        var progress = new CollectingProgress();

        var result = await proxy.CountAsync(5, progress, TestContext.Current.CancellationToken);

        result.ShouldBe(5);
        await progress.WaitForAsync(5).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        progress.Values.ShouldBe([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task Client_cancellation_reaches_the_server()
    {
        var proxy = Connect();
        using var cts = new CancellationTokenSource();

        var call = proxy.WaitForCancellationAsync(cts.Token);
        await _service.Started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(call);
        await _service.ServerSawCancellation.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Oversized_incoming_message_drops_the_connection()
    {
        var proxy = Connect(new RpcLimits(MaxIncomingMessageBytes: 1024, MaxOutgoingMessageBytes: 1024), new RpcLimits(1024 * 1024, 1024 * 1024));

        await Should.ThrowAsync<ConnectionLostException>(proxy.EchoAsync(new string('x', 4096), TestContext.Current.CancellationToken));
        var serverDone = await Task.WhenAny(_server!.Completion, Task.Delay(Timeout, TestContext.Current.CancellationToken));
        serverDone.ShouldBe(_server.Completion);
        _service.EchoCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Oversized_outgoing_message_is_not_sent()
    {
        var proxy = Connect(RpcLimits.Engine, new RpcLimits(MaxIncomingMessageBytes: 1024, MaxOutgoingMessageBytes: 1024));

        var ex = await Should.ThrowAsync<Exception>(proxy.EchoAsync(new string('x', 4096), TestContext.Current.CancellationToken));

        FindInChain<RpcMessageTooLargeException>(ex).ShouldNotBeNull();
        _service.EchoCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1025)]
    public async Task Reader_rejects_a_declared_length_above_the_limit_before_reading_the_body(int declared)
    {
        var frame = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(frame, declared);
        await using var handler = new BoundedMessageHandler(new MemoryStream(frame), new SystemTextJsonFormatter(), new RpcLimits(1024, 1024));

        await Should.ThrowAsync<RpcMessageTooLargeException>(handler.ReadAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Reader_rejects_non_positive_lengths(int declared)
    {
        var frame = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(frame, declared);
        await using var handler = new BoundedMessageHandler(new MemoryStream(frame), new SystemTextJsonFormatter(), new RpcLimits(1024, 1024));

        await Should.ThrowAsync<InvalidDataException>(handler.ReadAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Reader_returns_null_at_a_clean_end_of_stream()
    {
        await using var handler = new BoundedMessageHandler(new MemoryStream([]), new SystemTextJsonFormatter(), new RpcLimits(1024, 1024));

        (await handler.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Reader_fails_on_a_truncated_frame()
    {
        byte[] frame = [0, 0, 0, 10, (byte)'{'];
        await using var handler = new BoundedMessageHandler(new MemoryStream(frame), new SystemTextJsonFormatter(), new RpcLimits(1024, 1024));

        await Should.ThrowAsync<EndOfStreamException>(handler.ReadAsync(TestContext.Current.CancellationToken).AsTask());
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _server?.Dispose();
        if (_server is not null)
        {
            await _server.Completion.ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }
    }

    private static T? FindInChain<T>(Exception? ex)
        where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is T match)
            {
                return match;
            }
        }

        return null;
    }

    private sealed class TestService : ITestService
    {
        private int _echoCalls;

        public int EchoCalls => _echoCalls;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ServerSawCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> EchoAsync(string text, CancellationToken ct)
        {
            Interlocked.Increment(ref _echoCalls);
            return Task.FromResult(text);
        }

        public Task<int> CountAsync(int count, IProgress<int> progress, CancellationToken ct)
        {
            for (var i = 1; i <= count; i++)
            {
                progress.Report(i);
            }

            return Task.FromResult(count);
        }

        public async Task WaitForCancellationAsync(CancellationToken ct)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ServerSawCancellation.TrySetResult();
                throw;
            }
        }
    }

    private sealed class CollectingProgress : IProgress<int>
    {
        private readonly List<int> _values = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<int> Values
        {
            get
            {
                lock (_gate)
                {
                    return [.. _values];
                }
            }
        }

        public void Report(int value)
        {
            lock (_gate)
            {
                _values.Add(value);
            }
        }

        public async Task WaitForAsync(int count)
        {
            while (Values.Count < count)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }
        }
    }
}
