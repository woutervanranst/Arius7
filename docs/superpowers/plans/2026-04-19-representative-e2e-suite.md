# Representative E2E Suite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a deterministic, representative `Arius.E2E.Tests` suite that runs the same core archive/restore scenarios against Azurite and Azure, with Azure-only archive-tier scenarios split by backend capability.

**Architecture:** Add a manifest-driven synthetic repository generator with explicit `V1` and `V2` versions, refactor the E2E backend setup behind a shared test-backend interface, and drive scenario tests from a declarative scenario matrix that controls dataset version, cache warmth, and backend requirements. Keep archive-tier and rehydration tests capability-gated so Azurite and Azure share as much code as possible without faking Azure semantics.

**Tech Stack:** .NET 10, TUnit, Azure Blob SDK, Testcontainers Azurite, existing Arius Core/AzureBlob services

---

## File Structure

**Create**
- `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryProfile.cs`
  - Named dataset profiles such as `Small` and `Representative`.
- `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryVersion.cs`
  - Dataset version enum for `V1` and `V2`.
- `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryDefinition.cs`
  - Declarative golden dataset definition and mutation plan.
- `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryDefinitionFactory.cs`
  - Builds the fixed dataset shape for a given profile.
- `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializer.cs`
  - Writes deterministic bytes and applies version mutations to disk.
- `src/Arius.E2E.Tests/Datasets/RepositoryTreeSnapshot.cs`
  - Captures expected file-path-to-bytes metadata for assertions.
- `src/Arius.E2E.Tests/Datasets/RepositoryTreeAssertions.cs`
  - Whole-tree equality helpers for restore verification.
- `src/Arius.E2E.Tests/Fixtures/IE2EStorageBackend.cs`
  - Common backend interface for Azurite and Azure fixtures.
- `src/Arius.E2E.Tests/Fixtures/AzuriteE2EBackendFixture.cs`
  - Shared Azurite-backed implementation.
- `src/Arius.E2E.Tests/Fixtures/AzureE2EBackendFixture.cs`
  - Shared Azure-backed implementation, evolving from the current `AzureFixture`.
- `src/Arius.E2E.Tests/Fixtures/E2EStorageBackendContext.cs`
  - Carries `IBlobContainerService`, account/container names, optional concrete Azure handles/capabilities, and cleanup callback.
- `src/Arius.E2E.Tests/Fixtures/E2EBackendCapabilities.cs`
  - Declares whether a backend supports real archive-tier and rehydration semantics.
- `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioDefinition.cs`
  - Declarative scenario model for version, cache, backend requirement, and operation.
- `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioCatalog.cs`
  - The approved core scenario list.
- `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioRunner.cs`
  - Shared harness for archive and restore scenarios.
- `src/Arius.E2E.Tests/RepresentativeArchiveRestoreTests.cs`
  - Shared scenario tests running against both backends.
- `src/Arius.E2E.Tests/ArchiveTierRepresentativeTests.cs`
  - Azure-capability-only archive-tier planning and rehydration scenarios.

**Modify**
- `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`
  - Remove creation-time dependence on concrete Azure SDK types where possible.
  - Add explicit cache reset and preserve operations plus source dataset materialization hooks.
- `src/Arius.E2E.Tests/Fixtures/AzureFixture.cs`
  - Convert or replace with backend interface implementation.
- `src/Arius.E2E.Tests/E2ETests.cs`
  - Replace one-off file tests with scenario-driven representative tests or retire them if fully superseded.
- `src/Arius.E2E.Tests/RehydrationE2ETests.cs`
  - Move Azure-only behavior into capability-gated representative archive-tier tests.
- `src/Arius.E2E.Tests/Services/CopyTrackingBlobService.cs`
  - Decide whether it should wrap a backend capability abstraction or remain Azure-only and be used only in Azure-capability tests.
- `src/Arius.E2E.Tests/Arius.E2E.Tests.csproj`
  - Add project or package references if Azurite fixture code is reused directly from integration tests or a shared helper is introduced.
- `README.md`
  - Document the new representative E2E suite, backend selection, and Azure opt-in behavior in human terms.
- `AGENTS.md`
  - Document the test architecture expectations for deterministic datasets, shared backends, and scenario contracts.

**Test/Read During Implementation**
- `src/Arius.Integration.Tests/Storage/AzuriteFixture.cs`
- `src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs`
- `src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs`
- `src/Arius.Integration.Tests/Pipeline/RestoreDispositionTests.cs`
- `src/Arius.Core/Features/ArchiveCommand/ArchiveCommand.cs`
- `src/Arius.Core/Features/RestoreCommand/RestoreCommand.cs`

### Task 1: Lock Down the Dataset Contract in Tests

**Files:**
- Create: `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryProfile.cs`
- Create: `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryVersion.cs`
- Create: `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryDefinition.cs`
- Create: `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryDefinitionFactory.cs`
- Test: `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryDefinitionFactoryTests.cs`

- [ ] **Step 1: Write the failing tests for dataset shape and mutation intent**

```csharp
namespace Arius.E2E.Tests.Datasets;

public class SyntheticRepositoryDefinitionFactoryTests
{
    [Test]
    public async Task Representative_Profile_ContainsExpectedMix()
    {
        var definition = SyntheticRepositoryDefinitionFactory.Create(
            SyntheticRepositoryProfile.Representative);

        definition.RootDirectories.ShouldContain("docs");
        definition.RootDirectories.ShouldContain("media");
        definition.RootDirectories.ShouldContain("src");

        definition.Files.Count.ShouldBeGreaterThan(1000);
        definition.Files.Any(x => x.SizeBytes < definition.SmallFileThresholdBytes).ShouldBeTrue();
        definition.Files.Any(x => x.SizeBytes > definition.SmallFileThresholdBytes).ShouldBeTrue();
        definition.Files.Count(x => x.ContentId is not null).ShouldBeGreaterThan(0);
        definition.Files.Select(x => x.Path).Distinct().Count().ShouldBe(definition.Files.Count);
    }

    [Test]
    public async Task Representative_Profile_Defines_V2_MixedChanges()
    {
        var definition = SyntheticRepositoryDefinitionFactory.Create(
            SyntheticRepositoryProfile.Representative);

        definition.V2Mutations.Any(x => x.Kind == SyntheticMutationKind.Add).ShouldBeTrue();
        definition.V2Mutations.Any(x => x.Kind == SyntheticMutationKind.Delete).ShouldBeTrue();
        definition.V2Mutations.Any(x => x.Kind == SyntheticMutationKind.Rename).ShouldBeTrue();
        definition.V2Mutations.Any(x => x.Kind == SyntheticMutationKind.ChangeContent).ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/SyntheticRepositoryDefinitionFactoryTests/*"`
