using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Arius.CrossVersionBenchmark;

internal static class BenchmarkConfig
{
    public static IConfig Create()
    {
        var job = Job.Default
            .WithStrategy(RunStrategy.ColdStart) // each run is a cold, from-scratch archive
            .WithLaunchCount(1)
            .WithWarmupCount(0)
            .WithIterationCount(BenchmarkSettings.IterationCount)
            .WithInvocationCount(1)
            .WithUnrollFactor(1);

        // Default (out-of-process) toolchain — required for these multi-minute archives. The
        // benchmark child process receives the configuration through these environment variables.
        foreach (var (key, value) in BenchmarkSettings.ToEnvironment())
            job = job.WithEnvironmentVariables(new EnvironmentVariable(key, value));

        return ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(job)
            .AddColumn(new MebibyteColumn("Source",   "MiB of input archived (constant per run)",        v => BenchmarkSettings.ReadMetrics(v)?.Source))
            .AddColumn(new MebibyteColumn("Blob",     "Mean MiB stored in the container after archive",  v => BenchmarkSettings.ReadMetrics(v)?.Blob))
            .AddColumn(new MebibyteColumn("Peak mem", "Mean peak resident set size of the CLI process",  v => BenchmarkSettings.ReadMetrics(v)?.PeakRss));
    }
}

/// <summary>A result column that renders a per-version byte value as MiB. Runs in the host process.</summary>
internal sealed class MebibyteColumn : IColumn
{
    readonly Func<string, double?> _selector;

    public MebibyteColumn(string name, string legend, Func<string, double?> selector)
    {
        ColumnName = name;
        Legend     = legend;
        _selector  = selector;
    }

    public string Id        => $"Arius.{ColumnName}";
    public string ColumnName { get; }
    public string Legend     { get; }
    public bool AlwaysShow                 => true;
    public ColumnCategory Category         => ColumnCategory.Custom;
    public int PriorityInCategory          => 0;
    public bool IsNumeric                  => true;
    public UnitType UnitType               => UnitType.Size;

    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, summary.Style);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        var version = benchmarkCase.Parameters["Version"]?.ToString();
        if (version is null)
            return "-";

        var bytes = _selector(version);
        return bytes is null ? "-" : (bytes.Value / (1024.0 * 1024.0)).ToString("F1") + " MiB";
    }
}
