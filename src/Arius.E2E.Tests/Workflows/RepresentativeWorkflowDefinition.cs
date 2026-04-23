using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Workflows;

internal sealed record RepresentativeWorkflowDefinition(
    string Name,
    RepresentativeWorkflowOperation Operation,
    RepresentativeWorkflowBackendRequirement BackendRequirement,
    RepresentativeWorkflowState State,
    bool UseNoPointers = false,
    bool UseRemoveLocal = false,
    bool UseOverwrite = true)
{
    public RepresentativeWorkflowArchiveMode ArchiveMode { get; init; } = RepresentativeWorkflowArchiveMode.Initial;

    public RepresentativeWorkflowRestoreTarget RestoreTarget { get; init; } = RepresentativeWorkflowRestoreTarget.None;

    public SyntheticRepositoryVersion SourceVersion => State.SourceVersion;

    public RepresentativeWorkflowCacheState CacheState => State.CacheState;
}