Expected: FAIL because the dataset contract types do not exist yet.

- [ ] **Step 3: Write the minimal dataset contract types**

```csharp
namespace Arius.E2E.Tests.Datasets;

internal enum SyntheticRepositoryProfile
{
    Small,
    Representative,
}

internal enum SyntheticRepositoryVersion
{
    V1,
    V2,
}

internal enum SyntheticMutationKind
{
    Add,
    Delete,
    Rename,
    ChangeContent,
}

internal sealed record SyntheticFileDefinition(
    string Path,
    long SizeBytes,
    string? ContentId);

internal sealed record SyntheticMutation(
    SyntheticMutationKind Kind,
    string Path,
    string? TargetPath = null,
    string? ReplacementContentId = null);

internal sealed record SyntheticRepositoryDefinition(
    int SmallFileThresholdBytes,
    IReadOnlyList<string> RootDirectories,
    IReadOnlyList<SyntheticFileDefinition> Files,
    IReadOnlyList<SyntheticMutation> V2Mutations);

internal static class SyntheticRepositoryDefinitionFactory
{
    public static SyntheticRepositoryDefinition Create(SyntheticRepositoryProfile profile)
    {
        return profile switch
        {
            SyntheticRepositoryProfile.Small => CreateSmall(),
            SyntheticRepositoryProfile.Representative => CreateRepresentative(),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
    }

    static SyntheticRepositoryDefinition CreateSmall() => throw new NotImplementedException();
    static SyntheticRepositoryDefinition CreateRepresentative() => throw new NotImplementedException();
}
```

- [ ] **Step 4: Expand the factory with a fixed representative shape**

```csharp
static SyntheticRepositoryDefinition CreateRepresentative()
{
    const int threshold = 256 * 1024;

    var files = new List<SyntheticFileDefinition>();
    var roots = new[] { "docs", "media", "src", "archives", "nested" };

    for (var i = 0; i < 1600; i++)
    {
        files.Add(new SyntheticFileDefinition(
            $"src/module-{i % 40:D2}/group-{i % 7:D2}/file-{i:D4}.bin",
            4 * 1024 + (i % 16) * 1024,
            $"small-{i % 220:D3}"));
    }

    for (var i = 0; i < 380; i++)
    {
        files.Add(new SyntheticFileDefinition(
            $"docs/batch-{i % 12:D2}/doc-{i:D4}.txt",
            180 * 1024 + (i % 8) * 4096,
            $"edge-{i % 90:D3}"));
    }

    files.Add(new SyntheticFileDefinition("media/video/master-a.bin", 48 * 1024 * 1024, "large-001"));
    files.Add(new SyntheticFileDefinition("media/video/master-b.bin", 72 * 1024 * 1024, "large-002"));
    files.Add(new SyntheticFileDefinition("archives/duplicates/copy-a.bin", 512 * 1024, "dup-001"));
    files.Add(new SyntheticFileDefinition("nested/deep/a/b/c/d/e/f/copy-b.bin", 512 * 1024, "dup-001"));
    files.Add(new SyntheticFileDefinition("nested/deep/a/b/c/d/e/f/g/h/copy-c.bin", 512 * 1024, "dup-001"));

    var mutations = new List<SyntheticMutation>
    {
        new(SyntheticMutationKind.ChangeContent, "src/module-00/group-00/file-0000.bin", ReplacementContentId: "small-updated-000"),
        new(SyntheticMutationKind.Delete, "docs/batch-00/doc-0000.txt"),
        new(SyntheticMutationKind.Rename, "archives/duplicates/copy-a.bin", TargetPath: "archives/duplicates/copy-a-renamed.bin"),
        new(SyntheticMutationKind.Add, "src/module-99/group-00/new-file-0000.bin", ReplacementContentId: "new-000"),
    };

    return new SyntheticRepositoryDefinition(threshold, roots, files, mutations);
}
```

- [ ] **Step 5: Add the `Small` profile**

```csharp
static SyntheticRepositoryDefinition CreateSmall()
{
    const int threshold = 256 * 1024;

    return new SyntheticRepositoryDefinition(
        threshold,
        new[] { "docs", "media", "src" },
        new[]
        {
            new SyntheticFileDefinition("src/simple/a.bin", 8 * 1024, "small-001"),
            new SyntheticFileDefinition("src/simple/b.bin", 8 * 1024, "small-001"),
            new SyntheticFileDefinition("docs/readme.txt", 32 * 1024, "small-002"),
            new SyntheticFileDefinition("media/large.bin", 2 * 1024 * 1024, "large-001"),
        },
        new[]
        {
            new SyntheticMutation(SyntheticMutationKind.ChangeContent, "docs/readme.txt", ReplacementContentId: "small-003"),
            new SyntheticMutation(SyntheticMutationKind.Add, "src/simple/c.bin", ReplacementContentId: "small-004"),
        });
}
```

