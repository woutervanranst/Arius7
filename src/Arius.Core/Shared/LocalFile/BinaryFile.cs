using Arius.Core.Shared.FileSystem;

namespace Arius.Core.Shared.LocalFile;

internal sealed record BinaryFile
{
    public required RelativePath Path { get; init; }

    public required long Size { get; init; }

    public required DateTimeOffset Created { get; init; }

    public required DateTimeOffset Modified { get; init; }
}
