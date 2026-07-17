# Architecture Decision Records

Each ADR records **one** architectural decision — the context, the options weighed, the choice, and its consequences — in [MADR](https://adr.github.io/madr/) format. ADRs are **immutable**: once accepted they are superseded by a later ADR, never rewritten. They are the durable "why"; the [design docs](../design/README.md) describe the resulting current shape, and link back here.

New decision? Copy [`adr-template.md`](adr-template.md).

| ADR | Decision |
|---|---|
| [0001](adr-0001-structure-representative-e2e-coverage.md) | Structure E2E coverage around one representative archive→restore workflow |
| [0002](adr-0002-skip-snapshots-for-no-op-archives.md) | Skip publishing a snapshot for a no-op (unchanged) archive |
| [0003](adr-0003-use-distinct-typed-hashes.md) | Use distinct typed hashes (`ContentHash` / `ChunkHash` / `FileTreeHash`) |
| [0004](adr-0004-split-filetree-entry-hash-identities.md) | Split filetree entry and hash identities |
| [0005](adr-0005-adopt-scoped-stryker-mutation-testing.md) | Adopt scoped Stryker mutation testing |
| [0006](adr-0006-build-filetrees-from-hashed-directory-staging.md) | Build filetrees from hashed-directory staging |
| [0007](adr-0007-separate-phase-and-detail-logging-in-pipeline-handlers.md) | Separate phase and detail logging in pipeline handlers |
| [0008](adr-0008-introduce-internal-filesystem-domain-types.md) | Introduce internal filesystem domain types (`RelativePath`, `PathSegment`) |
| [0009](adr-0009-clarify-test-fixture-boundaries.md) | Clarify test fixture boundaries |
| [0010](adr-0010-use-feature-handlers-for-application-use-cases.md) | Route application use cases through feature handlers (`IMediator`) |
| [0011](adr-0011-require-90-percent-production-line-coverage.md) | Require 90% production line coverage |
| [0012](adr-0012-zstd-as-new-compression-algorithm.md) | Adopt zstd as the compression algorithm (gzip kept read-only) |
| [0013](adr-0013-core-host-separation.md) | Decouple Core from its hosts and storage backend |
| [0014](adr-0014-encryption-format-and-recoverability.md) | AES-256-GCM (`ArGCM1`) format with long-term recoverability |
| [0015](adr-0015-chunk-index-scalability.md) | Scale the chunk index via dynamic-length prefix sharding |
| [0016](adr-0016-multi-machine-cache-coherence.md) | Anchor multi-machine cache coherence on the snapshot epoch |
| [0017](adr-0017-idempotent-non-distributed-recovery.md) | Idempotent, non-distributed crash recovery via metadata-presence commit |
| [0018](adr-0018-archive-tier-metadata-sidecar.md) | v5→v7 migration only: carry Archive-tier chunk metadata in a sidecar |
| [0019](adr-0019-central-file-exclusion-configuration.md) | Centralize file/folder exclusion defaults in Arius.Core (embedded `appsettings.json` + options pattern) |
| [0020](adr-0020-provider-agnostic-cost-estimation.md) | Cost estimation behind a provider-agnostic interface |
| [0021](adr-0021-opt-in-change-detection-hashcache.md) | Opt-in change-detection hashcache for fast-hash archive runs |
| [0022](adr-0022-scripted-fake-core-test-harness.md) | Scripted-fake-Core harness for Api/Web test coverage |
