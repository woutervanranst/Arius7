using System.Runtime.CompilerServices;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;
using Arius.Core.Features.ListQuery;
using Mediator;

namespace Arius.Api.Features.Browse;

/// <summary>
/// Hub <c>SearchAll</c>: streams filename-search hits across all repositories as they are found. A repository that
/// fails is skipped and the search continues with the next one.
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

        // C# does not allow `yield return` inside a try block with a catch clause, so each step is guarded instead.
        // A failure while opening or listing a repository ends only that repository's hits; cancellation still propagates.
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
