# Representative Workflow Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the isolated representative scenario matrix with one canonical representative workflow that runs against both Azurite and Azure, validates one evolving repository history, includes stable remote-state assertions, and folds archive-tier simulation into capability-gated workflow steps.

**Architecture:** Keep the existing E2E backend fixtures and deterministic dataset generator, but replace `Scenarios/` with a focused `Workflows/` model: one workflow definition, one workflow runner, one workflow state object, and a small set of typed workflow steps. Preserve stable helper logic from the current runner, delete obsolete scenario-model code when replaced, and keep dataset scale controlled by one explicit constant so development can run against a smaller representative repository.

**Tech Stack:** .NET 10, TUnit, Arius shared services (`SnapshotService`, `ChunkIndexService`, `FileTreeService`, `ChunkStorageService`), Azure Blob adapter, Azurite via Testcontainers

---

## File Structure

**Create**
- `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowDefinition.cs`
  - One canonical workflow definition with profile, seed, and ordered typed steps.
- `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowCatalog.cs`
  - Exposes the canonical workflow instance.
- `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowState.cs`
  - Holds backend context, fixture, dataset definition, snapshot lineage, and remote counts.
- `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs`
  - Orchestrates one full workflow run in one container and one fixture lineage.
- `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunResult.cs`
  - Captures workflow success plus archive-tier outcome details.
- `src/Arius.E2E.Tests/Workflows/Steps/IRepresentativeWorkflowStep.cs`
  - Common step interface with stable step names.
- `src/Arius.E2E.Tests/Workflows/Steps/MaterializeVersionStep.cs`
  - Materializes `V1` or `V2` into the shared source root.
- `src/Arius.E2E.Tests/Workflows/Steps/ArchiveStep.cs`
  - Runs archive with flags such as `NoPointers`, `RemoveLocal`, or `UploadTier`.
- `src/Arius.E2E.Tests/Workflows/Steps/RestoreStep.cs`
  - Runs restore against latest or previous version with configurable expectations.
- `src/Arius.E2E.Tests/Workflows/Steps/ResetCacheStep.cs`
  - Makes cold-cache transitions explicit.
- `src/Arius.E2E.Tests/Workflows/Steps/AssertRemoteStateStep.cs`
  - Validates stable snapshot, chunk, filetree, and chunk-index invariants.
- `src/Arius.E2E.Tests/Workflows/Steps/AssertConflictBehaviorStep.cs`
  - Sets up local conflicts and verifies overwrite/no-overwrite behavior.
- `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`
  - Encapsulates archive-tier planning, pending restore, ready sideload, and cleanup.
- `src/Arius.E2E.Tests/Workflows/WorkflowBlobAssertions.cs`
  - Shared helpers for counting blobs by prefix, reading snapshot manifests, and checking chunk-index lookups.

**Modify**
- `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryDefinitionFactory.cs`
  - Add one explicit size-control constant and reduce the representative profile to a development-sized dataset around 30 MB / 300 files.
- `src/Arius.E2E.Tests/RepresentativeArchiveRestoreTests.cs`
  - Switch from the scenario matrix to the canonical workflow.
- `src/Arius.E2E.Tests/E2ETests.cs`
  - Keep only the live Azure sanity probes that still add unique value after workflow coverage.
- `README.md`
  - Update the representative E2E description from a scenario matrix to one canonical workflow.
- `AGENTS.md`
  - Update guidance from representative scenarios to the canonical workflow and dataset-size knob.

**Delete**
- `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioDefinition.cs`
- `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioCatalog.cs`
- `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioRunner.cs`
- `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioCatalogObjectIdentityTests.cs`
- `src/Arius.E2E.Tests/ArchiveTierRepresentativeTests.cs`

**Test/Read During Implementation**
- `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`
- `src/Arius.E2E.Tests/Fixtures/AzureFixture.cs`
- `src/Arius.E2E.Tests/Fixtures/AzuriteE2EBackendFixture.cs`
- `src/Arius.E2E.Tests/Services/CopyTrackingBlobService.cs`
- `src/Arius.Core/Shared/Snapshot/SnapshotService.cs`
- `src/Arius.Core/Shared/ChunkIndex/ChunkIndexService.cs`
- `src/Arius.Core/Shared/Storage/IBlobContainerService.cs`
- `src/Arius.Core/Shared/Storage/BlobConstants.cs`

### Task 1: Shrink the Representative Dataset Behind One Knob

**Files:**
- Modify: `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryDefinitionFactory.cs`

- [ ] **Step 1: Add one explicit representative dataset scale constant near the top of the factory**

```csharp
internal static class SyntheticRepositoryDefinitionFactory
{
    internal const int RepresentativeScale = 1;

    public static SyntheticRepositoryDefinition Create(SyntheticRepositoryProfile profile)
    {
        return profile switch
        {
            SyntheticRepositoryProfile.Small          => CreateSmall(),
            SyntheticRepositoryProfile.Representative => CreateRepresentative(),
            _                                         => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
    }
```

- [ ] **Step 2: Replace the hard-coded representative file counts and large-file sizes with values derived from that constant**

