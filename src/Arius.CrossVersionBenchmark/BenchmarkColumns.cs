using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Arius.CrossVersionBenchmark;

internal static class BenchmarkConfig
{
    public static IConfig Create() =>
        ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance) // same process → collector + columns share state
                .WithStrategy(RunStrategy.ColdStart)            // each run is a cold, from-scratch archive
                .WithLaunchCount(1)
                .WithWarmupCount(0)
                .WithIterationCount(3)
                .WithInvocationCount(1)
                .WithUnrollFactor(1))
            .AddColumn(new MebibyteColumn("Source", "MiB of input archived (constant per run)", _ => BenchmarkSettings.SourceBytes))
            .AddColumn(new MebibyteColumn("Blob", "Mean MiB stored in the container after archive", MetricCollector.MeanBlob))
            .AddColumn(new MebibyteColumn("Peak mem", "Mean peak resident set size of the CLI process", MetricCollector.MeanPeakRss))
            .WithOption(ConfigOptions.DisableOptimizationsValidator, true);
}

/// <summary>A result column that renders a per-version byte value as MiB.</summary>
internal sealed class MebibyteColumn : IColumn
{
    readonly Func<string, double?> _selector;

    public MebibyteColumn(string name, string legend, Func<string, double?> selector)
    {
        ColumnName = name;
        Legend     = legend;
        _selector  = selector;
    }

    public string Id         => $"Arius.{ColumnName}";
    public string ColumnName  { get; }
    public string Legend      { get; }
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
