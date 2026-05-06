using Arius.Core.Shared.FileSystem;

namespace Arius.Core.Shared.LocalFile;

/// <summary>
/// Represents the binary side of an archive-time file pair.
/// It exists so archive code can work with validated relative paths and captured file metadata instead of full host paths,
/// with responsibility for describing the binary file that may need hashing, upload, or restoration.
/// </summary>
internal sealed record BinaryFile
{
    public required RelativePath Path { get; init; }

    public required long Size { get; init; }

    public required DateTimeOffset Created { get; init; }

    public required DateTimeOffset Modified { get; init; }
}
