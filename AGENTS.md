# AGENTS.md

## Documentation map

Product documentation lives under [`docs/`](docs/) — start at [`docs/README.md`](docs/README.md):
- **[`docs/design/`](docs/design/)** — how each subsystem works and why; the tree **mirrors `src/`** (core/features, core/shared, hosts, cross-cutting). Start at [`docs/design/README.md`](docs/design/README.md).
- **[`docs/decisions/`](docs/decisions/)** — ADRs: the durable "why" for one-time architectural decisions.
- **[`docs/guide/`](docs/guide/)** — user/operator guides (CLI, Web UI, Explorer, deployment).
- **[`docs/glossary.md`](docs/glossary.md)** — the grounded domain vocabulary (term → defining type/file).
- **[`docs/history/`](docs/history/)** — frozen archaeology (OpenSpec / superpowers / agentic plans); read-only, never maintained.

Keep docs in sync with changes: record a one-time architectural decision as an ADR; update the relevant `docs/design/` doc when a subsystem's shape or invariants change; do **not** restate mechanical code behaviour in prose (the code and its docstrings are the source for that). Each `src/Arius.*` project also has its own nested `AGENTS.md` for project-local conventions.

## General

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

### 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

### 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

### 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

### 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

### 5. Guidance specifically for Anthropic Fable 5

Don't add features, refactor, or introduce abstractions beyond what the task requires. A
bug fix doesn't need surrounding cleanup and a one-shot operation usually doesn't need a
helper. Don't design for hypothetical future requirements: do the simplest thing that
works well. Avoid premature abstraction and half-finished implementations. Don't add
error handling, fallbacks, or validation for scenarios that cannot happen. Trust
internal code and framework guarantees. Only validate at system boundaries (user input,
external APIs). Don't use feature flags or backwards-compatibility shims when you can
just change the code.

## Agent Guidance: dotnet-skills

IMPORTANT: Prefer retrieval-led reasoning over pretraining for any .NET work.
Workflow: skim repo patterns -> consult dotnet-skills by name -> implement smallest-change -> note conflicts.

Routing (invoke by name)
- C# / code quality: modern-csharp-coding-standards, csharp-concurrency-patterns, api-design, type-design-performance
- ASP.NET Core / Web (incl. Aspire): aspire-service-defaults, aspire-integration-testing, transactional-emails
- Data: efcore-patterns, database-performance
- DI / config: dependency-injection-patterns, microsoft-extensions-configuration
- Testing: testcontainers-integration-tests, playwright-blazor-testing, snapshot-testing

Quality gates (use when applicable)
- crap-analysis: after tests added/changed in complex code

Specialist agents
- dotnet-concurrency-specialist, dotnet-performance-analyst, dotnet-benchmark-designer

## Way of Working

- Work in small steps. Work Test-Driven: first, write a failing test. Then, implement. When the tests pass, make a conventional git commit.
- Avoid coupling the test to the implementation - test the behavior.
- When making code changes, always run the relevant tests:
  - Unit test projects: Arius.Core.Tests / Arius.AzureBlob.Tests / Arius.Cli.Tests / Arius.Architecture.Tests / Arius.Explorer.Tests (skip this on non-Windows since it is Windows-only)
  - Integration tests: Arius.Integration.Tests
  - Slow (~ minutes) behavioral test to be run sparingly (eg. at the end of a PR or when making a big refactor) Arius.E2E.Tests

## Session Rules

- Update `README.md` with high signal & accessible for humans if applicable. Do not mention code concepts unless explicitly asked. Do not clutter it with implementation details.
- Update `AGENTS.md` for AI coding agents to reflect the current state of the project if relevant. Do not clutter it with implementation details.
- Keep product docs in sync with the change (see the Documentation map above): ADRs for decisions, `docs/design/` for subsystem shape and invariants, the glossary for vocabulary.

