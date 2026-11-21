namespace Arius.Api.Features.Filesystem;

/// <summary>A directory as the Arius.Api host/container sees it.</summary>
/// <param name="Name">Directory name.</param>
/// <param name="Path">Full path on the server.</param>
public sealed record FsEntryDto(string Name, string Path);

/// <summary>A directory listing: the resolved path, its parent (null at the root), and immediate subdirectories.</summary>
/// <param name="Path">Full path of the listed directory.</param>
/// <param name="Parent">Full path of the parent directory, or <c>null</c> at the root.</param>
/// <param name="Entries">Immediate subdirectories, sorted by name.</param>
public sealed record FsListDto(string Path, string? Parent, IReadOnlyList<FsEntryDto> Entries);
