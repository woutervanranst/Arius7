namespace Arius.Api.Features.Jobs;

/// <summary>Verbatim per-job warnings for GET /jobs/{id}/warnings. <see cref="Truncated"/> is true when more than
/// the retained tail (200) were emitted, so <see cref="Count"/> &gt; <see cref="Lines"/>.Count.</summary>
public sealed record JobWarningsDto(int Count, IReadOnlyList<string> Lines, bool Truncated);
