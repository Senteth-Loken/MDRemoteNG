using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Security.Cryptography;
using MDRemoteNG.Core;

namespace MDRemoteNG.Discovery;

internal sealed class PeerDiscoveryHost : IAsyncDisposable
{
    public const int DiscoveryPort = 48765;
    private const int BeaconMaximumSize = 256;
    private readonly Profile _profile;
    private readonly string _profileFingerprint;
    private readonly IReadOnlyList<LocalSubnet> _subnets;
    private readonly AttemptLimiter _limiter;
    private readonly CancellationTokenSource _stop;
    private readonly ConcurrentDictionary<Guid, DiscoveredPeer> _peers = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly SemaphoreSlim _slots = new(4, 4);
    private readonly ConcurrentDictionary<int, Task> _sessions = new();
    private readonly TcpListener _listener = new(IPAddress.Any, 0);
    private readonly Socket _udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private Task[] _loops = [];
    private int _sessionId;
    private sealed record Beacon(int Version, Guid InstanceId, int Port);

    public PeerDiscoveryHost(Profile profile, IReadOnlyList<LocalSubnet> subnets, AttemptLimiter limiter, CancellationToken cancellationToken)
    {
        _profile = profile;
        _profileFingerprint = Fingerprint(profile);
        _subnets = subnets;
        _limiter = limiter;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public void Start()
    {
        _listener.Start(8);
        _udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        _udp.EnableBroadcast = true;
        _udp.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        _loops = [AcceptLoopAsync(_stop.Token), ReceiveLoopAsync(_stop.Token), BroadcastLoopAsync(_stop.Token)];
    }

    public bool IsHealthy => _loops.All(loop => !loop.IsCompleted);

    public IReadOnlyList<DiscoveredPeer> GetPeers()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddSeconds(-90);
        foreach ((Guid id, DiscoveredPeer peer) in _peers)
            if (peer.LastSeenUtc < cutoff) _peers.TryRemove(id, out _);
        return _peers.Values.OrderBy(p => p.DisplayName).ToArray();
    }

    public bool MatchesProfile(Profile profile) => profile.DiscoveryEnabled && Fingerprint(profile) == _profileFingerprint;

    internal static string Fingerprint(Profile profile)
    {
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(new
        {
            profile.InstanceId, profile.DisplayName, profile.NormalizedPin,
            profile.DiscoveryEnabled, profile.SharedConnection, profile.SharedConnections, profile.VncHost
        });
        try { return Convert.ToHexString(SHA256.HashData(serialized)); }
        finally { CryptographicOperations.ZeroMemory(serialized); }
    }

