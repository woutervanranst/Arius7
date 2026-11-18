namespace Arius.Api.Contracts;

/// <summary>A directory as the Arius.Api host/container sees it.</summary>
public sealed record FsEntryDto(string Name, string Path);

/// <summary>A directory listing: the resolved path, its parent (null at the root), and immediate subdirectories.</summary>
public sealed record FsListDto(string Path, string? Parent, IReadOnlyList<FsEntryDto> Entries);