```csharp
    static SyntheticRepositoryDefinition CreateRepresentative()
    {
        var files = new List<SyntheticFileDefinition>();
        var sourceFileCount = 180 * RepresentativeScale;
        var docFileCount = 90 * RepresentativeScale;
        var largeBinarySize = 6 * 1024 * 1024L;
        var mediumBinarySize = 3 * 1024 * 1024L;

        for (var i = 0; i < sourceFileCount; i++)
        {
            files.Add(new SyntheticFileDefinition(
                $"src/module-{i % 18:D2}/group-{i % 6:D2}/file-{i:D4}.bin",
                4 * 1024 + (i % 12) * 1024,
                $"small-{i % 80:D3}"));
        }

        for (var i = 0; i < docFileCount; i++)
        {
            files.Add(new SyntheticFileDefinition(
                $"docs/batch-{i % 8:D2}/doc-{i:D4}.txt",
                96 * 1024 + (i % 6) * 4096,
                $"edge-{i % 40:D3}"));
        }

        files.Add(new SyntheticFileDefinition("media/video/master-a.bin", largeBinarySize, "large-001"));
        files.Add(new SyntheticFileDefinition("media/video/master-b.bin", largeBinarySize, "large-002"));
```

- [ ] **Step 3: Keep the duplicate small-file and duplicate large-file cases intact so remote dedup assertions stay meaningful**

```csharp
        files.Add(new SyntheticFileDefinition("archives/duplicates/copy-a.bin", 512 * 1024, "dup-small-001"));
        files.Add(new SyntheticFileDefinition("nested/deep/a/b/c/d/e/f/copy-b.bin", 512 * 1024, "dup-small-001"));
        files.Add(new SyntheticFileDefinition("nested/deep/a/b/c/d/e/f/g/h/copy-c.bin", 512 * 1024, "dup-small-001"));

        files.Add(new SyntheticFileDefinition("archives/duplicates/binary-a.bin", mediumBinarySize, "dup-large-001"));
        files.Add(new SyntheticFileDefinition("nested/deep/a/b/c/binary-b.bin", mediumBinarySize, "dup-large-001"));
```

- [ ] **Step 4: Keep the existing deterministic V2 mutation intent but point the add mutation at the reduced layout**

```csharp
        IReadOnlyList<SyntheticMutation> mutations =
        [
            new(SyntheticMutationKind.ChangeContent, "src/module-00/group-00/file-0000.bin", ReplacementContentId: "small-updated-000", ReplacementSizeBytes: 4 * 1024),
            new(SyntheticMutationKind.Delete, "docs/batch-00/doc-0000.txt"),
            new(SyntheticMutationKind.Rename, "archives/duplicates/copy-a.bin", TargetPath: "archives/duplicates/copy-a-renamed.bin"),
            new(SyntheticMutationKind.Add, "src/module-17/group-00/new-file-0000.bin", ReplacementContentId: "new-000", ReplacementSizeBytes: 24 * 1024),
        ];
```

- [ ] **Step 5: Run the E2E project build to verify the factory still compiles**

Run: `dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Arius.E2E.Tests/Datasets/SyntheticRepositoryDefinitionFactory.cs
git commit -m "test: shrink representative dataset for workflow refactor"
```

### Task 2: Introduce the Workflow Model and Delete the Old Scenario Types

**Files:**
- Create: `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowDefinition.cs`
- Create: `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowCatalog.cs`
- Create: `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowState.cs`
- Create: `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunResult.cs`
- Create: `src/Arius.E2E.Tests/Workflows/Steps/IRepresentativeWorkflowStep.cs`
- Delete: `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioDefinition.cs`
- Delete: `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioCatalog.cs`

- [ ] **Step 1: Add the shared workflow step interface**

```csharp
namespace Arius.E2E.Tests.Workflows.Steps;

internal interface IRepresentativeWorkflowStep
{
    string Name { get; }

    Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Add the workflow definition record**

```csharp
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Workflows.Steps;

namespace Arius.E2E.Tests.Workflows;

internal sealed record RepresentativeWorkflowDefinition(
    string Name,
    SyntheticRepositoryProfile Profile,
    int Seed,
    IReadOnlyList<IRepresentativeWorkflowStep> Steps);
```

- [ ] **Step 3: Add the workflow run result and state shells with only the fields already needed by the design**

```csharp
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests.Workflows;

internal sealed record ArchiveTierWorkflowOutcome(
    bool WasCostEstimateCaptured,
    int InitialPendingChunks,
    int InitialFilesRestored,
    int PendingChunksOnRerun,
    int RerunCopyCalls,
    int ReadyFilesRestored,
    int ReadyPendingChunks,
    int CleanupDeletedChunks,
    int PendingRehydratedBlobCount);

internal sealed record RepresentativeWorkflowRunResult(
    bool WasSkipped,
    string? SkipReason = null,
    ArchiveTierWorkflowOutcome? ArchiveTierOutcome = null);

internal sealed class RepresentativeWorkflowState
{
    public required E2EStorageBackendContext Context { get; init; }
    public required E2EFixture Fixture { get; init; }
    public required SyntheticRepositoryDefinition Definition { get; init; }
    public required int Seed { get; init; }

