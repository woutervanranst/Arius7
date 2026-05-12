# RelativePath Overload Removal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove `string` APIs that semantically represent Arius repository-relative paths, replace them with `RelativePath`, and delete obsolete string helpers such as `RepositoryPathStrings`.

**Architecture:** Keep Arius repository-relative values typed as `RelativePath` from the test call site inward, and only convert to `string` at real foreign boundaries such as `File.*`, `Directory.*`, `Path.*`, or internal dictionary keys inside fakes. Implement the change in narrow slices: first remove obvious duplicate overloads, then convert fixture/helper APIs, then finish E2E workflow helpers and remove `RepositoryPathStrings`.

**Tech Stack:** C#/.NET, TUnit, `dotnet test`, Arius typed filesystem/value objects (`RelativePath`, `LocalDirectory`, `RepositoryPaths`, `BlobPaths`)

---

### Task 1: Remove duplicate `string` overloads for blob and snapshot paths

**Files:**
- Modify: `src/Arius.Tests.Shared/Storage/FakeInMemoryBlobContainerService.cs`
- Modify: `src/Arius.Core.Tests/Fakes/FakeSeededBlobContainerService.cs`
- Modify: `src/Arius.Core/Shared/Snapshot/SnapshotService.cs`
- Modify: `src/Arius.Core.Tests/Shared/Snapshot/SnapshotSerializerTests.cs`
- Modify: `src/Arius.Integration.Tests/Snapshot/SnapshotServiceIntegrationTests.cs`
- Modify: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`
- Test: `src/Arius.Core.Tests/Shared/Snapshot/SnapshotSerializerTests.cs`
- Test: `src/Arius.Integration.Tests/Snapshot/SnapshotServiceIntegrationTests.cs`
- Test: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`

- [ ] **Step 1: Write the failing tests / caller updates first**

Update the snapshot parsing tests and blob-seeding callers to use only `RelativePath` values.

```csharp
// src/Arius.Core.Tests/Shared/Snapshot/SnapshotSerializerTests.cs
[Test]
public void ParseTimestamp_WithoutPrefix_AlsoWorks()
{
    var ts      = new DateTimeOffset(2024, 1, 15, 9, 30, 45, TimeSpan.Zero);
    var rawName = RelativePath.Parse(ts.UtcDateTime.ToString(SnapshotService.TimestampFormat));

    SnapshotService.ParseTimestamp(rawName).ShouldBe(ts);
}

// src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs
blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));
```

- [ ] **Step 2: Run the focused tests to verify they fail for the expected API removals**

