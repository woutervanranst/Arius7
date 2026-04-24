namespace Arius.E2E.Tests.Datasets;

internal enum SyntheticFileMutationKind
{
    Add,
    Delete,
    Rename,
    ChangeContent,
}

internal sealed record SyntheticFileMutation
{
    public SyntheticFileMutation(SyntheticFileMutationKind Kind, string Path, string? TargetPath = null, string? ReplacementContentId = null, long? ReplacementSizeBytes = null)
    {
        var normalizedPath = SyntheticRepositoryPath.NormalizeRelativePath(Path, nameof(Path));
        var normalizedTargetPath = TargetPath is null
            ? null
            : SyntheticRepositoryPath.NormalizeRelativePath(TargetPath, nameof(TargetPath));

        this.Kind                 = Kind;
        this.Path                 = normalizedPath;
        this.TargetPath           = normalizedTargetPath;
        this.ReplacementContentId = ReplacementContentId;
        this.ReplacementSizeBytes = ReplacementSizeBytes;

        switch (Kind)
        {
            case SyntheticFileMutationKind.Add:
            case SyntheticFileMutationKind.ChangeContent:
                ArgumentException.ThrowIfNullOrWhiteSpace(ReplacementContentId);

                if (ReplacementSizeBytes is null)
                    throw new ArgumentException("Replacement size is required.", nameof(ReplacementSizeBytes));

                if (ReplacementSizeBytes <= 0)
                    throw new ArgumentOutOfRangeException(nameof(ReplacementSizeBytes), "Replacement size must be greater than zero.");

                if (TargetPath is not null)
                    throw new ArgumentException("Target path is not valid for content replacement mutations.", nameof(TargetPath));

                break;

            case SyntheticFileMutationKind.Rename:
                ArgumentException.ThrowIfNullOrWhiteSpace(TargetPath);

                if (ReplacementContentId is not null)
                    throw new ArgumentException("Replacement content is not valid for rename mutations.", nameof(ReplacementContentId));

                if (ReplacementSizeBytes is not null)
                    throw new ArgumentException("Replacement size is not valid for rename mutations.", nameof(ReplacementSizeBytes));

                break;

            case SyntheticFileMutationKind.Delete:
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

    public SyntheticFileMutationKind Kind                 { get; }
    public string                Path                 { get; }
    public string?               TargetPath           { get; }
    public string?               ReplacementContentId { get; }
    public long?                 ReplacementSizeBytes { get; }
}