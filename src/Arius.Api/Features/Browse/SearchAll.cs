using System.Runtime.CompilerServices;
using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Api.Contracts;
using Arius.Core.Features.ListQuery;
using Mediator;

namespace Arius.Api.Hubs;

/// <summary>
/// Hub <c>SearchAll</c> — streams cross-repository search hits: runs a recursive filename filter across every
/// repository (each failure isolated so one unreachable repo doesn't fail the whole search).
/// </summary>
internal static class SearchAll
{
    public static async IAsyncEnumerable<SearchHitDto> HandleAsync(
        AppDatabase database,
        RepositoryProviderRegistry registry,
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) yield break;

        foreach (var repo in database.ListRepositories())
        {
            var hits = new List<SearchHitDto>();
            try
            {
                var provider = await registry.GetReadProviderAsync(repo.Id, cancellationToken);
                var mediator = provider.GetRequiredService<IMediator>();
                await foreach (var entry in mediator.CreateStream(new ListQuery(new ListQueryOptions { Filter = query, Recursive = true }), cancellationToken))
                    if (entry is RepositoryFileEntry file)
                        hits.Add(new SearchHitDto(repo.Id, repo.Alias, EntryMapping.ToDto(file)));
            }
            catch
            {
                // Skip repositories that can't be opened or listed; keep searching the rest.
            }

            foreach (var hit in hits)
                yield return hit;
        }
    }
}