Run:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/SnapshotSerializerTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/ListQueryHandlerTests/*"
dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/SnapshotServiceIntegrationTests/*"
```

Expected: compile errors or failing tests where `string` overloads are still referenced.

- [ ] **Step 3: Remove the duplicate overloads with minimal implementation changes**

```csharp
// src/Arius.Tests.Shared/Storage/FakeInMemoryBlobContainerService.cs
public void ThrowAlreadyExistsOnOpenWrite(RelativePath blobName, bool throwOnce = false)
    => _openWriteAlreadyExists[blobName.ToString()] = throwOnce ? 1 : int.MaxValue;

public async Task SeedLargeBlobAsync(RelativePath blobName, byte[] originalContent, BlobTier tier)
{
    var payload = await GzipAsync(originalContent);
    _blobs[blobName.ToString()] = new StoredBlob(
        payload,
        new Dictionary<string, string>
        {
            [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeLarge,
            [BlobMetadataKeys.OriginalSize] = originalContent.Length.ToString(),
            [BlobMetadataKeys.ChunkSize] = payload.Length.ToString(),
        },
        tier,
        ContentTypes.LargePlaintext,
        false);
}

public async Task SeedTarBlobAsync(RelativePath blobName, IReadOnlyList<byte[]> originalContents, BlobTier tier)
{
    var combined = originalContents.SelectMany(bytes => bytes).ToArray();
    var payload  = await GzipAsync(combined);
    _blobs[blobName.ToString()] = new StoredBlob(
        payload,
        new Dictionary<string, string>
        {
            [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeTar,
            [BlobMetadataKeys.ChunkSize] = payload.Length.ToString(),
        },
        tier,
        ContentTypes.TarPlaintext,
        false);
}

// src/Arius.Core.Tests/Fakes/FakeSeededBlobContainerService.cs
public void AddBlob(RelativePath blobName, byte[] content) => _blobs[blobName.ToString()] = content;

// src/Arius.Core/Shared/Snapshot/SnapshotService.cs
public static DateTimeOffset ParseTimestamp(RelativePath blobName)
{
    var name = GetSnapshotFileName(blobName);
    return DateTimeOffset.ParseExact(name.ToString(), TimestampFormat, null,
        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
}
```

- [ ] **Step 4: Run the focused tests to verify they pass**

Run:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/SnapshotSerializerTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/ListQueryHandlerTests/*"
dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/SnapshotServiceIntegrationTests/*"
```

Expected: all three classes pass.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Tests.Shared/Storage/FakeInMemoryBlobContainerService.cs src/Arius.Core.Tests/Fakes/FakeSeededBlobContainerService.cs src/Arius.Core/Shared/Snapshot/SnapshotService.cs src/Arius.Core.Tests/Shared/Snapshot/SnapshotSerializerTests.cs src/Arius.Integration.Tests/Snapshot/SnapshotServiceIntegrationTests.cs src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs
git commit -m "refactor: remove string blob path overloads"
```

### Task 2: Remove `RepositoryPathStrings` and switch cache-path callers to typed `RepositoryPaths`

**Files:**
- Delete: `src/Arius.Tests.Shared/RepositoryPathStrings.cs`
- Modify: `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs`
- Modify: `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`
- Modify: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs`
- Modify: `src/Arius.Core.Tests/Shared/RepositoryPathsTests.cs`
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs`
- Modify: `src/Arius.Integration.Tests/Shared/FileTree/FileTreeBuilderIntegrationTests.cs`
- Modify: `src/Arius.Integration.Tests/ChunkIndex/ChunkIndexServiceIntegrationTests.cs`
- Test: `src/Arius.Core.Tests/Shared/RepositoryPathsTests.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs`
- Test: `src/Arius.Integration.Tests/Shared/FileTree/FileTreeBuilderIntegrationTests.cs`
- Test: `src/Arius.Integration.Tests/ChunkIndex/ChunkIndexServiceIntegrationTests.cs`

- [ ] **Step 1: Update tests first to assert through `RepositoryPaths` directly**

```csharp
// src/Arius.Core.Tests/Shared/RepositoryPathsTests.cs
var root = RepositoryPaths.GetRepositoryRoot("account", "container").ToString();
RepositoryPaths.GetChunkIndexCacheRoot("account", "container").ToString()
    .ShouldBe(Path.Combine(root, "chunk-index"));
RepositoryPaths.GetFileTreeCacheRoot("account", "container").ToString()
    .ShouldBe(Path.Combine(root, "filetrees"));
RepositoryPaths.GetSnapshotCacheRoot("account", "container").ToString()
    .ShouldBe(Path.Combine(root, "snapshots"));
```

- [ ] **Step 2: Run the focused tests to verify they fail before implementation**

Run:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/RepositoryPathsTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeServiceTests/*"
dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderIntegrationTests/*"
dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/ChunkIndexServiceIntegrationTests/*"
```

Expected: compile failures on `RepositoryPathStrings` references.

- [ ] **Step 3: Remove the helper and convert callers to final-boundary `.ToString()` usage**

```csharp
// src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs
var cacheDir = RepositoryPaths.GetRepositoryRoot(accountName, containerName).ToString();

// src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs
Directory.CreateDirectory(RepositoryPaths.GetChunkIndexCacheRoot(AccountName, _containerName).ToString());
Directory.CreateDirectory(RepositoryPaths.GetFileTreeCacheRoot(AccountName, _containerName).ToString());

public string FileTreeCacheDirectory => RepositoryPaths.GetFileTreeCacheRoot(AccountName, _containerName).ToString();

// src/Arius.Integration.Tests/ChunkIndex/ChunkIndexServiceIntegrationTests.cs
var l2Path = Path.Combine(RepositoryPaths.GetChunkIndexCacheRoot(Account, containerName).ToString(), prefix.ToString());
```

- [ ] **Step 4: Run the focused tests to verify they pass**

Run the same commands from Step 2.

Expected: all targeted test classes pass and `RepositoryPathStrings` is no longer referenced.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs src/Arius.E2E.Tests/Fixtures/E2EFixture.cs src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs src/Arius.Core.Tests/Shared/RepositoryPathsTests.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs src/Arius.Integration.Tests/Shared/FileTree/FileTreeBuilderIntegrationTests.cs src/Arius.Integration.Tests/ChunkIndex/ChunkIndexServiceIntegrationTests.cs src/Arius.Tests.Shared/RepositoryPathStrings.cs
git commit -m "refactor: remove RepositoryPathStrings helper"
```

### Task 3: Convert repository test fixtures and direct test callers to `RelativePath`

**Files:**
- Modify: `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
- Modify: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`
- Modify: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs`
- Modify: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs`
- Test: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`
- Test: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs`

- [ ] **Step 1: Update the focused Core tests first to call the typed APIs**

```csharp
// src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs
fixture.WriteFile(RelativePath.Parse("archives/duplicates/binary-a.bin"), content);
fixture.ReadRestored(RelativePath.Parse("archives/duplicates/binary-a.bin")).ShouldBe(content);
fixture.RestoredExists(RelativePath.Parse("photos/pic.jpg")).ShouldBeTrue();

// src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs
var content = env.WriteRandomFile(RelativePath.Parse("large.bin"), 2 * 1024 * 1024);
env.WriteRandomFile(RelativePath.Parse("docs/readme.txt"), 1024);
```

- [ ] **Step 2: Run the focused Core tests to verify they fail**

Run:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/RestoreCommandHandlerTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/ArchiveRecoveryTests/*"
```

Expected: compile failures where fixture APIs still accept `string`.

- [ ] **Step 3: Convert the fixture and environment APIs to `RelativePath`**

```csharp
// src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs
public string WriteFile(RelativePath relativePath, byte[] content)
{
    var full = CombineValidatedRelativePath(LocalRoot, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllBytes(full, content);
    return full;
}

public string WriteFile(RelativePath relativePath, byte[] content, DateTime created, DateTime modified)
{
    var full = WriteFile(relativePath, content);
    File.SetCreationTimeUtc(full, created);
    File.SetLastWriteTimeUtc(full, modified);
    return full;
}

public byte[] ReadRestored(RelativePath relativePath)
    => File.ReadAllBytes(CombineValidatedRelativePath(RestoreRoot, relativePath));

public bool RestoredExists(RelativePath relativePath)
    => File.Exists(CombineValidatedRelativePath(RestoreRoot, relativePath));

private static string CombineValidatedRelativePath(string root, RelativePath relativePath)
{
    var combined       = Path.GetFullPath(Path.Combine(root, relativePath.ToString().Replace('/', Path.DirectorySeparatorChar)));
    var normalizedRoot = Path.GetFullPath(root);

    if (!combined.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
        !string.Equals(combined, normalizedRoot, StringComparison.Ordinal))
    {
        throw new ArgumentOutOfRangeException(nameof(relativePath), "Path must stay within the fixture root.");
    }

    return combined;
}

// src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs
public byte[] WriteRandomFile(RelativePath relativePath, int sizeBytes)
{
    var content  = new byte[sizeBytes];
    var fullPath = Path.Combine(_rootDirectory, relativePath.ToString().Replace('/', Path.DirectorySeparatorChar));
    Random.Shared.NextBytes(content);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    File.WriteAllBytes(fullPath, content);
    return content;
}
```

- [ ] **Step 4: Run the focused Core tests to verify they pass**

Run the same commands from Step 2.

Expected: both classes pass with only typed repository-relative path usage.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs
git commit -m "refactor: type core test fixture paths"
```

### Task 4: Convert pipeline and E2E fixture APIs and their callers to `RelativePath`

**Files:**
- Modify: `src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs`
- Modify: `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RestorePointerTimestampTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RestoreDispositionTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RecoveryScriptTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RehydrationStateTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/ContainerCreationTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/ListQueryIntegrationTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/GcmIntegrationTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RestoreCostModelTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/CrashRecoveryTests.cs`
- Modify: `src/Arius.E2E.Tests/E2ETests.cs`
- Test: `src/Arius.Integration.Tests/Pipeline/*.cs`
- Test: `src/Arius.E2E.Tests/E2ETests.cs`

- [ ] **Step 1: Update a small integration caller set first so the fixture signatures drive failures**

Start with the smallest representative callers before sweeping all of them.

```csharp
// src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs
fix.WriteFile(RelativePath.Parse("big.bin"), original);
fix.ReadRestored(RelativePath.Parse("big.bin")).ShouldBe(original);
fix.WriteRandomFile(RelativePath.Parse($"file{i:D2}.bin"), sizeBytes: 512);

// src/Arius.E2E.Tests/E2ETests.cs
fixture.WriteFile(RelativePath.Parse("hot.bin"), content);
fixture.ReadRestored(RelativePath.Parse("hot.bin")).ShouldBe(content);
```

- [ ] **Step 2: Run focused integration and E2E tests to verify they fail**

Run:

```bash
dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/RoundtripTests/*"
dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/RestoreDispositionTests/*"
dotnet test --project src/Arius.E2E.Tests/Arius.E2E.Tests.csproj --treenode-filter "/*/*/E2ETests/*"
```

Expected: compile failures where `PipelineFixture` and `E2EFixture` still expose `string` repository-relative helpers.

- [ ] **Step 3: Convert fixture APIs and sweep the callers mechanically**

```csharp
// src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs
public string WriteFile(RelativePath relativePath, byte[] content)
    => _repository.WriteFile(relativePath, content);

public string WriteRandomFile(RelativePath relativePath, int sizeBytes)
{
    var bytes = new byte[sizeBytes];
    Random.Shared.NextBytes(bytes);
    return WriteFile(relativePath, bytes);
}

public byte[] ReadRestored(RelativePath relativePath)
    => _repository.ReadRestored(relativePath);

public bool RestoredExists(RelativePath relativePath)
    => _repository.RestoredExists(relativePath);

// src/Arius.E2E.Tests/Fixtures/E2EFixture.cs
public string WriteFile(RelativePath relativePath, byte[] content)
    => _repository.WriteFile(relativePath, content);

public byte[] ReadRestored(RelativePath relativePath)
    => _repository.ReadRestored(relativePath);

public bool RestoredExists(RelativePath relativePath)
    => _repository.RestoredExists(relativePath);
```

Then sweep all remaining callers under:

```text
src/Arius.Integration.Tests/Pipeline/
src/Arius.E2E.Tests/E2ETests.cs
```

Use `RelativePath.Parse(...)` at call sites and keep loops/local string variables only where they are still genuine text data.

- [ ] **Step 4: Run the focused integration and E2E tests to verify they pass**

Run:

```bash
dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/RoundtripTests/*"
dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/RestoreDispositionTests/*"
dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/RestorePointerTimestampTests/*"
dotnet test --project src/Arius.E2E.Tests/Arius.E2E.Tests.csproj --treenode-filter "/*/*/E2ETests/*"
```

Expected: the selected integration/E2E classes pass with typed fixture paths.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs src/Arius.E2E.Tests/Fixtures/E2EFixture.cs src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs src/Arius.Integration.Tests/Pipeline/RestorePointerTimestampTests.cs src/Arius.Integration.Tests/Pipeline/RestoreDispositionTests.cs src/Arius.Integration.Tests/Pipeline/RecoveryScriptTests.cs src/Arius.Integration.Tests/Pipeline/RehydrationStateTests.cs src/Arius.Integration.Tests/Pipeline/ContainerCreationTests.cs src/Arius.Integration.Tests/Pipeline/ListQueryIntegrationTests.cs src/Arius.Integration.Tests/Pipeline/GcmIntegrationTests.cs src/Arius.Integration.Tests/Pipeline/RestoreCostModelTests.cs src/Arius.Integration.Tests/Pipeline/CrashRecoveryTests.cs src/Arius.E2E.Tests/E2ETests.cs
git commit -m "refactor: type pipeline and e2e fixture paths"
```

### Task 5: Convert remaining E2E repository-relative helpers to `RelativePath`

**Files:**
- Modify: `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializer.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/Helpers.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`
- Modify: any immediate callers revealed by compile errors in `src/Arius.E2E.Tests/Workflows/`
- Test: `src/Arius.E2E.Tests/Workflows/Steps/*.cs`

- [ ] **Step 1: Update one E2E helper caller first to express the intended typed boundary**

```csharp
// src/Arius.E2E.Tests/Workflows/Steps/Helpers.cs
var contentHashA = await ComputeContentHashAsync(state, RelativePath.Parse(pathA), cancellationToken);

// src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs
var targetChunk = await IdentifyTargetTarChunkAsync(state.Fixture, RelativePath.Parse(TargetPath), cancellationToken);
```

- [ ] **Step 2: Run the focused E2E tests or build to verify they fail first**

Run:

```bash
dotnet test --project src/Arius.E2E.Tests/Arius.E2E.Tests.csproj --treenode-filter "/*/*/E2ETests/*"
```

Expected: compile failures or failing tests around the remaining string-based repository-relative helper signatures.

- [ ] **Step 3: Convert the helper APIs, keeping host roots as `string` and repository-relative inputs as `RelativePath`**

```csharp
// src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializer.cs
static string GetFullPath(string rootPath, RelativePath relativePath)
    => Path.Combine(rootPath, relativePath.ToString().Replace('/', Path.DirectorySeparatorChar));

static async Task WriteFileAsync(string rootPath, RelativePath relativePath, byte[] bytes)
{
    var fullPath = GetFullPath(rootPath, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    await File.WriteAllBytesAsync(fullPath, bytes);
}

// src/Arius.E2E.Tests/Workflows/Steps/Helpers.cs
static async Task<ContentHash> ComputeContentHashAsync(RepresentativeWorkflowState state, RelativePath relativePath, CancellationToken cancellationToken)
{
    var fullPath = E2EFixture.CombineValidatedRelativePath(state.Fixture.LocalRoot, relativePath);
    await using var file = File.OpenRead(fullPath);
    return await state.Fixture.Encryption.ComputeHashAsync(file, cancellationToken);
}

// src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs
static async Task<ArchiveTierTargetChunk> IdentifyTargetTarChunkAsync(E2EFixture fixture, RelativePath targetPath, CancellationToken cancellationToken)
{
    var targetRoot = E2EFixture.CombineValidatedRelativePath(fixture.LocalRoot, targetPath);
    // existing enumeration logic remains the same
}
```

- [ ] **Step 4: Run the focused E2E test/build verification to verify it passes**

Run:

```bash
dotnet test --project src/Arius.E2E.Tests/Arius.E2E.Tests.csproj --treenode-filter "/*/*/E2ETests/*"
```

Expected: compilation succeeds and the selected E2E class still passes. If runtime cost is too high in the current environment, a successful compile plus the narrower affected test class is the minimum acceptable evidence.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializer.cs src/Arius.E2E.Tests/Workflows/Steps/Helpers.cs src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs src/Arius.E2E.Tests/Workflows
git commit -m "refactor: type e2e relative path helpers"
```

### Task 6: Full verification and cleanup

**Files:**
- Modify: any remaining touched files from earlier tasks if verification reveals missed callers
- Test: `src/Arius.Core.Tests/Arius.Core.Tests.csproj`
- Test: `src/Arius.Integration.Tests/Arius.Integration.Tests.csproj`
- Test: `src/Arius.E2E.Tests/Arius.E2E.Tests.csproj` if feasible for the touched scope

- [ ] **Step 1: Search for any remaining targeted string helpers or duplicate overloads**

Run:

```bash
rg "RepositoryPathStrings|ParseTimestamp\(string|AddBlob\(string|ThrowAlreadyExistsOnOpenWrite\(string|SeedLargeBlobAsync\(string|SeedTarBlobAsync\(string" src
rg "WriteFile\(string relativePath|WriteRandomFile\(string relativePath|ReadRestored\(string relativePath|RestoredExists\(string relativePath" src
```

Expected: no matches in the targeted Arius repository-relative helper areas.

- [ ] **Step 2: Run the main Core test project**

Run:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj -p:UseAppHost=false
```

Expected: all Core tests pass.

- [ ] **Step 3: Run the integration test project**

Run:

```bash
dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj -p:UseAppHost=false
```

Expected: integration tests pass or skip appropriately for environment-dependent cases.

- [ ] **Step 4: Run E2E verification if the touched changes require it and the environment supports it**

Run:

```bash
dotnet test --project src/Arius.E2E.Tests/Arius.E2E.Tests.csproj -p:UseAppHost=false
```

Expected: E2E tests pass or clearly skip where backend prerequisites are unavailable.

- [ ] **Step 5: Final commit**

```bash
git add src docs/superpowers/specs/2026-05-12-relativepath-overload-removal-design.md docs/superpowers/plans/2026-05-12-relativepath-overload-removal.md
git commit -m "refactor: remove string relative path helpers"
```
