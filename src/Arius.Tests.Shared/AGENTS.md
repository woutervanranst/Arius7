# Arius.Tests.Shared — agent contract

Read the root contract first: [../../AGENTS.md](../../AGENTS.md). It holds the cross-cutting rules (think-before-coding, simplicity, TUnit workflow, code style, the doc map). This file only covers what is specific to this project.

**What it is:** the reusable, *non-test* test-infrastructure library. It exists so that Docker-backed (Azurite) and repository fixtures can be shared across test projects **without one test assembly depending on another** (E2E used to reference Integration just to reuse `AzuriteFixture`, which made CI Docker-requirement discovery fragile). It targets `net10.0` and is consumed by Core/Cli/Integration/E2E/AzureBlob/Architecture/Explorer test projects.

**Design:** see [docs/design/cross-cutting/testing.md](../../docs/design/cross-cutting/testing.md) and [ADR-0009](../../docs/decisions/adr-0009-clarify-test-fixture-boundaries.md). Don't restate the fixture rationale here.

## Layout
- `Fixtures/` — `RepositoryTestFixture` (the repository core: owns the full service graph + source/restore roots + repository-local cache state) and `AzuriteFixture` (owns the shared Docker Azurite process, hands out per-test containers).
- `Storage/` — `FakeInMemoryBlobContainerService`, the stateful in-memory `IBlobContainerService` double (records requested/uploaded/deleted blobs, models etag conflicts and rerun recovery).
- `IO/`, `Hashes/` — `FileSystemHelper` (directory copy), `HashTestHelpers`/`HashTestData` (content/chunk/filetree hash builders).
- Root: `TestDefaults` (shared `Plaintext`/`Encrypted`/`Ztd` instances via extension members), `TestTempRoots`, `RepositoryLocalStatePathsCleanup`, `AssemblyMarker`.

## Conventions / gotchas
- **Fixture hierarchy** (ADR-0009): `RepositoryTestFixture` is the *single* owner of the repository service graph. The wrappers `PipelineFixture` (in `Arius.Integration.Tests/Pipeline`) and `E2EFixture` (in `Arius.E2E.Tests/Fixtures`) compose it; they live **beside their tests, not here**. Only genuinely reusable doubles/fixtures belong in this assembly — scenario-specific ones stay next to the test that needs them.
- Construct `RepositoryTestFixture` via the static factories (`CreateInMemoryAsync`, `CreateWithPassphraseAsync`, `CreateWithEncryptionAsync`), never the initializer directly. It is `internal` — test access goes through `InternalsVisibleTo` in `AssemblyMarker.cs` (add new consumers there).
- **Cache-reset coordination:** repository-local cache lives under `RepositoryLocalStatePaths`. `RepositoryLocalStatePathsCleanup` deletes `unittest-*` repo caches `[Before(TestSession)]`; `TestTempRoots` reaps stale temp roots `[After(TestSession)]`. `RepositoryTestFixture.DeleteLocalCacheDirectory` also calls `SqliteConnection.ClearPool` first — the chunk-index SQLite pool pins file handles, so skipping it makes deletes flaky. `E2EFixture` owns explicit E2E cache reset; disposal only releases the lease.
- `AzuriteFixture` self-skips (`Skip.Test`) with a visible reason when Docker/the Azurite image is unavailable — keep that behavior; don't filter Azurite tests out of the matrix.
- Make new classes `internal` by default; only go `public` when another non-test assembly must consume the type.

## Build / test
This is a class library — there are no tests to run here. Build it (`dotnet build src/Arius.Tests.Shared/Arius.Tests.Shared.csproj`), then run the consuming test project (e.g. `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj`). Changing a shared fixture/double can break every consumer, so build the affected test projects after edits.