    private async Task BroadcastLoopAsync(CancellationToken cancellationToken)
    {
        byte[] beacon = JsonSerializer.SerializeToUtf8Bytes(new Beacon(1, _profile.InstanceId,
            ((IPEndPoint)_listener.LocalEndpoint).Port));
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (LocalSubnet subnet in _subnets)
            {
                try
                {
                    using var sender = new UdpClient(new IPEndPoint(subnet.Address, 0));
                    sender.EnableBroadcast = true;
                    await sender.SendAsync(beacon, new IPEndPoint(subnet.Broadcast, DiscoveryPort), cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException) { }
            }
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[BeaconMaximumSize + 1];
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult message;
            try
            {
                message = await _udp.ReceiveMessageFromAsync(buffer, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException) { continue; }
            if (message.ReceivedBytes > BeaconMaximumSize ||
                message.RemoteEndPoint is not IPEndPoint source ||
                !_subnets.Any(s => s.InterfaceIndex == message.PacketInformation.Interface && s.Contains(source.Address)) ||
                _subnets.Any(s => s.Address.Equals(source.Address))) continue;
            Beacon? beacon;
            try { beacon = JsonSerializer.Deserialize<Beacon>(buffer.AsSpan(0, message.ReceivedBytes)); }
            catch (JsonException) { continue; }
            if (beacon is null || beacon.Version != 1 || beacon.InstanceId == Guid.Empty ||
                beacon.InstanceId == _profile.InstanceId || beacon.Port is < 1024 or > 65535) continue;
            if (_peers.TryGetValue(beacon.InstanceId, out DiscoveredPeer? cached) &&
                DateTimeOffset.UtcNow - cached.LastSeenUtc < TimeSpan.FromSeconds(45)) continue;
            // Exactly one side initiates, so simultaneous beacons cannot produce duplicate sessions.
            if (_profile.InstanceId.CompareTo(beacon.InstanceId) >= 0) continue;
            StartSession(source.Address.ToString(), () => ConnectAsync(source.Address, beacon.Port, beacon.InstanceId, cancellationToken));
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            if (client.Client.RemoteEndPoint is not IPEndPoint remote || client.Client.LocalEndPoint is not IPEndPoint local ||
                !_subnets.Any(s => s.Address.Equals(local.Address) && s.Contains(remote.Address)) ||
                _subnets.Any(s => s.Address.Equals(remote.Address)))
            { client.Dispose(); continue; }
            if (!StartSession(remote.Address.ToString(), () => AuthenticateAsync(client, remote.Address, true, null, cancellationToken)))
                client.Dispose();
        }
    }

    private bool StartSession(string address, Func<Task<bool>> exchange)
    {
        if (!_inFlight.TryAdd(address, 0)) return false;
        if (!_slots.Wait(0)) { _inFlight.TryRemove(address, out _); return false; }
        if (!_limiter.TryBegin(address))
        { _slots.Release(); _inFlight.TryRemove(address, out _); return false; }
        int id = Interlocked.Increment(ref _sessionId);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessions[id] = completion.Task;
        _ = Task.Run(async () =>
        {
            bool success = false;
            try { success = await exchange().ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or
                                      System.Security.Cryptography.CryptographicException or InvalidDataException or ArgumentException or
                                      System.Security.Authentication.AuthenticationException or JsonException)
            { }
            finally
            {
                _limiter.Complete(address, success);
                _slots.Release();
                _inFlight.TryRemove(address, out _);
                _sessions.TryRemove(id, out _);
                completion.TrySetResult();
            }
        });
        return true;
    }

    private async Task<bool> ConnectAsync(IPAddress address, int port, Guid advertisedId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
        return await AuthenticateAsync(client, address, false, advertisedId, timeout.Token).ConfigureAwait(false);
    }

    private async Task<bool> AuthenticateAsync(TcpClient client, IPAddress source, bool server, Guid? advertisedId, CancellationToken cancellationToken)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            client.NoDelay = true;
            if (!CurrentProfileMatchesProtectedStore()) return false;
            RemotePeer peer = await PeerHandshake.ExchangeAsync(client.GetStream(), _profile, server, timeout.Token,
                CurrentProfileMatchesProtectedStore).ConfigureAwait(false);
            if (peer.InstanceId == Guid.Empty || peer.InstanceId == _profile.InstanceId ||
                (advertisedId.HasValue && peer.InstanceId != advertisedId.Value) ||
                peer.DisplayName is null || peer.DisplayName.Length > 128 ||
                timeout.IsCancellationRequested || !CurrentProfileMatchesProtectedStore())
                return false;
            if (_peers.Count >= 128 && !_peers.ContainsKey(peer.InstanceId)) return false;
            _peers[peer.InstanceId] = new DiscoveredPeer
            {
                InstanceId = peer.InstanceId,
                DisplayName = peer.DisplayName,
                Address = source.ToString(),
                SharedConnection = peer.SharedConnection,
                SharedConnections = peer.SharedConnections,
                LastSeenUtc = DateTimeOffset.UtcNow
            };
            return true;
        }
    }

    private bool CurrentProfileMatchesProtectedStore()
    {
        try
        {
            var store = new ProfileStore();
            return store.Exists && MatchesProfile(store.Load());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException)
        { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _udp.Dispose();
        try { await Task.WhenAll(_loops).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        await Task.WhenAll(_sessions.Values).ConfigureAwait(false);
        _peers.Clear();
        _slots.Dispose();
        _stop.Dispose();
    }
}
