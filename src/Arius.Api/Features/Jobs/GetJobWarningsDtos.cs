namespace Arius.Api.Features.Jobs;

/// <summary>A job's warnings, verbatim. <see cref="Truncated"/> is true when more warnings were emitted than the
/// retained tail holds, so <see cref="Count"/> exceeds the number of <see cref="Lines"/>.</summary>
public sealed record JobWarningsDto(int Count, IReadOnlyList<string> Lines, bool Truncated);
