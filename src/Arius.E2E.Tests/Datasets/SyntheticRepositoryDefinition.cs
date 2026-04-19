namespace Arius.E2E.Tests.Datasets;

internal enum SyntheticMutationKind
{
    Add,
    Delete,
    Rename,
    ChangeContent,
}

internal sealed record SyntheticFileDefinition
{
    public SyntheticFileDefinition(string Path, long SizeBytes, string? ContentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);

        if (SizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(SizeBytes), "File size must be greater than zero.");

        ArgumentException.ThrowIfNullOrWhiteSpace(ContentId);

        this.Path = Path;
        this.SizeBytes = SizeBytes;
        this.ContentId = ContentId;
    }

    public string Path { get; }
    public long SizeBytes { get; }
    public string? ContentId { get; }
}

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

internal sealed record SyntheticRepositoryDefinition
{
    public SyntheticRepositoryDefinition(
        int SmallFileThresholdBytes,
        IReadOnlyList<string> RootDirectories,
        IReadOnlyList<SyntheticFileDefinition> Files,
        IReadOnlyList<SyntheticMutation> V2Mutations)
    {
        if (SmallFileThresholdBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(SmallFileThresholdBytes), "Threshold must be greater than zero.");

        ArgumentNullException.ThrowIfNull(RootDirectories);
        ArgumentNullException.ThrowIfNull(Files);
        ArgumentNullException.ThrowIfNull(V2Mutations);

        var rootDirectoriesCopy = RootDirectories.ToArray();
        var filesCopy = Files.ToArray();
        var mutationsCopy = V2Mutations.ToArray();

        foreach (var rootDirectory in rootDirectoriesCopy)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        }

        var v1Paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in filesCopy)
        {
            ArgumentNullException.ThrowIfNull(file);

            if (!v1Paths.Add(file.Path))
                throw new ArgumentException($"Duplicate V1 file path '{file.Path}'.", nameof(Files));
        }

        var finalPaths = new HashSet<string>(v1Paths, StringComparer.Ordinal);
        foreach (var mutation in mutationsCopy)
        {
            ArgumentNullException.ThrowIfNull(mutation);

            switch (mutation.Kind)
            {
                case SyntheticMutationKind.Delete:
                case SyntheticMutationKind.ChangeContent:
                    if (!v1Paths.Contains(mutation.Path))
                        throw new ArgumentException($"Mutation source '{mutation.Path}' must exist in V1.", nameof(V2Mutations));

                    if (mutation.Kind == SyntheticMutationKind.Delete)
                        finalPaths.Remove(mutation.Path);

                    break;

                case SyntheticMutationKind.Rename:
                    if (!v1Paths.Contains(mutation.Path))
                        throw new ArgumentException($"Rename source '{mutation.Path}' must exist in V1.", nameof(V2Mutations));

                    if (string.Equals(mutation.Path, mutation.TargetPath, StringComparison.Ordinal))
                        throw new ArgumentException("Rename target must differ from source.", nameof(V2Mutations));

                    if (v1Paths.Contains(mutation.TargetPath!))
                        throw new ArgumentException($"Rename target '{mutation.TargetPath}' must be absent in V1.", nameof(V2Mutations));

                    finalPaths.Remove(mutation.Path);
                    if (!finalPaths.Add(mutation.TargetPath!))
                        throw new ArgumentException($"Mutation set produces duplicate final path '{mutation.TargetPath}'.", nameof(V2Mutations));

                    break;

                case SyntheticMutationKind.Add:
                    if (v1Paths.Contains(mutation.Path))
                        throw new ArgumentException($"Add target '{mutation.Path}' must be absent in V1.", nameof(V2Mutations));

                    if (!finalPaths.Add(mutation.Path))
                        throw new ArgumentException($"Mutation set produces duplicate final path '{mutation.Path}'.", nameof(V2Mutations));

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation.Kind));
            }
        }

        this.SmallFileThresholdBytes = SmallFileThresholdBytes;
        this.RootDirectories = Array.AsReadOnly(rootDirectoriesCopy);
        this.Files = Array.AsReadOnly(filesCopy);
        this.V2Mutations = Array.AsReadOnly(mutationsCopy);
    }

    public int SmallFileThresholdBytes { get; }
    public IReadOnlyList<string> RootDirectories { get; }
    public IReadOnlyList<SyntheticFileDefinition> Files { get; }
    public IReadOnlyList<SyntheticMutation> V2Mutations { get; }
}
