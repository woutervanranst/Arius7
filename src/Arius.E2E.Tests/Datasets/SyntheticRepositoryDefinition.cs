namespace Arius.E2E.Tests.Datasets;

internal sealed record SyntheticRepositoryDefinition
{
    public SyntheticRepositoryDefinition(IReadOnlyList<RelativePath> RootDirectories, IReadOnlyList<SyntheticFileDefinition> Files, IReadOnlyList<SyntheticFileMutation> V2Mutations)
    {
        ArgumentNullException.ThrowIfNull(RootDirectories);
        ArgumentNullException.ThrowIfNull(Files);
        ArgumentNullException.ThrowIfNull(V2Mutations);

        var rootDirectoriesCopy = RootDirectories.ToArray();
        var filesCopy = Files.ToArray();
        var mutationsCopy = V2Mutations.ToArray();
        var rootDirectorySet = new HashSet<RelativePath>();

        foreach (var rootDirectory in rootDirectoriesCopy)
        {
            if (rootDirectory == RelativePath.Root)
                throw new ArgumentException("Root directory entries must not be the repository root.", nameof(RootDirectories));

            if (!rootDirectorySet.Add(rootDirectory))
                throw new ArgumentException($"Duplicate root directory '{rootDirectory}'.", nameof(RootDirectories));
        }

        bool IsUnderDeclaredRoot(RelativePath path) => rootDirectoriesCopy.Any(path.StartsWith);

        var v1Paths = new HashSet<RelativePath>();
        foreach (var file in filesCopy)
        {
            ArgumentNullException.ThrowIfNull(file);

            if (rootDirectorySet.Contains(file.Path))
                throw new ArgumentException($"File path '{file.Path}' must not point at a declared root directory.", nameof(Files));

            if (!IsUnderDeclaredRoot(file.Path))
                throw new ArgumentException($"File path '{file.Path}' is outside declared roots.", nameof(Files));

            if (!v1Paths.Add(file.Path))
                throw new ArgumentException($"Duplicate V1 file path '{file.Path}'.", nameof(Files));
        }

        var finalPaths = new HashSet<RelativePath>(v1Paths);
        var mutatedSourcePaths = new HashSet<RelativePath>();
        foreach (var mutation in mutationsCopy)
        {
            ArgumentNullException.ThrowIfNull(mutation);

            if (rootDirectorySet.Contains(mutation.Path))
                throw new ArgumentException($"Mutation path '{mutation.Path}' must not point at a declared root directory.", nameof(V2Mutations));

            if (!mutatedSourcePaths.Add(mutation.Path))
                throw new ArgumentException($"Mutation source '{mutation.Path}' may only be mutated once.", nameof(V2Mutations));

            switch (mutation.Kind)
            {
                case SyntheticFileMutationKind.Delete:
                case SyntheticFileMutationKind.ChangeContent:
                    if (!v1Paths.Contains(mutation.Path))
                        throw new ArgumentException($"Mutation source '{mutation.Path}' must exist in V1.", nameof(V2Mutations));

                    if (mutation.Kind == SyntheticFileMutationKind.Delete)
                        finalPaths.Remove(mutation.Path);

                    break;

                case SyntheticFileMutationKind.Rename:
                    if (!v1Paths.Contains(mutation.Path))
                        throw new ArgumentException($"Rename source '{mutation.Path}' must exist in V1.", nameof(V2Mutations));

                    if (mutation.TargetPath is null)
                        throw new ArgumentException("Rename target is required.", nameof(V2Mutations));

                    if (mutation.Path == mutation.TargetPath)
                        throw new ArgumentException("Rename target must differ from source.", nameof(V2Mutations));

                    if (rootDirectorySet.Contains(mutation.TargetPath.Value))
                        throw new ArgumentException($"Rename target '{mutation.TargetPath}' must not point at a declared root directory.", nameof(V2Mutations));

                    if (!IsUnderDeclaredRoot(mutation.TargetPath.Value))
                        throw new ArgumentException($"Rename target '{mutation.TargetPath}' is outside declared roots.", nameof(V2Mutations));

                    if (v1Paths.Contains(mutation.TargetPath.Value))
                        throw new ArgumentException($"Rename target '{mutation.TargetPath}' must be absent in V1.", nameof(V2Mutations));

                    finalPaths.Remove(mutation.Path);
                    if (!finalPaths.Add(mutation.TargetPath.Value))
                        throw new ArgumentException($"Mutation set produces duplicate final path '{mutation.TargetPath}'.", nameof(V2Mutations));

                    break;

                case SyntheticFileMutationKind.Add:
                    if (rootDirectorySet.Contains(mutation.Path))
                        throw new ArgumentException($"Add target '{mutation.Path}' must not point at a declared root directory.", nameof(V2Mutations));

                    if (!IsUnderDeclaredRoot(mutation.Path))
                        throw new ArgumentException($"Add target '{mutation.Path}' is outside declared roots.", nameof(V2Mutations));

                    if (v1Paths.Contains(mutation.Path))
                        throw new ArgumentException($"Add target '{mutation.Path}' must be absent in V1.", nameof(V2Mutations));

                    if (!finalPaths.Add(mutation.Path))
                        throw new ArgumentException($"Mutation set produces duplicate final path '{mutation.Path}'.", nameof(V2Mutations));

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation.Kind));
            }
        }

        this.RootDirectories = Array.AsReadOnly(rootDirectoriesCopy);
        this.Files = Array.AsReadOnly(filesCopy);
        this.V2Mutations = Array.AsReadOnly(mutationsCopy);
    }

    public IReadOnlyList<RelativePath>         RootDirectories { get; }
    public IReadOnlyList<SyntheticFileDefinition> Files        { get; }
    public IReadOnlyList<SyntheticFileMutation> V2Mutations    { get; }
}
