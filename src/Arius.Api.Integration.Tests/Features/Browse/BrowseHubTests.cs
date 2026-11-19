using Arius.Api.FakeTestHost;
using Arius.Api.Features.Browse;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Shared.AppData;
using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests.Features.Browse;

/// <summary>The hub's folder listing (<c>StreamEntries</c>) and cross-repository search (<c>SearchAll</c>): each entry
/// mapped to the wire <see cref="EntryDto"/> with its state decoded.</summary>
public class BrowseHubTests
{
    private static readonly DateTimeOffset Created  = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset Modified = new(2024, 6, 7, 8, 9, 10, TimeSpan.Zero);

    private const RepositoryEntryState ArchivedState = RepositoryEntryState.LocalPointer | RepositoryEntryState.Repository
                                                       | RepositoryEntryState.RepositoryArchived | RepositoryEntryState.RepositoryRehydrating;
    private const RepositoryEntryState HydratedState = RepositoryEntryState.LocalBinary | RepositoryEntryState.Repository | RepositoryEntryState.RepositoryHydrated;

    private static readonly RepositoryDirectoryEntry Folder = new(RelativePath.Parse("photos/2024"),
        RepositoryEntryState.LocalDirectory | RepositoryEntryState.Repository, TreeHash: null);
    private static readonly RepositoryFileEntry ArchivedPhoto = new(RelativePath.Parse("photos/a.jpg"), ArchivedState,
        ContentHash.Parse(new string('a', 64)), OriginalSize: 1234, Created, Modified);
    private static readonly RepositoryFileEntry HydratedPhoto = new(RelativePath.Parse("photos/b.jpg"), HydratedState,
        ContentHash.Parse(new string('b', 64)), OriginalSize: 10, Created: null, Modified: null);

    private static readonly EntryDto ArchivedPhotoDto = new("photos/a.jpg", "a.jpg", "file", (int)ArchivedState,
        new StateFlagsDto(LocalPointer: true, LocalBinary: false, LocalDirectory: false, Repository: true,
            RepositoryHydrated: false, RepositoryArchived: true, RepositoryRehydrating: true),
        new string('a', 64), 1234, Created, Modified);
    private static readonly EntryDto HydratedPhotoDto = new("photos/b.jpg", "b.jpg", "file", (int)HydratedState,
        new StateFlagsDto(LocalPointer: false, LocalBinary: true, LocalDirectory: false, Repository: true,
            RepositoryHydrated: true, RepositoryArchived: false, RepositoryRehydrating: false),
        new string('b', 64), 10, Created: null, Modified: null);

    private static readonly EntryDto FolderDto = new("photos/2024", "2024", "dir",
        (int)(RepositoryEntryState.LocalDirectory | RepositoryEntryState.Repository),
        new StateFlagsDto(LocalPointer: false, LocalBinary: false, LocalDirectory: true, Repository: true,
            RepositoryHydrated: false, RepositoryArchived: false, RepositoryRehydrating: false),
        ContentHash: null, OriginalSize: null, Created: null, Modified: null);

    [Test]
    public async Task StreamEntries_streams_the_folders_directories_and_files_and_asks_Core_for_that_folder_only()
    {
        await using var factory = new AriusApiFactory();
        var repoId   = factory.SeedRepository();
        var scenario = new ReadScenario(Entries: [Folder, ArchivedPhoto]);
        factory.Scenarios.SetRead(repoId, scenario);
        await using var conn = factory.CreateHubConnection();
        await conn.StartAsync();

        var entries = await conn.StreamAsync<EntryDto>("StreamEntries", repoId, "2024-01-01T000000.000Z", "photos", "a", false).ToListAsync();

        await Assert.That(entries).IsEquivalentTo([FolderDto, ArchivedPhotoDto]);
        await Assert.That(scenario.Received.OfType<ListQuery>().Single().Options).IsEqualTo(new ListQueryOptions
        {
            Version = "2024-01-01T000000.000Z", Prefix = RelativePath.Parse("photos"), Filter = "a", Recursive = false,
        });
    }