    public SyntheticRepositoryVersion? CurrentSourceVersion { get; set; }
    public RepositoryTreeSnapshot? CurrentMaterializedSnapshot { get; set; }
    public string? PreviousSnapshotVersion { get; set; }
    public string? LatestSnapshotVersion { get; set; }
    public ArchiveTierWorkflowOutcome? ArchiveTierOutcome { get; set; }
}
```

- [ ] **Step 4: Add the canonical workflow catalog with placeholders for the real step types that will be created next**

```csharp
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Workflows.Steps;

namespace Arius.E2E.Tests.Workflows;

internal static class RepresentativeWorkflowCatalog
{
    internal static readonly RepresentativeWorkflowDefinition Canonical =
        new(
            "canonical-representative-workflow",
            SyntheticRepositoryProfile.Representative,
            20260419,
            []);
}
```

- [ ] **Step 5: Delete the old scenario definition and catalog files once the new workflow types compile**

Delete:

```text
src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioDefinition.cs
src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioCatalog.cs
```

- [ ] **Step 6: Run the E2E build to verify the workflow types compile before the runner is moved**

Run: `dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj"`
Expected: FAIL in files that still reference the old scenario types, but PASS for the new workflow type definitions themselves

- [ ] **Step 7: Commit**

```bash
git add src/Arius.E2E.Tests/Workflows src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioDefinition.cs src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioCatalog.cs
git commit -m "test: add representative workflow model"
```

### Task 3: Move Shared Runner Logic into a Workflow Runner Shell

**Files:**
- Create: `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowCatalog.cs`
- Delete: `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioRunner.cs`

- [ ] **Step 1: Create a workflow runner that owns one backend context and one fixture for the full run**

```csharp
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests.Workflows;

internal static class RepresentativeWorkflowRunner
{
    public static async Task<RepresentativeWorkflowRunResult> RunAsync(
        IE2EStorageBackend backend,
        RepresentativeWorkflowDefinition workflow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(workflow);

        await using var context = await backend.CreateContextAsync(cancellationToken);
        await using var fixture = await E2EFixture.CreateAsync(
            context.BlobContainer,
            context.AccountName,
            context.ContainerName,
            BlobTier.Cool,
            ct: cancellationToken);

        var definition = SyntheticRepositoryDefinitionFactory.Create(workflow.Profile);
        var state = new RepresentativeWorkflowState
        {
            Context = context,
            Fixture = fixture,
            Definition = definition,
            Seed = workflow.Seed,
        };

        foreach (var step in workflow.Steps)
            await step.ExecuteAsync(state, cancellationToken);

        return new RepresentativeWorkflowRunResult(false, ArchiveTierOutcome: state.ArchiveTierOutcome);
    }
}
```

- [ ] **Step 2: Port the archive-tier helper logic out of the old scenario runner into the new workflow runner file as private helper methods**

Move and adapt these methods from `RepresentativeScenarioRunner.cs` into `RepresentativeWorkflowRunner.cs` or a dedicated helper file without changing their core behavior yet:

```csharp
static string FormatSnapshotVersion(DateTimeOffset snapshotTime) =>
    snapshotTime.UtcDateTime.ToString(SnapshotService.TimestampFormat);

static async Task<string?> PollForArchiveTierTarChunkAsync(...)
static async Task<Dictionary<string, byte[]>> ReadArchiveTierContentBytesAsync(...)
static async Task SideloadRehydratedTarChunkAsync(...)
static RepositoryTreeSnapshot FilterSnapshotToPrefix(...)
```

- [ ] **Step 3: Delete the old scenario runner file once the helper logic has been moved**

Delete:

```text
src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioRunner.cs
```

- [ ] **Step 4: Run the E2E build to verify the old runner is fully replaced**

Run: `dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj"`
Expected: FAIL only in tests and files that still reference the old runner by name

- [ ] **Step 5: Commit**

```bash
git add src/Arius.E2E.Tests/Workflows src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioRunner.cs
git commit -m "test: move representative orchestration to workflow runner"
```

### Task 4: Implement the Basic Typed Workflow Steps

