namespace Arius.E2E.Tests.Datasets;

internal enum SyntheticMutationKind
{
    Add,
    Delete,
    Rename,
    ChangeContent,
}

internal sealed record SyntheticFileDefinition(
    string Path,
    long SizeBytes,
    string? ContentId);

internal sealed record SyntheticMutation(
    SyntheticMutationKind Kind,
    string Path,
    string? TargetPath = null,
    string? ReplacementContentId = null);

internal sealed record SyntheticRepositoryDefinition(
    int SmallFileThresholdBytes,
    IReadOnlyList<string> RootDirectories,
    IReadOnlyList<SyntheticFileDefinition> Files,
    IReadOnlyList<SyntheticMutation> V2Mutations);
