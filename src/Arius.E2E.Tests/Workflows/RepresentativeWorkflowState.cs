using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Workflows;

internal enum RepresentativeWorkflowOperation
{
    Archive,
    Restore,
    ArchiveThenRestore,
}

internal enum RepresentativeWorkflowCacheState
{
    Cold,
    Warm,
}

internal enum RepresentativeWorkflowBackendRequirement
{
    Any,
    AzureArchiveCapable,
}

internal enum RepresentativeWorkflowArchiveMode
{
    Initial,
    Incremental,
    NoChanges,
}

internal enum RepresentativeWorkflowRestoreTarget
{
    None,
    Latest,
    Previous,
    MultipleVersions,
}

internal sealed record RepresentativeWorkflowState(
    SyntheticRepositoryVersion SourceVersion,
    RepresentativeWorkflowCacheState CacheState);
