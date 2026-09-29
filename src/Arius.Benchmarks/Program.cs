using Arius.Benchmarks;
using Arius.E2E.Tests.Datasets;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;

// In-process allocation micro-benchmarks run on BenchmarkDotNet's default job and take its own options,
// e.g. `micro --filter '*TarBuilder*'`. They need no Docker and do not append to the tail log.
if (args is ["micro", .. var microArgs])
{
    BenchmarkSwitcher.FromTypes([typeof(AllocationBenchmarks)]).Run(microArgs);
    return;
}

var options = BenchmarkRunOptions.Parse(args);
var runStartedAt = DateTimeOffset.UtcNow;
var runId = runStartedAt.ToString("yyyyMMddTHHmmss.fffZ");
var rawOutputDirectory = Path.Combine(options.RawOutputRoot, runId);
Directory.CreateDirectory(rawOutputDirectory);
Directory.CreateDirectory(Path.GetDirectoryName(options.TailLogPath)!);

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
    .AddLogger(new StreamLogger(Path.Combine(rawOutputDirectory, "benchmark-output.log"), append: false))
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
