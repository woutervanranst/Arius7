using Arius.Benchmarks;
using Arius.E2E.Tests.Datasets;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;

var options = BenchmarkRunOptions.Parse(args);
var runStartedAt = DateTimeOffset.UtcNow;
var runId = runStartedAt.ToString("yyyyMMddTHHmmss.fffZ");
var rawOutputDirectory = Path.Combine(options.RawOutputRoot, runId);
Directory.CreateDirectory(rawOutputDirectory);
Directory.CreateDirectory(Path.GetDirectoryName(options.TailLogPath)!);

var logger = new StreamLogger(Path.Combine(rawOutputDirectory, "benchmark-output.log"), append: false);

if (options.Class is BenchmarkClass.Micro)
{
    // Micro-benchmarks need BenchmarkDotNet's normal warmup/invocation defaults to produce meaningful
    // per-operation numbers, so they deliberately do NOT use the single-invocation job below. They also
    // do not append to the tail log: its schema (RepresentativeScaleDivisor, ...) is archive-specific.
    var microConfig = ManualConfig
        .Create(DefaultConfig.Instance)
        .AddLogger(logger)
        .WithArtifactsPath(rawOutputDirectory);

    // --filter lets a single optimization be re-measured on its own benchmark rather than re-running
    // the whole suite after every change.
    if (options.Filter is { } filter)
        microConfig = microConfig.AddFilter(new GlobFilter([filter]));

    BenchmarkRunner.Run<AllocationBenchmarks>(microConfig);

    return;
}

// The archive step runs for tens of seconds and mutates real fixtures per iteration, so it is measured
// with one invocation per iteration and no warmup.
var config = ManualConfig
    .Create(DefaultConfig.Instance)
    .AddJob(Job.Default
        .WithLaunchCount(1)
        .WithWarmupCount(0)
        .WithIterationCount(BenchmarkRunOptions.Iterations)
        .WithInvocationCount(1)
        .WithUnrollFactor(1))
    .AddLogger(logger)
    .WithArtifactsPath(rawOutputDirectory);

var summary = BenchmarkRunner.Run<ArchiveStepBenchmarks>(config);

BenchmarkTailLog.Append(
    options.TailLogPath,
    new BenchmarkTailLogEntry(
        ComputerName: Environment.MachineName,
        DateTimeUtc: runStartedAt.ToString("O"),
        GitHead: GitHeadResolver.Resolve(options.RepositoryRoot),
        RepresentativeScaleDivisor: SyntheticRepositoryDefinitionFactory.RepresentativeScaleDivisor,
        Iterations: BenchmarkRunOptions.Iterations,
        RawOutputPath: Path.GetRelativePath(options.RepositoryRoot, rawOutputDirectory)),
    summary);
