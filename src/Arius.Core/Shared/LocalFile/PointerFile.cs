using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.LocalFile;

internal sealed record PointerFile
{
    public required RelativePath Path { get; init; }

    public required RelativePath BinaryPath { get; init; }

    public ContentHash? Hash { get; init; }
}
