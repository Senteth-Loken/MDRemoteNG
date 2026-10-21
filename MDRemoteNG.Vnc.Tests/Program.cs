using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using MDRemoteNG.Core;
using MDRemoteNG.Desktop;
using RemoteViewing.Vnc;
using VncSharpCore;

namespace MDRemoteNG.Vnc.Tests;

/// <summary>Exercises the actual embedded viewer with synthetic pixels on loopback only.</summary>
internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The real VNC control checks require Windows.");
            return 1;
        }
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        using var deadline = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("FAIL The synthetic VNC client checks exceeded their process deadline.");
            Environment.Exit(3);
        }, null, TimeSpan.FromSeconds(45), Timeout.InfiniteTimeSpan);
        try
        {
            CheckViewerResources();
            CheckRealViewerLifecycle();
            foreach (string rejection in new[] { "certificate pin", "sharing token", "RFB password" })
                CheckAuthenticationRejection(rejection);
            Console.WriteLine($"All {_passed} actual VNC control checks passed. No windows were shown, real desktop captured, clipboard accessed or Windows input sent.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + exception);
            return 1;
        }
    }

    private static void CheckViewerResources()
    {
        var assembly = typeof(RemoteDesktop).Assembly;
        using var cursor = assembly.GetManifestResourceStream("VncSharpCore.Resources.vnccursor.cur");
        using var screenshot = assembly.GetManifestResourceStream("VncSharpCore.Resources.screenshot.png");
        Check(cursor is { Length: > 0 } && screenshot is { Length: > 0 },
            "The viewer embeds both cursor and designer image resources required during connection.");
        using var nativeCursor = new Cursor(cursor!);
        Check(nativeCursor.Handle != IntPtr.Zero,
            "The connected-state cursor can be created from its actual embedded stream.");
    }

    private static void CheckRealViewerLifecycle()
    {
        using var host = new SyntheticHost();
        using var viewer = new HiddenViewer();
        int firstTunnelPort;
        using (var tunnel = host.Tunnel())
        {
            firstTunnelPort = tunnel.Port;
            viewer.Connect(tunnel, host.Password);
            PumpUntil(() => viewer.ConnectedEvents == 1 && PixelsMatch(viewer.Control), "The first synthetic framebuffer was not decoded.");
            Check(viewer.Control.IsConnected && viewer.PasswordRequests == 1 && viewer.EventThread == Environment.CurrentManagedThreadId,
                "The actual viewer completes TLS/RFB password authentication and raises connection completion on its STA thread.");
            Check(PixelsMatch(viewer.Control) && host.Created.Single().Captures > 0,
                "The actual viewer decodes every expected synthetic framebuffer pixel through the pinned TLS tunnel.");
            Check(!viewer.Owner.Visible && viewer.Control.ViewOnly && !viewer.Control.ContainsFocus && host.TotalInput == 0,
                "The hidden view-only control exchanges no keyboard or pointer input and shows no window.");
            viewer.Control.Disconnect();
            PumpTask(tunnel.Completion, "The first tunnel did not finish after client disconnect.");
            PumpUntil(() => host.Created.Single().Disposed, "The first fake desktop was not disposed.");
            Check(!viewer.Control.IsConnected && viewer.DisconnectedEvents == 1 && !tunnel.Completion.IsFaulted && host.Host.IsHealthy,
                "Normal viewer disconnect finishes its tunnel and releases the synthetic host session.");
        }
        using (var tunnel = host.Tunnel())
        {
            viewer.Connect(tunnel, host.Password);
            PumpUntil(() => viewer.ConnectedEvents == 2 && host.Created.Count == 2 && PixelsMatch(viewer.Control),
                "The same viewer could not reconnect to a fresh synthetic session.");
            Check(viewer.Control.IsConnected && viewer.PasswordRequests == 2 && host.Created.Count == 2,
                "The same real control reconnects using a new tunnel and a freshly authenticated desktop session.");
            Check(!PortAcceptsConnection(firstTunnelPort),
                "The previous ephemeral loopback listener stays closed after reconnecting.");
            host.Created.Last().FailCapture = true;
            viewer.Control.FullScreenUpdate();
            PumpUntil(() => !viewer.Control.IsConnected && viewer.DisconnectedEvents == 2,
                "Synthetic capture failure did not close the real viewer.");
            PumpTask(tunnel.Completion, "The failed capture tunnel did not finish.");
            PumpUntil(() => host.Created.Last().Disposed, "Capture failure did not dispose its fake desktop.");
            Check(host.Host.IsHealthy && !viewer.Control.IsConnected && host.TotalInput == 0,
                "Synthetic capture failure disconnects the actual viewer without crashing the host or sending input.");
        }
        using (var tunnel = host.Tunnel())
        {
            viewer.Connect(tunnel, host.Password);
            PumpUntil(() => viewer.ConnectedEvents == 3 && host.Created.Count == 3 && PixelsMatch(viewer.Control),
                "The viewer could not reconnect after synthetic capture failure.");
            Check(viewer.Control.IsConnected && PixelsMatch(viewer.Control),
                "The same control recovers from capture loss and renders another authenticated synthetic framebuffer.");
            viewer.Control.Disconnect();
            PumpTask(tunnel.Completion, "The recovery tunnel did not finish after disconnect.");
            PumpUntil(() => host.Created.Last().Disposed, "The recovery desktop was not disposed.");
        }
        Check(host.Created.All(desktop => desktop.Disposed) && host.TotalInput == 0,
            "Every synthetic desktop is disposed after repeated connection and disconnection, with no input delivered.");
    }

    private static void CheckAuthenticationRejection(string rejection)
    {
        using var host = new SyntheticHost();
        using var viewer = new HiddenViewer();
        string wrongToken = (host.Password[0] == 'A' ? "B" : "A") + host.Password[1..];
        using var tunnel = host.Tunnel(rejection == "certificate pin" ? new string('0', 64) : null,
            rejection == "sharing token" ? wrongToken : null);
        try { viewer.Connect(tunnel, rejection == "RFB password" ? "BADPASS!" : host.Password); }
        catch (Exception exception) when (exception is IOException or VncProtocolException or InvalidOperationException) { }
        PumpTask(tunnel.Completion, "The rejected " + rejection + " tunnel did not finish.");
        Check(!viewer.Control.IsConnected && viewer.ConnectedEvents == 0 && host.TotalCaptures == 0 && host.TotalInput == 0,
            "An incorrect " + rejection + " cannot connect the real viewer, capture synthetic pixels or deliver input.");
        if (rejection == "RFB password")
        {
            Check(viewer.PasswordRequests == 1 && host.Created.Count == 1,
                "RFB rejection follows outer TLS/token authentication and uses only the injected password callback.");
        }
        else
        {
            Exception? failure = tunnel.Completion.Exception?.GetBaseException();
            Check(viewer.PasswordRequests == 0 && host.Created.Count == 0 && failure is AuthenticationException or IOException,
                "TLS rejection finishes with an observed authentication/transport error before constructing a desktop or requesting its RFB password.");
        }
    }

    private static bool PixelsMatch(RemoteDesktop control)
    {
        if (control.Desktop is not Bitmap bitmap || bitmap.Width != SyntheticDesktop.Width || bitmap.Height != SyntheticDesktop.Height)
            return false;
        for (int y = 0; y < SyntheticDesktop.Height; y++)
            for (int x = 0; x < SyntheticDesktop.Width; x++)
                if ((bitmap.GetPixel(x, y).ToArgb() & 0xFFFFFF) != SyntheticDesktop.Pixel(x, y)) return false;
        return true;
    }

    private static void PumpUntil(Func<bool> complete, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (!complete() && timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        if (!complete()) throw new TimeoutException(failure);
    }

    private static void PumpTask(Task task, string failure)
    {
        PumpUntil(() => task.IsCompleted, failure);
        _ = task.Exception; // Expected negative TLS tests must observe transport failures.
    }

    private static bool PortAcceptsConnection(int port)
    {
        using var probe = new TcpClient(AddressFamily.InterNetwork);
        try { probe.Connect(IPAddress.Loopback, port); return true; }
        catch (SocketException) { return false; }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _passed++;
        Console.WriteLine("PASS " + description);
    }

    private sealed class HiddenViewer : IDisposable
    {
        public Form Owner { get; } = new() { ShowInTaskbar = false, ClientSize = new Size(320, 240) };
        public RemoteDesktop Control { get; } = new() { Dock = DockStyle.Fill, ViewOnly = true };
        public int ConnectedEvents { get; private set; }
        public int DisconnectedEvents { get; private set; }
        public int PasswordRequests { get; private set; }
        public int EventThread { get; private set; }
        public HiddenViewer()
        {
            Owner.Controls.Add(Control);
            _ = Owner.Handle;
            _ = Control.Handle;
            Control.ConnectComplete += (_, _) => { ConnectedEvents++; EventThread = Environment.CurrentManagedThreadId; };
            Control.ConnectionLost += (_, _) => DisconnectedEvents++;
        }
        public void Connect(VncClientTunnel tunnel, string password)
        {
            Control.VncPort = tunnel.Port;
            Control.GetPassword = () => { PasswordRequests++; return password; };
            using var timeout = new System.Threading.Timer(_ => tunnel.Dispose(), null, TimeSpan.FromSeconds(8), Timeout.InfiniteTimeSpan);
            Control.Connect("127.0.0.1", true, false);
        }
        public void Dispose()
        {
            if (Control.IsConnected) Control.Disconnect();
            Owner.Dispose();
        }
    }

    private sealed class SyntheticHost : IDisposable
    {
        private readonly (VncHostSettings Settings, string Fingerprint, string Password) _security = VncSecurity.Create();
        public ConcurrentQueue<SyntheticDesktop> Created { get; } = new();
        public VncSharingHost Host { get; }
        public string Password => _security.Password;
        public int TotalCaptures => Created.Sum(desktop => desktop.Captures);
        public int TotalInput => Created.Sum(desktop => desktop.Input);
        public SyntheticHost()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Host = new VncSharingHost(_security.Settings, new SharedConnection
            {
                Protocol = "VNC", Port = port, Password = Password, CertificateFingerprint = _security.Fingerprint
            }, IPAddress.IsLoopback, () =>
            {
                var desktop = new SyntheticDesktop();
                Created.Enqueue(desktop);
                return desktop;
            }, IPAddress.Loopback);
            Host.Start();
        }
        public VncClientTunnel Tunnel(string? fingerprint = null, string? password = null) =>
            new("127.0.0.1", Host.Port, fingerprint ?? _security.Fingerprint, password ?? Password);
        public void Dispose() => Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class SyntheticDesktop : IVncDesktopSession
    {
        public const int Width = 64, Height = 48;
        private readonly VncFramebuffer _frame = new("Synthetic real-viewer framebuffer", Width, Height);
        private int _captures, _input, _disposed, _failCapture;
        public int Captures => Volatile.Read(ref _captures);
        public int Input => Volatile.Read(ref _input);
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public bool FailCapture { set => Volatile.Write(ref _failCapture, value ? 1 : 0); }
        public static int Pixel(int x, int y) => (x * 3) << 16 | (y * 4) << 8 | 0x55;
        public SyntheticDesktop()
        {
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++) _frame.SetPixel(x, y, Pixel(x, y));
        }
        public VncFramebuffer Capture()
        {
            Interlocked.Increment(ref _captures);
            if (Volatile.Read(ref _failCapture) != 0) throw new IOException("Synthetic capture failure.");
            return _frame;
        }
        public void KeyChanged(KeyChangedEventArgs args) => Interlocked.Increment(ref _input);
        public void PointerChanged(PointerChangedEventArgs args) => Interlocked.Increment(ref _input);
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
