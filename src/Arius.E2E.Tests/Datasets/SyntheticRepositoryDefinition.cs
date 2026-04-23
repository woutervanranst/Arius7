namespace Arius.E2E.Tests.Datasets;

internal sealed record SyntheticRepositoryDefinition
{
    public SyntheticRepositoryDefinition(IReadOnlyList<string> RootDirectories, IReadOnlyList<SyntheticFileDefinition> Files, IReadOnlyList<SyntheticMutation> V2Mutations)
    {
        ArgumentNullException.ThrowIfNull(RootDirectories);
        ArgumentNullException.ThrowIfNull(Files);
        ArgumentNullException.ThrowIfNull(V2Mutations);

        var rootDirectoriesCopy = RootDirectories
            .Select(x => SyntheticRepositoryPath.NormalizeRootDirectory(x, nameof(RootDirectories)))
            .ToArray();
        var filesCopy = Files.ToArray();
        var mutationsCopy = V2Mutations.ToArray();
        var rootDirectorySet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rootDirectory in rootDirectoriesCopy)
        {
            if (!rootDirectorySet.Add(rootDirectory))
                throw new ArgumentException($"Duplicate root directory '{rootDirectory}'.", nameof(RootDirectories));
        }

        bool IsUnderDeclaredRoot(string path) => rootDirectoriesCopy.Any(rootDirectory =>
            path.StartsWith($"{rootDirectory}/", StringComparison.Ordinal));

        var v1Paths = new HashSet<string>(StringComparer.Ordinal);
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

        var finalPaths = new HashSet<string>(v1Paths, StringComparer.Ordinal);
        var mutatedSourcePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mutation in mutationsCopy)
        {
            ArgumentNullException.ThrowIfNull(mutation);

            if (rootDirectorySet.Contains(mutation.Path))
                throw new ArgumentException($"Mutation path '{mutation.Path}' must not point at a declared root directory.", nameof(V2Mutations));

            if (!mutatedSourcePaths.Add(mutation.Path))
                throw new ArgumentException($"Mutation source '{mutation.Path}' may only be mutated once.", nameof(V2Mutations));

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

                    if (rootDirectorySet.Contains(mutation.TargetPath!))
                        throw new ArgumentException($"Rename target '{mutation.TargetPath}' must not point at a declared root directory.", nameof(V2Mutations));

                    if (!IsUnderDeclaredRoot(mutation.TargetPath!))
                        throw new ArgumentException($"Rename target '{mutation.TargetPath}' is outside declared roots.", nameof(V2Mutations));

                    if (v1Paths.Contains(mutation.TargetPath!))
                        throw new ArgumentException($"Rename target '{mutation.TargetPath}' must be absent in V1.", nameof(V2Mutations));

                    finalPaths.Remove(mutation.Path);
                    if (!finalPaths.Add(mutation.TargetPath!))
                        throw new ArgumentException($"Mutation set produces duplicate final path '{mutation.TargetPath}'.", nameof(V2Mutations));

                    break;

                case SyntheticMutationKind.Add:
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

        this.RootDirectories         = Array.AsReadOnly(rootDirectoriesCopy);
        this.Files                   = Array.AsReadOnly(filesCopy);
        this.V2Mutations             = Array.AsReadOnly(mutationsCopy);
    }

    public IReadOnlyList<string>                  RootDirectories         { get; }
    public IReadOnlyList<SyntheticFileDefinition> Files                   { get; }
    public IReadOnlyList<SyntheticMutation>       V2Mutations             { get; }
}

internal static class SyntheticRepositoryPath
{
    public static string NormalizeRootDirectory(string path, string paramName)
    {
        var normalized = NormalizeRelativePath(path, paramName);

        if (!normalized.Contains('/', StringComparison.Ordinal))
            return normalized;

        return normalized;
    }

    public static string NormalizeRelativePath(string path, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Path.IsPathRooted(path))
            throw new ArgumentException($"Path '{path}' must be relative.", paramName);

        var normalized = path.Replace('\\', '/');

        if (normalized.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException($"Path '{path}' must be relative.", paramName);

        if (normalized.EndsWith("/", StringComparison.Ordinal))
            throw new ArgumentException($"Path '{path}' must not end with a separator.", paramName);

        if (normalized.Contains("//", StringComparison.Ordinal))
            throw new ArgumentException($"Path '{path}' must not contain repeated separators.", paramName);

        var parts = normalized.Split('/', StringSplitOptions.None);
        if (parts.Contains(".", StringComparer.Ordinal))
            throw new ArgumentException($"Path '{path}' must not contain '.' segments.", paramName);

        if (parts.Contains("..", StringComparer.Ordinal))
            throw new ArgumentException($"Path '{path}' must not contain '..' segments.", paramName);

        return normalized;
    }
}
