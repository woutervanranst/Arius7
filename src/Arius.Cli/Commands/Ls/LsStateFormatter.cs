using Arius.Core.Features.ListQuery;

namespace Arius.Cli.Commands.Ls;

/// <summary>
/// Renders a <see cref="RepositoryEntryState"/> as a fixed-width 4-char state cell:
/// local pointer (P), local binary (B), repository presence (R), and chunk tier
/// (H = hydrated, A = archived, ~ = rehydrating, ? = not in the chunk index).
/// Absent flags render as a dot.
/// </summary>
internal static class LsStateFormatter
{
    /// <summary>The plain 4-char state cell, e.g. <c>PBRH</c> or <c>.B..</c>.</summary>
    internal static string ToChars(RepositoryEntryState state) =>
        $"{(state.HasFlag(RepositoryEntryState.LocalPointer) ? 'P' : '.')}" +
        $"{(state.HasFlag(RepositoryEntryState.LocalBinary) ? 'B' : '.')}" +
        $"{(state.HasFlag(RepositoryEntryState.Repository) ? 'R' : '.')}" +
        $"{TierChar(state)}";

    /// <summary>The state cell with Spectre markup; colors echo Arius.Explorer.</summary>
    internal static string ToMarkup(RepositoryEntryState state)
    {
        var pointer = state.HasFlag(RepositoryEntryState.LocalPointer) ? "P" : Dot;
        var binary  = state.HasFlag(RepositoryEntryState.LocalBinary) ? "[blue]B[/]" : Dot;
        var repo    = state.HasFlag(RepositoryEntryState.Repository) ? "R" : Dot;
        var tier    = TierChar(state) switch
        {
            'H' => "[blue]H[/]",
            'A' => "[lightskyblue1]A[/]",
            '~' => "[purple]~[/]",
            '?' => "?",
            _   => Dot,
        };

        return pointer + binary + repo + tier;
    }

    private const string Dot = "[dim].[/]";

    private static char TierChar(RepositoryEntryState state) =>
        state.HasFlag(RepositoryEntryState.RepositoryRehydrating) ? '~'
        : state.HasFlag(RepositoryEntryState.RepositoryArchived)  ? 'A'
        : state.HasFlag(RepositoryEntryState.RepositoryHydrated)  ? 'H'
        : state.HasFlag(RepositoryEntryState.Repository)          ? '?'
                                                                  : '.';
}
