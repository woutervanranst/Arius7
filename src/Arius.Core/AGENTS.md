# AGENTS.md — Arius.Core

Root contract (think-before-coding, simplicity, testing workflow, code style, the
documentation map): [`../../AGENTS.md`](../../AGENTS.md). This file adds only what is
true of **this** project. Architecture/how-it-works lives in the design docs — start at
[`../../docs/design/README.md`](../../docs/design/README.md); domain vocabulary is in
[`../../docs/glossary.md`](../../docs/glossary.md). Don't restate either here.

`Arius.Core` is the **host-agnostic domain**: no Azure SDK, no UI. Hosts drive it through
`IMediator`. Two top-level folders, and a hard rule about which one new code goes in:

- **`Features/`** — one folder per user-facing workflow (`ArchiveCommand`,
  `RestoreCommand`, `ListQuery`, `RepairChunkIndexCommand`, the `*Query` reads). A slice
  decides *when* — it orchestrates the shared services it needs. A `Command`/`Query`
  record + its handler + slice-local helpers + `Events.cs`/`Models.cs` live together.
  Design: [`../../docs/design/core/features/`](../../docs/design/core/features/).
- **`Shared/`** — the reusable mechanisms that decide *how*: `Snapshot`, `FileTree`,
  `ChunkIndex`, `ChunkStorage`, plus `Compression`, `Encryption`, `Hashes`, `FileSystem`,
  `Streaming`, and the `Storage` boundary (`IBlobContainerService`/`IBlobService` — the
  only seam the Azure impl plugs into; do not reach for a cloud SDK in Core).
  Design: [`../../docs/design/core/shared/`](../../docs/design/core/shared/).

**Split rule:** logic specific to one workflow stays in its `Features/` slice; promote to
`Shared/` only when a *second* slice needs it. Don't add generic "manager" layers.

## Local idioms

- **Long-linear handler + in-code pipeline doc.** A handler is one long `Handle` that
  inlines its stages with numbered `// ── Stage N ──` banners, mirrored in the type's
  XML `<summary>` (see `Features/ArchiveCommand/ArchiveCommandHandler.cs` — channels,
  events, and the stage/dataflow diagram all live in the docstring). That docstring **is**
  the pipeline documentation: keep it in sync with the code and **do not duplicate it** in
  markdown. `ListQueryHandler` shows the same idiom for a streaming read.
- **Symmetric vocabulary for mirrored ops.** Operations that mirror each other share a
  shape (e.g. `ListQueryHandler`'s remote/local read+merge quartet; archive/restore
  up/down). Name the halves symmetrically.
- **Everything resolves through `IMediator`.** Hosts never call a handler directly;
  requests go in as `ICommand`/`IQuery`, progress goes out as `INotification` events.
- **`internal` by default**, public only for the host-facing surface (the request/result
  records and `ServiceCollectionExtensions`). Tests see internals via
  `InternalsVisibleTo` in `AssemblyMarker.cs` — add a new test assembly there, don't widen
  visibility.

## Wiring — `ServiceCollectionExtensions.cs`

`AddArius(blobContainer, passphrase, account, container)` registers the shared services
and **explicit handler factories** (handlers take per-repository `accountName`/
`containerName` ctor args that DI can't supply). One provider == one repository; services
are singletons scoped to that provider. **`AddMediator()` is intentionally NOT called
here** — the source generator must run in the outermost assembly (a host or test
project), so the host calls it. Adding a handler that needs account/container? Register a
factory here too.

## Build & test (this project only)

- `dotnet build src/Arius.Core` — but a changed shared record/interface ripples into
  `Arius.Cli(.Tests)`; build all `src/Arius.*` for contract changes.
- TUnit (Microsoft.Testing.Platform), not VSTest: `dotnet test --project
  tests/Arius.Core.Tests` and filter with `-- --treenode-filter "/*/*/Class/*"` (MTP
  swallows `Console` output). Coverage on arm64 needs coverlet.console, not
  dotnet-coverage.

## Gotchas / relevant ADRs

- **Breaking changes are fine in this dev phase** — no migrations/back-compat for
  persisted formats.
- Distinct typed hashes (`ContentHash`/`ChunkHash`/`FileTreeHash`) are deliberate — don't
  collapse them: ADR-0003, ADR-0004; design [`hashes`](../../docs/design/core/shared/hashes.md).
- `Shared/FileSystem` uses internal domain types over raw paths/`FileInfo`: ADR-0008;
  design [`filesystem`](../../docs/design/core/shared/filesystem.md).
- Service **lifetime** is the sharp edge: the per-repository model assumes a short-lived
  provider; `ChunkIndexService` is single-shot after flush. ADR-0016, ADR-0017; see
  [`../../docs/design/cross-cutting/service-lifetimes.md`](../../docs/design/cross-cutting/service-lifetimes.md).
- Verify the compression round-trip inline (bounded tee at write time); never re-download
  archive-tier blobs to check.
