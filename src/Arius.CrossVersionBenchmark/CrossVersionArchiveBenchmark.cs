using BenchmarkDotNet.Attributes;

namespace Arius.CrossVersionBenchmark;

/// <summary>
/// One cold archive of the pristine dataset per iteration, into a brand-new container, driven
/// through the real v5 / v7 CLI. Runs in a BenchmarkDotNet child process: <see cref="GlobalSetup"/>
/// re-parses the configuration from environment variables and builds the dataset snapshot.
/// BenchmarkDotNet times the archive itself; peak memory and resulting blob size are gathered
/// outside the timed region and written to per-version CSV files the host reads for its columns.
/// </summary>
[MarkdownExporter]
public class CrossVersionArchiveBenchmark
{
    [Params("v5", "v7")]
    public string Version { get; set; } = "";

    int    _iteration;
    string _container     = "";
    string _workingSource = "";
    long   _peakRssBytes;

    [GlobalSetup]
    public void GlobalSetup()
    {
        BenchmarkSettings.Parse([]);
        BenchmarkSettings.BuildHeavyState();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _iteration++;
        _container     = BenchmarkSettings.NewContainerName(Version, _iteration);
        _workingSource = Path.Combine(BenchmarkSettings.OutputDirectory, "work", $"{Version}-{_iteration}");
        BenchmarkSettings.CopyPristineTo(_workingSource);
    }

    [Benchmark(Description = "archive")]
    public void Archive()
    {
        var logPath = Path.Combine(BenchmarkSettings.OutputDirectory, $"{Version}-{_iteration}.log");
        _peakRssBytes = BenchmarkSettings.RunArchive(Version, _container, _workingSource, logPath);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // Done outside the timed Archive() body so the blob listing never counts as archive time.
        var blobBytes = BenchmarkSettings.GetContainerBytes(_container);
        BenchmarkSettings.AppendMetric(Version, _peakRssBytes, blobBytes);

        if (Directory.Exists(_workingSource))
            Directory.Delete(_workingSource, recursive: true);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (BenchmarkSettings.KeepContainers)
        {
            foreach (var container in BenchmarkSettings.ListCreatedContainers())
                Console.WriteLine($"  kept container {container}");
        }
        else
        {
            BenchmarkSettings.DeleteCreatedContainers();
        }
    }
}