**Files:**
- Create: `src/Arius.E2E.Tests/Workflows/Steps/MaterializeVersionStep.cs`
- Create: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveStep.cs`
- Create: `src/Arius.E2E.Tests/Workflows/Steps/RestoreStep.cs`
- Create: `src/Arius.E2E.Tests/Workflows/Steps/ResetCacheStep.cs`

- [ ] **Step 1: Add the materialize step**

```csharp
using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record MaterializeVersionStep(SyntheticRepositoryVersion Version) : IRepresentativeWorkflowStep
{
    public string Name => $"materialize-{Version}";

    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        state.CurrentMaterializedSnapshot = await state.Fixture.MaterializeSourceAsync(
            state.Definition,
            Version,
            state.Seed);
        state.CurrentSourceVersion = Version;
    }
}
```

- [ ] **Step 2: Add the archive step with explicit options only for current needs**

```csharp
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record ArchiveStep(
    string Name,
    BlobTier UploadTier = BlobTier.Cool,
    bool NoPointers = false,
    bool RemoveLocal = false) : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        var result = await state.Fixture.CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = state.Fixture.LocalRoot,
                UploadTier = UploadTier,
                NoPointers = NoPointers,
                RemoveLocal = RemoveLocal,
            }),
            cancellationToken).AsTask();

        result.Success.ShouldBeTrue($"{Name}: {result.ErrorMessage}");
        state.PreviousSnapshotVersion = state.LatestSnapshotVersion;
        state.LatestSnapshotVersion = result.SnapshotTime.UtcDateTime.ToString(SnapshotService.TimestampFormat);
    }
}
```

- [ ] **Step 3: Add the reset-cache step**

```csharp
namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record ResetCacheStep(string Name = "reset-cache") : IRepresentativeWorkflowStep
{
    public Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
        => E2EFixture.ResetLocalCacheAsync(state.Context.AccountName, state.Context.ContainerName);
}
```

- [ ] **Step 4: Add the restore step with current/previous target support and pointer assertions**

```csharp
using Arius.Core.Features.RestoreCommand;
using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Workflows.Steps;

internal enum WorkflowRestoreTarget
{
    Latest,
    Previous,
}

internal sealed record RestoreStep(
    string Name,
    WorkflowRestoreTarget Target,
    SyntheticRepositoryVersion ExpectedVersion,
    bool Overwrite = true,
    bool ExpectPointers = true) : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        if (Directory.Exists(state.Fixture.RestoreRoot))
            Directory.Delete(state.Fixture.RestoreRoot, recursive: true);

        Directory.CreateDirectory(state.Fixture.RestoreRoot);

        var version = Target == WorkflowRestoreTarget.Previous
            ? state.PreviousSnapshotVersion
            : null;

        var result = await state.Fixture.CreateRestoreHandler().Handle(
            new RestoreCommand(new RestoreOptions
            {
                RootDirectory = state.Fixture.RestoreRoot,
                Overwrite = Overwrite,
                Version = version,
            }),
            cancellationToken).AsTask();

        result.Success.ShouldBeTrue($"{Name}: {result.ErrorMessage}");

        var expectedRoot = Path.Combine(Path.GetTempPath(), $"arius-expected-{Guid.NewGuid():N}");
        try
        {
            var expected = await SyntheticRepositoryMaterializer.MaterializeAsync(
                state.Definition,
                ExpectedVersion,
                state.Seed,
                expectedRoot);

            await RepositoryTreeAssertions.AssertMatchesDiskTreeAsync(
                expected,
                state.Fixture.RestoreRoot,
                includePointerFiles: false);

            foreach (var relativePath in expected.Files.Keys)
            {
                var pointerPath = Path.Combine(
                    state.Fixture.RestoreRoot,
                    (relativePath + ".pointer.arius").Replace('/', Path.DirectorySeparatorChar));

                File.Exists(pointerPath).ShouldBe(
                    ExpectPointers,
                    $"{Name}: unexpected pointer file state for {relativePath}");
            }
        }
        finally
        {
            if (Directory.Exists(expectedRoot))
                Directory.Delete(expectedRoot, recursive: true);
        }
    }
}
```

- [ ] **Step 5: Run the E2E build so these step files compile together with the new runner**

Run: `dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj"`
Expected: FAIL only in files that still rely on the old representative test entry points

- [ ] **Step 6: Commit**

```bash
git add src/Arius.E2E.Tests/Workflows/Steps src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs
git commit -m "test: add basic representative workflow steps"
```

### Task 5: Add Stable Remote-State Assertions

**Files:**
- Create: `src/Arius.E2E.Tests/Workflows/WorkflowBlobAssertions.cs`
- Create: `src/Arius.E2E.Tests/Workflows/Steps/AssertRemoteStateStep.cs`

- [ ] **Step 1: Add shared helpers for blob-prefix counts and snapshot resolution**

```csharp
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;

namespace Arius.E2E.Tests.Workflows;

internal static class WorkflowBlobAssertions
{
    public static async Task<int> CountBlobsAsync(IBlobContainerService blobs, string prefix, CancellationToken cancellationToken)
    {
        var count = 0;
        await foreach (var _ in blobs.ListAsync(prefix, cancellationToken))
            count++;

        return count;
    }

    public static Task<SnapshotManifest?> ResolveLatestAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
        => state.Fixture.Snapshot.ResolveAsync(cancellationToken: cancellationToken);

    public static Task<SnapshotManifest?> ResolveVersionAsync(RepresentativeWorkflowState state, string version, CancellationToken cancellationToken)
        => state.Fixture.Snapshot.ResolveAsync(version, cancellationToken);

    public static Task<ShardEntry?> LookupChunkAsync(RepresentativeWorkflowState state, string contentHash, CancellationToken cancellationToken)
        => state.Fixture.Index.LookupAsync(contentHash, cancellationToken);
}
```

- [ ] **Step 2: Add a remote-state step that handles the stable invariants from the design**

