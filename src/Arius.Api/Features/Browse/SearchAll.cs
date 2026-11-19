using System.Runtime.CompilerServices;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;
using Arius.Core.Features.ListQuery;
using Mediator;

namespace Arius.Api.Features.Browse;

/// <summary>
/// Hub <c>SearchAll</c> — streams cross-repository search hits as they are found: runs a recursive filename filter
/// across every repository in turn (each failure isolated so one unreachable repo doesn't fail the whole search).
/// </summary>
public sealed class SearchAll(AppDatabase database, RepositoryProviderRegistry registry) : IHubHandler
{
    public async IAsyncEnumerable<SearchHitDto> HandleAsync(
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) yield break;

        foreach (var repo in database.ListRepositories())
        {
            await using var hits = SearchRepositoryAsync(repo).GetAsyncEnumerator();
            while (await TryMoveNextAsync(hits))
                yield return hits.Current;
        }

        async IAsyncEnumerable<SearchHitDto> SearchRepositoryAsync(RepositoryRecord repo)
        {
            var provider = await registry.GetReadProviderAsync(repo.Id, cancellationToken);
            var mediator = provider.GetRequiredService<IMediator>();
            await foreach (var entry in mediator.CreateStream(new ListQuery(new ListQueryOptions { Filter = query, Recursive = true }), cancellationToken))
                if (entry is RepositoryFileEntry file)
                    yield return new SearchHitDto(repo.Id, repo.Alias, EntryMapping.ToDto(file));
        }

        // C# forbids `yield return` inside a try with a catch, so the failure isolation guards each step instead:
        // a repository that can't be opened or listed (even partway through) ends its own hits; the search goes on
        // unless the caller cancelled it (the web UI does so whenever a newer query supersedes this one).
        async ValueTask<bool> TryMoveNextAsync(IAsyncEnumerator<SearchHitDto> hits)
        {
            try
            {
                return await hits.MoveNextAsync();
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }
}
