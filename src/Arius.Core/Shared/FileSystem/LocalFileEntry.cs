namespace Arius.Core.Shared.FileSystem;

internal sealed record LocalFileEntry
{
    public required RelativePath Path { get; init; }

    public required long Size { get; init; }

    public required DateTimeOffset Created { get; init; }

    public required DateTimeOffset Modified { get; init; }
}
