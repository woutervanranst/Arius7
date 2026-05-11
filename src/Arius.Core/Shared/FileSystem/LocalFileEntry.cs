namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Describes one file discovered through <see cref="RelativeFileSystem"/> enumeration.
/// It exists to carry repository-relative identity plus filesystem metadata without leaking host paths,
/// with responsibility for representing the file facts Arius needs for archive and restore workflows.
/// </summary>
internal sealed record LocalFileEntry
{
    public required RelativePath Path { get; init; }
}
