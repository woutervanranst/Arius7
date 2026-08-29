---
status: "accepted"
date: 2026-08-29
decision-makers: ["Wouter Van Ranst"]
consulted: ["Claude Code"]
informed: ["Arius maintainers"]
confidence: "high"
---

# Treat the requested upload tier as a ceiling: never archive a chunk within the small-file threshold

## Context and Problem Statement

The archive pipeline takes two size-based decisions on two *different* sizes. Stage 3 routes each file to the large-chunk or tar-bundle path by comparing its **uncompressed** size to `ArchiveCommandOptions.SmallFileThreshold` (1 MB) — a bundling decision, correct as it stands. `ChunkStorageService` then applies `opts.UploadTier` to the finished blob. But what Azure bills and rehydrates is the **stored** blob: compressed with zstd and encrypted. A file just above the threshold can compress far below it, and nothing re-checked the size before tiering.

Measured over four production runs (~2.8 GB of logs, all `--tier Archive`): **715 of 1436 large-chunk uploads (49.8%) landed in the Archive tier storing 1 MB or less**, 220 of them under 100 KB, the smallest 228 bytes from a 4 MB source file. They were `.eml` files of 1–2 MB compressing 10–18000×. Archive-tier rehydration is charged per blob and takes hours; on Azure list prices the tier saves so little on a blob that small that the break-even against ever rehydrating it once is measured in years, not months. v5 Arius guarded exactly this in `EncryptedCompressedStorage.SetChunkStorageTierPerPolicy`, with a comment putting the break-even for a 1 MB chunk at ~5.5 years; the v7 rewrite dropped the guard, and no equivalent has existed in this repository since.

The question for this ADR is **where a stored-size floor on the Archive tier belongs, what the floor should be, and what the chunk index should then record as a chunk's storage tier**.

## Decision Drivers

