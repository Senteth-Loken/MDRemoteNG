using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using MDRemoteNG.Core;
using MDRemoteNG.Desktop;

namespace MDRemoteNG.Discovery;

internal static class Program
{
    private static readonly object HostGate = new();
    private static PeerDiscoveryHost? _host;
    private static VncSharingHost? _vncHost;

    [STAThread]
    private static async Task Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 1 || args[0] != "--helper") return;
        using var singleton = new Mutex(true, @"Local\MDRemoteNG.Discovery." + DiscoveryClient.UserKey, out bool isNew);
        if (!isNew) return;
        using var stop = new CancellationTokenSource();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
        try
        {
            Task[] workers = [SuperviseAsync(stop.Token), ServePipeAsync(stop.Token)];
            await Task.WhenAny(workers).ConfigureAwait(false);
            // A failed IPC/profile worker must stop its sibling; WhenAll alone would
            // otherwise wait forever while leaving a partially running helper behind.
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Never record profile, peer data, PINs, or authentication messages.
            Debug.WriteLine("MDRemoteNG discovery stopped: " + ex.GetType().Name);
        }
        finally
        {
            PeerDiscoveryHost? old;
            VncSharingHost? oldDesktop;
            lock (HostGate) { old = _host; _host = null; oldDesktop = _vncHost; _vncHost = null; }
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
            if (oldDesktop is not null) await oldDesktop.DisposeAsync().ConfigureAwait(false);
        }
        // Mutex ownership is tied to its acquiring thread, whereas async continuations may
        // move threads. Closing the sole process handle releases the singleton safely.
    }

    private static async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        var store = new ProfileStore();
        string stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDRemoteNG");
        var limiter = new AttemptLimiter(Path.Combine(stateDirectory, "discovery-limits.json"));
        string currentFingerprint = "";
        while (!cancellationToken.IsCancellationRequested)
        {
            Profile? profile = null;
            IReadOnlyList<LocalSubnet> subnets = [];
            string fingerprint = "disabled";
            try
            {
                if (store.Exists)
                {
                    profile = store.Load();
                    if (profile.DiscoveryEnabled)
                    {
                        subnets = NetworkScope.GetAllowedSubnets();
                        fingerprint = PeerDiscoveryHost.Fingerprint(profile) + ":" +
                            string.Join(";", subnets.Select(s => $"{s.Address}/{s.Mask}/{s.InterfaceIndex}"));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException)
            {
                profile = null;
                fingerprint = "disabled";
            }

            bool unhealthy;
            lock (HostGate) unhealthy = (_host is not null && !_host.IsHealthy) || (_vncHost is not null && !_vncHost.IsHealthy);
            if (fingerprint != currentFingerprint || unhealthy)
            {
                PeerDiscoveryHost? old;
                VncSharingHost? oldDesktop;
                lock (HostGate) { old = _host; _host = null; oldDesktop = _vncHost; _vncHost = null; }
                if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
                if (oldDesktop is not null) await oldDesktop.DisposeAsync().ConfigureAwait(false);
                currentFingerprint = fingerprint;
                if (profile?.DiscoveryEnabled == true && subnets.Count > 0)
                {
                    var host = new PeerDiscoveryHost(profile, subnets, limiter, cancellationToken);
                    try { host.Start(); lock (HostGate) _host = host; }
                    catch (SocketException)
                    {
                        await host.DisposeAsync().ConfigureAwait(false);
                        currentFingerprint = ""; // Retry transient adapter/port failures.
                    }
                    SharedConnection? vnc = SharedConnectionSet.GetConnections(profile.SharedConnection, profile.SharedConnections)
                        .FirstOrDefault(endpoint => endpoint.Protocol == "VNC" && endpoint.CertificateFingerprint.Length > 0);
                    if (vnc != null && profile.VncHost != null)
                    {
                        VncSharingHost? desktop = null;
                        try
                        {
                            desktop = new VncSharingHost(profile.VncHost, vnc,
                                address => NetworkScope.GetAllowedSubnets().Any(subnet => subnet.Contains(address)));
                            desktop.Start();
                            lock (HostGate) _vncHost = desktop;
                        }
                        catch (Exception ex) when (ex is SocketException or CryptographicException or IOException or InvalidDataException)
                        {
                            if (desktop != null) await desktop.DisposeAsync().ConfigureAwait(false);
                            currentFingerprint = "";
                        }
                    }
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ServePipeAsync(CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(4, 4);
        var clients = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(DiscoveryClient.PipeName, PipeDirection.InOut, 4,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    NamedPipeServerStream connected = pipe;
                    clients.RemoveAll(t => t.IsCompleted);
                    clients.Add(ServeClientAsync(connected, slots, cancellationToken));
                    pipe = null;
                }
                finally
                {
                    if (pipe is not null) { pipe.Dispose(); slots.Release(); }
                }
            }
        }
        finally { await Task.WhenAll(clients).ConfigureAwait(false); }
    }

    private static async Task ServeClientAsync(NamedPipeServerStream pipe, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                byte[] request = await Framing.ReadAsync(pipe, 16, timeout.Token).ConfigureAwait(false);
                if (!request.AsSpan().SequenceEqual("peers"u8)) return;
                IReadOnlyList<DiscoveredPeer> peers = [];
                // Invalidate credentials immediately on disable, PIN change, account change,
                // corrupted/deleted profile; do not wait for the background five-second reload.
                try
                {
                    var store = new ProfileStore();
                    if (store.Exists)
                    {
                        Profile current = store.Load();
                        lock (HostGate)
                            if (_host?.MatchesProfile(current) == true) peers = _host.GetPeers();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException) { }
                byte[] response = JsonSerializer.SerializeToUtf8Bytes(peers);
                try
                {
                    if (response.Length <= DiscoveryClient.MaximumMessageSize)
                        await Framing.WriteAsync(pipe, response, timeout.Token).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(response); }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException) { }
            finally { slots.Release(); }
        }
    }
}