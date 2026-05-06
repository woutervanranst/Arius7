using Arius.Core.Shared.FileSystem;

namespace Arius.Core.Shared.LocalFile;

internal sealed record FilePair
{
    public required RelativePath Path { get; init; }

    public BinaryFile? Binary { get; init; }

    public PointerFile? Pointer { get; init; }
}
