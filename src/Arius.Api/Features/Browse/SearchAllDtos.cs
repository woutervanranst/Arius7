namespace Arius.Api.Features.Browse;

/// <summary>A cross-repository search hit: the entry plus its owning repository.</summary>
public sealed record SearchHitDto(long RepoId, string Repo, EntryDto Entry);
