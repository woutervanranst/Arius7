namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Describes one directory discovered through <see cref="RelativeFileSystem"/> enumeration.
/// It exists to expose child directories as repository-relative values, with responsibility for carrying
/// only the validated relative path identity needed by traversal code.
/// </summary>
internal sealed record LocalDirectoryEntry
{
    public required RelativePath Path { get; init; }
}
