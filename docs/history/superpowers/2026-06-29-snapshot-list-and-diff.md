# Snapshot List & Diff Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `arius snapshot list` and `arius snapshot diff` CLI commands, backed by a streaming snapshot-list query (reused by Api) and a new MECE snapshot-diff query.

**Architecture:** Two Core Mediator feature slices drive everything. `SnapshotsListQuery` (renamed from `SnapshotsQuery`, converted `ICommand`→`IStreamQuery<SnapshotInfo>`) lists snapshots oldest→newest; Api and the CLI both consume it. A new `SnapshotDiffQuery : IStreamQuery<SnapshotDiffEntry>` walks two snapshot filetrees in lockstep with Merkle pruning and emits one classified entry (`Added`/`Removed`/`Modified`/`TimestampChanged`) per changed path. The CLI adds a `snapshot` command group; index/timestamp argument resolution lives in a pure CLI helper.

**Tech Stack:** C# / .NET 10, martinothamar `Mediator` (source-generated, `IStreamQuery`/`IStreamQueryHandler`), System.CommandLine, Spectre.Console, TUnit + Shouldly + NSubstitute, Serilog.

## Global Constraints

- **Spec:** `docs/history/superpowers/2026-06-29-snapshot-list-and-diff-design.md`.
- **`AddMediator()` is never called in `AddArius`** — only in the outermost assembly (CLI/Api/test). Register per-repository handlers as explicit singleton factories in `src/Arius.Core/ServiceCollectionExtensions.cs`.
- **Code style:** `internal` by default; one top-level type per file; prefer local methods over private statics for single-method helpers. Match surrounding style.
- **Typed hashes stay distinct** (`ContentHash`/`ChunkHash`/`FileTreeHash`) — no collapsing, no implicit conversions. Compare hashes as typed values; stringify only at boundaries.
- **TDD:** failing test → run (see it fail) → minimal implementation → run (see it pass) → commit. Conventional commits.
- **TUnit test runs** use `--treenode-filter`, never `--filter`. Example: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/SnapshotDiffQueryHandlerTests/*"`.
- **Streaming, never buffered** for potentially large reads; the diff handler `yield return`s per entry and holds only the BFS frontier.

---

### Task 1: Rename + stream-convert `SnapshotsQuery` → `SnapshotsListQuery`

This is one atomic refactor: renaming the type and changing its handler signature breaks every call site, so they all move together (solution must build).

**Files:**
- Create: `src/Arius.Core/Features/SnapshotsListQuery/SnapshotsListQuery.cs`
- Delete: `src/Arius.Core/Features/SnapshotsQuery/SnapshotsQuery.cs`
- Modify: `src/Arius.Core/ServiceCollectionExtensions.cs` (line 7 using; lines 179-182 registration)
- Modify: `src/Arius.Api/Endpoints/BrowseEndpoints.cs` (line 5 using; lines 17-23 endpoint)
- Modify: `src/Arius.Cli.Tests/TestSupport/CliHarness.cs` (line 7 using; lines 46, 88-90, 119 mock)
- Create test: `src/Arius.Core.Tests/Features/SnapshotsListQuery/SnapshotsListQueryHandlerTests.cs`
- Delete test: `src/Arius.Core.Tests/Features/SnapshotsQuery/SnapshotsQueryHandlerTests.cs`

**Interfaces:**
- Produces: `record SnapshotsListQuery() : IStreamQuery<SnapshotInfo>` and `record SnapshotInfo(string Version, DateTimeOffset Timestamp, long FileCount)` in namespace `Arius.Core.Features.SnapshotsListQuery`; `class SnapshotsListQueryHandler(ISnapshotService, ILogger<SnapshotsListQueryHandler>) : IStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>` with `IAsyncEnumerable<SnapshotInfo> Handle(SnapshotsListQuery, CancellationToken)`.

- [ ] **Step 1: Write the new streaming handler test** (also delete the old test file)

Delete `src/Arius.Core.Tests/Features/SnapshotsQuery/SnapshotsQueryHandlerTests.cs` and create `src/Arius.Core.Tests/Features/SnapshotsListQuery/SnapshotsListQueryHandlerTests.cs`:

```csharp
using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Tests.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using SnapshotsListQueryType = Arius.Core.Features.SnapshotsListQuery.SnapshotsListQuery;

namespace Arius.Core.Tests.Features.SnapshotsListQuery;

public class SnapshotsListQueryHandlerTests
{
    [Test]
    public async Task Handle_MultipleSnapshots_StreamsOldestToNewestWithVersionTimestampAndFileCount()
    {
        var blobs = new FakeSeededBlobContainerService();
        var s1 = await SeedSnapshotAsync(blobs, new DateTimeOffset(2024, 1, 10, 8, 0, 0, TimeSpan.Zero), fileCount: 100);
        var s3 = await SeedSnapshotAsync(blobs, new DateTimeOffset(2024, 3, 12, 9, 14, 0, TimeSpan.Zero), fileCount: 142);
        var s2 = await SeedSnapshotAsync(blobs, new DateTimeOffset(2024, 2, 2, 23, 40, 0, TimeSpan.Zero), fileCount: 120);

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-snap-1", "ctr-snap-1", IEncryptionService.PlaintextInstance);
        var handler = new SnapshotsListQueryHandler(fixture.Snapshot, NullLogger<SnapshotsListQueryHandler>.Instance);

        var result = await handler.Handle(new SnapshotsListQueryType(), CancellationToken.None).ToListAsync();

        result.Count.ShouldBe(3);
        result.Select(s => s.Timestamp).ShouldBe([s1, s2, s3]); // oldest → newest
        result.Select(s => s.FileCount).ShouldBe([100, 120, 142]);

        foreach (var snapshot in result)
        {
            var resolved = await fixture.Snapshot.ResolveAsync(snapshot.Version, CancellationToken.None);
            resolved.ShouldNotBeNull();
            resolved!.Timestamp.ShouldBe(snapshot.Timestamp);
        }
    }

    [Test]
    public async Task Handle_NoSnapshots_StreamsEmpty()
    {
        var blobs = new FakeSeededBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-snap-empty", "ctr-snap-empty", IEncryptionService.PlaintextInstance);
        var handler = new SnapshotsListQueryHandler(fixture.Snapshot, NullLogger<SnapshotsListQueryHandler>.Instance);

        var result = await handler.Handle(new SnapshotsListQueryType(), CancellationToken.None).ToListAsync();

        result.ShouldBeEmpty();
    }

    private static async Task<DateTimeOffset> SeedSnapshotAsync(FakeSeededBlobContainerService blobs, DateTimeOffset timestamp, long fileCount)
    {
        var manifest = new SnapshotManifest
        {
            Timestamp    = timestamp,
            RootHash     = FileTreeHashOf($"root-{timestamp:O}"),
            FileCount    = fileCount,
            OriginalSize = fileCount * 1000,
            AriusVersion = "test"
        };
        blobs.AddBlob(BlobPaths.SnapshotPath(timestamp), await SnapshotSerializer.SerializeAsync(manifest, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance));
        return timestamp;
    }
}
```

- [ ] **Step 2: Run the test — verify it fails to compile**

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/SnapshotsListQueryHandlerTests/*"`
Expected: BUILD FAILURE — `SnapshotsListQuery` / `SnapshotsListQueryHandler` do not exist yet.

- [ ] **Step 3: Create the streaming query + handler** (and delete the old file)

Delete `src/Arius.Core/Features/SnapshotsQuery/SnapshotsQuery.cs` and create `src/Arius.Core/Features/SnapshotsListQuery/SnapshotsListQuery.cs`:

```csharp
using System.Runtime.CompilerServices;
using Arius.Core.Shared.Snapshot;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.SnapshotsListQuery;

// --- QUERY

/// <summary>
/// Mediator stream query: enumerate the snapshots of a repository (oldest → newest), as needed by
/// the time-travel picker/scrubber and the CLI <c>snapshot list</c>. Streams so callers render rows
/// as each manifest resolves.
/// </summary>
public sealed record SnapshotsListQuery() : IStreamQuery<SnapshotInfo>;

// --- RESULT

/// <summary>
/// One snapshot, as needed by the UI / CLI.
/// </summary>
/// <param name="Version">
/// The snapshot's version identifier — the snapshot blob filename (the timestamp formatted with
/// <see cref="SnapshotService.TimestampFormat"/>). Exactly what <c>ListQueryOptions.Version</c> /
/// <c>RestoreOptions.Version</c> / <c>SnapshotDiffQuery</c> are <c>StartsWith</c>-matched against.
/// </param>
/// <param name="Timestamp">UTC creation time of the snapshot.</param>
/// <param name="FileCount">Total number of files in the snapshot.</param>
public sealed record SnapshotInfo(string Version, DateTimeOffset Timestamp, long FileCount);

// --- HANDLER

/// <summary>
/// Streams snapshots oldest → newest, resolving each manifest (disk-cache-first) for its timestamp
/// and file count. Unresolvable manifests are logged and skipped.
/// </summary>
public sealed class SnapshotsListQueryHandler(
    ISnapshotService                   snapshots,
    ILogger<SnapshotsListQueryHandler> logger)
    : IStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>
{
    public async IAsyncEnumerable<SnapshotInfo> Handle(
        SnapshotsListQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // ── Stage 1: list snapshot blobs (oldest → newest) ──────────────────────
        var blobNames = await snapshots.ListBlobNamesAsync(cancellationToken);

        // ── Stage 2: resolve each manifest (disk-cache-first), yield as it resolves ──
        var count = 0;
        foreach (var blobName in blobNames)
        {
            var version  = snapshots.GetVersion(blobName);
            var manifest = await snapshots.ResolveAsync(version, cancellationToken);
            if (manifest is null)
            {
                logger.LogWarning("[snapshots] manifest for {Version} could not be resolved; skipping", version);
                continue;
            }

            count++;
            yield return new SnapshotInfo(version, manifest.Timestamp, manifest.FileCount);
        }

        logger.LogDebug("[snapshots] streamed {Count} snapshots", count);
    }
}
```

- [ ] **Step 4: Update the Core DI registration**

In `src/Arius.Core/ServiceCollectionExtensions.cs`: change the using on line 7 from `using Arius.Core.Features.SnapshotsQuery;` to `using Arius.Core.Features.SnapshotsListQuery;`, and replace the registration block (lines 179-182):

```csharp
        services.AddSingleton<IStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>>(sp =>
            new SnapshotsListQueryHandler(
                sp.GetRequiredService<ISnapshotService>(),
                sp.GetRequiredService<ILogger<SnapshotsListQueryHandler>>()));
```

- [ ] **Step 5: Update the Api endpoint to stream**

In `src/Arius.Api/Endpoints/BrowseEndpoints.cs`: change the using on line 5 from `using Arius.Core.Features.SnapshotsQuery;` to `using Arius.Core.Features.SnapshotsListQuery;`, and replace the snapshots endpoint body (lines 17-23):

```csharp
        app.MapGet("/repos/{id:long}/snapshots", async (long id, RepositoryProviderRegistry registry, CancellationToken ct) =>
        {
            var provider = await registry.GetReadProviderAsync(id, ct);
            var mediator = provider.GetRequiredService<IMediator>();
            var snapshots = new List<SnapshotDto>();
            await foreach (var s in mediator.CreateStream(new SnapshotsListQuery(), ct))
                snapshots.Add(new SnapshotDto(s.Version, s.Timestamp, s.FileCount));
            return snapshots;
        });
```

- [ ] **Step 6: Update the CLI test harness**

In `src/Arius.Cli.Tests/TestSupport/CliHarness.cs`: change the using on line 7 to `using Arius.Core.Features.SnapshotsListQuery;`. Replace the snapshots mock declaration (line 46) and its setup (lines 88-90), and its registration (line 119):

Line 46 becomes:
```csharp
        var snapshotsHandler = Substitute.For<IStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>>();
```
Lines 88-90 become:
```csharp
        snapshotsHandler
            .Handle(Arg.Any<SnapshotsListQuery>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<SnapshotInfo>());
```
Line 119 (`services.AddSingleton(snapshotsHandler);`) stays as-is — the variable type changed, so it now registers the stream handler interface.

- [ ] **Step 7: Run the full build + the renamed test + the Cli tests**

Run: `dotnet build src/Arius.Cli/Arius.Cli.csproj && dotnet build src/Arius.Api/Arius.Api.csproj`
Expected: BUILD SUCCEEDED (all references updated).

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/SnapshotsListQueryHandlerTests/*"`
Expected: PASS (2 tests).

Run: `dotnet test --project src/Arius.Cli.Tests`
Expected: PASS (harness compiles + existing CLI tests green).

- [ ] **Step 8: Commit**

```bash
git add src/Arius.Core/Features src/Arius.Core/ServiceCollectionExtensions.cs src/Arius.Api/Endpoints/BrowseEndpoints.cs src/Arius.Cli.Tests/TestSupport/CliHarness.cs src/Arius.Core.Tests/Features
git commit -m "refactor: rename SnapshotsQuery to streaming SnapshotsListQuery

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `SnapshotDiffQuery` Core feature (types + handler)

**Files:**
- Create: `src/Arius.Core/Features/SnapshotDiffQuery/SnapshotDiffQuery.cs`
- Modify: `src/Arius.Core/ServiceCollectionExtensions.cs` (add using + registration)
- Modify: `src/Arius.Cli.Tests/TestSupport/CliHarness.cs` (register a diff stream-handler mock so the harness provider stays complete)
- Create test: `src/Arius.Core.Tests/Features/SnapshotDiffQuery/SnapshotDiffQueryHandlerTests.cs`

**Interfaces:**
- Consumes: `ISnapshotService.ResolveAsync(string?, CancellationToken)` → `SnapshotManifest?` (with `.RootHash`, `.AriusVersion`); `IFileTreeService.ReadAsync(FileTreeHash, CancellationToken)` → `IReadOnlyList<FileTreeEntry>`; `FileEntry(Name, ContentHash, Created, Modified)`, `DirectoryEntry(Name, FileTreeHash)`; `RelativePath.Root`, `RelativePath operator /(RelativePath, PathSegment)`.
- Produces: `record SnapshotDiffQuery(string VersionA, string VersionB) : IStreamQuery<SnapshotDiffEntry>`; `enum ChangeType { Added, Removed, Modified, TimestampChanged }`; `record SnapshotDiffEntry(ChangeType Change, RelativePath Path, FileEntry? Before, FileEntry? After)`; `class SnapshotDiffQueryHandler(ISnapshotService, IFileTreeService, ILogger<SnapshotDiffQueryHandler>) : IStreamQueryHandler<SnapshotDiffQuery, SnapshotDiffEntry>` — all in namespace `Arius.Core.Features.SnapshotDiffQuery`.

- [ ] **Step 1: Write the handler tests**

Create `src/Arius.Core.Tests/Features/SnapshotDiffQuery/SnapshotDiffQueryHandlerTests.cs`:

```csharp
using Arius.Core.Features.SnapshotDiffQuery;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Tests.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using SnapshotDiffQueryType = Arius.Core.Features.SnapshotDiffQuery.SnapshotDiffQuery;

namespace Arius.Core.Tests.Features.SnapshotDiffQuery;

public class SnapshotDiffQueryHandlerTests
{
    private static readonly DateTimeOffset s_t1 = new(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_t2 = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_tsA = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_tsB = new(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Handle_ClassifiesAddedRemovedModifiedAndTimestampChanged()
    {
        var rootA = Entries(
            File("keep.txt",  ContentHashOf("keep"),  s_t1, s_t1),   // unchanged
            File("edit.txt",  ContentHashOf("v1"),    s_t1, s_t1),   // modified
            File("gone.txt",  ContentHashOf("gone"),  s_t1, s_t1),   // removed
            File("touch.txt", ContentHashOf("same"),  s_t1, s_t1));  // timestamp-only
        var rootB = Entries(
            File("keep.txt",  ContentHashOf("keep"),  s_t1, s_t1),
            File("edit.txt",  ContentHashOf("v2"),    s_t1, s_t1),
            File("new.txt",   ContentHashOf("new"),   s_t1, s_t1),   // added
            File("touch.txt", ContentHashOf("same"),  s_t1, s_t2));  // modified-time only

        var handler = await BuildHandlerAsync("acct-diff-1", "ctr-diff-1", rootA, rootB);

        var results = await handler.Handle(new SnapshotDiffQueryType(VersionOf(s_tsA), VersionOf(s_tsB)), CancellationToken.None).ToListAsync();

        Single(results, ChangeType.Added).Path.ToString().ShouldBe("new.txt");
        Single(results, ChangeType.Removed).Path.ToString().ShouldBe("gone.txt");
        Single(results, ChangeType.Modified).Path.ToString().ShouldBe("edit.txt");
        Single(results, ChangeType.TimestampChanged).Path.ToString().ShouldBe("touch.txt");
        results.Count.ShouldBe(4); // keep.txt (unchanged) is not emitted
    }

    [Test]
    public async Task Handle_RecursesIntoChangedSubdirectories()
    {
        var subA = Entries(File("inner.txt", ContentHashOf("inner"), s_t1, s_t1));
        var subB = Entries(
            File("inner.txt",    ContentHashOf("inner"),    s_t1, s_t1),
            File("innernew.txt", ContentHashOf("innernew"), s_t1, s_t1));
        var subAHash = FileTreeBuilder.ComputeHash(subA, IEncryptionService.PlaintextInstance);
        var subBHash = FileTreeBuilder.ComputeHash(subB, IEncryptionService.PlaintextInstance);

        var rootA = Entries(Dir("changing", subAHash));
        var rootB = Entries(Dir("changing", subBHash));

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, subA);
        await SeedTreeAsync(blobs, subB);
        var handler = await BuildHandlerAsync("acct-diff-2", "ctr-diff-2", rootA, rootB, blobs);

        var results = await handler.Handle(new SnapshotDiffQueryType(VersionOf(s_tsA), VersionOf(s_tsB)), CancellationToken.None).ToListAsync();

        Single(results, ChangeType.Added).Path.ToString().ShouldBe("changing/innernew.txt");
        results.Count.ShouldBe(1); // inner.txt unchanged
    }

    [Test]
    public async Task Handle_IdenticalSubtree_IsPrunedAndNotRead()
    {
        // Both sides reference the SAME directory hash that is deliberately NOT seeded as a blob.
        // If pruning regresses, the handler would ReadAsync this hash and throw.
        var sharedHash = FakeFileTreeHash('s');
        var rootA = Entries(Dir("static", sharedHash), File("a.txt", ContentHashOf("a"), s_t1, s_t1));
        var rootB = Entries(Dir("static", sharedHash), File("a.txt", ContentHashOf("a"), s_t1, s_t1));

        var handler = await BuildHandlerAsync("acct-diff-3", "ctr-diff-3", rootA, rootB);

        var results = await handler.Handle(new SnapshotDiffQueryType(VersionOf(s_tsA), VersionOf(s_tsB)), CancellationToken.None).ToListAsync();

        results.ShouldBeEmpty(); // identical everywhere; pruned subtree never read
    }

    [Test]
    public async Task Handle_MissingSnapshot_Throws()
    {
        var rootA = Entries(File("x.txt", ContentHashOf("x"), s_t1, s_t1));
        var handler = await BuildHandlerAsync("acct-diff-4", "ctr-diff-4", rootA, rootA);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in handler.Handle(new SnapshotDiffQueryType(VersionOf(s_tsA), "9999-nope"), CancellationToken.None)) { }
        });
    }

    [Test]
    public async Task Handle_DifferentAriusVersion_LogsWarning()
    {
        var rootA = Entries(File("x.txt", ContentHashOf("x1"), s_t1, s_t1));
        var rootB = Entries(File("x.txt", ContentHashOf("x2"), s_t1, s_t1));
        var logger = new FakeLogger<SnapshotDiffQueryHandler>();
        var handler = await BuildHandlerAsync("acct-diff-5", "ctr-diff-5", rootA, rootB, ariusVersionA: "v1", ariusVersionB: "v2", logger: logger);

        await handler.Handle(new SnapshotDiffQueryType(VersionOf(s_tsA), VersionOf(s_tsB)), CancellationToken.None).ToListAsync();

        logger.Collector.GetSnapshot().ShouldContain(r => r.Level == LogLevel.Warning && r.Message.Contains("different Arius versions"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SnapshotDiffEntry Single(IReadOnlyList<SnapshotDiffEntry> results, ChangeType change)
        => results.Single(e => e.Change == change);

    private static string VersionOf(DateTimeOffset ts) => ts.UtcDateTime.ToString(SnapshotService.TimestampFormat);

    private static IReadOnlyList<FileTreeEntry> Entries(params FileTreeEntry[] entries) => entries;

    private static FileEntry File(string name, ContentHash hash, DateTimeOffset created, DateTimeOffset modified) => new()
    {
        Name = PathSegment.Parse(name), ContentHash = hash, Created = created, Modified = modified
    };

    private static DirectoryEntry Dir(string name, FileTreeHash hash) => new()
    {
        Name = PathSegment.Parse(name), FileTreeHash = hash
    };

    private static async Task<SnapshotDiffQueryHandler> BuildHandlerAsync(
        string account, string container,
        IReadOnlyList<FileTreeEntry> rootA, IReadOnlyList<FileTreeEntry> rootB,
        FakeSeededBlobContainerService? blobs = null,
        string ariusVersionA = "test", string ariusVersionB = "test",
        ILogger<SnapshotDiffQueryHandler>? logger = null)
    {
        blobs ??= new FakeSeededBlobContainerService();
        var rootAHash = await SeedTreeAsync(blobs, rootA);
        var rootBHash = await SeedTreeAsync(blobs, rootB);
        await SeedSnapshotAsync(blobs, s_tsA, rootAHash, ariusVersionA);
        await SeedSnapshotAsync(blobs, s_tsB, rootBHash, ariusVersionB);

        var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, account, container, IEncryptionService.PlaintextInstance);
        return new SnapshotDiffQueryHandler(fixture.Snapshot, fixture.FileTreeService, logger ?? NullLogger<SnapshotDiffQueryHandler>.Instance);
    }

    private static async Task<FileTreeHash> SeedTreeAsync(FakeSeededBlobContainerService blobs, IReadOnlyList<FileTreeEntry> entries)
    {
        var plaintext = FileTreeSerializer.Serialize(entries);
        var hash = FileTreeHashOf(plaintext);
        using var ms = new MemoryStream();
        await using (var encStream = IEncryptionService.PlaintextInstance.WrapForEncryption(ms))
        await using (var gzipStream = new System.IO.Compression.GZipStream(encStream, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            await gzipStream.WriteAsync((ReadOnlyMemory<byte>)plaintext);
        blobs.AddBlob(BlobPaths.FileTreePath(hash), ms.ToArray());
        return hash;
    }

    private static async Task SeedSnapshotAsync(FakeSeededBlobContainerService blobs, DateTimeOffset ts, FileTreeHash rootHash, string ariusVersion)
    {
        var manifest = new SnapshotManifest { Timestamp = ts, RootHash = rootHash, FileCount = 0, OriginalSize = 0, AriusVersion = ariusVersion };
        blobs.AddBlob(BlobPaths.SnapshotPath(ts), await SnapshotSerializer.SerializeAsync(manifest, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance));
    }
}
```

Note: a `RepositoryTestFixture` is `IAsyncDisposable`; this helper deliberately leaks it (test lifetime). That matches the throwaway-fixture usage in existing handler tests where a fixture per call is acceptable.

- [ ] **Step 2: Run the tests — verify they fail to compile**

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/SnapshotDiffQueryHandlerTests/*"`
Expected: BUILD FAILURE — `SnapshotDiffQuery` / `SnapshotDiffEntry` / `ChangeType` / `SnapshotDiffQueryHandler` do not exist.

- [ ] **Step 3: Create the query, result types, and handler**

Create `src/Arius.Core/Features/SnapshotDiffQuery/SnapshotDiffQuery.cs`:

```csharp
using System.Runtime.CompilerServices;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Snapshot;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.SnapshotDiffQuery;

// --- QUERY

/// <summary>
/// Mediator stream query: report what changed between two snapshots. Walks both root filetrees in
/// lockstep, pruning subtrees whose <see cref="FileTreeHash"/> matches, and emits one classified
/// entry per changed path. <c>VersionA</c>/<c>VersionB</c> are <c>StartsWith</c>-matched the same way
/// as <c>ls --version</c> (see <see cref="ISnapshotService.ResolveAsync"/>).
/// </summary>
public sealed record SnapshotDiffQuery(string VersionA, string VersionB) : IStreamQuery<SnapshotDiffEntry>;

// --- RESULT

/// <summary>
/// How a single path changed between snapshot A and snapshot B. Exactly one value applies per path
/// (the diff is MECE) — deliberately NOT a <c>[Flags]</c> enum.
/// </summary>
public enum ChangeType
{
    /// <summary>Path present only in B.</summary>
    Added,
    /// <summary>Path present only in A.</summary>
    Removed,
    /// <summary>Same path, different <see cref="ContentHash"/>.</summary>
    Modified,
    /// <summary>Same path, same <see cref="ContentHash"/>, different Created/Modified.</summary>
    TimestampChanged,
}

/// <summary>
/// One changed path. <paramref name="Before"/> is the file entry in snapshot A, <paramref name="After"/>
/// in snapshot B: <see cref="ChangeType.Added"/> ⇒ Before is null; <see cref="ChangeType.Removed"/> ⇒
/// After is null; <see cref="ChangeType.Modified"/>/<see cref="ChangeType.TimestampChanged"/> ⇒ both set.
/// </summary>
public sealed record SnapshotDiffEntry(ChangeType Change, RelativePath Path, FileEntry? Before, FileEntry? After);

// --- HANDLER

/// <summary>
/// Resolves both manifests, warns on an Arius-version mismatch (the cross-platform line-ending hash
/// boundary), then BFS-walks the two root filetrees in lockstep. Equal child <see cref="FileTreeHash"/>
/// ⇒ prune; otherwise read both nodes and classify files by name. A subtree present on only one side
/// streams out wholesale as Added/Removed.
/// </summary>
public sealed class SnapshotDiffQueryHandler(
    ISnapshotService                  snapshots,
    IFileTreeService                  fileTree,
    ILogger<SnapshotDiffQueryHandler> logger)
    : IStreamQueryHandler<SnapshotDiffQuery, SnapshotDiffEntry>
{
    public async IAsyncEnumerable<SnapshotDiffEntry> Handle(
        SnapshotDiffQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var manifestA = await snapshots.ResolveAsync(query.VersionA, cancellationToken)
            ?? throw new InvalidOperationException($"Snapshot not found: '{query.VersionA}'.");
        var manifestB = await snapshots.ResolveAsync(query.VersionB, cancellationToken)
            ?? throw new InvalidOperationException($"Snapshot not found: '{query.VersionB}'.");

        if (!string.Equals(manifestA.AriusVersion, manifestB.AriusVersion, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "[diff] comparing snapshots written by different Arius versions ({VersionA} vs {VersionB}); " +
                "a cross-platform line-ending hash change can make identical content appear changed",
                manifestA.AriusVersion, manifestB.AriusVersion);
        }

        var queue = new Queue<DirectoryPair>();
        queue.Enqueue(new DirectoryPair(manifestA.RootHash, manifestB.RootHash, RelativePath.Root));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pair = queue.Dequeue();

            // Identical subtree → prune (no read, no allocation).
            if (pair.HashA is { } sameA && pair.HashB is { } sameB && sameA == sameB)
                continue;

            IReadOnlyList<FileTreeEntry> entriesA = pair.HashA is { } ha ? await fileTree.ReadAsync(ha, cancellationToken) : [];
            IReadOnlyList<FileTreeEntry> entriesB = pair.HashB is { } hb ? await fileTree.ReadAsync(hb, cancellationToken) : [];

            // ── Files: classify by name ──
            var filesA = entriesA.OfType<FileEntry>().ToDictionary(e => e.Name);
            var filesB = entriesB.OfType<FileEntry>().ToDictionary(e => e.Name);

            foreach (var (name, fileA) in filesA)
            {
                var path = pair.Path / name;
                if (filesB.TryGetValue(name, out var fileB))
                {
                    if (fileA.ContentHash != fileB.ContentHash)
                        yield return new SnapshotDiffEntry(ChangeType.Modified, path, fileA, fileB);
                    else if (fileA.Created != fileB.Created || fileA.Modified != fileB.Modified)
                        yield return new SnapshotDiffEntry(ChangeType.TimestampChanged, path, fileA, fileB);
                    // else identical → emit nothing
                }
                else
                {
                    yield return new SnapshotDiffEntry(ChangeType.Removed, path, fileA, null);
                }
            }

            foreach (var (name, fileB) in filesB)
            {
                if (!filesA.ContainsKey(name))
                    yield return new SnapshotDiffEntry(ChangeType.Added, pair.Path / name, null, fileB);
            }

            // ── Subdirectories: prune equal, enqueue differing/one-sided ──
            var dirsA = entriesA.OfType<DirectoryEntry>().ToDictionary(e => e.Name);
            var dirsB = entriesB.OfType<DirectoryEntry>().ToDictionary(e => e.Name);

            foreach (var (name, dirA) in dirsA)
            {
                var childPath = pair.Path / name;
                if (dirsB.TryGetValue(name, out var dirB))
                {
                    if (dirA.FileTreeHash != dirB.FileTreeHash)
                        queue.Enqueue(new DirectoryPair(dirA.FileTreeHash, dirB.FileTreeHash, childPath));
                    // equal → prune
                }
                else
                {
                    queue.Enqueue(new DirectoryPair(dirA.FileTreeHash, null, childPath)); // removed subtree
                }
            }

            foreach (var (name, dirB) in dirsB)
            {
                if (!dirsA.ContainsKey(name))
                    queue.Enqueue(new DirectoryPair(null, dirB.FileTreeHash, pair.Path / name)); // added subtree
            }
        }
    }

    private readonly record struct DirectoryPair(FileTreeHash? HashA, FileTreeHash? HashB, RelativePath Path);
}
```

- [ ] **Step 4: Register the diff handler in Core DI**

In `src/Arius.Core/ServiceCollectionExtensions.cs`: add `using Arius.Core.Features.SnapshotDiffQuery;` to the using block, and add this registration directly after the `SnapshotsListQuery` registration:

```csharp
        services.AddSingleton<IStreamQueryHandler<SnapshotDiffQuery, SnapshotDiffEntry>>(sp =>
            new SnapshotDiffQueryHandler(
                sp.GetRequiredService<ISnapshotService>(),
                sp.GetRequiredService<IFileTreeService>(),
                sp.GetRequiredService<ILogger<SnapshotDiffQueryHandler>>()));
```

- [ ] **Step 5: Register a diff mock in the CLI harness** (keep the harness provider complete, matching the existing snapshot/stats pattern)

In `src/Arius.Cli.Tests/TestSupport/CliHarness.cs`: add `using Arius.Core.Features.SnapshotDiffQuery;`. After the `snapshotsHandler` declaration (around line 46) add:
```csharp
        var snapshotDiffHandler = Substitute.For<IStreamQueryHandler<SnapshotDiffQuery, SnapshotDiffEntry>>();
```
After the `snapshotsHandler` setup (around line 90) add:
```csharp
        snapshotDiffHandler
            .Handle(Arg.Any<SnapshotDiffQuery>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<SnapshotDiffEntry>());
```
In the factory body (after `services.AddSingleton(snapshotsHandler);`, around line 119) add:
```csharp
            services.AddSingleton(snapshotDiffHandler);
```

- [ ] **Step 6: Run the diff tests and the Cli tests**

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/SnapshotDiffQueryHandlerTests/*"`
Expected: PASS (5 tests).

Run: `dotnet test --project src/Arius.Cli.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Arius.Core/Features/SnapshotDiffQuery src/Arius.Core/ServiceCollectionExtensions.cs src/Arius.Cli.Tests/TestSupport/CliHarness.cs src/Arius.Core.Tests/Features/SnapshotDiffQuery
git commit -m "feat: add SnapshotDiffQuery with Merkle-pruned lockstep walk

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: CLI `snapshot` group + `snapshot list` verb

**Files:**
- Create: `src/Arius.Cli/Commands/Snapshot/SnapshotListVerb.cs`
- Modify: `src/Arius.Cli/CliBuilder.cs` (using + register the `snapshot` group)
- Create test: `src/Arius.Cli.Tests/Commands/Snapshot/SnapshotListVerbTests.cs`

**Interfaces:**
- Consumes: `SnapshotsListQuery`, `SnapshotInfo` (Task 1); `CliBuilder.AccountOption/KeyOption/PassphraseOption/ContainerOption/ResolveAccount/ResolveKey/ConfigureAuditLogging`.
- Produces: `static Command SnapshotListVerb.Build(Func<string,string?,string?,string,PreflightMode,Task<IServiceProvider>>)`.

- [ ] **Step 1: Write the verb test** (bare `IMediator` mock, mirroring `RepairCommandTests`)

Create `src/Arius.Cli.Tests/Commands/Snapshot/SnapshotListVerbTests.cs`:

```csharp
using Arius.Core.Features.SnapshotsListQuery;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Spectre.Console;

namespace Arius.Cli.Tests.Commands.Snapshot;

[NotInParallel("AnsiConsoleRecorder")]
public class SnapshotListVerbTests
{
    [Test]
    public async Task SnapshotList_NumbersOldestToNewestAndReturnsZero()
    {
        var t1 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var mediator = Substitute.For<IMediator>();
        mediator.CreateStream(Arg.Any<SnapshotsListQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => new[] { new SnapshotInfo("2024-01-01T000000.000Z", t1, 10), new SnapshotInfo("2024-02-01T000000.000Z", t2, 20) }.ToAsyncEnumerable());

        var rootCommand = CliBuilder.BuildRootCommand(serviceProviderFactory: (_, _, _, _, _) =>
        {
            var services = new ServiceCollection();
            services.AddSingleton(mediator);
            return Task.FromResult<IServiceProvider>(services.BuildServiceProvider());
        });

        var (exitCode, output) = await CaptureOutputAsync(() => rootCommand.Parse("snapshot list -a acct -k key -c ctr").InvokeAsync());

        exitCode.ShouldBe(0);
        output.ShouldContain("1");
        output.ShouldContain("2024-01-01T000000.000Z");
        output.ShouldContain("2");
        output.ShouldContain("2024-02-01T000000.000Z");
        output.ShouldContain("2 snapshot(s)");
    }

    [Test]
    public async Task SnapshotList_MissingAccount_ReturnsFailure()
    {
        var rootCommand = CliBuilder.BuildRootCommand(serviceProviderFactory: (_, _, _, _, _) =>
            Task.FromResult<IServiceProvider>(new ServiceCollection().BuildServiceProvider()));

        var (exitCode, output) = await CaptureOutputAsync(() => rootCommand.Parse("snapshot list -k key -c ctr").InvokeAsync());

        exitCode.ShouldBe(1);
        output.ShouldContain("No account provided");
    }

    private static async Task<(int ExitCode, string Output)> CaptureOutputAsync(Func<Task<int>> invokeAsync)
    {
        var recorder = AnsiConsole.Console.CreateRecorder();
        var savedConsole = AnsiConsole.Console;
        AnsiConsole.Console = recorder;
        try { return (await invokeAsync(), recorder.ExportText()); }
        finally { AnsiConsole.Console = savedConsole; }
    }
}
```

- [ ] **Step 2: Run the test — verify it fails**

Run: `dotnet test --project src/Arius.Cli.Tests --treenode-filter "/*/*/SnapshotListVerbTests/*"`
Expected: FAIL — `snapshot list` is not a known command (parse error / non-zero), or build failure (verb missing).

- [ ] **Step 3: Create the list verb**

Create `src/Arius.Cli/Commands/Snapshot/SnapshotListVerb.cs`:

```csharp
using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Cli.Commands.Snapshot;

internal static class SnapshotListVerb
{
    internal static Command Build(
        Func<string, string?, string?, string, PreflightMode, Task<IServiceProvider>> serviceProviderFactory)
    {
        var accountOption    = CliBuilder.AccountOption();
        var keyOption        = CliBuilder.KeyOption();
        var passphraseOption = CliBuilder.PassphraseOption();
        var containerOption  = CliBuilder.ContainerOption();

        var cmd = new Command("list", "List all snapshots, oldest first");
        cmd.Options.Add(accountOption);
        cmd.Options.Add(keyOption);
        cmd.Options.Add(passphraseOption);
        cmd.Options.Add(containerOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var account    = parseResult.GetValue(accountOption);
            var key        = parseResult.GetValue(keyOption);
            var passphrase = parseResult.GetValue(passphraseOption);
            var container  = parseResult.GetValue(containerOption)!;

            var resolvedAccount = CliBuilder.ResolveAccount(account);
            if (resolvedAccount is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account provided. Use --account / -a or set ARIUS_ACCOUNT.");
                return 1;
            }
            var resolvedKey = CliBuilder.ResolveKey(key, resolvedAccount);

            CliBuilder.ConfigureAuditLogging(resolvedAccount, container, "snapshot-list");

            try
            {
                IServiceProvider services;
                try
                {
                    services = await serviceProviderFactory(resolvedAccount, resolvedKey, passphrase, container, PreflightMode.ReadOnly).ConfigureAwait(false);
                }
                catch (PreflightException ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                    return 1;
                }

                var mediator = services.GetRequiredService<IMediator>();

                var index = 0;
                AnsiConsole.MarkupLine($"[bold]{"#",4}  {"Version",-24}  {"Created",-19}  Files[/]");
                await foreach (var snapshot in mediator.CreateStream(new SnapshotsListQuery(), ct))
                {
                    index++;
                    var created = snapshot.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                    AnsiConsole.MarkupLine($"{index,4}  {Markup.Escape(snapshot.Version),-24}  {created,-19}  {snapshot.FileCount}");
                }

                AnsiConsole.MarkupLine(index == 0 ? "[dim]No snapshots found.[/]" : $"[dim]{index} snapshot(s)[/]");
                return 0;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        });

        return cmd;
    }
}
```

- [ ] **Step 4: Register the `snapshot` group in `CliBuilder`**

In `src/Arius.Cli/CliBuilder.cs`: add `using Arius.Cli.Commands.Snapshot;` to the usings, and add the group inside `BuildRootCommand` after the `LsVerb` line (line 71):

```csharp
        var snapshot = new Command("snapshot", "Inspect snapshots");
        snapshot.Subcommands.Add(SnapshotListVerb.Build(serviceProviderFactory));
        rootCommand.Subcommands.Add(snapshot);
```

(The `diff` subcommand is added to this same group in Task 5.)

- [ ] **Step 5: Run the test — verify it passes**

Run: `dotnet test --project src/Arius.Cli.Tests --treenode-filter "/*/*/SnapshotListVerbTests/*"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Cli/Commands/Snapshot/SnapshotListVerb.cs src/Arius.Cli/CliBuilder.cs src/Arius.Cli.Tests/Commands/Snapshot/SnapshotListVerbTests.cs
git commit -m "feat: add 'arius snapshot list' command

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: `SnapshotArgumentResolver` (index/timestamp → version string)

**Files:**
- Create: `src/Arius.Cli/Commands/Snapshot/SnapshotArgumentResolver.cs`
- Create test: `src/Arius.Cli.Tests/Commands/Snapshot/SnapshotArgumentResolverTests.cs`

**Interfaces:**
- Consumes: `SnapshotInfo` (Task 1).
- Produces: `static string SnapshotArgumentResolver.Resolve(string argument, IReadOnlyList<SnapshotInfo> snapshots)`.

- [ ] **Step 1: Write the resolver tests**

Create `src/Arius.Cli.Tests/Commands/Snapshot/SnapshotArgumentResolverTests.cs`:

```csharp
using Arius.Cli.Commands.Snapshot;
using Arius.Core.Features.SnapshotsListQuery;

namespace Arius.Cli.Tests.Commands.Snapshot;

public class SnapshotArgumentResolverTests
{
    private static readonly IReadOnlyList<SnapshotInfo> Snapshots =
    [
        new("2024-01-01T000000.000Z", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), 1),
        new("2024-02-01T000000.000Z", new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero), 2),
        new("2024-03-01T000000.000Z", new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero), 3),
    ];

    [Test]
    public void Resolve_Index_ReturnsVersionByOneBasedPosition()
    {
        SnapshotArgumentResolver.Resolve("1", Snapshots).ShouldBe("2024-01-01T000000.000Z");
        SnapshotArgumentResolver.Resolve("3", Snapshots).ShouldBe("2024-03-01T000000.000Z");
    }

    [Test]
    public void Resolve_IndexOutOfRange_Throws()
    {
        Should.Throw<ArgumentException>(() => SnapshotArgumentResolver.Resolve("0", Snapshots));
        Should.Throw<ArgumentException>(() => SnapshotArgumentResolver.Resolve("4", Snapshots));
    }

    [Test]
    public void Resolve_TimestampWithColons_StripsColonsToVersionPrefix()
    {
        SnapshotArgumentResolver.Resolve("2024-04-02T13:09:54", Snapshots).ShouldBe("2024-04-02T130954");
    }

    [Test]
    public void Resolve_PartialOrStoredFormat_ReturnedVerbatim()
    {
        SnapshotArgumentResolver.Resolve("2024-04-02", Snapshots).ShouldBe("2024-04-02");
        SnapshotArgumentResolver.Resolve("2024-02-01T000000.000Z", Snapshots).ShouldBe("2024-02-01T000000.000Z");
    }
}
```

- [ ] **Step 2: Run the test — verify it fails**

Run: `dotnet test --project src/Arius.Cli.Tests --treenode-filter "/*/*/SnapshotArgumentResolverTests/*"`
Expected: BUILD FAILURE — `SnapshotArgumentResolver` does not exist.

- [ ] **Step 3: Create the resolver**

Create `src/Arius.Cli/Commands/Snapshot/SnapshotArgumentResolver.cs`:

```csharp
using Arius.Core.Features.SnapshotsListQuery;

namespace Arius.Cli.Commands.Snapshot;

/// <summary>
/// Resolves a CLI snapshot argument to a Core version string (a <c>StartsWith</c> prefix matched by
/// <c>ISnapshotService.ResolveAsync</c>). A pure integer is a 1-based index into <paramref name="snapshots"/>
/// (oldest = 1). Anything else is a version prefix with ':' stripped, so a typed timestamp like
/// "2024-04-02T13:09:54" becomes the stored-format prefix "2024-04-02T130954".
/// </summary>
internal static class SnapshotArgumentResolver
{
    public static string Resolve(string argument, IReadOnlyList<SnapshotInfo> snapshots)
    {
        if (int.TryParse(argument, out var index))
        {
            if (index < 1 || index > snapshots.Count)
                throw new ArgumentException($"Snapshot index {index} is out of range (1..{snapshots.Count}).");
            return snapshots[index - 1].Version;
        }

        return argument.Replace(":", string.Empty);
    }
}
```

- [ ] **Step 4: Run the test — verify it passes**

Run: `dotnet test --project src/Arius.Cli.Tests --treenode-filter "/*/*/SnapshotArgumentResolverTests/*"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Cli/Commands/Snapshot/SnapshotArgumentResolver.cs src/Arius.Cli.Tests/Commands/Snapshot/SnapshotArgumentResolverTests.cs
git commit -m "feat: add snapshot argument resolver (index/timestamp to version)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: CLI `snapshot diff` verb

**Files:**
- Create: `src/Arius.Cli/Commands/Snapshot/SnapshotDiffVerb.cs`
- Modify: `src/Arius.Cli/CliBuilder.cs` (add `diff` to the `snapshot` group)
- Create test: `src/Arius.Cli.Tests/Commands/Snapshot/SnapshotDiffVerbTests.cs`

**Interfaces:**
- Consumes: `SnapshotDiffQuery`, `SnapshotDiffEntry`, `ChangeType` (Task 2); `SnapshotsListQuery`, `SnapshotInfo` (Task 1); `SnapshotArgumentResolver.Resolve` (Task 4).
- Produces: `static Command SnapshotDiffVerb.Build(...)`.

- [ ] **Step 1: Write the verb tests** (bare `IMediator` mock; covers index resolution AND timestamp resolution)

Create `src/Arius.Cli.Tests/Commands/Snapshot/SnapshotDiffVerbTests.cs`:

```csharp
using Arius.Core.Features.SnapshotDiffQuery;
using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Shared.FileTree;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Spectre.Console;

namespace Arius.Cli.Tests.Commands.Snapshot;

[NotInParallel("AnsiConsoleRecorder")]
public class SnapshotDiffVerbTests
{
    [Test]
    public async Task SnapshotDiff_IndexArguments_ResolveAgainstListAndRenderEntries()
    {
        var t1 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var mediator = Substitute.For<IMediator>();
        mediator.CreateStream(Arg.Any<SnapshotsListQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => new[] { new SnapshotInfo("2024-01-01T000000.000Z", t1, 1), new SnapshotInfo("2024-02-01T000000.000Z", t2, 2) }.ToAsyncEnumerable());
        mediator.CreateStream(Arg.Any<SnapshotDiffQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => new[]
            {
                new SnapshotDiffEntry(ChangeType.Added, RelativePath.Parse("new.txt"), null, MakeFile("new.txt")),
                new SnapshotDiffEntry(ChangeType.Removed, RelativePath.Parse("gone.txt"), MakeFile("gone.txt"), null),
            }.ToAsyncEnumerable());

        var rootCommand = BuildRoot(mediator);
        var (exitCode, output) = await CaptureOutputAsync(() => rootCommand.Parse("snapshot diff 1 2 -a acct -k key -c ctr").InvokeAsync());

        exitCode.ShouldBe(0);
        output.ShouldContain("new.txt");
        output.ShouldContain("gone.txt");
        output.ShouldContain("1 added");
        output.ShouldContain("1 removed");
        await mediator.Received(1).CreateStream(
            Arg.Is<SnapshotDiffQuery>(q => q.VersionA == "2024-01-01T000000.000Z" && q.VersionB == "2024-02-01T000000.000Z"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SnapshotDiff_TimestampArguments_StripColonsAndDoNotFetchList()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.CreateStream(Arg.Any<SnapshotDiffQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => AsyncEnumerable.Empty<SnapshotDiffEntry>());

        var rootCommand = BuildRoot(mediator);
        var (exitCode, _) = await CaptureOutputAsync(() => rootCommand.Parse("snapshot diff 2024-01-01T00:00:00 2024-02-01T00:00:00 -a acct -k key -c ctr").InvokeAsync());

        exitCode.ShouldBe(0);
        await mediator.Received(1).CreateStream(
            Arg.Is<SnapshotDiffQuery>(q => q.VersionA == "2024-01-01T000000" && q.VersionB == "2024-02-01T000000"),
            Arg.Any<CancellationToken>());
        await mediator.DidNotReceive().CreateStream(Arg.Any<SnapshotsListQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SnapshotDiff_SnapshotNotFound_ReturnsFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.CreateStream(Arg.Any<SnapshotDiffQuery>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncEnumerable<SnapshotDiffEntry>>(_ => throw new InvalidOperationException("Snapshot not found: 'nope'."));

        var rootCommand = BuildRoot(mediator);
        var (exitCode, output) = await CaptureOutputAsync(() => rootCommand.Parse("snapshot diff nope other -a acct -k key -c ctr").InvokeAsync());

        exitCode.ShouldBe(1);
        output.ShouldContain("Snapshot not found");
    }

    private static FileEntry MakeFile(string name) => new()
    {
        Name = PathSegment.Parse(name), ContentHash = FakeContentHash('a'),
        Created = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Modified = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static RootCommand BuildRoot(IMediator mediator) =>
        CliBuilder.BuildRootCommand(serviceProviderFactory: (_, _, _, _, _) =>
        {
            var services = new ServiceCollection();
            services.AddSingleton(mediator);
            return Task.FromResult<IServiceProvider>(services.BuildServiceProvider());
        });

    private static async Task<(int ExitCode, string Output)> CaptureOutputAsync(Func<Task<int>> invokeAsync)
    {
        var recorder = AnsiConsole.Console.CreateRecorder();
        var savedConsole = AnsiConsole.Console;
        AnsiConsole.Console = recorder;
        try { return (await invokeAsync(), recorder.ExportText()); }
        finally { AnsiConsole.Console = savedConsole; }
    }
}
```

(`FakeContentHash` is a global `using static` in `Arius.Cli.Tests` — see `Usings.cs`.)

- [ ] **Step 2: Run the test — verify it fails**

Run: `dotnet test --project src/Arius.Cli.Tests --treenode-filter "/*/*/SnapshotDiffVerbTests/*"`
Expected: FAIL — `snapshot diff` is not a known command / verb missing.

- [ ] **Step 3: Create the diff verb**

Create `src/Arius.Cli/Commands/Snapshot/SnapshotDiffVerb.cs`:

```csharp
using Arius.Core.Features.SnapshotDiffQuery;
using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Cli.Commands.Snapshot;

internal static class SnapshotDiffVerb
{
    internal static Command Build(
        Func<string, string?, string?, string, PreflightMode, Task<IServiceProvider>> serviceProviderFactory)
    {
        var accountOption    = CliBuilder.AccountOption();
        var keyOption        = CliBuilder.KeyOption();
        var passphraseOption = CliBuilder.PassphraseOption();
        var containerOption  = CliBuilder.ContainerOption();

        var fromArg = new Argument<string>("from") { Description = "Older snapshot: an index from `snapshot list`, or a version/timestamp prefix" };
        var toArg   = new Argument<string>("to")   { Description = "Newer snapshot: an index from `snapshot list`, or a version/timestamp prefix" };

        var cmd = new Command("diff", "Show what changed between two snapshots");
        cmd.Options.Add(accountOption);
        cmd.Options.Add(keyOption);
        cmd.Options.Add(passphraseOption);
        cmd.Options.Add(containerOption);
        cmd.Arguments.Add(fromArg);
        cmd.Arguments.Add(toArg);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var account    = parseResult.GetValue(accountOption);
            var key        = parseResult.GetValue(keyOption);
            var passphrase = parseResult.GetValue(passphraseOption);
            var container  = parseResult.GetValue(containerOption)!;
            var fromValue  = parseResult.GetValue(fromArg)!;
            var toValue    = parseResult.GetValue(toArg)!;

            var resolvedAccount = CliBuilder.ResolveAccount(account);
            if (resolvedAccount is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account provided. Use --account / -a or set ARIUS_ACCOUNT.");
                return 1;
            }
            var resolvedKey = CliBuilder.ResolveKey(key, resolvedAccount);

            CliBuilder.ConfigureAuditLogging(resolvedAccount, container, "snapshot-diff");

            try
            {
                IServiceProvider services;
                try
                {
                    services = await serviceProviderFactory(resolvedAccount, resolvedKey, passphrase, container, PreflightMode.ReadOnly).ConfigureAwait(false);
                }
                catch (PreflightException ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                    return 1;
                }

                var mediator = services.GetRequiredService<IMediator>();

                string fromVersion, toVersion;
                try
                {
                    // Only fetch the snapshot list when an index argument is actually used.
                    IReadOnlyList<SnapshotInfo> snapshots = (IsIndex(fromValue) || IsIndex(toValue))
                        ? await mediator.CreateStream(new SnapshotsListQuery(), ct).ToListAsync(ct)
                        : [];
                    fromVersion = SnapshotArgumentResolver.Resolve(fromValue, snapshots);
                    toVersion   = SnapshotArgumentResolver.Resolve(toValue, snapshots);
                }
                catch (ArgumentException ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                    return 1;
                }

                var counts = new Dictionary<ChangeType, int>();
                try
                {
                    await foreach (var entry in mediator.CreateStream(new SnapshotDiffQuery(fromVersion, toVersion), ct))
                    {
                        counts[entry.Change] = counts.GetValueOrDefault(entry.Change) + 1;
                        AnsiConsole.MarkupLine($"{Glyph(entry.Change)}  {Markup.Escape(entry.Path.ToString())}");
                    }
                }
                catch (InvalidOperationException ex)
                {
                    AnsiConsole.MarkupLine($"[red]Diff failed:[/] {Markup.Escape(ex.Message)}");
                    return 1;
                }

                AnsiConsole.MarkupLine(
                    $"[dim]{counts.GetValueOrDefault(ChangeType.Added)} added, " +
                    $"{counts.GetValueOrDefault(ChangeType.Removed)} removed, " +
                    $"{counts.GetValueOrDefault(ChangeType.Modified)} modified, " +
                    $"{counts.GetValueOrDefault(ChangeType.TimestampChanged)} timestamp-only[/]");
                return 0;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        });

        return cmd;

        static bool IsIndex(string arg) => int.TryParse(arg, out _);

        static string Glyph(ChangeType change) => change switch
        {
            ChangeType.Added            => "[green]A[/]",
            ChangeType.Removed          => "[red]D[/]",
            ChangeType.Modified         => "[yellow]M[/]",
            ChangeType.TimestampChanged => "[blue]T[/]",
            _                           => "?",
        };
    }
}
```

- [ ] **Step 4: Add `diff` to the `snapshot` group**

In `src/Arius.Cli/CliBuilder.cs`, in the `snapshot` group block added in Task 3, add the diff subcommand before `rootCommand.Subcommands.Add(snapshot);`:

```csharp
        snapshot.Subcommands.Add(SnapshotDiffVerb.Build(serviceProviderFactory));
```

- [ ] **Step 5: Run the test — verify it passes**

Run: `dotnet test --project src/Arius.Cli.Tests --treenode-filter "/*/*/SnapshotDiffVerbTests/*"`
Expected: PASS (3 tests).

Run: `dotnet test --project src/Arius.Cli.Tests`
Expected: PASS (whole CLI test project).

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Cli/Commands/Snapshot/SnapshotDiffVerb.cs src/Arius.Cli/CliBuilder.cs src/Arius.Cli.Tests/Commands/Snapshot/SnapshotDiffVerbTests.cs
git commit -m "feat: add 'arius snapshot diff' command

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Documentation

**Files:**
- Modify: `docs/design/core/features/queries.md`
- Modify: `docs/guide/cli.md`

- [ ] **Step 1: Update the queries design doc**

In `docs/design/core/features/queries.md`:
1. In the `> **Code:**` header line, change `SnapshotsQuery` to `SnapshotsListQuery` and add `SnapshotDiffQuery` to the path list.
2. Rename the `### SnapshotsQuery` section to `### SnapshotsListQuery` and update its body so it reads as a stream query. Replace its first sentence with:

```markdown
`SnapshotsListQuery()` is an `IStreamQuery<SnapshotInfo>` streaming `SnapshotInfo(Version, Timestamp, FileCount)` — the [snapshot](../../../glossary.md#snapshot) list for the time-travel picker, the CLI `snapshot list`, and `arius snapshot diff` argument resolution. It streams (one blob per snapshot, resolved oldest→newest) so callers render rows as each manifest resolves. The handler lists blob names oldest→newest via `ISnapshotService.ListBlobNamesAsync`, then resolves each manifest (disk-cache-first) for its timestamp and file count; unresolvable manifests are logged and skipped.
```

3. Add a new section after it:

```markdown
### SnapshotDiffQuery

`SnapshotDiffQuery(VersionA, VersionB)` is an `IStreamQuery<SnapshotDiffEntry>` reporting what changed between two snapshots. It resolves both manifests (`ISnapshotService.ResolveAsync`), warns when their `AriusVersion` differs (the cross-platform line-ending hash boundary makes identical content hash differently), then BFS-walks both root [filetrees](../../../glossary.md#filetree) in lockstep: child directories with an equal `FileTreeHash` are **pruned** (never read), so work is `O(changed nodes)`. Each remaining leaf is classified into exactly one `ChangeType` — `Added` (path only in B), `Removed` (path only in A), `Modified` (same path, different `ContentHash`), `TimestampChanged` (same path + same `ContentHash`, different timestamps); identical leaves are not emitted. The classification is MECE: `ChangeType` is a plain enum, not `[Flags]`. Rename detection and net-new-content reporting are deliberately out of scope (see the design spec). Each `SnapshotDiffEntry` carries the `FileEntry` from snapshot A (`Before`) and B (`After`); `Added`⇒`Before` null, `Removed`⇒`After` null.
```

- [ ] **Step 2: Update the CLI guide**

In `docs/guide/cli.md`, add a section documenting the new commands (place it near the `ls` command docs):

```markdown
## Inspecting snapshots

### `arius snapshot list`

Lists every snapshot, oldest first, with a 1-based index, the version id, creation time, and file count:

    arius snapshot list -a <account> -c <container>

The index is a convenience for `snapshot diff` — index 1 is the oldest snapshot, the highest index the latest.

### `arius snapshot diff <from> <to>`

Shows what changed between two snapshots. Each argument is either an index from `snapshot list` or a version/timestamp prefix:

    arius snapshot diff 5 6                                  -a <account> -c <container>
    arius snapshot diff 2024-04-02T13:09:54 2024-12-30T16:17:32 -a <account> -c <container>

Output is git `--name-status`-style — `A` added, `D` removed, `M` modified (content changed), `T` timestamp-only — followed by a summary line. The command is read-only. A warning is logged when the two snapshots were written by different Arius versions, because a cross-platform line-ending change can make identical content appear changed.
```

- [ ] **Step 3: Commit**

```bash
git add docs/design/core/features/queries.md docs/guide/cli.md
git commit -m "docs: document snapshot list & diff queries and CLI commands

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: File the net-new-content follow-up issue

**Files:** none (creates a GitHub issue).

- [ ] **Step 1: Create the follow-up issue**

Run:
```bash
gh issue create \
  --title "snapshot diff: chunk-index-backed repo-wide 'new bytes' summary" \
  --body "Follow-up to the snapshot diff command (spec: docs/history/superpowers/2026-06-29-snapshot-list-and-diff-design.md).

The v1 \`arius snapshot diff\` is purely structural (Added/Removed/Modified/TimestampChanged from the filetree, zero storage reads). 'Net-new content' was deliberately dropped because, under Merkle pruning, snapshot A's unchanged subtrees are never walked — so 'content absent from A' is not soundly computable. The chunk index answers the different, weaker question 'absent from the whole repository, ever'.

Proposal: add an optional, clearly-labelled summary (NOT a per-path sibling array) of the \`ContentHash\`es in the diff's Added ∪ Modified sets that are absent repo-wide per \`ChunkIndexService.LookupAsync\`, behind a flag (touches storage). Label it 'new to the repository' — never 'new vs snapshot A'." \
  --label enhancement
```
Expected: prints the new issue URL.

(If `--label enhancement` is rejected because the label is missing, re-run without `--label`.)

---

## Self-Review

**1. Spec coverage:**
- Rename + stream `SnapshotsListQuery`, migrate DI/Api/harness → Task 1. ✓
- `SnapshotDiffQuery` streaming, `ChangeType` plain enum, `SnapshotDiffEntry` reusing `FileEntry`, lockstep pruned walk, cross-`AriusVersion` warning → Task 2. ✓
- CLI `snapshot` group + `list` (1-based index) → Task 3. ✓
- Index/timestamp argument resolution (`5 6` and `2024-…T13:09:54`) → Task 4 (pure) + Task 5 (wired). ✓
- `snapshot diff` verb, name-status output, summary → Task 5. ✓
- MECE rationale, scope (CLI-only diff, no `--json`), docs, net-new follow-up → Tasks 2/6/7. ✓
- Tests: MECE classification + pruning-not-read + missing-snapshot + version-warning (Task 2); resolver (Task 4); verb smokes incl. index + timestamp resolution (Tasks 3/5). ✓

**2. Placeholder scan:** No TBD/TODO; every code and doc step contains literal content. ✓

**3. Type consistency:** `SnapshotsListQuery`/`SnapshotInfo`/`SnapshotsListQueryHandler` (Task 1) are used verbatim in Tasks 3/5 and DI. `SnapshotDiffQuery(VersionA, VersionB)`, `SnapshotDiffEntry(Change, Path, Before, After)`, `ChangeType{Added,Removed,Modified,TimestampChanged}` (Task 2) match their uses in DI, the verb (Task 5), and tests. `SnapshotArgumentResolver.Resolve(string, IReadOnlyList<SnapshotInfo>)` (Task 4) matches its call in Task 5. `IFileTreeService.ReadAsync` / `ISnapshotService.ResolveAsync` signatures match Core. ✓