```csharp
using Arius.Core.Shared.Storage;

namespace Arius.E2E.Tests.Workflows.Steps;

internal enum RemoteAssertionKind
{
    InitialArchive,
    IncrementalArchive,
    NoOpArchive,
}

internal sealed record AssertRemoteStateStep(string Name, RemoteAssertionKind Kind) : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        var latest = await WorkflowBlobAssertions.ResolveLatestAsync(state, cancellationToken);
        latest.ShouldNotBeNull($"{Name}: latest snapshot should exist");

        switch (Kind)
        {
            case RemoteAssertionKind.InitialArchive:
                (await WorkflowBlobAssertions.CountBlobsAsync(state.Context.BlobContainer, BlobPaths.Snapshots, cancellationToken)).ShouldBe(1);
                latest.FileCount.ShouldBe(state.CurrentMaterializedSnapshot!.Files.Count);
                break;

            case RemoteAssertionKind.IncrementalArchive:
                (await WorkflowBlobAssertions.CountBlobsAsync(state.Context.BlobContainer, BlobPaths.Snapshots, cancellationToken)).ShouldBe(2);
                latest.FileCount.ShouldBe(state.CurrentMaterializedSnapshot!.Files.Count);
                await AssertDuplicateLargeBinaryDedupAsync(state, cancellationToken);
                await AssertSmallFileTarPathAsync(state, cancellationToken);
                break;

            case RemoteAssertionKind.NoOpArchive:
                var previous = await WorkflowBlobAssertions.ResolveVersionAsync(state, state.PreviousSnapshotVersion!, cancellationToken);
                previous.ShouldNotBeNull($"{Name}: previous snapshot should exist");
                latest.RootHash.ShouldBe(previous.RootHash);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(Kind));
        }
    }

    static async Task AssertDuplicateLargeBinaryDedupAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        var sourceBytes = await File.ReadAllBytesAsync(
            E2EFixture.CombineValidatedRelativePath(state.Fixture.LocalRoot, "archives/duplicates/binary-a.bin"),
            cancellationToken);
        var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var shardEntry = await WorkflowBlobAssertions.LookupChunkAsync(state, contentHash, cancellationToken);
        shardEntry.ShouldNotBeNull();
        shardEntry.ContentHash.ShouldBe(contentHash);
    }

    static async Task AssertSmallFileTarPathAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        var sourceBytes = await File.ReadAllBytesAsync(
            E2EFixture.CombineValidatedRelativePath(state.Fixture.LocalRoot, "src/module-00/group-00/file-0000.bin"),
            cancellationToken);
        var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var shardEntry = await WorkflowBlobAssertions.LookupChunkAsync(state, contentHash, cancellationToken);
        shardEntry.ShouldNotBeNull();
        shardEntry.ChunkHash.ShouldNotBe(contentHash);
    }
}
```

- [ ] **Step 3: Extend the no-op branch to assert chunk and filetree counts do not grow**

Add these fields to `RepresentativeWorkflowState`:

```csharp
    public int? ChunkBlobCountBeforeNoOpArchive { get; set; }
    public int? FileTreeBlobCountBeforeNoOpArchive { get; set; }
```

Add these checks inside `RemoteAssertionKind.NoOpArchive`:

```csharp
                var chunkCount = await WorkflowBlobAssertions.CountBlobsAsync(state.Context.BlobContainer, BlobPaths.Chunks, cancellationToken);
                var fileTreeCount = await WorkflowBlobAssertions.CountBlobsAsync(state.Context.BlobContainer, BlobPaths.FileTrees, cancellationToken);
                chunkCount.ShouldBe(state.ChunkBlobCountBeforeNoOpArchive);
                fileTreeCount.ShouldBe(state.FileTreeBlobCountBeforeNoOpArchive);
```

- [ ] **Step 4: Run the E2E build to verify the remote assertion helpers compile**

Run: `dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj"`
Expected: FAIL only in tests and workflow catalog usage that have not yet been rewired

- [ ] **Step 5: Commit**

```bash
git add src/Arius.E2E.Tests/Workflows/WorkflowBlobAssertions.cs src/Arius.E2E.Tests/Workflows/Steps/AssertRemoteStateStep.cs src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowState.cs
git commit -m "test: add representative workflow remote assertions"
```

### Task 6: Add Conflict and Archive-Tier Lifecycle Steps

