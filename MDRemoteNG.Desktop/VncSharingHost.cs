using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MDRemoteNG.Core;
using RemoteViewing.Vnc;
using RemoteViewing.Vnc.Server;

namespace MDRemoteNG.Desktop;

/// <summary>Opt-in per-user desktop sharing. Unauthenticated clients never reach screen capture or input.</summary>
public sealed class VncSharingHost : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly SharedConnection _endpoint;
    private readonly Func<IPAddress, bool> _allowed;
    private readonly Func<IVncDesktopSession> _desktopFactory;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _handshakes = new(4, 4), _desktop = new(1, 1);
    private readonly ConcurrentDictionary<int, Task> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Next)> _failures = new();
    private Task? _accept;
    private int _next;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public bool IsHealthy => _accept is { IsCompleted: false };

    public VncSharingHost(VncHostSettings settings, SharedConnection endpoint, Func<IPAddress, bool> allowed,
        Func<IVncDesktopSession>? desktopFactory = null, IPAddress? listenAddress = null)
    {
        endpoint.Validate();
        if (endpoint.Protocol != "VNC" || endpoint.CertificateFingerprint.Length == 0)
            throw new InvalidDataException("Encrypted VNC host settings are required.");
        VncSecurity.ValidatePassword(endpoint.Password);
        _certificate = VncSecurity.LoadCertificate(settings, endpoint.CertificateFingerprint);
        _endpoint = endpoint;
        _allowed = allowed;
        _desktopFactory = desktopFactory ?? (() => new WindowsDesktopSession());
        _listener = new TcpListener(listenAddress ?? IPAddress.Any, endpoint.Port);
    }

    public void Start()
    {
        if (_accept != null) throw new InvalidOperationException("Desktop sharing is already running.");
        _listener.Start(4);
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                IPAddress? address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (address == null || !_allowed(address) ||
                    (_failures.TryGetValue(address.ToString(), out var failure) && failure.Next > DateTimeOffset.UtcNow) ||
                    !_handshakes.Wait(0))
                {
                    client.Dispose();
                    continue;
                }
                int id = Interlocked.Increment(ref _next);
                Task session = ServeAsync(client, address);
                _sessions[id] = session;
                _ = session.ContinueWith(completed => { _sessions.TryRemove(id, out var removed); _ = completed.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client, IPAddress address)
    {
        bool desktopSlot = false, authenticated = false;
        try
        {
            using (client)
            using (var secure = new SslStream(client.GetStream(), false))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                await VncSecurity.AuthenticateServerAsync(secure, _certificate, _endpoint.Password, timeout.Token).ConfigureAwait(false);
                authenticated = true;
                _failures.TryRemove(address.ToString(), out _);
                if (!_allowed(address) || !_desktop.Wait(0)) return;
                desktopSlot = true;
                // Screen/input objects are created only after pinned TLS and the full random token authenticate.
                using IVncDesktopSession desktop = _desktopFactory();
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var session = new VncServerSession { MaxUpdateRate = 8 };
                session.PasswordProvided += (_, args) => args.Accept(_endpoint.Password.ToCharArray());
                session.KeyChanged += (_, args) => SafeInput(() => desktop.KeyChanged(args), client);
                session.PointerChanged += (_, args) => SafeInput(() => desktop.PointerChanged(args), client);
                session.Closed += (_, _) => closed.TrySetResult();
                session.ConnectionFailed += (_, _) => closed.TrySetResult();
                session.SetFramebufferSource(new ClosingFramebufferSource(desktop, client));
                using var cancel = _stop.Token.Register(() => client.Dispose());
                session.Connect(secure, new VncServerSessionOptions { AuthenticationMethod = AuthenticationMethod.Password });
                try { await closed.Task.WaitAsync(_stop.Token).ConfigureAwait(false); }
                finally
                {
                    client.Dispose();
                    await Task.Run(session.Close).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or AuthenticationException or SocketException or
                                         OperationCanceledException or ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!authenticated && !_stop.IsCancellationRequested)
            {
                string key = address.ToString();
                if (_failures.Count < 128 || _failures.ContainsKey(key))
                    _failures.AddOrUpdate(key, (1, DateTimeOffset.UtcNow.AddSeconds(2)), (_, previous) =>
                    {
                        int count = Math.Min(previous.Count + 1, 10);
                        return (count, DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 1 << count)));
                    });
            }
        }
        finally
        {
            client.Dispose();
            if (desktopSlot) _desktop.Release();
            _handshakes.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_accept != null) await _accept.ConfigureAwait(false);
        await Task.WhenAll(_sessions.Values).ConfigureAwait(false);
        _certificate.Dispose();
        _stop.Dispose();
        _handshakes.Dispose();
        _desktop.Dispose();
    }

    private sealed class ClosingFramebufferSource(IVncDesktopSession desktop, TcpClient client) : IVncFramebufferSource
    {
        public VncFramebuffer Capture()
        {
            try { return desktop.Capture(); }
            catch (Exception exception)
            {
                client.Dispose();
                // The RFB library's background receive thread handles IO failures.
                // Convert display/GDI failures so a locked desktop closes only this session.
                throw new IOException("The shared Windows desktop became unavailable.", exception);
            }
        }
    }

    private static void SafeInput(Action input, TcpClient client)
    {
        try { input(); }
        catch (Exception exception)
        {
            client.Dispose();
            throw new IOException("The shared desktop cannot accept input.", exception);
        }
    }
}
