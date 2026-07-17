# Testing

> **Code:** `src/Arius.Architecture.Tests`, `src/Arius.Tests.Shared`, `src/Arius.E2E.Tests/Workflows`, `src/Arius.Integration.Tests`, `src/Arius.Core.Tests`, `src/Arius.Cli.Tests`, `src/Arius.Api.Tests`, `src/Arius.Api.Integration.Tests`, `src/Arius.Api.FakeTestHost`, `src/Arius.Web/src/app/**/*.spec.ts`, `src/Arius.Web/e2e`  ·  **Decisions:** [ADR-0001](../../decisions/adr-0001-structure-representative-e2e-coverage.md) · [ADR-0005](../../decisions/adr-0005-adopt-scoped-stryker-mutation-testing.md) · [ADR-0009](../../decisions/adr-0009-clarify-test-fixture-boundaries.md) · [ADR-0011](../../decisions/adr-0011-require-90-percent-production-line-coverage.md) · [ADR-0022](../../decisions/adr-0022-scripted-fake-core-test-harness.md)  ·  **Terms:** [snapshot](../../glossary.md#snapshot) · [chunk index](../../glossary.md#chunk-index) · [tar chunk](../../glossary.md#tar-chunk) · [storage tier hint](../../glossary.md#storage-tier-hint)

## Purpose

Arius is a backup tool, so tests are the primary review surface for correctness, durability, and recoverability — not an afterthought. This doc describes how the suite is *shaped*: how fixtures layer Azurite/Azure/in-memory backends under one repository service graph, why there is exactly **one** representative end-to-end workflow rather than a scenario matrix, how a scripted-fake `Arius.Core` gives the Api/Web host deterministic offline HTTP/hub/browser coverage, how architecture tests enforce the Core⊥hosts boundary as an executable contract, and how mutation testing and the coverage floor act as quality gates. It is intentionally not a how-to-run guide (that lives in `AGENTS.md`).

## How it works

### The test projects

| Project | Scope | Backend |
|---|---|---|
| `Arius.Core.Tests` | Fast unit/behavior tests for Core feature handlers and shared services | in-memory (`FakeInMemoryBlobContainerService`) |
| `Arius.AzureBlob.Tests` | Azure adapter logic — container discovery/listing, cost estimator/calculator | fake `BlobServiceClient`/`BlobContainerClient` (no real Azure) |
| `Arius.Cli.Tests` | CLI parsing, option validation, account/key resolution, DI wiring | none (no Azure, no network) |
| `Arius.Api.Tests` | Web-host logic in isolation — `AppDatabase`/statistics cache, `JobSink` aggregate/ETA/warnings, `JobStateRegistry`, `RehydrationSchedule` | host SQLite only (no Azure, no Core) |
| `Arius.Api.Integration.Tests` | HTTP/hub-level Api behavior — job lifecycle, reattach, cost handshake, single-active-job guard, stale-approval sweep | in-process `WebApplicationFactory<Program>` + scripted Core |
| `Arius.Api.FakeTestHost` | **Not a test project** — the scripted-fake `Arius.Core` substitute (scenario-driven command handlers + deterministic cost estimator) and `Testing` control endpoints; reused in-process (above) and out-of-process by hermetic Playwright | n/a |
| `Arius.Integration.Tests` | Repository pipeline behavior against a real blob backend; crash recovery, faulting, rehydration simulation | Azurite (Docker) |
| `Arius.E2E.Tests` | One representative archive→restore lifecycle; live-Azure-only probes | Azurite **and** real Azure |
| `Arius.Architecture.Tests` | Dependency/boundary rules as executable contracts | reflection only (no I/O) |
| `Arius.Tests.Shared` | **Not a test project** — reusable fixture/backend infrastructure | n/a |
| `Arius.Explorer.Tests` | WPF host (Windows-only) | n/a |

### The whole-solution project graph

Every project in `src/` (plus `Arius.Web`'s three test suites, which live inside that one npm project rather than as separate projects), grouped into **production**, **test infrastructure** (non-test, but test-only), **benchmarks** (non-test), and **test projects**. An arrow is a `ProjectReference` (or, for `Arius.Web`, the equivalent app/spec relationship) — for a test project that reference *is* what it exercises; for a non-test project it is an ordinary compile-time dependency.

```mermaid
graph LR
    subgraph tests["Test projects"]
        CORETESTS["Arius.Core.Tests"]
        AZBLOBTESTS["Arius.AzureBlob.Tests"]
        CLITESTS["Arius.Cli.Tests"]
        APITESTS["Arius.Api.Tests"]
        APIINTTESTS["Arius.Api.Integration.Tests"]
        INTTESTS["Arius.Integration.Tests"]
        E2ETESTS["Arius.E2E.Tests"]
        ARCHTESTS["Arius.Architecture.Tests"]
        EXPLORERTESTS["Arius.Explorer.Tests"]
        VITEST["Arius.Web:<br/>Vitest unit specs"]
        PWREAL["Arius.Web:<br/>Playwright e2e/specs<br/>(real Azure)"]
        PWHERM["Arius.Web:<br/>Playwright e2e/hermetic<br/>(scripted Core)"]
    end

    subgraph infra["Test infrastructure - not test projects"]
        TESTSHARED["Arius.Tests.Shared"]
        FAKEHOST["Arius.Api.FakeTestHost"]
    end

    subgraph bench["Benchmarks - not tests"]
        AZBENCH["Arius.AzureBlob.Benchmarks"]
        BENCH["Arius.Benchmarks"]
    end

    subgraph prod["Production"]
        CORE["Arius.Core"]
        AZBLOB["Arius.AzureBlob"]
        CLI["Arius.Cli"]
        API["Arius.Api"]
        EXPLORER["Arius.Explorer"]
        MIGRATION["Arius.Migration"]
        WEBAPP["Arius.Web app code<br/>(components/stores/services)"]
    end

    %% production-internal references
    AZBLOB --> CORE
    CLI --> CORE
    CLI --> AZBLOB
    API --> CORE
    API --> AZBLOB
    EXPLORER --> CORE
    EXPLORER --> AZBLOB
    MIGRATION --> CORE
    MIGRATION --> AZBLOB

    %% test infrastructure -> production
    TESTSHARED --> CORE
    TESTSHARED --> AZBLOB
    FAKEHOST --> API
    FAKEHOST --> CORE

    %% benchmarks
    AZBENCH --> AZBLOB
    AZBENCH --> CORE
    BENCH --> E2ETESTS

    %% test projects -> what they test (production) + what they depend on (other tests/infra)
    CORETESTS --> CORE
    CORETESTS --> TESTSHARED

    AZBLOBTESTS --> AZBLOB
    AZBLOBTESTS --> TESTSHARED

    CLITESTS --> CLI
    CLITESTS --> CORE
    CLITESTS --> TESTSHARED

    APITESTS --> API

    APIINTTESTS --> API
    APIINTTESTS --> FAKEHOST
    APIINTTESTS --> TESTSHARED

    INTTESTS --> CORE
    INTTESTS --> AZBLOB
    INTTESTS --> TESTSHARED

    E2ETESTS --> CORE
    E2ETESTS --> AZBLOB
    E2ETESTS --> CLI
    E2ETESTS --> TESTSHARED

    ARCHTESTS --> CORE
    ARCHTESTS --> AZBLOB
    ARCHTESTS --> CLI
    ARCHTESTS --> TESTSHARED

    EXPLORERTESTS --> EXPLORER
    EXPLORERTESTS --> TESTSHARED

    VITEST --> WEBAPP
    PWREAL --> WEBAPP
    PWREAL --> API
    PWHERM --> WEBAPP
    PWHERM --> FAKEHOST
```

A few things this makes visible: `Arius.Api.Tests` is the only .NET test project with a **single** production dependency (`Arius.Api` only — it never touches Core, by design, see the table above); `Arius.Benchmarks` is the one place a **non-test** project depends on a **test** project (it reuses `Arius.E2E.Tests`' representative-workflow fixtures/datasets for its own harness); and `Arius.Migration` has **no dedicated test project** at all (an open seam, noted below).

### Fixture hierarchy

The load-bearing idea ([ADR-0009](../../decisions/adr-0009-clarify-test-fixture-boundaries.md)) is that *Azurite process lifetime*, *repository service-graph lifetime*, *backend provisioning*, and *workflow-specific behavior* are four different concerns, each owned by exactly one fixture type. `RepositoryTestFixture` is the single owner of the repository service graph and repository-local cache state; everything else is a scenario-specific wrapper around it.

```mermaid
graph TD
    subgraph shared[Arius.Tests.Shared - reusable, non-test]
        AZ["AzuriteFixture<br/>owns Docker Azurite process<br/>self-skips when Docker unavailable<br/>CreateTestServiceAsync per test"]
        RTF["RepositoryTestFixture<br/>THE repository core<br/>service graph + source/restore roots<br/>+ repository-local cache state"]
        FAKE["FakeInMemoryBlobContainerService<br/>stateful in-memory blob double"]
    end

    subgraph integ[Arius.Integration.Tests]
        PF["PipelineFixture<br/>integration wrapper<br/>list-query helper, container reuse"]
    end

    subgraph e2e[Arius.E2E.Tests]
        AZBF["AzuriteE2EBackendFixture<br/>SupportsArchiveTier=false"]
        AEBF["AzureE2EBackendFixture<br/>SupportsArchiveTier=true"]
        CTX["E2EStorageBackendContext<br/>backend handle + capabilities + cleanup"]
        E2EF["E2EFixture<br/>E2E wrapper<br/>cache-reset lease coordination"]
    end

    subgraph core[Arius.Core.Tests]
        UT["unit/behavior tests"]
    end

    PF --> AZ
    PF --> RTF
    AZBF --> AZ
    AZBF --> CTX
    AEBF --> CTX
    CTX --> E2EF
    E2EF --> RTF
    UT --> FAKE
    FAKE --> RTF
```

`RepositoryTestFixture` accepts an already-constructed `IBlobContainerService` plus account/container names and constructs the full repository service graph (`SnapshotService`, `ChunkIndexService`, `ChunkStorageService`, `FileTreeService`) and the `ArchiveCommandHandler`/`RestoreCommandHandler`/`ListQueryHandler` factories around it. The three entry points pick the backend and encryption:

- `CreateInMemoryAsync` — `FakeInMemoryBlobContainerService` + `PlaintextInstance` encryption, for fast Core tests that need a complete service graph without Azurite.
- `CreateWithPassphraseAsync` — caller-provided container + production passphrase encryption path, for Azurite/Azure pipeline tests.
- `CreateWithEncryptionAsync` — caller-provided container + explicit encryption service, for legacy-format or seeded-data tests.

Because the same fixture base serves in-memory, Azurite, and live Azure, the *one repository service graph* is identical across all three; only the storage boundary underneath changes.

`E2EFixture` adds one piece of E2E-specific lifecycle: a static **cache-reset lease** keyed by account/container. `ResetLocalCache(...)` refuses to delete the repository-local cache while a fixture is still alive, so the cold-cache transition in the representative workflow is an explicit, guarded step rather than an accidental race. Disposing a fixture releases the lease but does **not** itself reset the cache.

### The one representative E2E workflow

There is exactly one canonical end-to-end story ([ADR-0001](../../decisions/adr-0001-structure-representative-e2e-coverage.md)): one deterministic synthetic repository, archived and restored repeatedly as it evolves `V1 → V2`. It replaced an earlier matrix of isolated one-off scenarios, each of which provisioned a fresh container/temp-root and synthesized its own setup — which validated commands in isolation but never validated the property the suite exists for: *one archive history evolving over time*.

The workflow is data, not a method: `RepresentativeWorkflowCatalog.Canonical` is a `RepresentativeWorkflowDefinition` holding an ordered list of typed `IRepresentativeWorkflowStep` records. `RepresentativeWorkflowRunner.RunAsync` creates one backend context and one fixture for the whole run, then executes each step against a shared mutable `RepresentativeWorkflowState` (current source version, latest/previous [snapshot](../../glossary.md#snapshot) versions, captured pre-no-op blob counts). The step records are small and named — `MaterializeVersionStep`, `ArchiveStep`, `RestoreStep`, `ResetCacheStep`, `AssertRemoteStateStep`, `AssertConflictBehaviorStep`, `ArchiveTierLifecycleStep` — deliberately *not* a general-purpose DSL; each corresponds to one concrete test concern.

```mermaid
sequenceDiagram
    participant R as RepresentativeWorkflowRunner
    participant S as WorkflowState
    participant Az as Azurite / Azure backend

    R->>S: materialize V1, archive(Cool)
    R->>Az: AssertRemoteState(InitialArchive) - 1 snapshot
    R->>Az: restore latest -> verify V1
    R->>S: materialize V2, archive(Cool)
    R->>Az: AssertRemoteState(IncrementalArchive) - 2 snapshots + dedup/tar lookups
    R->>S: restore latest WARM -> verify V2
    R->>S: ResetCache (lease-guarded)
    R->>S: restore latest COLD -> verify V2
    R->>S: restore PREVIOUS -> verify V1
    R->>S: re-archive V2 (no change)
    R->>Az: AssertRemoteState(NoOpArchive) - snapshot/chunk/filetree counts unchanged
    R->>S: archive --no-pointers -> restore (assert no pointer files)
    R->>S: archive --remove-local -> restore
    R->>S: conflict restore no-overwrite / overwrite
    Note over R,Az: if SupportsArchiveTier (Azure only)
    R->>Az: ArchiveTierLifecycle - pending vs ready rehydration + cleanup
```

Capability gating keeps Azure-only semantics inside the *same* definition. `AzuriteE2EBackendFixture` declares `SupportsArchiveTier: false`; the live Azure backend declares it `true`. `ArchiveTierLifecycleStep` self-skips when the capability is absent, so Azurite and Azure run the identical workflow and only the archive-tier rehydration steps fork — no backend-specific copy of the main story. The archive tier is offline, so faithful rehydration behavior cannot be simulated in Azurite and must run against real Azure.

Assertions favor stable product behavior over storage-layout details. `AssertRemoteStateStep` checks snapshot *count* deltas (one new snapshot per state-changing archive, none for a no-op), latest-snapshot `FileCount`, [chunk-index](../../glossary.md#chunk-index) dedup lookups (two paths sharing one content hash resolve to one chunk), and the [tar-chunk](../../glossary.md#tar-chunk) path (a small file's resolved chunk hash differs from its content hash). It does **not** assert exact total chunk/filetree counts — those are too coupled to bundling internals. The no-op archive case is the explicit exception that validates the product rule (refined by [ADR-0002](../../decisions/adr-0002-skip-snapshots-for-no-op-archives.md)): an unchanged re-archive preserves the existing latest snapshot rather than publishing a redundant one.

Dataset scale is one explicit knob: `SyntheticRepositoryDefinitionFactory.RepresentativeScaleDivisor`. The workflow definition stays independent of scale, so the same canonical story runs against a development-sized (~32 MB / ~254 files) repository now and a larger one later by changing one constant. `E2ETests.cs` keeps the narrow live-Azure credential sanity check plus hot-tier pointer/large-file probes that the representative workflow doesn't cover directly.

### The Api/Web scripted-fake-Core harness

`Arius.Api` has no analog of the Core-side fixture hierarchy above — it talks to Core only through `IMediator`, never through `IBlobContainerService` directly — so it needed a different vehicle to get deterministic, offline coverage of tier/cost/rehydration/warning scenarios ([ADR-0022](../../decisions/adr-0022-scripted-fake-core-test-harness.md)). The seam is `IRepositoryCoreComposer` (`src/Arius.Api/Composition/`), extracted from `RepositoryProviderRegistry.BuildAsync`: the registry always runs `AddMediator()` itself (the Api's auto-registered event forwarders live in this assembly), then delegates the rest of the per-repository Core composition to the composer.

```mermaid
graph LR
    reg["RepositoryProviderRegistry.BuildAsync<br/>AddMediator() always"] --> seam["IRepositoryCoreComposer.ComposeAsync"]
    seam -->|production| azc["AzureRepositoryCoreComposer<br/>AddAzureBlobStorage() + AddArius(...)<br/>byte-identical to pre-extraction"]
    seam -->|test| sfc["ScriptedRepositoryCoreComposer<br/>(Arius.Api.FakeTestHost)"]
    sfc --> sar["ScriptedArchiveHandler /<br/>ScriptedRestoreHandler<br/>publish real Core INotifications<br/>from a per-repo ScenarioRegistry"]
    sfc --> nc["NotConfigured*Handler stand-ins<br/>for every other Core command/query"]
    sfc --> sce[ScriptedStorageCostEstimator]
```

- **The fake replaces only the storage-touching handlers**, not the pipeline around them: `ScriptedArchiveHandler`/`ScriptedRestoreHandler` publish a scripted, ordered list of the **real** Core `INotification` types (`ScanCompleteEvent`, `ChunkUploadedEvent`, `ChunkResolutionCompleteEvent`, `RehydrationStatusEvent`, …) from a per-repository `ScenarioRegistry`, then return the scripted `ArchiveResult`/`RestoreResult`. The real Mediator pipeline, the Api's event forwarders, `JobSink`, and the SignalR hub all run unchanged — a scenario reproduces a finding exactly because everything downstream of Core is production code.
- **`NotConfiguredCommandHandler`/`NotConfiguredQueryHandler`/`NotConfiguredStreamQueryHandler`** stand in for every Core command/query a scenario doesn't script. Othamar Mediator eagerly resolves *every* discovered handler on first `Send`/`Publish`, not just the invoked one, and the scripted composer never calls `AddArius()` — so without a stand-in, an unrelated call fails DI resolution instead of throwing the intended `NotSupportedException`.
- **`ScenarioGate`** is a per-repository sticky latch: a scenario marked `Gated` holds the scripted handler in-flight (still "running") until a control endpoint calls `Release` — release-before-wait is remembered, so both orderings resolve. This is what lets a browser test observe a job sitting in the Active list before completing it.
- **`ScriptedStorageCostEstimator`** replicates `Arius.Tests.Shared`'s `FakeStorageCostEstimator` arithmetic independently, so the shipped-to-a-real-process `Arius.Api.FakeTestHost` never references TUnit/NSubstitute/Azurite.
- **Fidelity today is compile-time only**: the fake emits/consumes the real Core event, result, and DTO types, so a renamed field breaks the build. There is no runtime check that a canonical scenario's event *sequence* still matches what real Core would actually emit (open seam, below).

The harness is consumed two ways, both wired through the same `AriusApiHost.AddAriusApi()`/`MapAriusApi()` extension methods that production `Program.cs` calls:

| Consumer | How the scripted composer wins | Vehicle |
|---|---|---|
| `Arius.Api.Integration.Tests` | `AriusApiFactory : WebApplicationFactory<Program>` calls `RemoveAll<IRepositoryCoreComposer>()` then registers `ScriptedRepositoryCoreComposer` | in-process TUnit tests |
| Hermetic Playwright | `Arius.Api.FakeTestHost` (`TestHost.Main`) registers the scripted composer **before** `AddAriusApi()` runs, so its `TryAddSingleton<IRepositoryCoreComposer, AzureRepositoryCoreComposer>()` is a no-op | out-of-process `dotnet run`, driven by a real browser |

`Arius.Api.FakeTestHost` also maps `Testing`-only control endpoints (`POST /api/testing/{reset,seed-repo,scenario,release/{repoId}}`) — the out-of-process equivalent of the in-process factory's `SeedRepository`/`Scenarios.Set*` — so Playwright can seed a repo, select one of `CanonicalScenarios`' named scenarios, and release a gate, all before driving the real UI. Production `Arius.Api` never references `Arius.Api.FakeTestHost` and has no environment branch: the scripted composer is selected purely by registration order in a separate host.

### Web test tiers

`Arius.Web` splits unit and browser coverage across two runners, both under `src/Arius.Web`:

- **Vitest** (`vitest.config.ts`, `globals: true`, `environment: 'node'`) runs pure-function and store specs matching `src/app/**/*.spec.ts` — `job-format.spec.ts`, `drawer.store.spec.ts`, `job-pill.store.spec.ts`, `realtime.service.spec.ts` — with `v8` coverage uploaded to Codecov under the `web` flag. It replaced the deprecated Karma/Jasmine runner and deliberately does **not** render Angular components (that would need `@analogjs/vitest-angular`); component rendering is Playwright's job.
- **Playwright** runs two independent suites from the same `e2e/` root:

| Suite | Config | Backend | CI gate |
|---|---|---|---|
| `e2e/specs/**` | `playwright.config.ts` | real Azure Blob Storage, real archive | full-stack behavioral gate; needs Azure secrets |
| `e2e/hermetic/specs/**` | `playwright.hermetic.config.ts` | `Arius.Api.FakeTestHost` + `ng serve`, no Azure | `web-e2e-hermetic`, runs on every PR |

The hermetic suite's `support/{control.ts,fixtures.ts,global-setup.ts}` wrap the `Testing` control endpoints into a `control` fixture (`seedRepo`/`scenario`/`release`) that each spec uses to set up a canonical scenario before driving the real UI: `jobs-live-update.spec.ts` (list reloads when a job finishes), `cost-reattach.spec.ts`/`cost-online-restore.spec.ts` (the cost modal survives a fresh reattach), `rehydrating-reattach.spec.ts` (auto-resume toggle + "≈ hydrated by" ETA persist across reattach), and `single-active-job.spec.ts` (a busy repo rejects a second job, by design).

### Architecture tests as contract enforcement

`Arius.Architecture.Tests` turns the [design overview's](../../design/README.md) structural rules into executable ArchUnitNET assertions — the mechanical enforcement layer that complements behavior tests and design review. `DependencyTests` loads `Arius.Core`, `Arius.AzureBlob`, and `Arius.Cli` and enforces:

- **No Azure leakage:** neither `Arius.Core` nor `Arius.Cli` may depend on any `Azure.*` namespace type; all Azure access is mediated through `Arius.AzureBlob`. Adding a stray `using Azure.Storage.Blobs` to the CLI fails the build.
- **Core exposed only through Mediator:** `Core_Is_Exposed_Primarily_Through_Mediator` reflects over Core's public surface, builds the transitive contract-type graph (commands, queries, results, notifications, public-interface signatures), and asserts that no non-Core class depends on any *other* Core type — with narrow, named exceptions for the Mediator source-generator and a couple of composition-root entry points.
- **Facade boundaries:** chunk-index internals (`ChunkIndexLocalStore`, `ChunkIndexRouter`) stay behind `IChunkIndexService`; only `ChunkIndexLocalStore` may touch `Microsoft.Data.Sqlite`; the archive-time local file models (`BinaryFile`, `PointerFile`, `FilePair`) are usable only within the archive feature namespace and remain `internal`.

`ModulithTests` enforces a namespace-scoped meaning of `internal`: an internal type in namespace `N` may be referenced only from `N` or a descendant, turning each folder into a module boundary. Intentional cross-namespace sharing must opt in with `[SharedWithinAssembly]`, and that attribute may only decorate non-public types.

### Mutation testing and coverage floor

Two quality gates sit on top of the tests, deliberately at different strengths:

- **Mutation testing (advisory, [ADR-0005](../../decisions/adr-0005-adopt-scoped-stryker-mutation-testing.md)):** `stryker-config.json` scopes Stryker to `Arius.Core` (mutated) driven by `Arius.Core.Tests` via the MTP runner. Runs are local/manual; scores are diagnostic guidance for finding weak assertions, **not** a CI gate, because the preview MTP runner produces fluctuating scores and runtime is high.
- **Coverage floor (enforced, [ADR-0011](../../decisions/adr-0011-require-90-percent-production-line-coverage.md)):** 90% overall *production* line coverage. `codecov.yml` excludes `src/*.Tests/**`, `src/Arius.Tests.Shared/**`, and `src/Arius.Api.FakeTestHost/**` from the denominator, so test code and test scaffolding never inflate the number. CI collects coverage with `dotnet-coverage` and uploads to Codecov; Vitest uploads its own `web`-flagged coverage separately (see [Web test tiers](#web-test-tiers)).

## Key invariants

- **`RepositoryTestFixture` is the single owner of the repository service graph and repository-local cache state.** Wrapper fixtures (`PipelineFixture`, `E2EFixture`) expose it as `Repository` and must not duplicate repository service state. A refactor that gives a wrapper its own parallel service graph would split cache/validation state and break [ADR-0009](../../decisions/adr-0009-clarify-test-fixture-boundaries.md).
- **Reusable backend/fixture infrastructure lives in `Arius.Tests.Shared`, never in a test project.** `Arius.E2E.Tests` must not reference `Arius.Integration.Tests`.
- **Exactly one representative E2E workflow definition.** It runs on Azurite and Azure from the *same* `RepresentativeWorkflowCatalog.Canonical`; backend differences live only inside capability-gated step execution, never as a forked workflow. Don't reintroduce the isolated-scenario matrix.
- **Real archive-tier and rehydration semantics stay in Azure-capability-gated steps.** Azurite must not pretend to support the offline archive tier.
- **The cold-cache transition is lease-guarded.** `E2EFixture.ResetLocalCache` throws if a fixture still holds the cache lease, keeping warm→cold an explicit workflow step.
- **Representative assertions target stable behavior, not storage layout.** Assert snapshot lineage, file counts, dedup/tar lookups, pointer presence/absence, and cleanup — not exact chunk/tar/filetree blob counts.
- **Azurite/E2E suites self-skip at runtime when Docker or Azure credentials are absent**, with a visible reason in the report, rather than being filtered out of the CI matrix (`AzuriteFixture.EnsureAvailable` → `Skip.Test`).
- **Architecture rules are executable.** The Core⊥host boundary, the Azure-isolation rule, the Mediator-only exposure, and the chunk-index/SQLite facades are enforced by `Arius.Architecture.Tests`, not just by convention.
- **The coverage denominator excludes test code.** Test projects, `Arius.Tests.Shared`, and `Arius.Api.FakeTestHost` are ignored in `codecov.yml`; the 90% floor applies to production code only.
- **Production `Arius.Api` has zero references to `Arius.Api.FakeTestHost` and no environment branch.** The scripted `IRepositoryCoreComposer` wins only because the separate out-of-process host pre-registers it before `AddAriusApi()`'s `TryAddSingleton` runs — never by an `IsEnvironment` check in production code.
- **The real-Azure Playwright suite (`e2e/specs`) remains the full-stack behavioral gate.** The hermetic suite (`e2e/hermetic/specs`) is additive scripted-Core coverage for scenarios real Azure can't deterministically fabricate, not a replacement.

## Why this shape

- **One canonical workflow over a scenario matrix** — see [ADR-0001](../../decisions/adr-0001-structure-representative-e2e-coverage.md). It proves Arius can archive a repository, evolve it, re-archive, and restore both latest and previous states as one coherent history, which a disconnected scenario list cannot.
- **Repository-centered fixtures with thin wrappers** — see [ADR-0009](../../decisions/adr-0009-clarify-test-fixture-boundaries.md). Centralizing repository ownership keeps cache/validation state in one place while preserving readable per-suite helpers.
- **Scoped advisory mutation testing** — see [ADR-0005](../../decisions/adr-0005-adopt-scoped-stryker-mutation-testing.md). Mutation pressure goes where it matters most (Core) without making an unstable preview runner a release gate.
- **Enforced overall coverage floor** — see [ADR-0011](../../decisions/adr-0011-require-90-percent-production-line-coverage.md). An overall gate fails loudly on regressions without the noise of per-file thresholds; reviews still judge assertion quality, since the percentage alone is not proof that behavior is tested.
- **Architecture tests as the mechanical layer.** They catch boundary erosion (an Azure `using` in the CLI, a leaked chunk-index internal) deterministically, freeing behavior tests and design review to focus on intent and correctness.
- **Scripted-fake Core over a storage-layer fake or real-Azure-only** — see [ADR-0022](../../decisions/adr-0022-scripted-fake-core-test-harness.md). Replaying real Core's `INotification`/result/DTO types lets Api/Web scenarios (tier, cost, rehydration, warnings) be driven deterministically offline, while the real Mediator pipeline, forwarders, `JobSink`, and SignalR hub stay exercised unchanged — a fidelity a deep storage-layer fake or a hybrid strategy couldn't offer without duplicating the Core-level fixture hierarchy above.

## Open seams / future

- **Mutation testing is local/manual and Core-scoped.** Promoting it to a CI gate, or widening its target beyond `Arius.Core`, is a deliberate future decision once MTP-runner score stability and runtime cost are understood (a later ADR would supersede [ADR-0005](../../decisions/adr-0005-adopt-scoped-stryker-mutation-testing.md)).
- **The representative workflow is benchmark-ready but not yet benchmarked.** The runner exposes stable step names and per-step boundaries so a future benchmark can measure the whole workflow or selected steps (second archive, warm vs cold restore, ready-rehydration restore) without redesigning the suite — no benchmark code exists yet.
- **Dataset scale is one knob.** `RepresentativeScaleDivisor` tunes runtime cost; raising the representative profile toward production scale is a tuning decision, not a redesign.
- **ArchUnitNET cannot see usages inside lambdas / async state machines** (noted in both `DependencyTests` and `ModulithTests`). Boundary violations hidden in closures are not caught by the architecture suite and rely on review.
- **Some wrapper fixtures retain duplicated access paths by design** for ergonomics; keeping new responsibilities from drifting into the wrong fixture layer relies on discipline rather than a hard rule.
- **The scripted fake's only fidelity guard is compile-time coupling.** A runtime drift check — replay real Core's emitted event sequence and diff it against the canonical scripted scenarios — was scoped in the harness design but not built; a scripted scenario can silently diverge from what real Core would actually emit.
- **Vitest covers pure functions and stores only.** Angular component-level rendering is exercised by Playwright, not a component-test harness (`@analogjs/vitest-angular` would be needed for that).
- **`Arius.Migration` has no dedicated test project** ([whole-solution graph](#the-whole-solution-project-graph)) — the v5→v7 migration path relies on the legacy-format coverage inside `Arius.Core.Tests`/`Arius.Integration.Tests` rather than a harness of its own.
