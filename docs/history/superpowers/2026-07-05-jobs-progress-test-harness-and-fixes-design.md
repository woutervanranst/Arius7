# Jobs progress — scripted-fake-Core test harness + review-finding fixes

**Date:** 2026-07-05
**Branch:** `jobs-progress`
**Status:** design (approved for planning)

## 1. Context & goal

The `jobs-progress` branch reworks how Arius surfaces archive/restore progress
(SignalR `JobsHub`, `JobRunner`, `JobSink`, rehydration polling, and the Angular
Jobs overview + `/jobs/:id` detail page). An xhigh workflow code review confirmed
15 defects, clustered around restore cost-approval, reattach state, progress-bar
denominators, and a few lifecycle races.

Most of those defects have **no regression test today** because the only
integration coverage is a Playwright e2e suite that boots the **real** Arius.Api
against **real Azure Blob Storage** and a **real archive** — slow, non-hermetic,
and unable to fabricate the tier/cost/rehydration/warning scenarios the bugs live
in. The Api itself has *zero* HTTP/hub-level test coverage.

**Goal:** stand up a mocked-`Arius.Core` test harness that lets us drive arbitrary
progress / tier / cost / rehydration / warning scenarios deterministically and
offline, then fix all 15 findings (plus two owner-requested extras) each behind a
concrete regression test.

### Constraints & decisions