## Scale And Durability
- Arius is a backup tool for important files. Correctness, durability, and recoverability matter more than raw throughput.
- Repository scale can be large: potentially terabytes of binary data and many thousands of small files. Consider both byte scale and file-count scale when designing or optimizing archive, restore, list, and cache behavior.
- Prefer streaming, batching, and bounded-memory or bounded-disk pipelines over whole-repository in-memory materialization when file count can grow.
- Long-running handlers (`ArchiveCommandHandler`, `ListQueryHandler`) are structured as channel-connected stages: bounded channels for backpressure, each stage completes its writer when done (faults propagate via `Complete(exception)`), and a type-level doc header with stage/channel tables. Mirror that structure and documentation style when adding or restructuring pipelines.
- For interactive commands (`ls`), favor responsiveness: small lookup batches, stream output as results arrive, never buffer the full listing (no table/recorder materialization in the CLI).
- Avoid per-file remote round-trips when a batched list, manifest walk, shard lookup, or validated cache can answer the question.
- Blob storage is non-transactional across blobs. A run can leave partial remote updates if it crashes. Consider retry-safe and recoverable from partial flushes, partial uploads, and crashes.
- Local caches can be stale, incomplete, or corrupt. Cache contents are performance hints, not the source of truth.
- Snapshots are the repository commit point. Do not publish a snapshot until all referenced repository data is durably available.
- Prefer designs that can rebuild or revalidate mutable repository metadata after cache loss, corruption, or cross-machine divergence.

## Testing

When writing or reviewing TUnit tests, use the `csharp-tunit` skill.

- **Run tests**: `dotnet test --project <path-to-csproj>`
- **Filter by class**: use `--treenode-filter "/*/*/<ClassName>/*"` (NOT `--filter`)
- **Filter by test name**: use `--treenode-filter "/*/*/*/<TestMethodName>"`
- **List tests**: `dotnet test --project <path-to-csproj> --list-tests`
- The standard `--filter` flag does NOT work with TUnit; it silently runs zero tests.
- **Coverage with TUnit/MTP**: use `--coverage`, not `--collect:"XPlat Code Coverage"`

- Use `FakeLogger<T>` from `Microsoft.Extensions.Diagnostics.Testing` instead of `NullLogger<T>` in test projects.
- Test projects should mirror the structure of the project they exercise so intent stays obvious.
- Put reusable test doubles in `Fakes/`.
- Put scenario-specific test doubles in a local `Fakes/` subfolder beside the tests that use them.

## E2E Test Guidance

- Use the deterministic synthetic repository generator in `src/Arius.E2E.Tests/Datasets/` instead of ad hoc random files for reproducibility.
- Keep synthetic repository rename targets normalized and validated before root-containment checks so representative datasets cannot escape declared roots through path tricks.
- Reject Windows-style absolute dataset paths after slash normalization so cross-platform path validation stays consistent.
- Clean up representative workflow temp roots when fixture creation fails so failed E2E setup does not leak directories.
- Dispose shared test fixture index services before deleting temp roots so cache-backed resources are released in a safe order.
- Representative E2E coverage now runs one canonical workflow across Azurite and Azure instead of an isolated scenario matrix.
- Shared representative workflow coverage should run against both Azurite and Azure when supported by backend capabilities.
- Treat dataset versions (`V1` vs `V2`) and cache transitions (`Warm` vs `Cold`) as explicit workflow steps in one evolving repository history, not incidental fixture behavior.
- No-op archive coverage should assert that unchanged repositories preserve the current latest snapshot rather than publishing a redundant snapshot.
- Keep archive-tier behavior inside capability-gated workflow steps rather than separate top-level representative suites.
- The representative synthetic dataset size is controlled by a single explicit constant in `SyntheticRepositoryDefinitionFactory`; tune it deliberately when changing runtime cost.
- Remove obsolete representative workflow scaffolding when replacing it; do not keep both workflow and scenario models in parallel.
- Keep real archive-tier and rehydration semantics in Azure-capability-gated tests.
- Reusable Azurite and repository-fixture wiring belongs in `src/Arius.Tests.Shared/`, not in another test project assembly.
- Azurite-backed integration and E2E tests are discovered on every CI runner; when Docker is unavailable they should skip at runtime with a visible reason in the test report rather than being filtered out of the matrix.
- `src/Arius.E2E.Tests/` is reserved for actual end-to-end Arius behavior coverage. Do not add self-tests for E2E datasets, fixtures, scenario catalogs, or scenario runners there unless explicitly requested.
- `src/Arius.E2E.Tests/E2ETests.cs` keeps the live Azure credential/configuration sanity check plus narrow hot-tier pointer-file and large-file probes that the representative workflow does not cover directly.

## Code Style Preference

- Make non-test classes `internal`. Only make them `public` when they must be consumed by another non-test assembly; for test access, prefer InternalsVisibleTo.
- Prefer one top-level class per file, with the filename matching the class name.
- Prefer **local methods** over private static methods for helper functionality that is only used within a single method

