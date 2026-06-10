using Arius.Cli.Commands.Ls;
using Arius.Core.Features.ListQuery;

namespace Arius.Cli.Tests.Commands.Ls;

public class LsStateFormatterTests
{
    [Test]
    [Arguments(
        RepositoryEntryState.LocalPointer | RepositoryEntryState.LocalBinary | RepositoryEntryState.Repository | RepositoryEntryState.RepositoryHydrated,
        "PBRH")]
    [Arguments(
        RepositoryEntryState.LocalPointer | RepositoryEntryState.Repository | RepositoryEntryState.RepositoryArchived,
        "P.RA")]
    [Arguments(
        RepositoryEntryState.Repository | RepositoryEntryState.RepositoryHydrated,
        "..RH")]
    [Arguments(
        RepositoryEntryState.Repository,
        "..R?")]
    [Arguments(
        RepositoryEntryState.LocalBinary,
        ".B..")]
    [Arguments(
        RepositoryEntryState.LocalPointer | RepositoryEntryState.LocalBinary,
        "PB..")]
    [Arguments(
        RepositoryEntryState.Repository | RepositoryEntryState.RepositoryArchived | RepositoryEntryState.RepositoryRehydrating,
        "..R~")]
    [Arguments(
        RepositoryEntryState.None,
        "....")]
    public void ToChars_RendersFlagCombination(RepositoryEntryState state, string expected)
    {
        LsStateFormatter.ToChars(state).ShouldBe(expected);
    }

    [Test]
    public void ToMarkup_PresentFlags_AreColored()
    {
        var state = RepositoryEntryState.LocalPointer | RepositoryEntryState.LocalBinary | RepositoryEntryState.Repository | RepositoryEntryState.RepositoryHydrated;

        LsStateFormatter.ToMarkup(state).ShouldBe("P[blue]B[/]R[blue]H[/]");
    }

    [Test]
    public void ToMarkup_AbsentFlags_AreDimDots()
    {
        LsStateFormatter.ToMarkup(RepositoryEntryState.None).ShouldBe("[dim].[/][dim].[/][dim].[/][dim].[/]");
    }

    [Test]
    public void ToMarkup_Archived_UsesExplorerLikeColor()
    {
        var state = RepositoryEntryState.Repository | RepositoryEntryState.RepositoryArchived;

        LsStateFormatter.ToMarkup(state).ShouldBe("[dim].[/][dim].[/]R[lightskyblue1]A[/]");
    }
}
