using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using MDRemoteNG.Core;
using MDRemoteNG.Desktop;
using RemoteViewing.Vnc;

internal static class VncHostChecks
{
    internal static async Task<int> RunAsync(VncHostSettings settings, string fingerprint, string password)
    {
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            Console.WriteLine("PASS " + message);
            passed++;
        }

        foreach (bool wrongPin in new[] { true, false })
        {
            await using var fixture = Fixture.Create(settings, fingerprint, password);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var client = await ConnectAsync(fixture.Host.Port, timeout.Token);
            using var stream = new SslStream(client.GetStream(), false);
            bool rejected = false;
            try
            {
                await VncSecurity.AuthenticateClientAsync(stream, "127.0.0.1", wrongPin ? new string('0', 64) : fingerprint,
                    wrongPin ? password : (password[0] == 'A' ? "B" : "A") + password[1..], timeout.Token);
            }
            catch (Exception exception) when (exception is IOException or AuthenticationException) { rejected = true; }
            Check(rejected && fixture.Created.Count == 0, wrongPin
                ? "A wrong certificate pin cannot construct a desktop session."
                : "A wrong full sharing token cannot construct a desktop session.");
        }

        await using (var fixture = Fixture.Create(settings, fingerprint, password))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var client = await ConnectAsync(fixture.Host.Port, timeout.Token);
            await client.GetStream().WriteAsync("RFB 003.008\n"u8.ToArray(), timeout.Token);
            await ExpectClosedAsync(client.GetStream(), timeout.Token);
            Check(fixture.Created.Count == 0 && fixture.Host.IsHealthy, "Plaintext VNC is rejected before any desktop capture or input object exists.");
        }

        int sourceChecks = 0;
        await using (var fixture = Fixture.Create(settings, fingerprint, password, address =>
        {
            Interlocked.Increment(ref sourceChecks);
            return false;
        }))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var client = await ConnectAsync(fixture.Host.Port, timeout.Token);
            await ExpectClosedAsync(client.GetStream(), timeout.Token);
            Check(sourceChecks > 0 && fixture.Created.Count == 0, "The allowed-source predicate rejects connections before TLS or desktop construction.");
        }

        await using (var fixture = Fixture.Create(settings, fingerprint, password))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var client = await ConnectAsync(fixture.Host.Port, timeout.Token);
            using var stream = new SslStream(client.GetStream(), false);
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "127.0.0.1", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, certificate, _, _) => VncSecurity.Matches(certificate, fingerprint)
            }, timeout.Token);
            Check(fixture.Created.Count == 0, "TLS alone cannot expose a framebuffer before the full sharing token is supplied.");
        }

        await using (var fixture = Fixture.Create(settings, fingerprint, password))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var client = await ConnectAsync(fixture.Host.Port, timeout.Token);
            using var stream = new SslStream(client.GetStream(), false);
            await VncSecurity.AuthenticateClientAsync(stream, "127.0.0.1", fingerprint, password, timeout.Token);
            uint status = await AuthenticateRfbAsync(stream, "WRONGPASS", timeout.Token);
            Check(status != 0 && fixture.Created.All(session => session.Captures == 0),
                "The legacy RFB challenge still rejects a wrong password before capturing pixels.");
        }

        foreach (bool inputFailure in new[] { false, true })
        {
            await using var fixture = Fixture.Create(settings, fingerprint, password);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var client = await ConnectAsync(fixture.Host.Port, timeout.Token);
            using var stream = new SslStream(client.GetStream(), false);
            await VncSecurity.AuthenticateClientAsync(stream, "127.0.0.1", fingerprint, password, timeout.Token);
            if (await AuthenticateRfbAsync(stream, password, timeout.Token) != 0) throw new Exception("Synthetic RFB authentication failed.");
            if (inputFailure)
            {
                await InitializeDesktopAsync(stream, timeout.Token);
                fixture.Created.Single().InputFailure = new InvalidOperationException("Synthetic unavailable input.");
                await stream.WriteAsync(new byte[] { 4, 1, 0, 0, 0, 0, 0, 65 }, timeout.Token);
            }
            else
            {
                fixture.Created.Single().CaptureFailure = new System.ComponentModel.Win32Exception(5);
                await stream.WriteAsync(new byte[] { 1 }, timeout.Token);
            }
            await ExpectClosedAsync(stream, timeout.Token);
            await WaitUntilAsync(() => fixture.Created.Single().Disposed > 0, timeout.Token);
            Check(fixture.Host.IsHealthy, inputFailure
                ? "A synthetic input failure closes only its session and leaves the host running."
                : "A synthetic native capture error cannot escape the RFB background thread.");
        }

        await using (var fixture = Fixture.Create(settings, fingerprint, password))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var tunnel = new VncClientTunnel("127.0.0.1", fixture.Host.Port, fingerprint, password);
            using var client = await ConnectAsync(tunnel.Port, timeout.Token);
            using NetworkStream stream = client.GetStream();
            Check(await AuthenticateRfbAsync(stream, password, timeout.Token) == 0 && fixture.Created.All(session => session.Captures == 0),
                "The embedded-client tunnel authenticates TLS and forwards the synthetic RFB password challenge.");
            await InitializeDesktopAsync(stream, timeout.Token);
            Check(fixture.Created.Count == 1 && fixture.Created.Single().Captures > 0,
                "Only the authenticated RFB desktop initialization creates synthetic framebuffer pixels.");
            Check(await ReadFramebufferAsync(stream, timeout.Token), "Raw RFB framebuffer rectangles contain the expected synthetic pixels.");

            await stream.WriteAsync(new byte[] { 4, 1, 0, 0, 0, 0, 0, 65, 4, 0, 0, 0, 0, 0, 0, 65,
                5, 1, 0, 2, 0, 1, 5, 0, 0, 2, 0, 1 }, timeout.Token);
            await WaitUntilAsync(() => fixture.Created.Single().Keys >= 2 && fixture.Created.Single().Pointers >= 2, timeout.Token);
            Check(fixture.Created.Single().Keys == 2 && fixture.Created.Single().Pointers == 2,
                "Keyboard and pointer RFB packets reach only injected fake callbacks.");

            using (var second = await ConnectAsync(fixture.Host.Port, timeout.Token))
            using (var secondStream = new SslStream(second.GetStream(), false))
            {
                await VncSecurity.AuthenticateClientAsync(secondStream, "127.0.0.1", fingerprint, password, timeout.Token);
                await ExpectClosedAsync(secondStream, timeout.Token);
            }
            Check(fixture.Created.Count == 1, "A second authenticated client cannot take a second desktop slot.");

            // This simulates locking/capture loss without inspecting the actual desktop.
            fixture.Created.Single().FailCapture = true;
            await RequestFramebufferAsync(stream, timeout.Token);
            await ExpectClosedAsync(stream, timeout.Token);
            await WaitUntilAsync(() => fixture.Created.Single().Disposed > 0, timeout.Token);
            await tunnel.Completion.WaitAsync(timeout.Token);
            Check(fixture.Created.Single().Disposed > 0, "A capture failure closes the session and disposes its fake desktop.");
            using var replacement = await ConnectAsync(fixture.Host.Port, timeout.Token);
            using var replacementStream = new SslStream(replacement.GetStream(), false);
            await VncSecurity.AuthenticateClientAsync(replacementStream, "127.0.0.1", fingerprint, password, timeout.Token);
            if (await AuthenticateRfbAsync(replacementStream, password, timeout.Token) != 0) throw new Exception("Replacement RFB authentication failed.");
            await InitializeDesktopAsync(replacementStream, timeout.Token);
            Check(fixture.Created.Count == 2 && fixture.Created.Last().Captures > 0,
                "Capture loss releases the desktop slot so a new authenticated synthetic session can connect.");
        }

        await using (var fixture = Fixture.Create(settings, fingerprint, password))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var clients = new List<TcpClient>();
            try
            {
                for (int index = 0; index < 5; index++) clients.Add(await ConnectAsync(fixture.Host.Port, timeout.Token));
                await ExpectClosedAsync(clients[^1].GetStream(), timeout.Token);
                Check(fixture.Created.Count == 0, "At most four unauthenticated TLS handshakes are admitted and none captures pixels.");
            }
            finally { foreach (TcpClient client in clients) client.Dispose(); }
        }
        return passed;
    }

    private static async Task<TcpClient> ConnectAsync(int port, CancellationToken token)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        try { await client.ConnectAsync(IPAddress.Loopback, port, token); return client; }
        catch { client.Dispose(); throw; }
    }

    private static async Task ExpectClosedAsync(Stream stream, CancellationToken token)
    {
        try
        {
            byte[] data = new byte[256];
            while (await stream.ReadAsync(data, token) != 0) { }
        }
        catch (IOException) { }
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int length, CancellationToken token)
    {
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private static async Task<uint> AuthenticateRfbAsync(Stream stream, string password, CancellationToken token)
    {
        if (!Encoding.ASCII.GetString(await ReadAsync(stream, 12, token)).Equals("RFB 003.008\n", StringComparison.Ordinal))
            throw new Exception("Expected RFB 3.8.");
        await stream.WriteAsync("RFB 003.008\n"u8.ToArray(), token);
        byte[] count = await ReadAsync(stream, 1, token);
        byte[] methods = await ReadAsync(stream, count[0], token);
        if (!methods.AsSpan().SequenceEqual(new byte[] { 2 })) throw new Exception("The host must require RFB password authentication.");
        await stream.WriteAsync(new byte[] { 2 }, token);
        byte[] challenge = await ReadAsync(stream, 16, token);
        byte[] key = new byte[8];
        for (int index = 0; index < Math.Min(password.Length, key.Length); index++)
        {
            byte value = (byte)password[index];
            for (int bit = 0; bit < 8; bit++) key[index] |= (byte)(((value >> bit) & 1) << (7 - bit));
        }
        try
        {
            using DES des = DES.Create();
            des.Key = key;
            des.Mode = CipherMode.ECB;
            des.Padding = PaddingMode.None;
            using ICryptoTransform encryptor = des.CreateEncryptor();
            byte[] response = encryptor.TransformFinalBlock(challenge, 0, challenge.Length);
            await stream.WriteAsync(response, token);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        return BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(stream, 4, token));
    }

    private static async Task InitializeDesktopAsync(Stream stream, CancellationToken token)
    {
        await stream.WriteAsync(new byte[] { 1 }, token);
        byte[] header = await ReadAsync(stream, 24, token);
        int width = BinaryPrimitives.ReadUInt16BigEndian(header), height = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        int nameLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20)));
        if (width != FakeDesktop.Width || height != FakeDesktop.Height || nameLength is < 1 or > 256 || header[4] != 32 || header[6] != 0)
            throw new Exception("Unexpected synthetic framebuffer parameters.");
        await ReadAsync(stream, nameLength, token);
        await stream.WriteAsync(new byte[] { 2, 0, 0, 1, 0, 0, 0, 0 }, token); // Only raw rectangles.
    }

    private static async Task RequestFramebufferAsync(Stream stream, CancellationToken token)
    {
        byte[] request = new byte[10];
        request[0] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), FakeDesktop.Width);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), FakeDesktop.Height);
        await stream.WriteAsync(request, token);
    }

    private static async Task<bool> ReadFramebufferAsync(Stream stream, CancellationToken token)
    {
        await RequestFramebufferAsync(stream, token);
        byte[] header = await ReadAsync(stream, 4, token);
        int rectangles = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        if (header[0] != 0 || rectangles is < 1 or > 16) return false;
        int pixels = 0;
        for (int rectangle = 0; rectangle < rectangles; rectangle++)
        {
            byte[] region = await ReadAsync(stream, 12, token);
            int x = BinaryPrimitives.ReadUInt16BigEndian(region), y = BinaryPrimitives.ReadUInt16BigEndian(region.AsSpan(2));
            int width = BinaryPrimitives.ReadUInt16BigEndian(region.AsSpan(4)), height = BinaryPrimitives.ReadUInt16BigEndian(region.AsSpan(6));
            if (BinaryPrimitives.ReadInt32BigEndian(region.AsSpan(8)) != 0 || width <= 0 || height <= 0 || x + width > FakeDesktop.Width || y + height > FakeDesktop.Height)
                return false;
            byte[] data = await ReadAsync(stream, width * height * 4, token);
            for (int row = 0; row < height; row++)
                for (int column = 0; column < width; column++)
                    if ((BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((row * width + column) * 4)) & 0xFFFFFF) != FakeDesktop.Pixel(x + column, y + row))
                        return false;
            pixels += width * height;
        }
        return pixels == FakeDesktop.Width * FakeDesktop.Height;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal ConcurrentQueue<FakeDesktop> Created { get; } = new();
        internal VncSharingHost Host { get; private set; } = null!;
        internal static Fixture Create(VncHostSettings settings, string fingerprint, string password, Func<IPAddress, bool>? allowed = null)
        {
            var fixture = new Fixture();
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            fixture.Host = new VncSharingHost(settings, new SharedConnection
            {
                Protocol = "VNC", Port = port, Password = password, CertificateFingerprint = fingerprint
            }, allowed ?? IPAddress.IsLoopback, () =>
            {
                var fake = new FakeDesktop();
                fixture.Created.Enqueue(fake);
                return fake;
            }, IPAddress.Loopback);
            fixture.Host.Start();
            return fixture;
        }
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private sealed class FakeDesktop : IVncDesktopSession
    {
        internal const int Width = 4, Height = 3;
        private readonly VncFramebuffer _frame = new("Synthetic test framebuffer", Width, Height);
        private int _captures, _keys, _pointers, _disposed, _failCapture;
        private Exception? _captureFailure, _inputFailure;
        internal int Captures => Volatile.Read(ref _captures);
        internal int Keys => Volatile.Read(ref _keys);
        internal int Pointers => Volatile.Read(ref _pointers);
        internal int Disposed => Volatile.Read(ref _disposed);
        internal bool FailCapture { set => Volatile.Write(ref _failCapture, value ? 1 : 0); }
        internal Exception? CaptureFailure { set => Volatile.Write(ref _captureFailure, value); }
        internal Exception? InputFailure { set => Volatile.Write(ref _inputFailure, value); }
        internal static int Pixel(int x, int y) => x << 16 | y << 8 | 0x55;
        internal FakeDesktop()
        {
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++) _frame.SetPixel(x, y, Pixel(x, y));
        }
        public VncFramebuffer Capture()
        {
            Interlocked.Increment(ref _captures);
            if (Volatile.Read(ref _captureFailure) is Exception failure) throw failure;
            if (Volatile.Read(ref _failCapture) != 0) throw new IOException("Synthetic capture failure.");
            return _frame;
        }
        public void KeyChanged(KeyChangedEventArgs args)
        {
            Interlocked.Increment(ref _keys);
            if (Volatile.Read(ref _inputFailure) is Exception failure) throw failure;
        }
        public void PointerChanged(PointerChangedEventArgs args) => Interlocked.Increment(ref _pointers);
        public void Dispose() => Interlocked.Increment(ref _disposed);
    }
}