    [Test]
    public async Task StreamEntries_treats_blank_version_prefix_and_filter_as_not_given()
    {
        await using var factory = new AriusApiFactory();
        var repoId   = factory.SeedRepository();
        var scenario = new ReadScenario(Entries: [HydratedPhoto]);
        factory.Scenarios.SetRead(repoId, scenario);
        await using var conn = factory.CreateHubConnection();
        await conn.StartAsync();

        var entries = await conn.StreamAsync<EntryDto>("StreamEntries", repoId, " ", "", null, false).ToListAsync();

        await Assert.That(entries).IsEquivalentTo([HydratedPhotoDto]);
        await Assert.That(scenario.Received.OfType<ListQuery>().Single().Options).IsEqualTo(new ListQueryOptions { Recursive = false });
    }

    [Test]
    public async Task StreamEntries_is_empty_for_a_repository_whose_container_does_not_exist_yet()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        factory.Scenarios.SetRead(repoId, new ReadScenario(ContainerMissing: true));
        await using var conn = factory.CreateHubConnection();
        await conn.StartAsync();

        var entries = await conn.StreamAsync<EntryDto>("StreamEntries", repoId, null, null, null, false).ToListAsync();

        await Assert.That(entries).IsEmpty();
    }

    [Test]
    public async Task SearchAll_returns_the_file_hits_of_every_repository_and_skips_one_that_cannot_be_listed()
    {
        await using var factory = new AriusApiFactory();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var itest = factory.SeedRepository();
        var itestScenario = new ReadScenario(Entries: [Folder, ArchivedPhoto]);
        factory.Scenarios.SetRead(itest, itestScenario);
        var accountId = db.InsertAccount("other-account", null);
        db.InsertRepository("broken", "broken-container", accountId, localPath: null, encryptedPassphrase: null);   // no scripted listing: Core throws
        var zeta = db.InsertRepository("zeta", "zeta-container", accountId, localPath: null, encryptedPassphrase: null);
        factory.Scenarios.SetRead(zeta, new ReadScenario(Entries: [HydratedPhoto]));
        await using var conn = factory.CreateHubConnection();
        await conn.StartAsync();

        var hits = await conn.StreamAsync<SearchHitDto>("SearchAll", "jpg").ToListAsync();

        // Directories are not hits; the repository whose listing fails contributes nothing but does not end the search.
        await Assert.That(hits).IsEquivalentTo([new SearchHitDto(itest, "itest", ArchivedPhotoDto), new SearchHitDto(zeta, "zeta", HydratedPhotoDto)]);
        await Assert.That(itestScenario.Received.OfType<ListQuery>().Single().Options).IsEqualTo(new ListQueryOptions { Filter = "jpg", Recursive = true });
    }

    [Test]
    public async Task SearchAll_streams_a_repositorys_hits_while_its_listing_is_still_running()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        factory.Scenarios.SetRead(repoId, new ReadScenario(Entries: [ArchivedPhoto], Gated: true));
        await using var conn = factory.CreateHubConnection();
        await conn.StartAsync();

        try
        {
            await using var hits = conn.StreamAsync<SearchHitDto>("SearchAll", "jpg").GetAsyncEnumerator();

            await Assert.That(await hits.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
            await Assert.That(hits.Current).IsEqualTo(new SearchHitDto(repoId, "itest", ArchivedPhotoDto));

            factory.Services.GetRequiredService<ScenarioGate>().Release(repoId);
            await Assert.That(await hits.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsFalse();
        }
        finally
        {
            factory.Services.GetRequiredService<ScenarioGate>().Release(repoId);
        }
    }

    [Test]
    public async Task SearchAll_with_a_blank_query_returns_nothing_without_listing_any_repository()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var scenario = new ReadScenario(Entries: [HydratedPhoto]);
        factory.Scenarios.SetRead(repoId, scenario);
        await using var conn = factory.CreateHubConnection();
        await conn.StartAsync();

        var hits = await conn.StreamAsync<SearchHitDto>("SearchAll", "  ").ToListAsync();

        await Assert.That(hits).IsEmpty();
        await Assert.That(scenario.Received).IsEmpty();
    }
}
