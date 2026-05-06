namespace Arius.Core.Shared.FileSystem;

internal sealed record LocalDirectoryEntry
{
    public required RelativePath Path { get; init; }
}
