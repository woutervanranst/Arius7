using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Defaults;

/// <summary>Global defaults as set, the factory values beneath them, and per option the aliases of repositories that inherit it.</summary>
/// <param name="Set">Options set for the whole server; <c>null</c> means not set.</param>
/// <param name="Factory">Built-in values that apply when an option is not set.</param>
/// <param name="InheritedBy">For each option name, the aliases of the repositories that do not set it and so inherit it.</param>
public sealed record GlobalDefaultsDto(
    DefaultValues Set,
    DefaultValues Factory,
    IReadOnlyDictionary<string, IReadOnlyList<string>> InheritedBy);