- [ ] **Step 6: Run the tests again**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/SyntheticRepositoryDefinitionFactoryTests/*"`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/Arius.E2E.Tests/Datasets/SyntheticRepositoryProfile.cs \
  src/Arius.E2E.Tests/Datasets/SyntheticRepositoryVersion.cs \
  src/Arius.E2E.Tests/Datasets/SyntheticRepositoryDefinition.cs \
  src/Arius.E2E.Tests/Datasets/SyntheticRepositoryDefinitionFactory.cs \
  src/Arius.E2E.Tests/Datasets/SyntheticRepositoryDefinitionFactoryTests.cs
git commit -m "test: define representative E2E dataset contract"
```

### Task 2: Materialize Deterministic V1 and V2 Trees

**Files:**
- Create: `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializer.cs`
- Create: `src/Arius.E2E.Tests/Datasets/RepositoryTreeSnapshot.cs`
- Test: `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializerTests.cs`

- [ ] **Step 1: Write the failing tests for determinism and mutation behavior**

```csharp
namespace Arius.E2E.Tests.Datasets;

public class SyntheticRepositoryMaterializerTests
{
    [Test]
    public async Task Materialize_V1_Twice_WithSameSeed_ProducesSameTree()
    {
        var definition = SyntheticRepositoryDefinitionFactory.Create(
            SyntheticRepositoryProfile.Small);

        var leftRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var rightRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var left = await SyntheticRepositoryMaterializer.MaterializeAsync(
                definition, SyntheticRepositoryVersion.V1, seed: 12345, leftRoot);
            var right = await SyntheticRepositoryMaterializer.MaterializeAsync(
                definition, SyntheticRepositoryVersion.V1, seed: 12345, rightRoot);

            left.Files.ShouldBe(right.Files);
        }
        finally
        {
            if (Directory.Exists(leftRoot)) Directory.Delete(leftRoot, recursive: true);
            if (Directory.Exists(rightRoot)) Directory.Delete(rightRoot, recursive: true);
        }
    }

    [Test]
    public async Task Materialize_V2_AppliesConfiguredMutations()
    {
        var definition = SyntheticRepositoryDefinitionFactory.Create(
            SyntheticRepositoryProfile.Small);

        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var snapshot = await SyntheticRepositoryMaterializer.MaterializeAsync(
                definition, SyntheticRepositoryVersion.V2, seed: 12345, root);

            snapshot.Files.Keys.ShouldContain("src/simple/c.bin");
            snapshot.Files.Keys.ShouldContain("docs/readme.txt");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/SyntheticRepositoryMaterializerTests/*"`
Expected: FAIL because the materializer and snapshot types do not exist yet.

- [ ] **Step 3: Add the snapshot model and deterministic byte generator**

```csharp
namespace Arius.E2E.Tests.Datasets;

internal sealed record RepositoryTreeSnapshot(
    IReadOnlyDictionary<string, string> Files);

internal static class SyntheticRepositoryMaterializer
{
    public static async Task<RepositoryTreeSnapshot> MaterializeAsync(
        SyntheticRepositoryDefinition definition,
        SyntheticRepositoryVersion version,
        int seed,
        string rootPath)
    {
        Directory.CreateDirectory(rootPath);

        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in definition.Files)
        {
            var bytes = CreateBytes(seed, file.ContentId ?? file.Path, file.SizeBytes);
            var fullPath = Path.Combine(rootPath, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, bytes);
            files[file.Path] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        }

        if (version == SyntheticRepositoryVersion.V2)
            await ApplyV2MutationsAsync(definition, seed, rootPath, files);

        return new RepositoryTreeSnapshot(files);
    }

    static byte[] CreateBytes(int seed, string contentId, long sizeBytes)
    {
        var result = new byte[sizeBytes];
        var random = new Random(HashCode.Combine(seed, contentId));
        random.NextBytes(result);
        return result;
    }

    static async Task ApplyV2MutationsAsync(
        SyntheticRepositoryDefinition definition,
        int seed,
        string rootPath,
        Dictionary<string, string> files)
    {
        foreach (var mutation in definition.V2Mutations)
        {
        }

        await Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Implement V2 mutation application**

```csharp
static async Task ApplyV2MutationsAsync(
    SyntheticRepositoryDefinition definition,
    int seed,
    string rootPath,
    Dictionary<string, string> files)
{
    foreach (var mutation in definition.V2Mutations)
    {
        var sourcePath = Path.Combine(rootPath, mutation.Path.Replace('/', Path.DirectorySeparatorChar));

        switch (mutation.Kind)
        {
            case SyntheticMutationKind.Delete:
                if (File.Exists(sourcePath))
                    File.Delete(sourcePath);
                files.Remove(mutation.Path);
                break;

            case SyntheticMutationKind.Rename:
                var targetPath = Path.Combine(rootPath, mutation.TargetPath!.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Move(sourcePath, targetPath);
                var existingHash = files[mutation.Path];
                files.Remove(mutation.Path);
                files[mutation.TargetPath!] = existingHash;
                break;

            case SyntheticMutationKind.ChangeContent:
            case SyntheticMutationKind.Add:
                var writePath = sourcePath;
                Directory.CreateDirectory(Path.GetDirectoryName(writePath)!);
                var original = definition.Files.FirstOrDefault(x => x.Path == mutation.Path);
                var size = original?.SizeBytes ?? 16 * 1024;
                var bytes = CreateBytes(seed, mutation.ReplacementContentId!, size);
                await File.WriteAllBytesAsync(writePath, bytes);
                files[mutation.Path] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}
```

- [ ] **Step 5: Tighten tests to assert changed content precisely**

```csharp
var v1Root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
try
{
    var v1 = await SyntheticRepositoryMaterializer.MaterializeAsync(
        definition, SyntheticRepositoryVersion.V1, 12345, v1Root);

    snapshot.Files["docs/readme.txt"].ShouldNotBe(v1.Files["docs/readme.txt"]);
}
finally
{
    if (Directory.Exists(v1Root)) Directory.Delete(v1Root, recursive: true);
}
```

- [ ] **Step 6: Run tests again**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/SyntheticRepositoryMaterializerTests/*"`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializer.cs \
  src/Arius.E2E.Tests/Datasets/RepositoryTreeSnapshot.cs \
  src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializerTests.cs
git commit -m "test: materialize deterministic E2E datasets"
```

### Task 3: Add Whole-Tree Restore Assertions

**Files:**
- Create: `src/Arius.E2E.Tests/Datasets/RepositoryTreeAssertions.cs`
- Test: `src/Arius.E2E.Tests/Datasets/RepositoryTreeAssertionsTests.cs`

- [ ] **Step 1: Write the failing test for whole-tree comparisons**

```csharp
namespace Arius.E2E.Tests.Datasets;

public class RepositoryTreeAssertionsTests
{
    [Test]
    public async Task AssertMatchesDiskTree_Succeeds_ForEquivalentTree()
    {
        var definition = SyntheticRepositoryDefinitionFactory.Create(
            SyntheticRepositoryProfile.Small);

        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var snapshot = await SyntheticRepositoryMaterializer.MaterializeAsync(
                definition, SyntheticRepositoryVersion.V1, 12345, root);

            await RepositoryTreeAssertions.AssertMatchesDiskTreeAsync(snapshot, root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/RepositoryTreeAssertionsTests/*"`
Expected: FAIL because the assertion helper does not exist.

- [ ] **Step 3: Implement the minimal whole-tree assertion helper**

```csharp
namespace Arius.E2E.Tests.Datasets;

internal static class RepositoryTreeAssertions
{
    public static async Task AssertMatchesDiskTreeAsync(
        RepositoryTreeSnapshot expected,
        string rootPath)
    {
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(rootPath, filePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var bytes = await File.ReadAllBytesAsync(filePath);
            actual[relativePath] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        }

        actual.ShouldBe(expected.Files);
    }
}
```

- [ ] **Step 4: Run test again**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/RepositoryTreeAssertionsTests/*"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.E2E.Tests/Datasets/RepositoryTreeAssertions.cs \
  src/Arius.E2E.Tests/Datasets/RepositoryTreeAssertionsTests.cs
git commit -m "test: add whole-tree E2E assertions"
```

### Task 4: Introduce a Swappable Backend Interface

**Files:**
- Create: `src/Arius.E2E.Tests/Fixtures/IE2EStorageBackend.cs`
- Create: `src/Arius.E2E.Tests/Fixtures/E2EStorageBackendContext.cs`
- Create: `src/Arius.E2E.Tests/Fixtures/E2EBackendCapabilities.cs`
- Modify: `src/Arius.E2E.Tests/Fixtures/AzureFixture.cs`
- Create: `src/Arius.E2E.Tests/Fixtures/AzuriteE2EBackendFixture.cs`
- Test: `src/Arius.E2E.Tests/Fixtures/E2EStorageBackendFixtureTests.cs`

- [ ] **Step 1: Write the failing test for backend context shape**

```csharp
namespace Arius.E2E.Tests.Fixtures;

public class E2EStorageBackendFixtureTests
{
    [Test]
    public async Task Azure_Backend_Context_ReportsArchiveCapability()
    {
        await using var backend = new AzureE2EBackendFixture();
        await backend.InitializeAsync();

        var context = await backend.CreateContextAsync();

        context.Capabilities.SupportsArchiveTier.ShouldBeTrue();
        await context.DisposeAsync();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/E2EStorageBackendFixtureTests/*"`
Expected: FAIL because the backend abstraction does not exist.

- [ ] **Step 3: Add the common backend interface and context types**

```csharp
namespace Arius.E2E.Tests.Fixtures;

internal sealed record E2EBackendCapabilities(
    bool SupportsArchiveTier,
    bool SupportsRehydrationPlanning);

internal interface IE2EStorageBackend : IAsyncDisposable
{
    string Name { get; }
    E2EBackendCapabilities Capabilities { get; }
    Task InitializeAsync();
    Task<E2EStorageBackendContext> CreateContextAsync(CancellationToken cancellationToken = default);
}

internal sealed class E2EStorageBackendContext : IAsyncDisposable
{
    public required Arius.Core.Shared.Storage.IBlobContainerService BlobContainer { get; init; }
    public required string AccountName { get; init; }
    public required string ContainerName { get; init; }
    public BlobContainerClient? BlobContainerClient { get; init; }
    public AzureBlobContainerService? AzureBlobContainerService { get; init; }
    public required E2EBackendCapabilities Capabilities { get; init; }
    public required Func<ValueTask> CleanupAsync { get; init; }

    public ValueTask DisposeAsync() => CleanupAsync();
}
```

- [ ] **Step 4: Convert the current Azure fixture into `AzureE2EBackendFixture`**

```csharp
internal sealed class AzureE2EBackendFixture : IE2EStorageBackend
{
    public string Name => "Azure";

    public E2EBackendCapabilities Capabilities { get; } = new(
        SupportsArchiveTier: true,
        SupportsRehydrationPlanning: true);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task<E2EStorageBackendContext> CreateContextAsync(CancellationToken cancellationToken = default)
    {
        var (container, service, cleanup) = await CreateTestContainerAsync(cancellationToken);

        return new E2EStorageBackendContext
        {
            BlobContainer = service,
            AccountName = container.AccountName,
            ContainerName = container.Name,
            BlobContainerClient = container,
            AzureBlobContainerService = service,
            Capabilities = Capabilities,
            CleanupAsync = async () => await cleanup(),
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

- [ ] **Step 5: Add Azurite backend fixture in the E2E project**

```csharp
internal sealed class AzuriteE2EBackendFixture : IE2EStorageBackend, TUnit.Core.Interfaces.IAsyncInitializer
{
    private readonly Arius.Integration.Tests.Storage.AzuriteFixture _inner = new();

    public string Name => "Azurite";

    public E2EBackendCapabilities Capabilities { get; } = new(
        SupportsArchiveTier: false,
        SupportsRehydrationPlanning: false);

    public Task InitializeAsync() => _inner.InitializeAsync();

    public async Task<E2EStorageBackendContext> CreateContextAsync(CancellationToken cancellationToken = default)
    {
        var (container, service) = await _inner.CreateTestServiceAsync(cancellationToken);

        return new E2EStorageBackendContext
        {
            BlobContainer = service,
            AccountName = container.AccountName,
            ContainerName = container.Name,
            BlobContainerClient = container,
            AzureBlobContainerService = service,
            Capabilities = Capabilities,
            CleanupAsync = async () => await container.DeleteIfExistsAsync(cancellationToken: cancellationToken),
        };
    }

    public async ValueTask DisposeAsync() => await _inner.DisposeAsync();
}
```

- [ ] **Step 6: Run the fixture tests**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/E2EStorageBackendFixtureTests/*"`
Expected: PASS for Azure when env vars exist; Azurite-specific tests can be added and should pass when Docker is available.

- [ ] **Step 7: Commit**

```bash
git add src/Arius.E2E.Tests/Fixtures/IE2EStorageBackend.cs \
  src/Arius.E2E.Tests/Fixtures/E2EStorageBackendContext.cs \
  src/Arius.E2E.Tests/Fixtures/E2EBackendCapabilities.cs \
  src/Arius.E2E.Tests/Fixtures/AzureFixture.cs \
  src/Arius.E2E.Tests/Fixtures/AzuriteE2EBackendFixture.cs \
  src/Arius.E2E.Tests/Fixtures/E2EStorageBackendFixtureTests.cs
git commit -m "test: add swappable E2E storage backends"
```

### Task 5: Refactor `E2EFixture` Around Backend-Neutral Inputs and Explicit Cache State

**Files:**
- Modify: `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`
- Test: `src/Arius.E2E.Tests/Fixtures/E2EFixtureCacheStateTests.cs`

- [ ] **Step 1: Write the failing tests for cold and warm cache control**

```csharp
namespace Arius.E2E.Tests.Fixtures;

public class E2EFixtureCacheStateTests
{
    [Test]
    public async Task ResetLocalCache_RemovesRepositoryCacheDirectory()
    {
        var repositoryDirectory = Arius.Core.Shared.RepositoryPaths.GetRepositoryDirectory("account", "container");
        Directory.CreateDirectory(repositoryDirectory);

        await E2EFixture.ResetLocalCacheAsync("account", "container");

        Directory.Exists(repositoryDirectory).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/E2EFixtureCacheStateTests/*"`
Expected: FAIL because cache reset helpers do not exist.

- [ ] **Step 3: Refactor `E2EFixture.CreateAsync` to take backend-neutral values**

```csharp
public static async Task<E2EFixture> CreateAsync(
    Arius.Core.Shared.Storage.IBlobContainerService blobContainer,
    string accountName,
    string containerName,
    BlobTier defaultTier,
    string? passphrase = null,
    CancellationToken ct = default)
{
    var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-e2e-{Guid.NewGuid():N}");
    var localRoot = Path.Combine(tempRoot, "source");
    var restoreRoot = Path.Combine(tempRoot, "restore");
    Directory.CreateDirectory(localRoot);
    Directory.CreateDirectory(restoreRoot);

    var encryption = passphrase is not null
        ? (IEncryptionService)new PassphraseEncryptionService(passphrase)
        : new PlaintextPassthroughService();

    var index = new ChunkIndexService(blobContainer, encryption, accountName, containerName);
    var chunkStorage = new ChunkStorageService(blobContainer, encryption);
    var fileTreeService = new FileTreeService(blobContainer, encryption, index, accountName, containerName);
    var snapshot = new SnapshotService(blobContainer, encryption, accountName, containerName);

    return new E2EFixture(
        blobContainer,
        encryption,
        index,
        chunkStorage,
        fileTreeService,
        snapshot,
        tempRoot,
        localRoot,
        restoreRoot,
        accountName,
        containerName,
        defaultTier);
}
```

- [ ] **Step 4: Add explicit local cache control helpers**

```csharp
public static Task ResetLocalCacheAsync(string accountName, string containerName)
{
    var cacheDir = RepositoryPaths.GetRepositoryDirectory(accountName, containerName);
    if (Directory.Exists(cacheDir))
        Directory.Delete(cacheDir, recursive: true);

    return Task.CompletedTask;
}
```

- [ ] **Step 5: Add a source tree helper for deterministic dataset setup**

```csharp
public Task<RepositoryTreeSnapshot> MaterializeSourceAsync(
    SyntheticRepositoryDefinition definition,
    SyntheticRepositoryVersion version,
    int seed)
{
    if (Directory.Exists(LocalRoot))
        Directory.Delete(LocalRoot, recursive: true);

    Directory.CreateDirectory(LocalRoot);

    return SyntheticRepositoryMaterializer.MaterializeAsync(definition, version, seed, LocalRoot);
}
```

- [ ] **Step 6: Run fixture tests**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/E2EFixtureCacheStateTests/*"`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/Arius.E2E.Tests/Fixtures/E2EFixture.cs \
  src/Arius.E2E.Tests/Fixtures/E2EFixtureCacheStateTests.cs
git commit -m "test: make E2E fixture backend-neutral"
```

### Task 6: Define the Representative Scenario Catalog

**Files:**
- Create: `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioDefinition.cs`
- Create: `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioCatalog.cs`
- Test: `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioCatalogTests.cs`

- [ ] **Step 1: Write the failing test for scenario coverage**

```csharp
namespace Arius.E2E.Tests.Scenarios;

public class RepresentativeScenarioCatalogTests
{
    [Test]
    public async Task Catalog_ContainsApprovedCoreScenarios()
    {
        var scenarios = RepresentativeScenarioCatalog.All;

        scenarios.Select(x => x.Name).ShouldContain("initial-archive-v1");
        scenarios.Select(x => x.Name).ShouldContain("incremental-archive-v2");
        scenarios.Select(x => x.Name).ShouldContain("second-archive-no-changes");
        scenarios.Select(x => x.Name).ShouldContain("restore-latest-cold-cache");
        scenarios.Select(x => x.Name).ShouldContain("restore-latest-warm-cache");
        scenarios.Select(x => x.Name).ShouldContain("restore-previous-cold-cache");
        scenarios.Select(x => x.Name).ShouldContain("restore-previous-warm-cache");
        scenarios.Select(x => x.Name).ShouldContain("restore-multiple-versions");
        scenarios.Select(x => x.Name).ShouldContain("restore-local-conflict-no-overwrite");
        scenarios.Select(x => x.Name).ShouldContain("restore-local-conflict-overwrite");
        scenarios.Select(x => x.Name).ShouldContain("archive-no-pointers");
        scenarios.Select(x => x.Name).ShouldContain("archive-remove-local-then-thin-followup");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/RepresentativeScenarioCatalogTests/*"`
Expected: FAIL because the scenario catalog does not exist.

- [ ] **Step 3: Add the scenario definition types**

```csharp
namespace Arius.E2E.Tests.Scenarios;

internal enum ScenarioOperation
{
    Archive,
    Restore,
    ArchiveThenRestore,
}

internal enum ScenarioCacheState
{
    Cold,
    Warm,
}

internal enum ScenarioBackendRequirement
{
    Any,
    AzureArchiveCapable,
}

internal sealed record RepresentativeScenarioDefinition(
    string Name,
    ScenarioOperation Operation,
    ScenarioBackendRequirement BackendRequirement,
    Arius.E2E.Tests.Datasets.SyntheticRepositoryVersion SourceVersion,
    ScenarioCacheState CacheState,
    bool UseNoPointers = false,
    bool UseRemoveLocal = false,
    bool UseOverwrite = true,
    string? RestoreVersion = null);
```

- [ ] **Step 4: Add the approved scenario list**

```csharp
namespace Arius.E2E.Tests.Scenarios;

internal static class RepresentativeScenarioCatalog
{
    public static IReadOnlyList<RepresentativeScenarioDefinition> All { get; } =
    [
        new("initial-archive-v1", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold),
        new("incremental-archive-v2", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm),
        new("second-archive-no-changes", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm),
        new("restore-latest-cold-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Cold),
        new("restore-latest-warm-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm),
        new("restore-previous-cold-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold, RestoreVersion: "previous"),
        new("restore-previous-warm-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Warm, RestoreVersion: "previous"),
        new("restore-multiple-versions", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm),
        new("restore-local-conflict-no-overwrite", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Cold, UseOverwrite: false),
        new("restore-local-conflict-overwrite", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Cold, UseOverwrite: true),
        new("archive-no-pointers", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold, UseNoPointers: true),
        new("archive-remove-local-then-thin-followup", ScenarioOperation.ArchiveThenRestore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold, UseRemoveLocal: true),
        new("archive-tier-planning", ScenarioOperation.Restore, ScenarioBackendRequirement.AzureArchiveCapable, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold),
    ];
}
```

- [ ] **Step 5: Run the catalog tests**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/RepresentativeScenarioCatalogTests/*"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioDefinition.cs \
  src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioCatalog.cs \
  src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioCatalogTests.cs
git commit -m "test: define representative E2E scenarios"
```

### Task 7: Build the Shared Scenario Runner

**Files:**
- Create: `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioRunner.cs`
- Test: `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioRunnerTests.cs`

- [ ] **Step 1: Write the failing tests for scenario preconditions**

```csharp
namespace Arius.E2E.Tests.Scenarios;

public class RepresentativeScenarioRunnerTests
{
    [Test]
    public async Task ScenarioRunner_SkipsArchiveTierScenario_WhenBackendLacksCapability()
    {
        var scenario = RepresentativeScenarioCatalog.All.Single(x => x.Name == "archive-tier-planning");
        var backend = new FakeBackend(supportsArchiveTier: false);

        var result = await RepresentativeScenarioRunner.RunAsync(
            backend,
            scenario,
            SyntheticRepositoryProfile.Small,
            seed: 12345);

        result.WasSkipped.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/RepresentativeScenarioRunnerTests/*"`
Expected: FAIL because the runner does not exist.

- [ ] **Step 3: Add the runner result model and backend-capability check**

```csharp
namespace Arius.E2E.Tests.Scenarios;

internal sealed record RepresentativeScenarioRunResult(
    bool WasSkipped,
    string? SkipReason = null);

internal static class RepresentativeScenarioRunner
{
    public static async Task<RepresentativeScenarioRunResult> RunAsync(
        IE2EStorageBackend backend,
        RepresentativeScenarioDefinition scenario,
        SyntheticRepositoryProfile profile,
        int seed,
        CancellationToken cancellationToken = default)
    {
        if (scenario.BackendRequirement == ScenarioBackendRequirement.AzureArchiveCapable &&
            !backend.Capabilities.SupportsArchiveTier)
        {
            return new RepresentativeScenarioRunResult(true, "Backend lacks archive-tier capability.");
        }

        return new RepresentativeScenarioRunResult(false);
    }
}
```

- [ ] **Step 4: Extend the runner to prepare source version, remote state, and cache state**

```csharp
await using var context = await backend.CreateContextAsync(cancellationToken);
await using var fixture = await E2EFixture.CreateAsync(
    context.BlobContainer,
    context.AccountName,
    context.ContainerName,
    BlobTier.Cool,
    ct: cancellationToken);

var definition = SyntheticRepositoryDefinitionFactory.Create(profile);

if (scenario.CacheState == ScenarioCacheState.Cold)
    await E2EFixture.ResetLocalCacheAsync(context.AccountName, context.ContainerName);

await fixture.MaterializeSourceAsync(definition, SyntheticRepositoryVersion.V1, seed);
var initialArchive = await fixture.ArchiveAsync(cancellationToken);
initialArchive.Success.ShouldBeTrue(initialArchive.ErrorMessage);

if (scenario.SourceVersion == SyntheticRepositoryVersion.V2)
{
    await fixture.MaterializeSourceAsync(definition, SyntheticRepositoryVersion.V2, seed);
}
```

- [ ] **Step 5: Implement archive and restore branches minimally**

```csharp
switch (scenario.Operation)
{
    case ScenarioOperation.Archive:
        var archiveResult = await fixture.ArchiveAsync(cancellationToken);
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        break;

    case ScenarioOperation.Restore:
        var restoreResult = await fixture.RestoreAsync(cancellationToken);
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        break;

    case ScenarioOperation.ArchiveThenRestore:
        var archive = await fixture.ArchiveAsync(cancellationToken);
        archive.Success.ShouldBeTrue(archive.ErrorMessage);

        var restore = await fixture.RestoreAsync(cancellationToken);
        restore.Success.ShouldBeTrue(restore.ErrorMessage);
        break;
}
```

- [ ] **Step 6: Run the runner tests**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/RepresentativeScenarioRunnerTests/*"`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioRunner.cs \
  src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioRunnerTests.cs
git commit -m "test: add representative E2E scenario runner"
```

### Task 8: Cover Shared Representative Archive and Restore Scenarios

**Files:**
- Create: `src/Arius.E2E.Tests/RepresentativeArchiveRestoreTests.cs`

- [ ] **Step 1: Write the failing shared scenario tests for Azurite and Azure**

```csharp
namespace Arius.E2E.Tests;

[ClassDataSource<AzuriteE2EBackendFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<AzureE2EBackendFixture>(Shared = SharedType.PerTestSession)]
public class RepresentativeArchiveRestoreTests
{
    [Test]
    [MethodDataSource(typeof(RepresentativeScenarioCatalog), nameof(RepresentativeScenarioCatalog.All))]
    public async Task Representative_Scenario_Runs_OnSupportedBackends(
        IE2EStorageBackend backend,
        RepresentativeScenarioDefinition scenario,
        CancellationToken cancellationToken)
    {
        var result = await RepresentativeScenarioRunner.RunAsync(
            backend,
            scenario,
            SyntheticRepositoryProfile.Representative,
            seed: 20260419,
            cancellationToken);

        if (scenario.BackendRequirement == ScenarioBackendRequirement.Any)
            result.WasSkipped.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/RepresentativeArchiveRestoreTests/*"`
Expected: FAIL because runner behavior and test data plumbing are not complete enough yet.

- [ ] **Step 3: Extend `RepresentativeScenarioRunner` to assert restore trees and core scenario semantics**

```csharp
if (scenario.Operation == ScenarioOperation.Restore || scenario.Operation == ScenarioOperation.ArchiveThenRestore)
{
    var expectedVersion = scenario.RestoreVersion == "previous"
        ? SyntheticRepositoryVersion.V1
        : scenario.SourceVersion;

    await E2EFixture.ResetLocalCacheAsync(context.AccountName, context.ContainerName);

    var expectedRoot = Path.Combine(Path.GetTempPath(), $"arius-expected-{Guid.NewGuid():N}");
    try
    {
        var expected = await SyntheticRepositoryMaterializer.MaterializeAsync(
            definition, expectedVersion, seed, expectedRoot);

        var restoreResult = await fixture.RestoreAsync(cancellationToken);
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);

        await RepositoryTreeAssertions.AssertMatchesDiskTreeAsync(expected, fixture.RestoreRoot);
    }
    finally
    {
        if (Directory.Exists(expectedRoot))
            Directory.Delete(expectedRoot, recursive: true);
    }
}
```

- [ ] **Step 4: Add targeted branches for no-op second archive, no-pointers, remove-local follow-up, and local-conflict restore**

```csharp
if (scenario.Name == "second-archive-no-changes")
{
    var before = await fixture.ArchiveAsync(cancellationToken);
    before.Success.ShouldBeTrue(before.ErrorMessage);

    var after = await fixture.ArchiveAsync(cancellationToken);
    after.Success.ShouldBeTrue(after.ErrorMessage);
}

if (scenario.UseNoPointers)
{
    var result = await fixture.CreateArchiveHandler().Handle(
        new ArchiveCommand(new ArchiveCommandOptions
        {
            RootDirectory = fixture.LocalRoot,
            UploadTier = BlobTier.Cool,
            NoPointers = true,
        }),
        cancellationToken).AsTask();

    result.Success.ShouldBeTrue(result.ErrorMessage);
}

if (scenario.UseRemoveLocal)
{
    var result = await fixture.CreateArchiveHandler().Handle(
        new ArchiveCommand(new ArchiveCommandOptions
        {
            RootDirectory = fixture.LocalRoot,
            UploadTier = BlobTier.Cool,
            RemoveLocal = true,
        }),
        cancellationToken).AsTask();

    result.Success.ShouldBeTrue(result.ErrorMessage);
}
```

- [ ] **Step 5: Run the representative scenario tests for Azurite first**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/RepresentativeArchiveRestoreTests/*"`
Expected: PASS for Azurite-supported shared scenarios when Docker is available; Azure-backed cases may be skipped unless credentials are present.

- [ ] **Step 6: Run the same representative scenario tests with Azure credentials available**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/RepresentativeArchiveRestoreTests/*"`
Expected: PASS for the shared scenarios on Azure.

- [ ] **Step 7: Commit**

```bash
git add src/Arius.E2E.Tests/RepresentativeArchiveRestoreTests.cs \
  src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioRunner.cs
git commit -m "test: cover representative archive and restore scenarios"
```

### Task 9: Cover Azure-Only Archive-Tier Scenarios

**Files:**
- Create: `src/Arius.E2E.Tests/ArchiveTierRepresentativeTests.cs`
- Modify: `src/Arius.E2E.Tests/RehydrationE2ETests.cs`
- Modify: `src/Arius.E2E.Tests/Services/CopyTrackingBlobService.cs`

- [ ] **Step 1: Write the failing Azure-capability scenario tests**

```csharp
namespace Arius.E2E.Tests;

[ClassDataSource<AzureE2EBackendFixture>(Shared = SharedType.PerTestSession)]
public class ArchiveTierRepresentativeTests(AzureE2EBackendFixture backend)
{
    [Test]
    public async Task ArchiveTier_Planning_And_PendingVsReady_Are_Reported(CancellationToken cancellationToken)
    {
        var scenario = RepresentativeScenarioCatalog.All.Single(x => x.Name == "archive-tier-planning");

        var result = await RepresentativeScenarioRunner.RunAsync(
            backend,
            scenario,
            SyntheticRepositoryProfile.Small,
            seed: 20260419,
            cancellationToken);

        result.WasSkipped.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/ArchiveTierRepresentativeTests/*"`
Expected: FAIL because the archive-tier branch in the runner is not implemented yet.

- [ ] **Step 3: Fold the useful parts of `RehydrationE2ETests` into the representative archive-tier branch**

```csharp
var trackingSvc = new CopyTrackingBlobService(context.AzureBlobContainerService!);
var restoreHandler = new RestoreCommandHandler(
    fixture.Encryption,
    fixture.Index,
    new ChunkStorageService(trackingSvc, fixture.Encryption),
    new FileTreeService(trackingSvc, fixture.Encryption, fixture.Index, context.AccountName, context.ContainerName),
    new SnapshotService(trackingSvc, fixture.Encryption, context.AccountName, context.ContainerName),
    NSubstitute.Substitute.For<Mediator.IMediator>(),
    new Microsoft.Extensions.Logging.Testing.FakeLogger<RestoreCommandHandler>(),
    context.AccountName,
    context.ContainerName);
```

- [ ] **Step 4: Assert planning, pending rehydration, sideloaded-ready restore, and `chunks-rehydrated/` cleanup behavior**

```csharp
var result1 = await restoreHandler.Handle(new RestoreCommand(new RestoreOptions
{
    RootDirectory = fixture.RestoreRoot,
    Overwrite = true,
    ConfirmRehydration = (_, _) => Task.FromResult<RehydratePriority?>(RehydratePriority.Standard),
}), cancellationToken).AsTask();

result1.Success.ShouldBeTrue(result1.ErrorMessage);
result1.ChunksPendingRehydration.ShouldBeGreaterThan(0);

await SideloadRehydratedChunksAsync(
    context.AzureBlobContainerService!,
    contentHashToBytes,
    fixture.Index,
    cancellationToken);

var result2 = await fixture.RestoreAsync(cancellationToken);
result2.Success.ShouldBeTrue(result2.ErrorMessage);
result2.ChunksPendingRehydration.ShouldBe(0);
```

- [ ] **Step 5: Keep Azure-specific concrete service usage isolated to this test path**

```csharp
context.AzureBlobContainerService.ShouldNotBeNull();
context.Capabilities.SupportsArchiveTier.ShouldBeTrue();
```

- [ ] **Step 6: Run the archive-tier tests on Azure**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/ArchiveTierRepresentativeTests/*"`
Expected: PASS when Azure credentials are available.

- [ ] **Step 7: Commit**

```bash
git add src/Arius.E2E.Tests/ArchiveTierRepresentativeTests.cs \
  src/Arius.E2E.Tests/RehydrationE2ETests.cs \
  src/Arius.E2E.Tests/Services/CopyTrackingBlobService.cs \
  src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioRunner.cs
git commit -m "test: cover archive-tier representative scenarios"
```

### Task 10: Remove or Retire Superseded Ad Hoc E2E Tests

**Files:**
- Modify: `src/Arius.E2E.Tests/E2ETests.cs`
- Modify: `src/Arius.E2E.Tests/RehydrationE2ETests.cs`

- [ ] **Step 1: Write a narrow test or assertion proving any retained simple tests still add unique value**

```csharp
[Test]
public async Task E2E_Configuration_IsAvailable_WhenAzureBackendIsEnabled()
{
    AzureE2EBackendFixture.AccountName.ShouldNotBeNullOrWhiteSpace();
    AzureE2EBackendFixture.AccountKey.ShouldNotBeNullOrWhiteSpace();
}
```

- [ ] **Step 2: Delete or slim down cases fully covered by the representative suite**

```csharp
// Remove single-file hot/cool roundtrip cases once representative V1/V2 scenarios cover them.
// Keep only targeted sanity checks that verify Azure credential gating or unique service behavior.
```

- [ ] **Step 3: Keep only tests that exercise unique product concerns not represented in the scenario matrix**

```csharp
// Retain only archive-tier-specific probes that cannot be cleanly expressed through the shared scenario runner.
```

- [ ] **Step 4: Run the full E2E project**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.E2E.Tests/E2ETests.cs \
  src/Arius.E2E.Tests/RehydrationE2ETests.cs
git commit -m "test: retire superseded ad hoc E2E coverage"
```

### Task 11: Update Documentation

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`

- [ ] **Step 1: Update `README.md` with the representative E2E suite description**

```md
## End-to-End Tests

The end-to-end tests can now run the same representative archive and restore scenarios against two storage backends:

- Azurite for local and CI validation
- Azure Blob Storage for opt-in real-service validation

The test data is generated deterministically from a fixed seed and named dataset profile, so the same archive history can be reproduced across runs.
```

- [ ] **Step 2: Update `AGENTS.md` with guidance for future agent work**

```md
## E2E Test Guidance

- Prefer the deterministic synthetic repository generator in `src/Arius.E2E.Tests/Datasets/` over ad hoc random files.
- Shared representative scenarios should run against both Azurite and Azure when supported by backend capabilities.
- Treat cache state (`Cold` vs `Warm`) and dataset version (`V1` vs `V2`) as explicit scenario inputs, not incidental fixture behavior.
- Keep real archive-tier and rehydration semantics in Azure-capability-gated tests.
```

- [ ] **Step 3: Run the full non-Windows test suite**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" && dotnet test --project "src/Arius.AzureBlob.Tests/Arius.AzureBlob.Tests.csproj" && dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj" && dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" && dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" && dotnet test --project "src/Arius.Architecture.Tests/Arius.Architecture.Tests.csproj"`
Expected: PASS, excluding `Arius.Explorer.Tests` on non-Windows.

- [ ] **Step 4: Commit**

```bash
git add README.md AGENTS.md
git commit -m "docs: describe representative E2E suite"
```

## Self-Review

**Spec coverage**
- Covered deterministic `V1` and `V2` dataset generation.
- Covered shared Azurite and Azure backend swapping.
- Covered the main approved representative scenario list.
- Covered Azure-only archive-tier planning and rehydration scenarios.
- Benchmarks intentionally left out of scope.

**Gaps to watch during implementation**
- `Targeted subtree restore` is not yet included because current core support exists but CLI plumbing may not; decide whether to add it as an E2E-core test or hold it for a separate change.
- The exact assertion for `second-archive-no-changes` depends on current product behavior: `no additional uploads`, `no new snapshot`, or both. Confirm by reading existing archive tests before finalizing that branch.
- Reusing `Arius.Integration.Tests` Azurite fixture directly from `Arius.E2E.Tests` may be awkward. If project references become messy, extract a tiny shared test helper rather than duplicating the full fixture pattern blindly.

**Placeholder scan**
- No `TBD` or `TODO` placeholders.
- Task 8 Step 4 and Task 9 Steps 3 to 5 are the highest-risk integration steps and may need small API adjustments while implementing, but the intended behavior is concrete.

**Type consistency**
- The plan consistently uses `SyntheticRepositoryProfile`, `SyntheticRepositoryVersion`, `RepresentativeScenarioDefinition`, `IE2EStorageBackend`, and `E2EStorageBackendContext`.
