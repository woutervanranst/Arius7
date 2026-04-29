# Filetree Manifest Upload Parallelization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Measure and improve the archive tail by renaming filetree synchronization, avoiding duplicate validation, and uploading missing filetree blobs with bounded parallelism.

**Architecture:** Keep `ArchiveCommandHandler` as the archive orchestrator and `FileTreeBuilder` as the filetree synchronization unit. The builder will compute deterministic bottom-up filetree hashes, append child directory entries directly to parent lists, queue missing filetree uploads, and await all uploads before returning a root hash.

**Tech Stack:** .NET 10, C#, TUnit, BenchmarkDotNet, Arius in-memory blob container test double.

---

## File Structure

- Modify `src/Arius.Benchmarks/ArchiveStepBenchmarks.cs`: benchmark-only class that materializes representative V1 in setup and measures a single in-memory archive step.
- Modify `src/Arius.Benchmarks/Program.cs`: run `ArchiveStepBenchmarks` for this scoped benchmark path.
- Modify `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`: rename `BuildAsync` to `SynchronizeAsync`, remove duplicate validation, direct-append child directory entries, and add bounded parallel filetree upload draining.
- Modify `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`: call `SynchronizeAsync` after the existing single validation.
- Modify `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`: update tests for renamed API and add behavior coverage for required validation and completed upload before return.
- Modify `src/Arius.Integration.Tests/Shared/FileTree/FileTreeBuilderIntegrationTests.cs`: update integration test method names and calls to `SynchronizeAsync`.
- Modify `docs/superpowers/specs/2026-04-29-filetree-manifest-upload-parallelization-design.md`: already written design record.

### Task 1: Baseline Scoped Benchmark

**Files:**
- Create: `src/Arius.Benchmarks/ArchiveStepBenchmarks.cs`
- Modify: `src/Arius.Benchmarks/Program.cs`

- [x] **Step 1: Add the benchmark class**

```csharp
using Arius.Core.Features.ArchiveCommand;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Workflows;
using Arius.Tests.Shared.Fixtures;
using BenchmarkDotNet.Attributes;

namespace Arius.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
public class ArchiveStepBenchmarks
{
    private RepositoryTestFixture? _fixture;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixture = await RepositoryTestFixture.CreateInMemoryAsync();

        var definition = SyntheticRepositoryDefinitionFactory.Create(SyntheticRepositoryProfile.Representative);
        await SyntheticRepositoryMaterializer.MaterializeV1Async(
            definition,
            RepresentativeWorkflowCatalog.Canonical.Seed,
            _fixture.LocalRoot,
            _fixture.Encryption);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_fixture is not null)
            await _fixture.DisposeAsync();
    }

    [Benchmark(Description = "Archive_Step_V1_Representative_InMemory")]
    public async Task Archive_Step_V1_Representative_InMemory()
    {
        if (_fixture is null)
            throw new InvalidOperationException("Benchmark fixture was not initialized.");

        var result = await _fixture.CreateArchiveHandler()
            .Handle(
                new ArchiveCommand(new ArchiveCommandOptions
                {
                    RootDirectory = _fixture.LocalRoot,
                }),
                CancellationToken.None)
            .AsTask();

        if (!result.Success)
            throw new InvalidOperationException(result.ErrorMessage ?? "Archive benchmark failed.");
    }
}
```

- [x] **Step 2: Point the benchmark runner at the scoped benchmark**

Change `src/Arius.Benchmarks/Program.cs`:

```csharp
var summary = BenchmarkRunner.Run<ArchiveStepBenchmarks>(config);
```

- [x] **Step 3: Build the benchmark project**

Run: `dotnet build "src/Arius.Benchmarks/Arius.Benchmarks.csproj"`

Expected: build succeeds with `0 Warning(s)` and `0 Error(s)`.

- [x] **Step 4: Run and save the baseline**

Run: `dotnet run --project "src/Arius.Benchmarks/Arius.Benchmarks.csproj" -c Release -- --raw-output "src/Arius.Benchmarks/raw" --tail-log "src/Arius.Benchmarks/benchmark-tail.md"`

Expected: BenchmarkDotNet exports an `Arius.Benchmarks.ArchiveStepBenchmarks-report-github.md` file under `src/Arius.Benchmarks/raw/<timestamp>/results/`.

Baseline captured: `src/Arius.Benchmarks/raw/20260429T130338.969Z/results/Arius.Benchmarks.ArchiveStepBenchmarks-report-github.md` with mean `33.58 ms` and allocation `9.69 MB`.

