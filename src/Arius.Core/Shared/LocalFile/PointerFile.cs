using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.LocalFile;

/// <summary>
/// Represents a pointer file discovered beside or instead of a binary file.
/// It exists so Arius can model thin-archive state explicitly, with responsibility for carrying the pointer path,
/// the binary path it refers to, and the parsed content hash when the pointer content is valid.
/// </summary>
internal sealed record PointerFile
{
    public required RelativePath Path { get; init; }

    public required RelativePath BinaryPath { get; init; }

    public ContentHash? Hash { get; init; }
}
