using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MDRemoteNG.Desktop;

/// <summary>Signals the next capture deadline without blocking the protocol receiver or changing system timers.</summary>
internal sealed class VncCaptureClock : IDisposable
{
    private readonly object _sync = new();
    private readonly VncFramePacing _pacing;
    private readonly CancellationToken _stop;
    private readonly Action _signal;
    private readonly TimerWait? _timer;
    private readonly RegisteredWaitHandle? _registration;
    private readonly Timer? _fallback;
    private long _lastCapture, _armedDeadline, _generation;
    private int _scheduledWakes;
    private bool _disposed;
    internal bool UsesHighResolutionTimer => _timer != null;
    internal long Generation { get { lock (_sync) return _generation; } }
    internal int ScheduledWakeCount { get { lock (_sync) return _scheduledWakes; } }

    internal VncCaptureClock(VncFramePacing pacing, CancellationToken stop, Action signal)
    {
        _pacing = pacing;
        _stop = stop;
        _signal = signal;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
        {
            SafeWaitHandle handle = CreateWaitableTimerExW(IntPtr.Zero, null, 2, 0x00100002);
            if (!handle.IsInvalid)
            {
                _timer = new TimerWait(handle);
                _registration = ThreadPool.RegisterWaitForSingleObject(_timer, (_, _) => DeadlineReached(), null, Timeout.Infinite, false);
            }
            else handle.Dispose();
        }
        if (_timer == null) _fallback = new Timer(_ => DeadlineReached());
    }

    internal bool TryBeginCapture()
    {
        _stop.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long now = Stopwatch.GetTimestamp();
            long period = (long)Math.Ceiling(Stopwatch.Frequency / _pacing.TargetRate);
            long deadline = _lastCapture + period;
            long remaining = deadline - Stopwatch.GetTimestamp();
            if (_lastCapture == 0 || remaining <= 0)
            {
                // Missed deadlines are discarded rather than queued as catch-up captures.
                _lastCapture = now;
                _generation++;
                return true;
            }
            if (_armedDeadline == deadline) return false;
            _armedDeadline = deadline;
            if (_timer != null)
            {
                long due = -Math.Max(1, (long)Math.Ceiling(remaining * (10_000_000.0 / Stopwatch.Frequency)));
                if (!SetWaitableTimerEx(_timer.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            else
            {
                _fallback!.Change(Math.Max(1, (int)Math.Ceiling(remaining * (1000.0 / Stopwatch.Frequency))), Timeout.Infinite);
            }
            _scheduledWakes++;
            return false;
        }
    }

    private void DeadlineReached()
    {
        lock (_sync)
        {
            if (_disposed || _stop.IsCancellationRequested) return;
            _armedDeadline = 0;
        }
        try { _signal(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _registration?.Unregister(null);
        _timer?.Dispose();
        _fallback?.Dispose();
    }

    private sealed class TimerWait : WaitHandle
    {
        internal TimerWait(SafeWaitHandle timer) => SafeWaitHandle = timer;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long due, int period,
        IntPtr callback, IntPtr argument, IntPtr wakeContext, uint tolerableDelay);
}
