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

internal sealed record SyntheticMutation
{
    public SyntheticMutation(
        SyntheticMutationKind Kind,
        string Path,
        string? TargetPath = null,
        string? ReplacementContentId = null,
        long? ReplacementSizeBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);

        this.Kind = Kind;
        this.Path = Path;
        this.TargetPath = TargetPath;
        this.ReplacementContentId = ReplacementContentId;
        this.ReplacementSizeBytes = ReplacementSizeBytes;

        switch (Kind)
        {
            case SyntheticMutationKind.Add:
            case SyntheticMutationKind.ChangeContent:
                ArgumentException.ThrowIfNullOrWhiteSpace(ReplacementContentId);

                if (ReplacementSizeBytes is null)
                    throw new ArgumentException("Replacement size is required.", nameof(ReplacementSizeBytes));

                if (ReplacementSizeBytes <= 0)
                    throw new ArgumentOutOfRangeException(nameof(ReplacementSizeBytes), "Replacement size must be greater than zero.");

                if (TargetPath is not null)
                    throw new ArgumentException("Target path is not valid for content replacement mutations.", nameof(TargetPath));

                break;

            case SyntheticMutationKind.Rename:
                ArgumentException.ThrowIfNullOrWhiteSpace(TargetPath);

                if (ReplacementContentId is not null)
                    throw new ArgumentException("Replacement content is not valid for rename mutations.", nameof(ReplacementContentId));

                if (ReplacementSizeBytes is not null)
                    throw new ArgumentException("Replacement size is not valid for rename mutations.", nameof(ReplacementSizeBytes));

                break;

            case SyntheticMutationKind.Delete:
                if (TargetPath is not null)
                    throw new ArgumentException("Target path is not valid for delete mutations.", nameof(TargetPath));

                if (ReplacementContentId is not null)
                    throw new ArgumentException("Replacement content is not valid for delete mutations.", nameof(ReplacementContentId));

                if (ReplacementSizeBytes is not null)
                    throw new ArgumentException("Replacement size is not valid for delete mutations.", nameof(ReplacementSizeBytes));

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(Kind));
        }
    }

    public SyntheticMutationKind Kind { get; }
    public string Path { get; }
    public string? TargetPath { get; }
    public string? ReplacementContentId { get; }
    public long? ReplacementSizeBytes { get; }
}

internal sealed record SyntheticRepositoryDefinition(
    int SmallFileThresholdBytes,
    IReadOnlyList<string> RootDirectories,
    IReadOnlyList<SyntheticFileDefinition> Files,
    IReadOnlyList<SyntheticMutation> V2Mutations);
