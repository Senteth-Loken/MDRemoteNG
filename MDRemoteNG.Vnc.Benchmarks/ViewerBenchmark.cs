using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using MDRemoteNG.Core;
using MDRemoteNG.Desktop;
using RemoteViewing.Vnc;
using RemoteViewing.Vnc.Server;
using VncSharpCore;
using VncClient = VncSharpCore.VncClient;

namespace MDRemoteNG.Vnc.Benchmarks;

internal sealed class ViewerBenchmark : IDisposable
{
    private readonly RunOptions _options;
    private readonly BenchmarkReport _report;
    private readonly RemoteDesktop _viewer;
    private readonly Action<PaintEventArgs> _paint;
    private readonly ObserverControl _observer;
    private readonly Bitmap _paintSurface;
    private readonly Graphics _paintGraphics;
    private readonly ConcurrentDictionary<int, Presentation> _presentations = new();
    private readonly System.Threading.Timer _heartbeat;
    private SyntheticDesktop? _desktop;
    private int _lastPaintedId, _heartbeatPending;
    private long _lastPaintedAt, _lastPaintedSourceTimestamp, _continuousStart, _heartbeatPrevious;
    private bool _measureVisibleAge;
    private long _rectangles, _previousRectangles;
    private readonly HashSet<string> _encodings = [];
    public Form Owner { get; }

