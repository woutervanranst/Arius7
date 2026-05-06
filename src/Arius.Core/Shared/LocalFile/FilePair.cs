using Arius.Core.Shared.FileSystem;

namespace Arius.Core.Shared.LocalFile;

/// <summary>
/// Represents Arius's local archive-time view of one repository path.
/// It exists to unify binary-only, pointer-only, and binary-plus-pointer cases behind one domain model,
/// with responsibility for carrying the validated relative path and its optional binary and pointer components.
/// </summary>
internal sealed record FilePair
{
    public required RelativePath Path { get; init; }

    public BinaryFile? Binary { get; init; }

    public PointerFile? Pointer { get; init; }
}