**Files:**
- Create: `src/Arius.E2E.Tests/Workflows/Steps/AssertConflictBehaviorStep.cs`
- Create: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`
- Modify: `src/Arius.E2E.Tests/Services/CopyTrackingBlobService.cs` only if the existing API needs a small adaptation for the new step

- [ ] **Step 1: Add the conflict step with overwrite/no-overwrite behavior**

```csharp
using Arius.Core.Features.RestoreCommand;
using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record AssertConflictBehaviorStep(string Name, bool Overwrite) : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        const string relativePath = "src/module-00/group-00/file-0000.bin";

        if (Directory.Exists(state.Fixture.RestoreRoot))
            Directory.Delete(state.Fixture.RestoreRoot, recursive: true);

        Directory.CreateDirectory(state.Fixture.RestoreRoot);

        var restorePath = E2EFixture.CombineValidatedRelativePath(state.Fixture.RestoreRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(restorePath)!);

        var conflictBytes = new byte[1024];
        new Random(HashCode.Combine(state.Seed, Name)).NextBytes(conflictBytes);
        await File.WriteAllBytesAsync(restorePath, conflictBytes, cancellationToken);

        var result = await state.Fixture.CreateRestoreHandler().Handle(
            new RestoreCommand(new RestoreOptions
            {
                RootDirectory = state.Fixture.RestoreRoot,
                Overwrite = Overwrite,
            }),
            cancellationToken).AsTask();

        result.Success.ShouldBeTrue($"{Name}: {result.ErrorMessage}");

        var restoredBytes = await File.ReadAllBytesAsync(restorePath, cancellationToken);
        if (Overwrite)
            restoredBytes.ShouldNotBe(conflictBytes);
        else
            restoredBytes.ShouldBe(conflictBytes);
    }
}
```

- [ ] **Step 2: Add the archive-tier lifecycle step with explicit pending-blob deletion and deterministic ready sideloading**

```csharp
using Arius.AzureBlob;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Services;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record ArchiveTierLifecycleStep(string Name, string TargetPath) : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        if (!state.Context.Capabilities.SupportsArchiveTier)
            return;

        var azureBlobContainer = state.Context.AzureBlobContainerService;
        azureBlobContainer.ShouldNotBeNull();

        var archiveResult = await state.Fixture.CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = state.Fixture.LocalRoot,
                UploadTier = BlobTier.Archive,
            }),
            cancellationToken).AsTask();
        archiveResult.Success.ShouldBeTrue($"{Name}: {archiveResult.ErrorMessage}");

        var tarChunkHash = await RepresentativeWorkflowRunner.PollForArchiveTierTarChunkAsync(azureBlobContainer, cancellationToken);
        tarChunkHash.ShouldNotBeNullOrWhiteSpace();

        var contentHashToBytes = await RepresentativeWorkflowRunner.ReadArchiveTierContentBytesAsync(state.Fixture.LocalRoot, TargetPath);

        var trackingSvc1 = new CopyTrackingBlobService(azureBlobContainer);
        var firstEstimateCaptured = false;
        var initialResult = await RepresentativeWorkflowRunner.CreateArchiveTierRestoreHandler(state.Fixture, state.Context, trackingSvc1)
            .Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = state.Fixture.RestoreRoot,
                TargetPath = TargetPath,
                Overwrite = true,
                ConfirmRehydration = (estimate, _) =>
                {
                    firstEstimateCaptured = true;
                    (estimate.ChunksNeedingRehydration + estimate.ChunksPendingRehydration).ShouldBeGreaterThan(0);
                    return Task.FromResult<RehydratePriority?>(RehydratePriority.Standard);
                },
            }), cancellationToken).AsTask();

        initialResult.Success.ShouldBeTrue(initialResult.ErrorMessage);
        initialResult.ChunksPendingRehydration.ShouldBeGreaterThan(0);
        initialResult.FilesRestored.ShouldBe(0);

        var pendingRehydratedBlobs = new List<string>();
        await foreach (var blobName in state.Context.BlobContainer.ListAsync(BlobPaths.ChunksRehydrated, cancellationToken))
            pendingRehydratedBlobs.Add(blobName);
        pendingRehydratedBlobs.Count.ShouldBeGreaterThan(0);

        var trackingSvc2 = new CopyTrackingBlobService(azureBlobContainer);
        var rerunResult = await RepresentativeWorkflowRunner.CreateArchiveTierRestoreHandler(state.Fixture, state.Context, trackingSvc2)
            .Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = state.Fixture.RestoreRoot,
                TargetPath = TargetPath,
                Overwrite = true,
                ConfirmRehydration = (_, _) => Task.FromResult<RehydratePriority?>(RehydratePriority.Standard),
            }), cancellationToken).AsTask();

        rerunResult.Success.ShouldBeTrue(rerunResult.ErrorMessage);
        trackingSvc2.CopyCalls.Count.ShouldBe(0);

        foreach (var blobName in pendingRehydratedBlobs)
            await state.Context.BlobContainer.DeleteAsync(blobName, cancellationToken);

        await RepresentativeWorkflowRunner.SideloadRehydratedTarChunkAsync(
            azureBlobContainer,
            tarChunkHash!,
            contentHashToBytes,
            cancellationToken);

        var cleanupDeletedChunks = 0;
        var readyRestoreRoot = Path.Combine(Path.GetTempPath(), $"arius-archive-tier-ready-{Guid.NewGuid():N}");
        Directory.CreateDirectory(readyRestoreRoot);

        try
        {
            var readyResult = await state.Fixture.CreateRestoreHandler().Handle(
                new RestoreCommand(new RestoreOptions
                {
                    RootDirectory = readyRestoreRoot,
                    TargetPath = TargetPath,
                    Overwrite = true,
                    ConfirmCleanup = (count, _, _) =>
                    {
                        cleanupDeletedChunks = count;
                        return Task.FromResult(true);
                    },
                }),
                cancellationToken).AsTask();

            readyResult.Success.ShouldBeTrue(readyResult.ErrorMessage);
            readyResult.ChunksPendingRehydration.ShouldBe(0);
            cleanupDeletedChunks.ShouldBeGreaterThan(0);

            state.ArchiveTierOutcome = new ArchiveTierWorkflowOutcome(
                firstEstimateCaptured,
                initialResult.ChunksPendingRehydration,
                initialResult.FilesRestored,
                rerunResult.ChunksPendingRehydration,
                trackingSvc2.CopyCalls.Count,
                readyResult.FilesRestored,
                readyResult.ChunksPendingRehydration,
                cleanupDeletedChunks,
                pendingRehydratedBlobs.Count);
        }
        finally
        {
            if (Directory.Exists(readyRestoreRoot))
                Directory.Delete(readyRestoreRoot, recursive: true);
        }
    }
}
```

- [ ] **Step 3: Add the helper signatures to the workflow runner as `internal static` members so the archive-tier step can reuse the already moved logic**

```csharp
internal static RestoreCommandHandler CreateArchiveTierRestoreHandler(...)
internal static Task<string?> PollForArchiveTierTarChunkAsync(...)
internal static Task<Dictionary<string, byte[]>> ReadArchiveTierContentBytesAsync(...)
internal static Task SideloadRehydratedTarChunkAsync(...)
```

- [ ] **Step 4: Run the E2E build to verify the archive-tier step compiles against the moved helper methods**

Run: `dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj"`
Expected: FAIL only in the remaining test entry points that have not yet switched to the canonical workflow