## Domain language

Use the precise Arius domain vocabulary (binary file, pointer file, `FilePair`, chunk, large/tar/thin chunk, chunk index, shard, chunk size, storage tier hint, filetree, snapshot, content hash, chunk hash, …) consistently in code, tests, docs, and reviews; avoid generic words like "blob" or "pointer" when a precise term exists. Every term is defined and grounded to its code type in **[`docs/glossary.md`](docs/glossary.md)** — the single home for the vocabulary, which also disambiguates the easily-confused cache verbs (validated / revalidated / synchronized).

## Hash type guidance

- Keep distinct hash value objects for distinct identities: `ContentHash`, `ChunkHash`, `FileTreeHash`. Do not collapse them into one generic hash, use inheritance between them, or add implicit conversions (between hash types or to/from `string`).
- Use typed hashes inside the domain and as dictionary/set keys; persisted/wire formats are canonical lowercase hex; convert to string only at boundaries (storage names, payloads, logs, UI).
- Keep hash value objects fail-fast for `default`/uninitialized instances.
- Rationale and the typed-identity model: [ADR-0003](docs/decisions/adr-0003-use-distinct-typed-hashes.md), [ADR-0004](docs/decisions/adr-0004-split-filetree-entry-hash-identities.md), and [`docs/design/core/shared/hashes.md`](docs/design/core/shared/hashes.md).

## Filesystem type guidance

- Prefer strong domain types over raw primitives for Arius-specific values; avoid primitive obsession and stringify/parse round-trips (preserve types until a real foreign boundary: console, config, serialization, external SDK).
- `RelativePath` for repository-relative paths, subtree roots, and multi-segment prefixes; `PathSegment` only when the value is exactly one segment. Don't encode directory-ness via trailing slashes when a typed contract can represent it.
- Keep archive-time/local operational types internal (`BinaryFile`, `PointerFile`, `FilePair`, `LocalDirectory`, `RelativeFileSystem`); route local IO through `RelativeFileSystem`, not raw `System.IO`.
- Rationale: [ADR-0008](docs/decisions/adr-0008-introduce-internal-filesystem-domain-types.md) and [`docs/design/core/shared/filesystem.md`](docs/design/core/shared/filesystem.md).

## Architecture

The full architecture — layering, the per-repository shared-service stack and its lifetimes, the archive/restore/list flows, and each subsystem — is documented under **[`docs/design/`](docs/design/)** (start at [`docs/design/README.md`](docs/design/README.md), which mirrors `src/`). The agent-actionable boundary rules:

- **Features vs Shared.** `src/Arius.Core/Features/` holds vertical slices (`*Command`/`*Query`) that decide **when** to resolve a snapshot, walk a tree, look up chunk metadata, upload chunks, or restore. `src/Arius.Core/Shared/` decides **how** (caching, serialization, blob interpretation). Repository-wide logic reused by more than one feature belongs in `Shared`; single-flow logic belongs in the feature handler. Inject shared services into features; don't construct them ad hoc.
- **Hosts drive Core only through `IMediator`.** `Arius.Cli`, `Arius.Explorer`, and `Arius.Api` each build one provider per repository (`AddMediator()` + `AddArius(...)`) and never call handlers directly. Core depends on `IBlobContainerService`, never the Azure SDK ([ADR-0013](docs/decisions/adr-0013-core-host-separation.md), enforced by architecture tests).
- **Feature handlers must not depend on `IBlobContainerService`/`IBlobService`/`IBlobServiceFactory` directly.** Current approved exceptions: `ArchiveCommandHandler` (container creation) and `ContainerNamesQueryHandler` (repository-external enumeration). `restore` and `ls` are read-only and must not create containers. Remove stale direct blob/encryption deps when a handler no longer uses them.
- **Service lifetimes are per-repository-provider singletons.** Fine for the short-lived CLI, but long-lived hosts (Web/Explorer) must manage provider lifetime — `ChunkIndexService` is single-shot after flush. See [`docs/design/cross-cutting/service-lifetimes.md`](docs/design/cross-cutting/service-lifetimes.md).
- **DI.** Register shared services once per repository; helpers (`FileTreeBuilder`) receive already-constructed services; avoid duplicate service graphs for one repository (they split cache/validation state).

For project-local conventions (CLI display, Web/Angular, Explorer/WPF, test fixtures), see the nested `AGENTS.md` in each `src/Arius.*` project.