    public ViewerBenchmark(RunOptions options, BenchmarkReport report)
    {
        _options = options;
        _report = report;
        Owner = new Form { ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.None, ClientSize = new Size(options.Width, options.Height) };
        _viewer = new RemoteDesktop { ViewOnly = true, Bounds = new Rectangle(0, 0, options.Width, options.Height) };
        _paint = typeof(RemoteDesktop).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<PaintEventArgs>>(_viewer);
        _observer = new ObserverControl(OnRectangle);
        Owner.Controls.Add(_viewer);
        Owner.Controls.Add(_observer);
        _ = Owner.Handle;
        _ = _viewer.Handle;
        _ = _observer.Handle;
        _paintSurface = new Bitmap(options.Width, options.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        _paintGraphics = Graphics.FromImage(_paintSurface);
        _heartbeat = new System.Threading.Timer(_ =>
        {
            long posted = Stopwatch.GetTimestamp();
            if (Interlocked.Exchange(ref _heartbeatPending, 1) != 0) return;
            try
            {
                Owner.BeginInvoke(() =>
                {
                    Volatile.Write(ref _heartbeatPending, 0);
                    if (_measureVisibleAge) SampleVisibleAge(posted);
                });
            }
            catch (InvalidOperationException) { Volatile.Write(ref _heartbeatPending, 0); }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task RunAsync()
    {
        var generation = Stopwatch.StartNew();
        _desktop = new SyntheticDesktop(_options.Width, _options.Height, _presentations);
        _report.TemplateGenerationMs = generation.Elapsed.TotalMilliseconds;
        foreach (var assembly in new[] { typeof(RemoteDesktop).Assembly, typeof(VncSharingHost).Assembly, typeof(SharedConnection).Assembly, typeof(VncFramebuffer).Assembly })
            _report.RuntimeHashes[assembly.GetName().Name!] = Program.Hash(assembly.Location);
        long cpuStart = Process.GetCurrentProcess().TotalProcessorTime.Ticks;
        long allocatedStart = GC.GetTotalAllocatedBytes();
        var security = VncSecurity.Create();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        await using var host = new VncSharingHost(security.Settings, new SharedConnection
        {
            Protocol = "VNC", Port = port, Password = security.Password, CertificateFingerprint = security.Fingerprint
        }, IPAddress.IsLoopback, () => _desktop, IPAddress.Loopback);
        host.Start();
        await using var network = new TcpNetworkEmulator(host.Port, NetworkProfile.Named(_options.Profile));
        using var tunnel = new VncClientTunnel("127.0.0.1", network.Port, security.Fingerprint, security.Password);
        _viewer.GetPassword = () => security.Password;
        _viewer.VncPort = tunnel.Port;
        Presentation initial = _desktop.Present("warmup", true, 0);
        try
        {
            _viewer.Connect("127.0.0.1", true, false);
            _observer.Attach(_viewer);
            var session = await FindSession(host);
            if (session is not null)
            {
                if (_options.ServerRateOverride > 0) session.MaxUpdateRate = _options.ServerRateOverride;
                _report.LibraryMaxUpdateRate = session.MaxUpdateRate;
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                var property = typeof(VncSharingHost).GetProperty("TargetUpdateRate", flags);
                var field = typeof(VncSharingHost).GetField("TargetUpdateRate", flags);
                if (property is not null)
                    _report.PolicyTargetCaptureRate = Convert.ToDouble(property.GetValue(property.GetGetMethod(true)!.IsStatic ? null : host));
                else if (field is not null)
                    _report.PolicyTargetCaptureRate = Convert.ToDouble(field.GetValue(field.IsStatic ? null : host));
                else if (session.MaxUpdateRate <= 120) _report.PolicyTargetCaptureRate = session.MaxUpdateRate;
            }
            else if (_options.ServerRateOverride > 0) throw new InvalidOperationException("Cannot locate the test-only server pacing seam.");
            await WaitFor(initial, tunnel, network);
            await PresentAndWait("warmup", false, 1, tunnel, network);
            using (var snapshot = new Bitmap(_options.Width, _options.Height))
            {
                var snapshotTime = Stopwatch.StartNew();
                _viewer.DrawToBitmap(snapshot, new Rectangle(0, 0, _options.Width, _options.Height));
                _report.WarmupFullSnapshotMs = snapshotTime.Elapsed.TotalMilliseconds;
                if (!Matches(snapshot, _presentations[_lastPaintedId])) throw new InvalidDataException("Warmup snapshot verification failed.");
            }
            _report.Samples.Clear();
            var random = new Random(_options.Seed);
            for (int index = 0; index < _options.PatchSamples; index++)
            {
                // Fixed seed, no synchronization to the server's update cadence.
                await Task.Delay(random.Next(180, 361));
                await PresentAndWait("idle-patch", false, index + 2, tunnel, network);
            }
            for (int index = 0; index < _options.FullFrameSamples; index++)
            {
                await Task.Delay(random.Next(60, 181));
                await PresentAndWait("full-frame", true, index + 4, tunnel, network);
            }
            await MeasureContinuous(tunnel, network);
            if (Owner.Visible || !_viewer.ViewOnly || _desktop.InputCount != 0)
                throw new InvalidOperationException("Synthetic-only benchmark safety assertion failed.");
            if (_report.Samples.Count(sample => sample.Scenario == "idle-patch") != _options.PatchSamples ||
                _report.Samples.Count(sample => sample.Scenario == "full-frame") != _options.FullFrameSamples ||
                _report.ContinuousDisplayed < 3)
                throw new InvalidDataException("A workload did not finish; partial results cannot pass.");
        }
        finally
        {
            _measureVisibleAge = false;
            _heartbeat.Change(Timeout.Infinite, Timeout.Infinite);
            if (_viewer.IsConnected) _viewer.Disconnect();
            await Task.WhenAny(tunnel.Completion, Task.Delay(5000));
            _ = tunnel.Completion.Exception;
            _report.CaptureCount = _desktop.Captures;
            _report.InputCount = _desktop.InputCount;
            _report.ProcessCpuMs = TimeSpan.FromTicks(Process.GetCurrentProcess().TotalProcessorTime.Ticks - cpuStart).TotalMilliseconds;
            _report.ProcessAllocatedBytes = GC.GetTotalAllocatedBytes() - allocatedStart;
            _report.Network = network.Statistics;
        }
    }

    private async Task PresentAndWait(string scenario, bool full, int template, VncClientTunnel tunnel, TcpNetworkEmulator network)
    {
        Presentation presentation = await Task.Run(() => _desktop!.Present(scenario, full, template));
        await WaitFor(presentation, tunnel, network);
    }
    private static async Task WaitFor(Presentation presentation, VncClientTunnel tunnel, TcpNetworkEmulator network)
    {
        var timer = Stopwatch.StartNew();
        while (!presentation.Painted.Task.IsCompleted)
        {
            if (tunnel.Completion.IsCompleted || network.Completion.IsCompleted)
                throw new IOException("The synthetic transport closed before the requested frame painted.", tunnel.Completion.Exception ?? network.Completion.Exception);
            if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException("Frame did not paint: " + presentation.Scenario + " " + presentation.Id);
            await Task.Delay(2);
        }
        await presentation.Painted.Task;
    }

    private async Task MeasureContinuous(VncClientTunnel tunnel, TcpNetworkEmulator network)
    {
        int produced = 0, missed = 0, maximumBehind = 0;
        Presentation? last = null;
        _continuousStart = _heartbeatPrevious = Stopwatch.GetTimestamp();
        _measureVisibleAge = true;
        _heartbeat.Change(0, 10);
        long period = Stopwatch.Frequency / _options.ProducerHz;
        long stopAt = _continuousStart + _options.ContinuousSeconds * Stopwatch.Frequency;
        await Task.Run(async () =>
        {
            long due = _continuousStart;
            while (Stopwatch.GetTimestamp() < stopAt)
            {
                await TcpNetworkEmulator.WaitUntil(due, CancellationToken.None);
                if (Stopwatch.GetTimestamp() >= stopAt) break;
                last = _desktop!.Present("continuous", true, produced + 7, due);
                produced++;
                maximumBehind = Math.Max(maximumBehind, last.Id - Volatile.Read(ref _lastPaintedId));
                due += period;
                long now = Stopwatch.GetTimestamp();
                if (now - due > period)
                {
                    int skipped = (int)((now - due) / period);
                    missed += skipped;
                    due += skipped * period; // Never generate an unbounded catch-up queue.
                }
            }
        });
        SampleVisibleAge(Stopwatch.GetTimestamp());
        _measureVisibleAge = false;
        _heartbeat.Change(Timeout.Infinite, Timeout.Infinite);
        long stop = Stopwatch.GetTimestamp();
        if (last is not null) await WaitFor(last, tunnel, network);
        _report.ContinuousDrainMs = Milliseconds(Stopwatch.GetTimestamp() - stop);
        _report.ContinuousProduced = produced;
        _report.ContinuousDisplayed = _report.Samples.Count(sample => sample.Scenario == "continuous");
        _report.ContinuousSkipped = produced - _report.ContinuousDisplayed;
        _report.ProducerMissedDeadlines = missed;
        _report.MaximumProducedMinusDisplayed = maximumBehind;
    }

    private void SampleVisibleAge(long posted, long? exactBoundary = null)
    {
        long now = exactBoundary ?? Stopwatch.GetTimestamp();
        _report.VisibleAge.Add(new(Milliseconds(now - _continuousStart), Milliseconds(now - _heartbeatPrevious),
            _lastPaintedId, _desktop!.LatestPresentedId, Milliseconds(_heartbeatPrevious - _lastPaintedSourceTimestamp), Milliseconds(now - _lastPaintedSourceTimestamp),
            Math.Max(0, _desktop.LatestPresentedId - _lastPaintedId), Milliseconds(now - posted)));
        _heartbeatPrevious = now;
    }

    private void OnRectangle(object sender, VncEventArgs args)
    {
        _rectangles++;
        _encodings.Add(args.DesktopUpdater.GetType().Name);
        if (_desktop is null || _viewer.Desktop is not Bitmap decoded) return;
        foreach (bool full in new[] { true, false })
        {
            Point marker = _desktop.Marker(full);
            int id = decoded.GetPixel(marker.X, marker.Y).ToArgb() & 0xFFFFFF;
            if (id <= _lastPaintedId || !_presentations.TryGetValue(id, out Presentation? presentation) ||
                !presentation.Ready || presentation.Full != full || !Matches(decoded, presentation)) continue;
            long decodedAt = Stopwatch.GetTimestamp();
            PaintRegion(presentation.Full ? new Rectangle(0, 0, _options.Width, _options.Height) : SyntheticDesktop.Patch);
            long paintedAt = Stopwatch.GetTimestamp();
            if (!Matches(_paintSurface, presentation))
                throw new InvalidDataException("The actual control's offscreen paint did not contain its complete synthetic update.");
            var sample = new FrameSample(presentation.Scenario, id,
                Milliseconds(presentation.ReadyAt - presentation.StartedAt), Milliseconds(decodedAt - presentation.StartedAt),
                Milliseconds(paintedAt - decodedAt), Milliseconds(paintedAt - presentation.StartedAt),
                Milliseconds(paintedAt - presentation.ScheduledAt), _lastPaintedAt == 0 ? 0 : Milliseconds(paintedAt - _lastPaintedAt),
                _rectangles - _previousRectangles, _encodings.Order().ToArray(), Math.Max(0, _desktop.LatestPresentedId - id));
            _report.Samples.Add(sample);
            if (_measureVisibleAge) SampleVisibleAge(paintedAt, paintedAt);
            _previousRectangles = _rectangles;
            _encodings.Clear();
            _lastPaintedAt = paintedAt;
            _lastPaintedSourceTimestamp = presentation.StartedAt;
            Volatile.Write(ref _lastPaintedId, id);
            presentation.Painted.TrySetResult();
            break;
        }
    }

    private bool Matches(Bitmap bitmap, Presentation presentation)
    {
        Rectangle region = presentation.Full ? new Rectangle(0, 0, _options.Width, _options.Height) : SyntheticDesktop.Patch;
        Point marker = _desktop!.Marker(presentation.Full);
        if ((bitmap.GetPixel(marker.X, marker.Y).ToArgb() & 0xFFFFFF) != presentation.Id ||
            (bitmap.GetPixel(marker.X + 1, marker.Y).ToArgb() & 0xFFFFFF) != SyntheticDesktop.Checksum(presentation.Id)) return false;
        // Sample the complete region, including its edges; the frame marker is at its final scan-line.
        foreach (double fy in new[] { 0d, .25, .5, .75, 1d })
            foreach (double fx in new[] { 0d, .25, .5, .75, 1d })
            {
                int x = region.Left + (int)((region.Width - 1) * fx), y = region.Top + (int)((region.Height - 1) * fy);
                if (y == marker.Y && x >= marker.X) continue;
                if ((bitmap.GetPixel(x, y).ToArgb() & 0xFFFFFF) != SyntheticDesktop.Pixel(x, y, presentation.Template)) return false;
            }
        return true;
    }

    private static async Task<VncServerSession?> FindSession(VncSharingHost host)
    {
        var field = typeof(VncSharingHost).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(host) is not IEnumerable<KeyValuePair<int, Task>> tasks) return null;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            foreach (var pair in tasks)
            {
                object? machine = pair.Value.GetType().GetField("StateMachine", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(pair.Value);
                if (machine is null) continue;
                var session = FindSessionLocal(machine, 0);
                if (session is not null) return session;
            }
            await Task.Delay(2);
        }
        return null;
    }
    private static VncServerSession? FindSessionLocal(object instance, int depth)
    {
        if (instance is VncServerSession session) return session;
        if (depth > 4) return null;
        foreach (var local in instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            // The session can be hoisted into a compiler-generated event-handler closure.
            if (local.FieldType != typeof(VncServerSession) && local.FieldType.Assembly != typeof(VncSharingHost).Assembly) continue;
            object? value = local.GetValue(instance);
            if (value is null || ReferenceEquals(value, instance)) continue;
            var found = FindSessionLocal(value, depth + 1);
            if (found is not null) return found;
        }
        return null;
    }
    private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    public void Dispose()
    {
        _heartbeat.Dispose();
        if (_viewer.IsConnected) _viewer.Disconnect();
        Owner.Dispose();
        _paintGraphics.Dispose();
        _paintSurface.Dispose();
        _desktop?.Dispose();
    }

    private sealed class ObserverControl(Action<object, VncEventArgs> observe) : Control
    {
        private VncUpdateHandler? _draw;
        public void Attach(RemoteDesktop control)
        {
            var client = (VncClient)typeof(RemoteDesktop).GetField("vnc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control)!;
            _draw = (VncUpdateHandler)typeof(VncClient).GetField("VncUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            client.VncUpdate -= _draw;
            client.VncUpdate += DrawAndObserve; // A Control target preserves the production synchronous UI Invoke.
        }
        private void DrawAndObserve(object sender, VncEventArgs args)
        {
            _draw!(sender, args);
            observe(sender, args);
        }
    }

    private void PaintRegion(Rectangle region)
    {
        var state = _paintGraphics.Save();
        try
        {
            _paintGraphics.SetClip(region, System.Drawing.Drawing2D.CombineMode.Replace);
            using var args = new PaintEventArgs(_paintGraphics, region);
            _paint(args);
        }
        finally { _paintGraphics.Restore(state); }
    }
}

internal sealed class Presentation(int id, string scenario, bool full, int template, long started, long scheduled)
{
    public int Id { get; } = id;
    public string Scenario { get; } = scenario;
    public bool Full { get; } = full;
    public int Template { get; } = template;
    public long StartedAt { get; } = started;
    public long ScheduledAt { get; } = scheduled;
    public long ReadyAt;
    public volatile bool Ready;
    public TaskCompletionSource Painted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class SyntheticDesktop : IVncDesktopSession
{
    public static readonly Rectangle Patch = new(32, 32, 64, 64);
    private const int Templates = 17;
    private readonly int[][] _templates;
    private readonly VncFramebuffer _frame;
    private readonly ConcurrentDictionary<int, Presentation> _presentations;
    private int _nextId, _latest, _input;
    private long _captures;
    public int LatestPresentedId => Volatile.Read(ref _latest);
    public long Captures => Interlocked.Read(ref _captures);
    public int InputCount => Volatile.Read(ref _input);
    public SyntheticDesktop(int width, int height, ConcurrentDictionary<int, Presentation> presentations)
    {
        _presentations = presentations;
        _frame = new VncFramebuffer("Synthetic benchmark; no real desktop", width, height);
        _templates = Enumerable.Range(0, Templates).Select(template =>
        {
            int[] pixels = new int[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) pixels[y * width + x] = Pixel(x, y, template);
            return pixels;
        }).ToArray();
    }
    public static int Pixel(int x, int y, int template) => ((x * 73 + y * 29 + template * 61) & 255) << 16 |
        ((x * 31 + y * 37 + template * 23) & 255) << 8 | ((x * 17 + y * 43 + template * 83) & 255);
    public static int Checksum(int id) => (id ^ 0x5A33CC) & 0xFFFFFF;
    public Point Marker(bool full) => full ? new Point(_frame.Width - 2, _frame.Height - 1) : new Point(Patch.Right - 2, Patch.Bottom - 1);
    public Presentation Present(string scenario, bool full, int template, long scheduled = 0)
    {
        template %= Templates;
        long started = Stopwatch.GetTimestamp();
        var presentation = new Presentation(Interlocked.Increment(ref _nextId), scenario, full, template, started, scheduled == 0 ? started : scheduled);
        _presentations[presentation.Id] = presentation;
        lock (_frame.SyncRoot)
        {
            int[] target = _frame.GetPixels(), source = _templates[template];
            if (full) source.CopyTo(target, 0);
            else
                for (int y = Patch.Top; y < Patch.Bottom; y++)
                    source.AsSpan(y * _frame.Width + Patch.Left, Patch.Width).CopyTo(target.AsSpan(y * _frame.Width + Patch.Left, Patch.Width));
            Point marker = Marker(full);
            target[marker.Y * _frame.Width + marker.X] = presentation.Id;
            target[marker.Y * _frame.Width + marker.X + 1] = Checksum(presentation.Id);
            presentation.ReadyAt = Stopwatch.GetTimestamp();
            presentation.Ready = true;
            Volatile.Write(ref _latest, presentation.Id);
        }
        return presentation;
    }
    public VncFramebuffer Capture() { Interlocked.Increment(ref _captures); return _frame; }
    public void KeyChanged(KeyChangedEventArgs args) => Interlocked.Increment(ref _input);
    public void PointerChanged(PointerChangedEventArgs args) => Interlocked.Increment(ref _input);
    public void Dispose() { }
}
