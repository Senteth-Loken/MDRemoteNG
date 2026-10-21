using System.Security.Cryptography;
using System.Text.Json;

namespace MDRemoteNG.Vnc.Benchmarks;

internal static class Program
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test")) return MeasurementSelfChecks.Run();
            if (args.Length == 0 || args.Contains("--help"))
            {
                Console.WriteLine("Run: --label NAME --profile loopback|lan|constrained --workload standard|four-k --output FILE [--quick] [--server-rate LIBRARY_RATE_OVERRIDE]");
                Console.WriteLine("Compare: --compare BASELINE.json CURRENT.json --output FILE [--required-reduction 0.70]");
                Console.WriteLine("Each run uses synthetic pixels, a hidden view-only control, pinned TLS and offscreen painting. No live desktop/input.");
                return 0;
            }
            if (args.Contains("--compare")) return Compare(args);
            string workload = Value(args, "--workload", "standard");
            if (workload is not ("standard" or "four-k")) throw new ArgumentException("Unknown workload.");
            bool fourK = workload == "four-k";
            var options = new RunOptions(Value(args, "--label", "unlabelled"), Value(args, "--profile", "loopback"),
                int.Parse(Value(args, "--seed", "20261021")), int.Parse(Value(args, "--width", fourK ? "3840" : "1920")),
                int.Parse(Value(args, "--height", fourK ? "2160" : "1080")), args.Contains("--quick") ? 8 : fourK ? 30 : 60,
                args.Contains("--quick") ? 4 : fourK ? 20 : 40, args.Contains("--quick") ? 3 : fourK ? 10 : 15, 30,
                int.Parse(Value(args, "--server-rate", "0")), workload);
            if (options.Width < 256 || options.Height < 192 || options.ServerRateOverride is < 0 or > 120)
                throw new ArgumentException("Invalid benchmark dimensions or experimental frame rate.");
            _ = NetworkProfile.Named(options.Profile);
            string output = Path.GetFullPath(Value(args, "--output", "vnc-benchmark.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            var report = new BenchmarkReport { Options = options, HarnessSha256 = Hash(typeof(Program).Assembly.Location),
                Environment = new Dictionary<string, string> { ["Framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    ["OS"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription, ["Processors"] = System.Environment.ProcessorCount.ToString(),
                    ["Machine"] = System.Environment.MachineName, ["ServerGC"] = System.Runtime.GCSettings.IsServerGC.ToString() } };
            using var deadline = new System.Threading.Timer(_ =>
            {
                Console.Error.WriteLine("Benchmark exceeded its 10-minute process deadline.");
                Environment.Exit(3);
            }, null, TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
            using var context = new ApplicationContext();
            using var benchmark = new ViewerBenchmark(options, report);
            benchmark.Owner.BeginInvoke(async () =>
            {
                try { await benchmark.RunAsync(); report.Complete = true; }
                catch (Exception failure) { report.Error = failure.ToString(); Console.Error.WriteLine(failure); }
                finally
                {
                    report.Summaries = ReportSummary.From(report.Samples);
                    File.WriteAllText(output, JsonSerializer.Serialize(report, Json));
                    Console.WriteLine(JsonSerializer.Serialize(report.Summaries, Json));
                    Console.WriteLine("Report: " + output);
                    context.ExitThread();
                }
            });
            Application.Run(context);
            return report.Complete ? 0 : 1;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static int Compare(string[] args)
    {
        int index = Array.IndexOf(args, "--compare");
        var baseline = JsonSerializer.Deserialize<BenchmarkReport>(File.ReadAllText(args[index + 1]), Json)!;
        var current = JsonSerializer.Deserialize<BenchmarkReport>(File.ReadAllText(args[index + 2]), Json)!;
        if (!baseline.Complete || !current.Complete || baseline.Options is null || current.Options is null)
            throw new InvalidDataException("Only complete runs can be compared.");
        if (string.IsNullOrEmpty(baseline.HarnessSha256) || baseline.HarnessSha256 != current.HarnessSha256)
            throw new InvalidDataException("Paired runs must use identical compiled benchmark harnesses.");
        if (baseline.Options with { Label = "", ServerRateOverride = 0 } != current.Options with { Label = "", ServerRateOverride = 0 })
            throw new InvalidDataException("The paired workloads, dimensions, seed and network profile must match.");
        double required = double.Parse(Value(args, "--required-reduction", "0.70"), System.Globalization.CultureInfo.InvariantCulture);
        if (required is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(required));
        var verdicts = new List<object>();
        bool passed = true;
        foreach (string scenario in new[] { "idle-patch", "full-frame" })
        {
            var old = baseline.Summaries.Single(summary => summary.Scenario == scenario);
            var updated = current.Summaries.Single(summary => summary.Scenario == scenario);
            foreach (string quantile in new[] { "p50", "p95" })
            {
                double oldValue = quantile == "p50" ? old.PresentationToPaintP50Ms : old.PresentationToPaintP95Ms;
                double newValue = quantile == "p50" ? updated.PresentationToPaintP50Ms : updated.PresentationToPaintP95Ms;
                double reduction = 1 - newValue / oldValue;
                bool metricPassed = reduction >= required;
                passed &= metricPassed;
                verdicts.Add(new { Scenario = scenario, Quantile = quantile, BaselineMs = oldValue, CurrentMs = newValue, Reduction = reduction, Passed = metricPassed });
            }
        }
        foreach (double quantile in new[] { .5, .95 })
        {
            double oldValue = VisibleAgeSample.Quantile(baseline.VisibleAge, quantile);
            double newValue = VisibleAgeSample.Quantile(current.VisibleAge, quantile);
            if (oldValue <= 0 || baseline.VisibleAge.Count < 10 || current.VisibleAge.Count < 10)
                throw new InvalidDataException("Continuous visible-age samples are incomplete.");
            double reduction = 1 - newValue / oldValue;
            bool metricPassed = reduction >= required;
            passed &= metricPassed;
            verdicts.Add(new { Scenario = "continuous-time-weighted-visible-age", Quantile = "p" + (quantile * 100),
                BaselineMs = oldValue, CurrentMs = newValue, Reduction = reduction, Passed = metricPassed });
        }
        bool production = baseline.Options.ServerRateOverride == 0 && current.Options.ServerRateOverride == 0;
        bool fullWorkload = baseline.Options.Workload == "four-k"
            ? baseline.Options.Width == 3840 && baseline.Options.Height == 2160 && baseline.Options.PatchSamples >= 30 && baseline.Options.FullFrameSamples >= 20 && baseline.Options.ContinuousSeconds >= 10
            : baseline.Options.PatchSamples >= 60 && baseline.Options.FullFrameSamples >= 40 && baseline.Options.ContinuousSeconds >= 15;
        var comparison = new { Baseline = baseline.Options.Label, Current = current.Options.Label, baseline.Options.Profile,
            RequiredReduction = required, ProductionSettings = production, FullAcceptanceWorkload = fullWorkload, AllSixMetricsPassed = passed,
            ProductionAcceptancePassed = passed && production && fullWorkload, HarnessSha256 = baseline.HarnessSha256, Verdicts = verdicts,
            Boundary = "Precomputed frame presentation START (before lock/copy) through actual control OnPaint using a preallocated Graphics and production dirty-region clip. Continuous gate integrates exact linear visible-age segments split at each paint boundary. Delivered-frame age, skipped IDs and frame intervals remain separately reported." };
        string output = Path.GetFullPath(Value(args, "--output", "vnc-comparison.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(comparison, Json));
        Console.WriteLine(JsonSerializer.Serialize(comparison, Json));
        return passed && production && fullWorkload ? 0 : 2;
    }

    private static string Value(string[] args, string name, string fallback)
    {
        int index = Array.IndexOf(args, name);
        return index < 0 ? fallback : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing " + name);
    }
    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

internal sealed record RunOptions(string Label, string Profile, int Seed, int Width, int Height,
    int PatchSamples, int FullFrameSamples, int ContinuousSeconds, int ProducerHz, int ServerRateOverride, string Workload);
internal sealed class BenchmarkReport
{
    public RunOptions? Options { get; set; }
    public bool Complete { get; set; }
    public string? Error { get; set; }
    public string Boundary { get; set; } = "Presentation START before frame lock/copy through actual hidden control OnPaint on a preallocated Graphics, with production dirty-region clip and verified painted markers. Image generation and an out-of-band DrawToBitmap diagnostic are excluded. This is offscreen UI paint, not physical monitor scan-out. No real desktop/input/clipboard.";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> RuntimeHashes { get; set; } = [];
    public string HarnessSha256 { get; set; } = "";
    public Dictionary<string, string> Environment { get; set; } = [];
    public double TemplateGenerationMs { get; set; }
    public double WarmupFullSnapshotMs { get; set; }
    public double? LibraryMaxUpdateRate { get; set; }
    public double? PolicyTargetCaptureRate { get; set; }
    public long CaptureCount { get; set; }
    public int InputCount { get; set; }
    public int ContinuousProduced { get; set; }
    public int ContinuousDisplayed { get; set; }
    public int ContinuousSkipped { get; set; }
    public int ProducerMissedDeadlines { get; set; }
    public int MaximumProducedMinusDisplayed { get; set; }
    public double ContinuousDrainMs { get; set; }
    public double ProcessCpuMs { get; set; }
    public long ProcessAllocatedBytes { get; set; }
    public NetworkStatistics? Network { get; set; }
    public List<FrameSample> Samples { get; set; } = [];
    public List<VisibleAgeSample> VisibleAge { get; set; } = [];
    public List<ReportSummary> Summaries { get; set; } = [];
}
internal sealed record VisibleAgeSample(double ElapsedMs, double WeightMs, int PaintedId, int LatestPresentedId,
    double StartAgeMs, double EndAgeMs, int IdLag, double UiQueueLagMs)
{
    public static double Quantile(IReadOnlyList<VisibleAgeSample> samples, double fraction)
    {
        if (samples.Count == 0) return 0;
        double threshold = samples.Sum(sample => sample.WeightMs) * fraction;
        double low = samples.Min(sample => sample.StartAgeMs), high = samples.Max(sample => sample.EndAgeMs);
        for (int iteration = 0; iteration < 64; iteration++)
        {
            double middle = (low + high) / 2;
            double accumulated = 0;
            foreach (var sample in samples)
            {
                double span = sample.EndAgeMs - sample.StartAgeMs;
                accumulated += sample.WeightMs * (span <= 0 ? middle >= sample.EndAgeMs ? 1 : 0 : Math.Clamp((middle - sample.StartAgeMs) / span, 0, 1));
            }
            if (accumulated >= threshold) high = middle;
            else low = middle;
        }
        return high;
    }
}
internal sealed record FrameSample(string Scenario, int Id, double PresentationMs, double PresentationToDecodeMs,
    double PaintMs, double PresentationToPaintMs, double ScheduledToPaintMs, double DisplayIntervalMs, long Rectangles,
    string[] Encodings, int ProducedMinusDisplayed);
internal sealed record ReportSummary(string Scenario, int Samples, double PresentationToPaintP50Ms,
    double PresentationToPaintP95Ms, double PresentationToPaintMaxMs, double PresentationP50Ms,
    double DecodeReadyP50Ms, double PaintP50Ms, double DisplayIntervalP50Ms, double DisplayIntervalP95Ms)
{
    public static List<ReportSummary> From(IEnumerable<FrameSample> samples) => samples.GroupBy(sample => sample.Scenario)
        .Select(group => new ReportSummary(group.Key, group.Count(), Quantile(group.Select(s => s.PresentationToPaintMs), .5),
            Quantile(group.Select(s => s.PresentationToPaintMs), .95), group.Max(s => s.PresentationToPaintMs),
            Quantile(group.Select(s => s.PresentationMs), .5), Quantile(group.Select(s => s.PresentationToDecodeMs), .5),
            Quantile(group.Select(s => s.PaintMs), .5), Quantile(group.Select(s => s.DisplayIntervalMs).Where(ms => ms > 0), .5),
            Quantile(group.Select(s => s.DisplayIntervalMs).Where(ms => ms > 0), .95))).ToList();
    private static double Quantile(IEnumerable<double> values, double fraction)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)];
    }
}