- [ ] **Step 5: Commit**

```bash
git add src/Arius.E2E.Tests/Workflows/Steps src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs src/Arius.E2E.Tests/Services/CopyTrackingBlobService.cs
git commit -m "test: add archive tier and conflict workflow steps"
```

### Task 7: Assemble the Canonical Workflow Definition

**Files:**
- Modify: `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowCatalog.cs`

- [ ] **Step 1: Replace the empty workflow catalog with the ordered canonical step sequence**

```csharp
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Workflows.Steps;

namespace Arius.E2E.Tests.Workflows;

internal static class RepresentativeWorkflowCatalog
{
    internal static readonly RepresentativeWorkflowDefinition Canonical =
        new(
            "canonical-representative-workflow",
            SyntheticRepositoryProfile.Representative,
            20260419,
            [
                new MaterializeVersionStep(SyntheticRepositoryVersion.V1),
                new ArchiveStep("archive-v1"),
                new AssertRemoteStateStep("assert-initial-archive", RemoteAssertionKind.InitialArchive),
                new RestoreStep("restore-latest-v1", WorkflowRestoreTarget.Latest, SyntheticRepositoryVersion.V1),

                new MaterializeVersionStep(SyntheticRepositoryVersion.V2),
                new ArchiveStep("archive-v2"),
                new AssertRemoteStateStep("assert-incremental-archive", RemoteAssertionKind.IncrementalArchive),
                new RestoreStep("restore-latest-v2-warm", WorkflowRestoreTarget.Latest, SyntheticRepositoryVersion.V2),

                new ResetCacheStep(),
                new RestoreStep("restore-latest-v2-cold", WorkflowRestoreTarget.Latest, SyntheticRepositoryVersion.V2),
                new RestoreStep("restore-previous-v1", WorkflowRestoreTarget.Previous, SyntheticRepositoryVersion.V1),

                new ArchiveStep("archive-v2-noop"),
                new AssertRemoteStateStep("assert-noop-archive", RemoteAssertionKind.NoOpArchive),

                new ArchiveStep("archive-no-pointers", NoPointers: true),
                new RestoreStep("restore-no-pointers", WorkflowRestoreTarget.Latest, SyntheticRepositoryVersion.V2, ExpectPointers: false),

                new ArchiveStep("archive-remove-local", RemoveLocal: true),
                new RestoreStep("restore-after-remove-local", WorkflowRestoreTarget.Latest, SyntheticRepositoryVersion.V2),

                new AssertConflictBehaviorStep("restore-conflict-no-overwrite", Overwrite: false),
                new AssertConflictBehaviorStep("restore-conflict-overwrite", Overwrite: true),

                new MaterializeVersionStep(SyntheticRepositoryVersion.V2),
                new ArchiveTierLifecycleStep("archive-tier-lifecycle", "src"),
            ]);
}
```

- [ ] **Step 2: Capture the pre-noop chunk and filetree counts before the no-op archive assertion runs**

Add a small hook inside `ArchiveStep.ExecuteAsync`:

```csharp
        if (Name == "archive-v2-noop")
        {
            state.ChunkBlobCountBeforeNoOpArchive = await WorkflowBlobAssertions.CountBlobsAsync(
                state.Context.BlobContainer,
                BlobPaths.Chunks,
                cancellationToken);
            state.FileTreeBlobCountBeforeNoOpArchive = await WorkflowBlobAssertions.CountBlobsAsync(
                state.Context.BlobContainer,
                BlobPaths.FileTrees,
                cancellationToken);
        }
```

- [ ] **Step 3: Run the E2E build to verify the full workflow definition compiles**

Run: `dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj"`
Expected: FAIL only in the test classes that still point at the old scenario entry points