- **No `Arius.Core` changes.** The UX handoff is explicit ("its events and log
  output are already sufficient"), and this design confirms every fix and the
  harness seam are achievable Api/Web-side. The owner granted latitude to add Core
  *interfaces* if unavoidable; it is not needed. If an `internal`-visibility wall
  is hit, it will be flagged rather than silently widened.
- **Finding #1 is by design, not a bug.** A repository holds at most one
  non-terminal job (`running | awaiting-cost | rehydrating`). An abandoned cost
  prompt correctly parks under "Needs your attention"; the intended recovery is
  *cancel the restore, then archive*. Scheduled archives are intentionally skipped
  while a repo is busy. This is *locked in with a test*, not changed.
- **Harness strategy: scripted fake Core** (chosen over deep-fake / hybrid).

## 2. The scripted-fake-Core harness

### 2.1 Seam

`Arius.Api` talks to `Arius.Core` through Martin Othamar's source-generated
`Mediator` (not MediatR). It builds a **fresh per-repository DI container per job**
inside `RepositoryProviderRegistry.BuildAsync`, which today hard-calls
`AddMediator() + AddAzureBlobStorage() + AddArius(...)`. Core → Api progress flows
as Mediator `INotification`s into the Api's auto-registered forwarders → `JobSink`
→ SignalR; restore cost flows out-of-band via the `RestoreOptions.ConfirmRehydration`
callback → `RestoreApprovalRegistry`.

The Core-composition step becomes a **swappable module** the registry resolves from
the root provider:

- `AddMediator()` **stays in the registry always** — both real and fake need the
  Mediator pipeline and the Api's forwarders.
- **Production** registers the real module → `AddAzureBlobStorage() + AddArius(...)`.
  Byte-identical to today; no behavioral change.
- **Tests** register `AddScriptedCore(scenario)` → the scripted command handlers +
  `FakeStorageCostEstimator` + stub query handlers. It does **not** pull in the real
  storage graph (encryption / compression / chunk-index) — the scripted handlers
  never touch storage.

This is an Api-side abstraction (the registry owns it); no Core change.

### 2.2 The fake

- `ScriptedArchiveHandler : ICommandHandler<ArchiveCommand, ArchiveResult>` —
  `Publish(...)`es a chosen sequence of the **real** archive `INotification` events
  (`ScanCompleteEvent`, `FileScannedEvent`, `FileDedupedEvent`, `ChunkUploadedEvent`,
  `ChunkUploadingEvent`, `TarBundleSealing/Uploaded`, `SnapshotCreatedEvent`) with
  real byte fields, then returns an `ArchiveResult`.
- `ScriptedRestoreHandler : ICommandHandler<RestoreCommand, RestoreResult>` — emits
  `SnapshotResolved` → `TreeTraversalComplete` → `ChunkResolutionComplete` → a
  *timeline* of `RehydrationStatusEvent`s → `ChunkDownloadStarted` / `FileRestored`,
  and drives the `ConfirmRehydration` cost handshake.
- **Cost / tier / wait** = the existing `FakeStorageCostEstimator`
  (`src/Arius.Tests.Shared/Fakes/`).

Because the real Mediator pipeline, forwarders, `JobSink`, SignalR, and the whole
Web app run unchanged, a scenario such as *"1000 pointer-only deduped files + 10 new
100 MB uploads"* or *"park at awaiting-cost; 282 archive chunks; ready in 13 h;
approve on reattach"* reproduces the findings exactly.

### 2.3 Scenario selection

A `ScenarioRegistry` (keyed by repo) holds the next script. Api-integration tests
wire it directly; Playwright e2e sets it via a **test-only control endpoint**
(`POST /test/scenario`) gated to a `Testing` environment.

### 2.4 Fidelity guard — keeping the fake "representative"

A scripted fake is only worth testing against if it cannot silently drift from real
Core. Two mechanisms:

1. **Compile-time coupling** — the fake emits the real Core event/result types; a
   renamed field breaks the build.
2. **Drift alarm** — a handful of tests run the **real** Core against the existing
   `FakeInMemoryBlobContainerService`, capture the emitted event sequence, and assert
   the scripted fake's canonical archive/restore scenarios match shape-and-order.
   This is the one place real Core runs; it is a guard, not broad coverage.

### 2.5 Test tiers

| Tier | Vehicle | New? |
|---|---|---|
| **Unit** | TUnit + Shouldly (`Arius.Api.Tests`) and Web unit specs | extend |
| **Api integration** | `Arius.Api.Integration.Tests` — `WebApplicationFactory<Program>` + `AddScriptedCore` + in-memory hub + SQLite | **new** |
| **e2e** | Playwright against the scripted-fake Api (hermetic) | extend |
| **Fidelity** | real Core vs `FakeInMemoryBlobContainerService` | **new (small)** |

The Api-integration tier is entirely new (the Api has no HTTP/hub coverage today).
It requires `Program` to be test-accessible (`public partial class Program {}` —
trivial). Existing real-Azure e2e specs stay as-is; the new hermetic specs cover the
finding scenarios.

## 3. Consistent byte/counter representation

The representation model is *determined* by the documented event intent plus the
totals Core already emits — it needs no Core change.

### 3.1 Archive — one denominator, original bytes throughout

`ChunkUploadedEvent.OriginalSize`'s XML doc states the intent outright: express the
uploaded layer *"in the same original-dataset units as the scanned/hashed layers
(stored size would otherwise understate progress because of compression)."*

The #5 / #9 bugs share one root cause: the code mixes two denominators — `total`
for scanned/hashed, `totalNew = total − deduped` for uploaded — and that subtraction
**underflows** because `deduped` counts pointer-only files at full `OriginalSize`
while `total` (from `ScanCompleteEvent`) counts them as 0.

**Model:** all three layers divide by the same `D = totalBytes` (original bytes of
the scanned dataset). `totalNew` is **removed as a denominator**.

| Quantity | Definition | Role |
|---|---|---|
| `D = totalBytes` | Σ original bytes of scanned files (`ScanCompleteEvent.TotalBytes`) | the one denominator |
| `scannedBytes / D` | → 100% at scan complete | layer 1 |
| `hashedBytes / D` | → 100% at hash complete | layer 2 |
| `uploadedBytes / D` | Σ `ChunkUploadedEvent.OriginalSize` + tar `UncompressedSize` | layer 3 |
| dedup savings | Σ `FileDedupedEvent.OriginalSize` | KPI only — **never a denominator** |
| "new data" figure | derived **additively** (Σ new-chunk original bytes), not `total − deduped` | KPI label + ETA denominator |

Because deduped content is never uploaded, layer 3 converges to *just under* 100% —
the gap **is** the dedup savings (the mockup's small top-gap). Pointer-only files
contribute 0 to `D` (nothing to read/hash/upload) and their full size to the savings
KPI, so they never appear in the bar (correct — zero work) and the underflow class
of bug disappears. `pct`, ETA, and throughput all stay in original bytes.

A live ETA denominator (new bytes still to upload) comes from forwarding the
existing-but-ignored `ChunkUploadingEvent` (accumulate queued new-chunk bytes),
shown as "estimating…" until routing completes.

### 3.2 Restore — authoritative chunk total; rehydration is conditional & overlaps download

The #8 bug: the web reconstructs the chunk total as `available + rehydrated +
pending`, **omitting `needsRehydration`**. Core already emits the true total on
`ChunkResolutionCompleteEvent(TotalChunks, …, TotalChunkBytes)`; the Api just drops
it (no forwarder).

**Model:** forward `ChunkResolutionCompleteEvent` → `TotalChunks` (and
`TotalChunkBytes`) into `JobSink`; make `TotalChunks` the single hydration
denominator.

| Layer | Fraction | Space |
|---|---|---|
| Planned | 100% (all chunks resolved) | chunks |
| Hydrated & ready | `(available + rehydrated) / TotalChunks` | chunks |
| Restored to disk | `bytesRestored / restoreTotalBytes` | bytes |

**Rehydration is conditional and overlaps download** (owner correction): chunks
already in an online/hydrated tier download *immediately*; only archive-tier chunks
(`needsRehydration` / `pending`) wait for rehydration. When nothing is archive-tier,
"Hydrated & ready" starts at 100% and there is no wait card. So the layers are
**overlapping**, not a hard hydrate-then-download gate: `Planned ⊇ Hydrated & ready
⊇ Restored to disk`, each monotonic 0→100. A single-dataset bar would need per-bucket
chunk *bytes* (which Core does not emit); expressing hydration in chunk-space and
download in byte-space is the honest, Core-change-free representation and will be
documented on `LayeredBarComponent`.

The `available + rehydrated` grouping is **consolidated into one named quantity**
(today it is re-merged in three places — the cost DTO, `readyChunks`, and
`AvailableOrRehydratedCount` — with subtly different meanings).

This also drives the **#6 fix**: `firstChunkSeen` becomes *"a chunk newly became
ready via rehydration"* (a rise in `rehydrated`, or pending→available transitions,
versus the initial classification), **not** *"any chunk is available"* — so a mixed
Standard restore whose online-tier chunks download from the start no longer trips the
15-minute quiet-window.

## 4. Finding-by-finding fix + test matrix

Vehicles: **U** unit · **I** Api integration · **E** e2e · **F** fidelity guard.

### A. Lock in intended behavior (not a bug)
| # | Fix | Test |
|---|---|---|
| 1 | *None.* Repo holds one non-terminal job by design. | **E**: restore parks at awaiting-cost → new archive rejected + scheduled archive skipped → cancel frees repo → archive proceeds. **I**: `HasActiveJob` rejects start while awaiting-cost/rehydrating. |

### B. Reattach state seeding — one coherent change (`AttachToJob` + `GET /jobs/{id}` reconstruct from persisted `StateJson`)
| # | Fix | Test |
|---|---|---|
| 2 | Persist + surface the **cost estimate** so "Review cost ›" re-renders the modal. | **I**: attach parked job → cost present. **E**: Review cost → approve → proceeds. |
| 13 | Persist + surface the **rehydration window** → `hydratedBy` renders on reattach. | **I**: attach rehydrating job → window present. |
| 14 | Surface persisted **AutoResume**; initialise the toggle from it. | **I**: toggle OFF, reattach → still OFF. |

### C. Terminal-state guards (races)
| # | Fix | Test |
|---|---|---|
| 3 | Approval registry: atomic resolve-or-timeout under lock — a late `Resolve` after `TimedOut` cannot be silently dropped. | **U**: concurrent approve at the timeout boundary. |
| 4 | `CompleteJob` → conditional UPDATE (`WHERE status NOT IN (terminal)`); `CancelJob` no-ops on already-terminal. | **U**: `CompleteJob` guard. **I**: cancel-vs-poller-complete race. |

### D. Representation model (§3)
| # | Fix | Test |
|---|---|---|
| 5, 9 | Archive: single denominator `D = totalBytes`; drop `totalNew` denominator. | **U**: `JobSink` pointer-heavy scenario (pct sane, no underflow) + web bar computeds. |
| 8 | Restore: forward `ChunkResolutionCompleteEvent.TotalChunks` as hydration denominator. | **U**: web computeds. **I**: rehydration scenario. |
| 6 | `firstChunkSeen` = *newly* hydrated, not "any available". | **U**: `RehydrationSchedule`. **I**: mixed-tier restore does not trip the quiet-window. |
| 11 | `phaseSentence`: distinguish "nothing new" from "unknown" via a routing-complete flag, not `totalNewBytes === 0`. | **U**: `job-format.ts`. |

### E. Realtime / lifecycle
| # | Fix | Test |
|---|---|---|
| 7 | `/jobs` subscribes to `jobDone`; finished rows move to history and counters update. | **I**: `jobDone` emitted. **E**: job finishes → row leaves Active. |
| 10 | `ResumeRestoreAsync` builds the outcome from the real start (`RehydrationStartedAt` / `job.StartedAt`), not `UtcNow`. | **U**/**I**: duration > 0 via the poller path. |

### F. Endpoint correctness
| # | Fix | Test |
|---|---|---|
| 12 | Use `Snapshot.WarningCount` (true total) at `JobEndpoints:51` + `AttachToJob:100`. | **U**: `JobSink` >200 warnings. **I**: endpoint returns true count. |

### G. Cleanups
| # | Fix | Test |
|---|---|---|
| 15 | Drop `Group.SendAsync("Log", …)` in `JobSink.Log`; keep the warn/error capture side-effect. | **U**: capture still works. |
| — | **Pill center-bottom** (was right-bottom). | **E**: pill centered. |
| — | Remove the `" photos"` literal suffix in the pill. | **U**. |

### H. Fidelity guard (spans all scripted scenarios)
Real Core vs `FakeInMemoryBlobContainerService`: capture the emitted event sequence
and assert the scripted fake's canonical archive/restore scenarios match
shape-and-order. **F**.

## 5. Scope & non-goals

**In scope:** the harness (seam + fake + scenario control + fidelity guard), the new
`Arius.Api.Integration.Tests` project, the byte/counter representation, all 15
findings, pill re-position, and the `" photos"` cleanup.

**Out of scope / non-goals:**
- No `Arius.Core` behavior changes; no Core interface additions unless an
  `internal`-visibility wall forces one (flagged if so).
- Not converting the existing real-Azure e2e specs to the fake — they remain as
  real-backend coverage.
- No new progress semantics beyond making denominators consistent (YAGNI): stored
  vs original *wire* throughput and per-bucket chunk *bytes* are explicitly not
  pursued (would need Core changes for marginal fidelity).

## 6. Plan decomposition

Too large for a single implementation plan. It decomposes into three, in order —
the harness is a prerequisite for the **I**/**E** tests the others rely on:

1. **Harness** (§2): swappable Core-composition module, `ScriptedArchive/RestoreHandler`,
   `ScenarioRegistry` + test-only control endpoint, `Arius.Api.Integration.Tests`
   project (`WebApplicationFactory`), and the fidelity guard. Ships with a first
   scripted scenario proving the pipeline end-to-end.
2. **Representation** (§3, findings 5/6/8/9/11): archive single-denominator + the two
   new forwarders (`ChunkUploadingEvent`, `ChunkResolutionCompleteEvent`), restore
   `TotalChunks` denominator + `readyChunks` consolidation + quiet-window fix, phase
   text — with their **U**/**I** tests.
3. **Lifecycle, reattach & endpoints** (findings 1/2/3/4/7/10/12/13/14/15 + pill
   re-position + `" photos"`): reattach state seeding, terminal-state guards,
   `jobDone` list refresh, poller duration, warning count, dead-`Log` removal — with
   their **U**/**I**/**E** tests.
