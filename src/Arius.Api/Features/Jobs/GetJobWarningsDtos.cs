namespace Arius.Api.Features.Jobs;

/// <summary>A job's warnings, verbatim.</summary>
/// <param name="Count">Number of warnings the job emitted.</param>
/// <param name="Lines">The most recent warnings. Fewer than <c>count</c> when <c>truncated</c> is true.</param>
/// <param name="Truncated">Whether older warnings were dropped.</param>
public sealed record JobWarningsDto(int Count, IReadOnlyList<string> Lines, bool Truncated);
