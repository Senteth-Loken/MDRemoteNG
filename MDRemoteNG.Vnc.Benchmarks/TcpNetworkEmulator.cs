using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace MDRemoteNG.Vnc.Benchmarks;

internal sealed record NetworkProfile(string Name, double MegabitsPerSecond, double OneWayLatencyMs)
{
    public static NetworkProfile Named(string name) => name switch
    {
        "loopback" => new(name, 0, 0),
        "lan" => new(name, 1000, 2),
        "constrained" => new(name, 100, 8),
        _ => throw new ArgumentException("Unknown network profile: " + name)
    };
}
internal sealed record NetworkStatistics(NetworkProfile Profile, long OutwardBytes, long InwardBytes,
    int MaximumQueuedOrPendingChunks, string Model);

/// <summary>Deterministic bounded byte shaper between the real tunnel and pinned TLS host.</summary>
internal sealed class TcpNetworkEmulator : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly NetworkProfile _profile;
    private readonly Task _worker;
    private TcpClient? _incoming, _outgoing;
    private long _outwardBytes, _inwardBytes;
    private int _maximumQueued;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public Task Completion => _worker;
    public NetworkStatistics Statistics => new(_profile, Interlocked.Read(ref _outwardBytes), Interlocked.Read(ref _inwardBytes), _maximumQueued,
        "32-chunk bounded FIFO plus at most one pending writer, on encrypted TCP bytes; per-byte serialization deadlines plus one-way eligibility timestamps, not a delay per TCP read. Up-to64KiB delivery granularity adds up to5.2ms at100Mbps. No packet loss, jitter or real remote network. " +
        HighResolutionDelay.Mechanism + " Proxy CPU and thread-pool wakeup overhead are in-process costs. The v1 coarse Task.Delay scheduling defect is corrected in this harness; v1 raw reports remain separate.");
    public TcpNetworkEmulator(int destinationPort, NetworkProfile profile)
    {
        _profile = profile;
        _listener.Start(1);
        _worker = RelayAsync(destinationPort);
    }
    private async Task RelayAsync(int port)
    {
        try
        {
            _incoming = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            _listener.Stop();
            _incoming.NoDelay = true;
            _outgoing = new TcpClient { NoDelay = true };
            await _outgoing.ConnectAsync(IPAddress.Loopback, port, _stop.Token).ConfigureAwait(false);
            Task outward = CopyAsync(_incoming.GetStream(), _outgoing.GetStream(), false);
            Task inward = CopyAsync(_outgoing.GetStream(), _incoming.GetStream(), true);
            await Task.WhenAny(outward, inward).ConfigureAwait(false);
            _stop.Cancel();
            _incoming.Dispose();
            _outgoing.Dispose();
            await Task.WhenAll(outward, inward).ConfigureAwait(false);
        }
        catch (Exception exception) when (_stop.IsCancellationRequested && exception is OperationCanceledException or IOException or ObjectDisposedException or SocketException) { }
        finally { _listener.Stop(); _incoming?.Dispose(); _outgoing?.Dispose(); }
    }
    private async Task CopyAsync(Stream source, Stream destination, bool inward)
    {
        var queue = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true });
        int queued = 0;
        Task receive = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    byte[] bytes = ArrayPool<byte>.Shared.Rent(65536);
                    bool transferred = false;
                    bool counted = false;
                    try
                    {
                        int length = await source.ReadAsync(bytes.AsMemory(0, 65536), _stop.Token).ConfigureAwait(false);
                        if (length == 0) break;
                        long eligible = Stopwatch.GetTimestamp() + (long)(_profile.OneWayLatencyMs * Stopwatch.Frequency / 1000);
                        int count = Interlocked.Increment(ref queued);
                        counted = true;
                        int previous;
                        do { previous = Volatile.Read(ref _maximumQueued); }
                        while (count > previous && Interlocked.CompareExchange(ref _maximumQueued, count, previous) != previous);
                        await queue.Writer.WriteAsync(new Chunk(bytes, length, eligible), _stop.Token).ConfigureAwait(false);
                        transferred = true;
                    }
                    finally
                    {
                        if (!transferred)
                        {
                            if (counted) Interlocked.Decrement(ref queued);
                            ArrayPool<byte>.Shared.Return(bytes);
                        }
                    }
                }
                queue.Writer.TryComplete();
            }
            catch (Exception exception) { queue.Writer.TryComplete(exception); throw; }
        });
        long deadline = 0;
        try
        {
            await foreach (Chunk chunk in queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref queued);
                try
                {
                    deadline = Math.Max(deadline, chunk.EligibleAt);
                    if (_profile.MegabitsPerSecond > 0)
                        deadline += (long)(chunk.Length * 8d / (_profile.MegabitsPerSecond * 1_000_000) * Stopwatch.Frequency);
                    await WaitUntil(deadline, _stop.Token).ConfigureAwait(false);
                    await destination.WriteAsync(chunk.Bytes.AsMemory(0, chunk.Length), _stop.Token).ConfigureAwait(false);
                    if (inward) Interlocked.Add(ref _inwardBytes, chunk.Length);
                    else Interlocked.Add(ref _outwardBytes, chunk.Length);
                }
                finally { ArrayPool<byte>.Shared.Return(chunk.Bytes); }
            }
            await receive.ConfigureAwait(false);
        }
        finally
        {
            while (queue.Reader.TryRead(out Chunk? chunk)) ArrayPool<byte>.Shared.Return(chunk.Bytes);
            _ = receive.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
    public static Task WaitUntil(long deadline, CancellationToken cancellationToken) => HighResolutionDelay.Until(deadline, cancellationToken);
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        _incoming?.Dispose();
        _outgoing?.Dispose();
        try { await _worker.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }
    private sealed record Chunk(byte[] Bytes, int Length, long EligibleAt);
}