- [ ] **Step 4: Commit**

```bash
git add src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowCatalog.cs src/Arius.E2E.Tests/Workflows/Steps/ArchiveStep.cs
git commit -m "test: assemble canonical representative workflow"
```

### Task 8: Rewire the E2E Test Entry Points and Remove Obsolete Representative Tests

**Files:**
- Modify: `src/Arius.E2E.Tests/RepresentativeArchiveRestoreTests.cs`
- Delete: `src/Arius.E2E.Tests/ArchiveTierRepresentativeTests.cs`
- Delete: `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioCatalogObjectIdentityTests.cs`

- [ ] **Step 1: Replace the representative archive/restore test with a single canonical workflow test on both backends**

```csharp
using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Workflows;

namespace Arius.E2E.Tests;

internal class RepresentativeArchiveRestoreTests
{
    [Test]
    [CombinedDataSources]
    public async Task Canonical_Representative_Workflow_Runs_On_Supported_Backends(
        [ClassDataSource<AzuriteE2EBackendFixture>(Shared = SharedType.PerTestSession)]
        [ClassDataSource<AzureE2EBackendFixture>(Shared = SharedType.PerTestSession)]
        IE2EStorageBackend backend,
        CancellationToken cancellationToken)
    {
        if (backend is AzureE2EBackendFixture && !AzureFixture.IsAvailable)
        {
            Skip.Unless(false, "Azure credentials not available — skipping live representative backend coverage");
            return;
        }

        if (backend is AzureE2EBackendFixture)
        {
            Skip.Unless(false, "Azure canonical representative workflow still includes the cold-cache restore path tracked by issue #65");
            return;
        }

        var result = await RepresentativeWorkflowRunner.RunAsync(
            backend,
            RepresentativeWorkflowCatalog.Canonical,
            cancellationToken);

        result.WasSkipped.ShouldBeFalse();

        if (backend.Capabilities.SupportsArchiveTier)
        {
            result.ArchiveTierOutcome.ShouldNotBeNull();
            result.ArchiveTierOutcome.PendingRehydratedBlobCount.ShouldBeGreaterThan(0);
            result.ArchiveTierOutcome.WasCostEstimateCaptured.ShouldBeTrue();
            result.ArchiveTierOutcome.RerunCopyCalls.ShouldBe(0);
        }
    }
}
```

- [ ] **Step 2: Delete the obsolete archive-tier-only representative test and old identity test**

Delete:

```text
src/Arius.E2E.Tests/ArchiveTierRepresentativeTests.cs
src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioCatalogObjectIdentityTests.cs
```

- [ ] **Step 3: Run the representative E2E test class**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/RepresentativeArchiveRestoreTests/*"`
Expected: PASS on Azurite when Docker is available; Azure skips with a visible reference to issue `#65` until the cold-cache restore issue is fixed

- [ ] **Step 4: Commit**

```bash
git add src/Arius.E2E.Tests/RepresentativeArchiveRestoreTests.cs src/Arius.E2E.Tests/ArchiveTierRepresentativeTests.cs src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioCatalogObjectIdentityTests.cs
git commit -m "test: switch representative E2E coverage to canonical workflow"
```

### Task 9: Update Docs and Verify the Full Test Surface

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`

- [ ] **Step 1: Update the README representative E2E section to describe one canonical workflow and the dataset-size knob**

Add or revise these lines in `README.md`:

```md
- `RepresentativeArchiveRestoreTests.cs` runs one canonical representative workflow on Azurite and, when credentials are available, live Azure.
- The representative workflow exercises one evolving archive history rather than isolated one-off scenarios.
- The synthetic representative repository size is controlled by a single constant in the dataset factory so development can use a smaller profile and later scale it up.
- Archive-tier pending-versus-ready behavior is exercised inside the same workflow on Azure-capable storage.
```

- [ ] **Step 2: Update AGENTS guidance so future agents know the representative suite is workflow-based, not scenario-matrix based**

Add or revise these lines in `AGENTS.md`:

```md
- Representative E2E coverage now runs one canonical workflow per backend instead of an isolated scenario matrix.
- Keep archive-tier behavior inside capability-gated workflow steps rather than separate top-level representative suites.
- The representative synthetic dataset size is controlled by a single explicit constant in `SyntheticRepositoryDefinitionFactory`; tune it deliberately when changing runtime cost.
- Remove obsolete representative workflow scaffolding when replacing it; do not keep both workflow and scenario models in parallel.
```

- [ ] **Step 3: Run the full non-Windows test slate required by the repo instructions**

Run these commands:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"
dotnet test --project "src/Arius.AzureBlob.Tests/Arius.AzureBlob.Tests.csproj"
dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj"
dotnet test --project "src/Arius.Architecture.Tests/Arius.Architecture.Tests.csproj"
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj"
dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj"
```

Expected: PASS, with Azurite-backed tests skipping visibly when Docker is unavailable and Azure-backed live tests skipping visibly when credentials are unavailable

- [ ] **Step 4: Commit**

```bash
git add README.md AGENTS.md
git commit -m "docs: describe canonical representative workflow"
```
