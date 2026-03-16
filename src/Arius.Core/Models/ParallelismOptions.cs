namespace Arius.Core.Models;

/// <summary>
/// Per-stage concurrency limits for the backup and restore pipelines.
/// A value of 0 means "use the default".
/// </summary>
public sealed record ParallelismOptions(
    int MaxFileProcessors,
    int MaxSealWorkers,
    int MaxUploaders,
    int MaxDownloaders,
    int MaxAssemblers)
{
    /// <summary>
    /// Default concurrency levels:
    /// - File processors / Assemblers: ProcessorCount (CPU + disk I/O bound)
    /// - Seal workers: max(1, ProcessorCount / 2)  (~29 MB RAM each)
    /// - Uploaders / Downloaders: 4  (network bound)
    /// </summary>
    public static readonly ParallelismOptions Default = new(
        MaxFileProcessors: Environment.ProcessorCount,
        MaxSealWorkers:    Math.Max(1, Environment.ProcessorCount / 2),
        MaxUploaders:      4,
        MaxDownloaders:    4,
        MaxAssemblers:     Environment.ProcessorCount);

    /// <summary>
    /// Returns this instance if all values are non-zero, otherwise falls back to <see cref="Default"/>
    /// for any zero-valued field.
    /// </summary>
    public ParallelismOptions Resolve() => new(
        MaxFileProcessors: MaxFileProcessors > 0 ? MaxFileProcessors : Default.MaxFileProcessors,
        MaxSealWorkers:    MaxSealWorkers    > 0 ? MaxSealWorkers    : Default.MaxSealWorkers,
        MaxUploaders:      MaxUploaders      > 0 ? MaxUploaders      : Default.MaxUploaders,
        MaxDownloaders:    MaxDownloaders    > 0 ? MaxDownloaders    : Default.MaxDownloaders,
        MaxAssemblers:     MaxAssemblers     > 0 ? MaxAssemblers     : Default.MaxAssemblers);
}