### Task 2: Rename Builder API And Remove Duplicate Validation

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`
- Modify: `src/Arius.Integration.Tests/Shared/FileTree/FileTreeBuilderIntegrationTests.cs`

- [ ] **Step 1: Update tests to call `SynchronizeAsync` and validate explicitly**

In `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`, update `CreateBuilder` call sites so each test validates the service before synchronizing. Use this helper pattern locally in tests:

```csharp
var builder = CreateBuilder(blobs, acct, cont, out var fileTreeService);
await fileTreeService.ValidateAsync();
var root = await builder.SynchronizeAsync(manifestPath);
```

Update the helper signature:

```csharp
private static FileTreeBuilder CreateBuilder(
    IBlobContainerService blobs,
    string accountName,
    string containerName,
    out FileTreeService fileTreeService)
{
    var index = new ChunkIndexService(blobs, s_enc, accountName, containerName);
    fileTreeService = new FileTreeService(blobs, s_enc, index, accountName, containerName);
    return new FileTreeBuilder(s_enc, fileTreeService);
}
```

Rename test methods from `BuildAsync_*` to `SynchronizeAsync_*`.

- [ ] **Step 2: Add a failing validation behavior test**

Add this test to `FileTreeBuilderTests`:

```csharp
[Test]
public async Task SynchronizeAsync_WithoutValidation_FailsFastBeforeUpload()
{
    var manifestPath = Path.GetTempFileName();
    try
    {
        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await File.WriteAllTextAsync(
            manifestPath,
            new ManifestEntry("file.txt", FakeContentHash('2'), now, now).Serialize() + "\n");

        var blobs = new FakeRecordingBlobContainerService();
        var builder = CreateBuilder(blobs, "acc-unvalidated", "con-unvalidated", out _);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await builder.SynchronizeAsync(manifestPath));

        ex.Message.ShouldContain("ValidateAsync");
        blobs.Uploaded.ShouldBeEmpty();
    }
    finally
    {
        File.Delete(manifestPath);
        var cacheDir = FileTreeService.GetDiskCacheDirectory("acc-unvalidated", "con-unvalidated");
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }
}
```

- [ ] **Step 3: Run the focused test and verify it fails for the missing method**

Run: `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderTests/*"`

Expected: compile fails because `FileTreeBuilder` does not yet contain `SynchronizeAsync`, or tests fail because validation behavior is not yet aligned.

- [ ] **Step 4: Rename the production API and remove builder validation**

In `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`, rename the method:

```csharp
public async Task<FileTreeHash?> SynchronizeAsync(
    string            sortedManifestPath,
    CancellationToken cancellationToken = default)
```

Remove these lines from the method body:

```csharp
// Ensure cache is validated before calling ExistsInRemote (idempotent — no-op if already validated).
await _fileTreeService.ValidateAsync(cancellationToken);
```

Update the XML summary so it says the method synchronizes the filetree blobs from a sorted manifest and uploads missing filetrees.

- [ ] **Step 5: Update archive handler call site**

Change `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`:

```csharp
var rootHash = await treeBuilder.SynchronizeAsync(manifestPath, cancellationToken);
```

- [ ] **Step 6: Update integration tests**

In `src/Arius.Integration.Tests/Shared/FileTree/FileTreeBuilderIntegrationTests.cs`, rename methods from `BuildAsync_*` to `SynchronizeAsync_*`, call `await fileTreeService.ValidateAsync()` before synchronizing, and replace `BuildAsync` calls with `SynchronizeAsync`.

- [ ] **Step 7: Verify focused filetree tests pass**

Run: `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderTests/*"`

Expected: all `FileTreeBuilderTests` pass.

### Task 3: Parallelize Missing Filetree Uploads

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`

- [ ] **Step 1: Add a test proving synchronization waits for queued uploads**

Add a test blob service in `FileTreeBuilderTests`:

```csharp
private sealed class BlockingUploadBlobContainerService : IBlobContainerService
{
    private readonly TaskCompletionSource _uploadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allowUpload = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HashSet<string> Uploaded { get; } = new(StringComparer.Ordinal);

    public Task UploadStarted => _uploadStarted.Task;

    public void AllowUpload() => _allowUpload.SetResult();

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task UploadAsync(
        string blobName,
        Stream content,
        IReadOnlyDictionary<string, string> metadata,
        BlobTier tier,
        string? contentType = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (blobName.StartsWith(BlobPaths.FileTrees, StringComparison.Ordinal))
        {
            _uploadStarted.TrySetResult();
            await _allowUpload.Task.WaitAsync(cancellationToken);
        }

        Uploaded.Add(blobName);
    }

    public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream());

    public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream());

    public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BlobMetadata { Exists = false });

    public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<string>();

    public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
```

Add this test:

```csharp
[Test]
public async Task SynchronizeAsync_DoesNotReturnBeforeFileTreeUploadCompletes()
{
    var manifestPath = Path.GetTempFileName();
    try
    {
        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await File.WriteAllTextAsync(
            manifestPath,
            new ManifestEntry("file.txt", FakeContentHash('3'), now, now).Serialize() + "\n");

        var blobs = new BlockingUploadBlobContainerService();
        var builder = CreateBuilder(blobs, "acc-blocking", "con-blocking", out var fileTreeService);
        await fileTreeService.ValidateAsync();

        var synchronizeTask = builder.SynchronizeAsync(manifestPath);
        await blobs.UploadStarted.WaitAsync(TimeSpan.FromSeconds(5));

        synchronizeTask.IsCompleted.ShouldBeFalse();

        blobs.AllowUpload();
        var root = await synchronizeTask.WaitAsync(TimeSpan.FromSeconds(5));

        root.ShouldNotBeNull();
        blobs.Uploaded.Count.ShouldBeGreaterThan(0);
    }
    finally
    {
        File.Delete(manifestPath);
        var cacheDir = FileTreeService.GetDiskCacheDirectory("acc-blocking", "con-blocking");
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }
}
```

- [ ] **Step 2: Run the new test and verify it fails or hangs without implementation**

Run: `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderTests/SynchronizeAsync_DoesNotReturnBeforeFileTreeUploadCompletes"`

Expected before implementation: fails because the helper may need `FakeRecordingBlobContainerService.UploadAsync` to be virtual, or because production still uploads inline. If the method currently waits due to sequential upload, keep the test as a regression test and proceed to the implementation change.

- [ ] **Step 3: Add bounded upload helpers to `FileTreeBuilder`**

In `FileTreeBuilder`, add constants and helper local methods inside `SynchronizeAsync` rather than a new class:

```csharp
const int FileTreeUploadWorkers = 4;
var pendingUploads = Channel.CreateBounded<(FileTreeHash Hash, FileTreeBlob Tree)>(FileTreeUploadWorkers * 2);

var uploadTasks = Enumerable.Range(0, FileTreeUploadWorkers)
    .Select(_ => Task.Run(async () =>
    {
        await foreach (var upload in pendingUploads.Reader.ReadAllAsync(cancellationToken))
            await _fileTreeService.WriteAsync(upload.Hash, upload.Tree, cancellationToken);
    }, cancellationToken))
    .ToArray();

async Task QueueUploadAsync(FileTreeHash treeHash, FileTreeBlob tree)
{
    if (_fileTreeService.ExistsInRemote(treeHash))
        return;

    await pendingUploads.Writer.WriteAsync((treeHash, tree), cancellationToken);
}
```

Add `using System.Threading.Channels;` at the top of the file.

- [ ] **Step 4: Replace inline upload calls with queueing and draining**

Inside `SynchronizeAsync`, replace `await EnsureUploadedAsync(hash, tree, cancellationToken);` with `await QueueUploadAsync(hash, tree);`.

When all tree hashes have been computed and any needed root tree has been queued, complete and drain the upload workers before returning:

```csharp
pendingUploads.Writer.Complete();
await Task.WhenAll(uploadTasks);
return rootHash;
```

Wrap the method body in `try/catch` so a compute-side exception completes the channel with the exception before awaiting workers:

```csharp
try
{
    // existing synchronize body
}
catch (Exception ex)
{
    pendingUploads.Writer.TryComplete(ex);
    throw;
}
```

Remove the old `EnsureUploadedAsync` helper if it is no longer used.

- [ ] **Step 5: Verify focused filetree tests pass**

Run: `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderTests/*"`

Expected: all `FileTreeBuilderTests` pass.

### Task 4: Avoid Repeated Child Directory Scans

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`

- [ ] **Step 1: Add a nested tree regression test if existing coverage is insufficient**

If no Core test verifies nested directories, add this test to `FileTreeBuilderTests`:

```csharp
[Test]
public async Task SynchronizeAsync_NestedDirectories_ProducesStableRootHash()
{
    var manifestPath = Path.GetTempFileName();
    try
    {
        var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var lines = new[]
        {
            new ManifestEntry("a/b/c/file.txt", FakeContentHash('4'), now, now).Serialize(),
            new ManifestEntry("a/b/other.txt", FakeContentHash('5'), now, now).Serialize(),
            new ManifestEntry("z.txt", FakeContentHash('6'), now, now).Serialize(),
        };
        await File.WriteAllTextAsync(manifestPath, string.Join("\n", lines) + "\n");

        var blobs = new FakeRecordingBlobContainerService();
        var builder = CreateBuilder(blobs, "acc-nested-core", "con-nested-core", out var fileTreeService);
        await fileTreeService.ValidateAsync();

        var root = await builder.SynchronizeAsync(manifestPath);

        root.ShouldNotBeNull();
        blobs.Uploaded.Count.ShouldBeGreaterThanOrEqualTo(1);
    }
    finally
    {
        File.Delete(manifestPath);
        var cacheDir = FileTreeService.GetDiskCacheDirectory("acc-nested-core", "con-nested-core");
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }
}
```

- [ ] **Step 2: Run nested behavior test before refactor**

Run: `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderTests/SynchronizeAsync_NestedDirectories_ProducesStableRootHash"`

Expected: pass before refactor, proving behavior is covered.

- [ ] **Step 3: Replace child scan with parent append**

In `SynchronizeAsync`, remove the loop that scans `dirHashMap` for immediate children:

```csharp
foreach (var (childDirPath, childHash) in dirHashMap)
{
    var childParent = GetDirectoryPath(childDirPath);
    if (childParent == dirPath)
    {
        var childName = GetLastSegment(childDirPath);
        entries.Add(new DirectoryEntry
        {
            Name         = childName + "/",
            FileTreeHash = childHash
        });
    }
}
```

After computing and queueing the current directory, append it directly to the parent entry list:

```csharp
var parent = GetDirectoryPath(dirPath);
if (parent != dirPath && dirEntries.TryGetValue(parent, out var parentEntries))
{
    parentEntries.Add(new DirectoryEntry
    {
        Name         = GetLastSegment(dirPath) + "/",
        FileTreeHash = hash
    });
}
```

Keep `dirHashMap[dirPath] = hash;` only if still needed for root fallback. If the parent append removes all other `dirHashMap` uses except fallback, simplify root detection instead of keeping a redundant dictionary.

- [ ] **Step 4: Run focused filetree tests after refactor**

Run: `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderTests/*"`

Expected: all `FileTreeBuilderTests` pass.

### Task 5: Full Verification And Post-Change Benchmark

**Files:**
- No additional source files unless verification exposes a defect.

- [ ] **Step 1: Run Core filetree tests**

Run: `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderTests/*"`

Expected: pass.

- [ ] **Step 2: Run archive command tests**

Run: `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/ArchiveCommand*/*"`

