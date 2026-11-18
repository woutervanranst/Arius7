using Arius.Api.AppData;

namespace Arius.Api.Contracts;

/// <summary>Global defaults as set, the factory values beneath them, and per option the aliases of repositories that inherit it.</summary>
public sealed record GlobalDefaultsDto(
    DefaultValues Set,
    DefaultValues Factory,
    IReadOnlyDictionary<string, IReadOnlyList<string>> InheritedBy);
