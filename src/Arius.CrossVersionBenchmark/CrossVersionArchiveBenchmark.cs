using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;

namespace Arius.CrossVersionBenchmark;

/// <summary>
/// One cold archive of the pristine dataset per iteration, into a brand-new container,
/// driven through the real v5 / v7 CLI. BenchmarkDotNet times the archive itself; peak
/// memory and resulting blob size are gathered outside the timed region and stashed in
/// <see cref="MetricCollector"/> for the custom result columns.
/// </summary>
[MarkdownExporter]
public class CrossVersionArchiveBenchmark
{
    [Params("v5", "v7")]
    public string Version { get; set; } = "";

    static int _counter;
    string _container      = "";
    string _workingSource  = "";
    long   _peakRssBytes;

    [IterationSetup]
    public void IterationSetup()
    {
        var n = Interlocked.Increment(ref _counter);
        _container     = $"{BenchmarkSettings.ContainerPrefix}-{Version}-{BenchmarkSettings.RunId}-{n}";
        _workingSource = Path.Combine(BenchmarkSettings.OutputDirectory, "work", $"{Version}-{n}");
        BenchmarkSettings.CreatedContainers.Add(_container);
        BenchmarkSettings.CopyPristineTo(_workingSource);
    }

    [Benchmark(Description = "archive")]
    public void Archive()
    {
        var logPath = Path.Combine(BenchmarkSettings.OutputDirectory, $"{Path.GetFileName(_workingSource)}.log");
        _peakRssBytes = BenchmarkSettings.RunArchive(Version, _container, _workingSource, logPath);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // Done outside the timed Archive() body so the blob listing never counts as archive time.
        var blobBytes = BenchmarkSettings.GetContainerBytes(_container);
        MetricCollector.Add(Version, _peakRssBytes, blobBytes);

        if (Directory.Exists(_workingSource))
            Directory.Delete(_workingSource, recursive: true);
    }
}

/// <summary>Per-version peak-memory and blob-size samples, shared across the in-process run.</summary>
internal static class MetricCollector
{
    static readonly ConcurrentDictionary<string, ConcurrentBag<(long PeakRss, long Blob)>> Samples = new();

    public static void Add(string version, long peakRss, long blob)
        => Samples.GetOrAdd(version, _ => new()).Add((peakRss, blob));

    public static double? MeanPeakRss(string version)
        => Samples.TryGetValue(version, out var bag) && bag.Count > 0 ? bag.Average(s => s.PeakRss) : null;

    public static double? MeanBlob(string version)
        => Samples.TryGetValue(version, out var bag) && bag.Count > 0 ? bag.Average(s => s.Blob) : null;
}
