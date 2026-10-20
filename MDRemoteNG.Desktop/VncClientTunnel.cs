using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace MDRemoteNG.Desktop;

/// <summary>Feeds the embedded legacy RFB client through a pinned, authenticated TLS channel.</summary>
public sealed class VncClientTunnel : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private TcpClient? _local, _remote;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public Task Completion => _worker;

    public VncClientTunnel(string address, int port, string fingerprint, string password)
    {
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(address)) throw new ArgumentException("Invalid VNC host.");
        VncSecurity.ValidatePassword(password);
        _listener.Start(1);
        _worker = RunAsync(address, port, fingerprint, password);
    }

    private async Task RunAsync(string address, int port, string fingerprint, string password)
    {
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            _local = await _listener.AcceptTcpClientAsync(connectTimeout.Token).ConfigureAwait(false);
            _listener.Stop(); // Exactly one embedded client uses this ephemeral loopback endpoint.
            _remote = new TcpClient();
            await _remote.ConnectAsync(address, port, connectTimeout.Token).ConfigureAwait(false);
            using var secure = new SslStream(_remote.GetStream(), false);
            await VncSecurity.AuthenticateClientAsync(secure, address, fingerprint, password, connectTimeout.Token).ConfigureAwait(false);
            NetworkStream local = _local.GetStream();
            Task outward = local.CopyToAsync(secure, _stop.Token);
            Task inward = secure.CopyToAsync(local, _stop.Token);
            await Task.WhenAny(outward, inward).ConfigureAwait(false);
            _stop.Cancel();
            _local.Dispose();
            _remote.Dispose();
            try { await Task.WhenAll(outward, inward).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        finally
        {
            _listener.Stop();
            _local?.Dispose();
            _remote?.Dispose();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _local?.Dispose();
        _remote?.Dispose();
        // Observe errors without logging credentials or blocking the UI on a network task.
        _ = _worker.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
