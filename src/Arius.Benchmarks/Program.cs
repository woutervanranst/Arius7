using Arius.Benchmarks;
using Arius.E2E.Tests.Datasets;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;

var options = BenchmarkRunOptions.Parse(args);
var runStartedAt = DateTimeOffset.UtcNow;
var runId = runStartedAt.ToString("yyyyMMddTHHmmss.fffZ");
var rawOutputDirectory = Path.Combine(options.RawOutputRoot, runId);
Directory.CreateDirectory(rawOutputDirectory);
Directory.CreateDirectory(Path.GetDirectoryName(options.TailLogPath)!);

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