Expected: pass. If TUnit discovers no tests with that filter, run the whole Core test project instead.

- [ ] **Step 3: Run integration filetree tests when Azurite is available**

Run: `dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderIntegrationTests/*"`

Expected: pass or skip visibly if Azurite/Docker is unavailable.

- [ ] **Step 4: Run Slopwatch after code modifications**

Run the repository's Slopwatch command as described by the `dotnet-slopwatch` skill.

Expected: no reward-hacking findings such as disabled tests, empty catches introduced by this change, suppressed warnings, or skipped verification.

- [ ] **Step 5: Build benchmark project**

Run: `dotnet build "src/Arius.Benchmarks/Arius.Benchmarks.csproj"`

Expected: build succeeds with `0 Warning(s)` and `0 Error(s)`.

- [ ] **Step 6: Run post-change benchmark**

Run: `dotnet run --project "src/Arius.Benchmarks/Arius.Benchmarks.csproj" -c Release -- --raw-output "src/Arius.Benchmarks/raw" --tail-log "src/Arius.Benchmarks/benchmark-tail.md"`

Expected: BenchmarkDotNet exports a new `Arius.Benchmarks.ArchiveStepBenchmarks-report-github.md` file under `src/Arius.Benchmarks/raw/<new-timestamp>/results/`.

- [ ] **Step 7: Compare benchmark results**

Compare the new mean and allocation against baseline `src/Arius.Benchmarks/raw/20260429T130338.969Z/results/Arius.Benchmarks.ArchiveStepBenchmarks-report-github.md`.

Expected: report the before/after mean and allocation. Note that the in-memory benchmark is short and BenchmarkDotNet warned about iteration time, so use it as same-machine directional evidence rather than a precise absolute measurement.

## Self-Review

- Spec coverage: benchmark, rename, duplicate validation removal, bounded parallel uploads, child-scan optimization, and verification are each covered by tasks.
- Placeholder scan: no unresolved implementation placeholders remain.
- Type consistency: method name is consistently `SynchronizeAsync`; benchmark class is consistently `ArchiveStepBenchmarks`; fixture type is `RepositoryTestFixture`.
