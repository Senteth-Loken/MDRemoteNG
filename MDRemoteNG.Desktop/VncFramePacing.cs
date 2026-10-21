namespace MDRemoteNG.Desktop;

/// <summary>Targets responsive updates while limiting capture/diff work on slower displays.</summary>
internal sealed class VncFramePacing
{
    internal const double MaximumRate = 60;
    internal const double MinimumRate = 1;
    // The private source clock caps capture work. A nonzero library timeout
    // keeps no-request checks from spinning between public request signals.
    internal const double LibraryPollingRate = 1000;
    private const double WorkMillisecondsPerSecond = 400;
    private readonly object _sync = new();
    private double _captureMilliseconds, _scanMilliseconds;

    internal double TargetRate
    {
        get
        {
            lock (_sync)
            {
                double work = _captureMilliseconds + _scanMilliseconds;
                return work <= 0 ? MaximumRate : Math.Clamp(WorkMillisecondsPerSecond / work, MinimumRate, MaximumRate);
            }
        }
    }

    internal void RecordCapture(TimeSpan elapsed)
    {
        lock (_sync) _captureMilliseconds = Estimate(_captureMilliseconds, elapsed);
    }

    internal void RecordScan(TimeSpan elapsed)
    {
        lock (_sync) _scanMilliseconds = Estimate(_scanMilliseconds, elapsed);
    }

    private static double Estimate(double previous, TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        double sample = elapsed.TotalMilliseconds;
        // Back off immediately for expensive work, recover gradually to avoid oscillation.
        return sample >= previous ? sample : previous * 0.75 + sample * 0.25;
    }
}
