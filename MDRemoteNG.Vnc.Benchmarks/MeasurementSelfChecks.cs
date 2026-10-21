namespace MDRemoteNG.Vnc.Benchmarks;

internal static class MeasurementSelfChecks
{
    public static int Run()
    {
        Check([Segment(0, 100)], 50, 95);
        Check([Segment(0, 50), Segment(0, 50)], 25, 47.5);
        Check([Segment(100, 110), Segment(0, 90)], 50, 105);
        Check([Segment(100, 110), Segment(5, 15)], 15, 109);
        Check([Segment(9, 10), Segment(50, 59)], 54, 58.5);
        TimerChecks().GetAwaiter().GetResult();
        Console.WriteLine("All 5 exact visible-age integration and 4 timer lifecycle checks passed.");
        return 0;
    }
    private static async Task TimerChecks()
    {
        await HighResolutionDelay.Until(System.Diagnostics.Stopwatch.GetTimestamp() - 1, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectCancellation(HighResolutionDelay.Until(long.MaxValue, cancelled.Token));
        using var pending = new CancellationTokenSource();
        Task waiting = HighResolutionDelay.Until(System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 60, pending.Token);
        pending.Cancel();
        await ExpectCancellation(waiting);
        // Exercise completed/signalled callback teardown repeatedly, without
        // asserting scheduler-dependent elapsed times or changing timer resolution.
        for (int iteration = 0; iteration < 16; iteration++)
            await HighResolutionDelay.Until(System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 1000, CancellationToken.None);
    }
    private static async Task ExpectCancellation(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new InvalidDataException("A cancelled private timer completed successfully.");
    }
    private static VisibleAgeSample Segment(double start, double end) => new(0, end - start, 1, 1, start, end, 0, 0);
    private static void Check(IReadOnlyList<VisibleAgeSample> samples, double median, double p95)
    {
        if (Math.Abs(VisibleAgeSample.Quantile(samples, .5) - median) > 0.000001 ||
            Math.Abs(VisibleAgeSample.Quantile(samples, .95) - p95) > 0.000001)
            throw new InvalidDataException("Visible-age integration did not match an analytic distribution.");
    }
}