* The floor must be exact, not an estimate — a mis-tiered chunk costs real money on restore.
* It must cover every path that can produce an Archive-tier blob, including the tiny end-of-run partial tar bundle, without the rule being written twice.
* The [chunk index](../glossary.md#chunk-index) must describe storage as it *is*: a [storage tier hint](../glossary.md#storage-tier-hint) claiming `Archive` for an online blob makes restore pay to rehydrate a blob it could download.
* An explicitly requested online tier (`--tier Cool`) must be honoured unchanged at any size.
* No new operator-facing knob: `performance.md` records that the only per-run options are `SmallFileThreshold`, `TarTargetSize`, and `UploadTier`.
* A blob recovered from a prior crashed run must not be re-tiered — on Azure, moving one out of the Archive tier *is* a rehydration.

## Considered Options

* **Unconditional tiering** — the status quo.
* **Route by stored size** — compress before deciding the large/tar route, so these files become tar members instead.
* **A floor in `ArchiveCommandHandler`** — re-tier from the feature handler after the upload returns, using `uploadResult.StoredSize`.
* **A floor inside `ChunkStorageService.UploadChunkAsync`**, at the existing `SetTierAsync` call, with the threshold passed in from `ArchiveCommandOptions` and the resulting tier returned to the caller.
* **A fresh private const inside `ChunkStorageService`** rather than reusing `SmallFileThreshold`.

## Decision Outcome

Chosen option: **a floor inside `ChunkStorageService.UploadChunkAsync`, fed by `opts.SmallFileThreshold`, with the effective tier returned on `ChunkUploadResult`** — because it is the only place the stored size exists before the tier is applied, and it is a single choke point both upload paths already funnel through.

`tier` becomes a **ceiling rather than a mandate**: `GetActualStorageTier` downgrades an `Archive` request to `Cold` when `storedSize <= smallFileThreshold`, and leaves every other request untouched. `Cold` matches v5's choice and is the cheapest tier that stays online. The comparison is inclusive, as in v5.

Making the service override a caller-supplied argument is only safe because the caller is *forced* to learn the outcome: `ChunkUploadResult.ActualTier` is positional and non-defaulted, and stages 4a/4c record it — not `opts.UploadTier` — as the chunk's storage tier hint. The `BlobAlreadyExistsException` recovery branch reports the tier read off the existing blob and never re-tiers.

The threshold is reused rather than reinvented because it is the same operator-visible number and v5 left the open TODO to derive it from the command options. The two comparisons stay separate: routing compares the *uncompressed* size, the ceiling compares the *stored* size.

Confidence: high. The mechanism is small, exact, unit-tested at both boundaries, and reproduces a guard that ran in production in v5. What is *not* certain is the number: 1 MB is a deliberately conservative floor, chosen so it only ever re-tiers blobs whose archive savings are provably negligible. A strictly economic floor would be considerably higher and would pull most tar bundles online too — that is a product-cost decision, not a bug fix, and would need its own ADR.

Before — the tier is applied to every chunk regardless of what was stored:

```csharp
await blobs.SetMetadataAsync(blobName, metadata, cancellationToken);
await blobs.SetTierAsync(blobName, tier, cancellationToken);          // 228-byte blob → Archive

return new ChunkUploadResult(chunkHash, storedSize, AlreadyExisted: false, OriginalSize: sourceSize);
```

After — the request is a ceiling, and the outcome is reported:

```csharp
await blobs.SetMetadataAsync(blobName, metadata, cancellationToken);

var actualTier = GetActualStorageTier(tier, storedSize, smallFileThreshold);
await blobs.SetTierAsync(blobName, actualTier, cancellationToken);    // 228-byte blob → Cold

return new ChunkUploadResult(chunkHash, storedSize, AlreadyExisted: false, ActualTier: actualTier, OriginalSize: sourceSize);

static BlobTier GetActualStorageTier(BlobTier targetTier, long storedSize, long smallFileThreshold)
    => targetTier == BlobTier.Archive && storedSize <= smallFileThreshold
        ? BlobTier.Cold
        : targetTier;
```

### Consequences and Tradeoffs

* Good, because the floor is applied to the size Azure actually bills, at the one moment that size is known and the blob is still online.
* Good, because one guard covers the large path, the tar path, and the tiny end-of-run partial bundle — `TarBuilder` needs no minimum-bundle-size logic at all.
* Good, because the storage tier hint stops being a statement of intent and becomes an observation, which also makes `ChunkTierStatistic` and the cost estimates honest.
* Good, because `--tier Cool`/`Cold`/`Hot` runs are provably unaffected (a green boundary test pins this).
* Bad, because a `Shared` service now overrides an argument its caller supplied. This is mitigated, not removed, by returning `ActualTier`; a caller that ignored the result would record a false tier.
* Bad, because **this changes behaviour on the default path** — `UploadTier` defaults to `Archive`, so every default `arius archive` run tiers differently from before, with no opt-out flag. That is deliberate: an option to keep archiving tiny chunks would be an option to keep losing money.
* Bad, because the decision is **forward-only**. The ~715 chunks already in the Archive tier stay there; their hint says `Archive`, which is truthful, so nothing is broken, but correcting them would mean paying for 715 rehydrations. Any such sweep is a separate decision.
* Neutral, because the fix corrects the *tier* of those chunks but not the *blob count*: routing still keys off the uncompressed size, so a compressible large file remains its own standalone blob rather than a tar member. Recorded as an open seam in [archive-command](../design/core/features/archive-command.md#open-seams-future).
* Neutral, because `SmallFileThreshold` now feeds two comparisons. Retuning it moves both; deriving one from the other was rejected precisely so the coupling stays visible.

### Confirmation

`ChunkStorageServiceUploadTests` pins the invariant at both boundaries — a compressible chunk requested as `Archive` lands on `Cold`; an incompressible 2 MB chunk stays on `Archive`; a chunk requested as `Cool` stays on `Cool` at any size; and the already-exists path reports the blob's own tier without re-tiering it. `ArchiveRecoveryTests` confirms end-to-end that the chunk index records the actual tier for both a large chunk that compresses within the threshold and a tar-backed file.

In production the `[upload] Done …, tier={Tier}` and `[tar] Uploaded …, tier={Tier}` detail lines make it directly greppable — this bug went unnoticed for as long as it did because no per-chunk log line recorded a tier at all.

## Pros and Cons of the Options

### Unconditional tiering (status quo)

* Good, because the tier argument means exactly what it says.
* Bad, because half of all large-chunk uploads were archived at a size where the tier is a net loss.

### Route by stored size

* Good, because it addresses the deeper cause: these files should have been *bundled*, not merely re-tiered — a bundle amortises one rehydration over many files.
* Bad, because the route is chosen before any compression happens, so it needs a compressibility probe, which is an estimate and would still need an exact backstop.
* Bad, because `TarBuilder` holds the **uncompressed** bundle in memory and seals on uncompressed size, so `TarTargetSize` is what bounds RAM; admitting big-but-compressible files would blow that bound.
* Not mutually exclusive with this ADR, and recorded as an open seam.

### A floor in `ArchiveCommandHandler`

* Good, because tier policy would visibly live in the feature that owns the options.
* Bad, because it needs a second public method on `IChunkStorageService` and a second `SetTierAsync` round-trip, and it opens a window in which a committed blob is untiered.
* Bad, because the rule would be written twice, once per upload stage — and the handler is not where the blob protocol lives.

### A fresh private const inside `ChunkStorageService`

* Good, because it makes the floor unambiguously a storage-layer invariant, independent of any feature's options.
* Bad, because it silently duplicates a number the operator already sets, so the two could drift apart with no signal.
* Bad, because it leaves v5's `// TODO Derive this from the IArchiveCommandOptions?` unanswered.

## More Information

The v5 implementation this restores: [`EncryptedCompressedStorage.SetChunkStorageTierPerPolicy`](https://github.com/woutervanranst/Arius/blob/main/src/Arius.Core/Shared/Storage/EncryptedCompressedStorage.cs#L229). Related: [ADR-0017](adr-0017-idempotent-non-distributed-recovery.md) (the commit protocol whose recovery branch this constrains), [ADR-0020](adr-0020-provider-agnostic-cost-estimation.md) (which consumes the per-tier statistics this makes truthful), and [ADR-0018](adr-0018-archive-tier-metadata-sidecar.md) (which relies on native v7 writing metadata while a blob is still online, before it is tiered).
