using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Scenarios;

internal enum ScenarioOperation
{
    Archive,
    Restore,
    ArchiveThenRestore,
}

internal enum ScenarioCacheState
{
    Cold,
    Warm,
}

internal enum ScenarioBackendRequirement
{
    Any,
    AzureArchiveCapable,
}

internal enum ScenarioArchiveMode
{
    Initial,
    Incremental,
    NoChanges,
}

internal enum ScenarioRestoreTarget
{
    None,
    Latest,
    Previous,
    MultipleVersions,
}

internal sealed record RepresentativeScenarioDefinition(
    string Name,
    ScenarioOperation Operation,
    ScenarioBackendRequirement BackendRequirement,
    SyntheticRepositoryVersion SourceVersion,
    ScenarioCacheState CacheState,
    bool UseNoPointers = false,
    bool UseRemoveLocal = false,
    bool UseOverwrite = true)
{
    public ScenarioArchiveMode ArchiveMode { get; init; } = ScenarioArchiveMode.Initial;

    public ScenarioRestoreTarget RestoreTarget { get; init; } = ScenarioRestoreTarget.None;
}
