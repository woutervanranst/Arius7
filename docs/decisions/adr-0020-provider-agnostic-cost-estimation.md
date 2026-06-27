---
status: accepted
date: 2026-06-27
decision-makers: woutervanranst
confidence: high
---

# Cost estimation behind a provider-agnostic interface

## Context and Problem Statement

The Statistics and Restore features need cost figures: a per-tier monthly storage estimate and a pre-restore cost (retrieval/rehydration + operations + egress). The first implementation put the rate model, the embedded `pricing.json`, and the calculators in **Arius.Core** — but tiers, retrieval/rehydration semantics, egress bands, regions, and the rates themselves are **Azure Blob Storage specifics**. Core is otherwise cloud-agnostic: storage already sits behind `IBlobService`/`IBlobContainerService` ports with the only implementation in the `Arius.AzureBlob` adapter ([ADR-0013](adr-0013-core-host-separation.md)), and S3 / local-filesystem backends are the stated reason that boundary exists.

The question for this ADR is **where storage cost estimation lives, and what contract Core exposes so a future backend can price its own storage** without Core carrying any provider's pricing.

## Decision Drivers

* **Core ⊥ provider** — Core must not reference a specific cloud's pricing data or model ([ADR-0013](adr-0013-core-host-separation.md)).
* **A second backend (S3/B2/GCS) should price storage by implementing one interface**, exactly as it implements `IBlobService` today.
* **Scope to current needs** — model what Arius shows (storage-by-tier, restore cost with Standard/High rehydration), not a universal pricing engine.
* **The consumers stay provider-neutral** — the `StatisticsQuery`/`RestoreCommand` handlers and the Web/CLI UIs must depend only on Core types.

## Considered Options

* **Keep cost in Core** (status quo) — pricing.json + calculators in `Arius.Core/Shared/Pricing`.
* **Interface in Core, implementation in the Azure adapter** — mirror the storage port/adapter split.
* For the restore estimate shape: **rich** (per-component breakdown in the Core contract) vs **slim** (counts + Standard/High totals; components private to the provider).
* For the surface: a **single** `IStorageCostEstimator` vs **split** interfaces (regions / storage / restore).

## Decision Outcome

Chosen: **a single `IStorageCostEstimator` port in `Arius.Core.Shared.Cost`, implemented by `AzureBlobCostEstimator` in `Arius.AzureBlob/Pricing`**, returning a **slim** `RestoreCostEstimate`. The Azure adapter owns the embedded region-keyed `pricing.json`, the rate model, and the cost math; Core keeps only the canonical contract (`IStorageCostEstimator` + `StorageCostEstimate` / `TierStorageCost` / `RestoreCostRequest` / `RestoreCostEstimate`, keyed on the existing canonical `BlobTier`). The estimator is passed into `AddArius(...)`, mirroring how `IBlobContainerService` is supplied.

Confidence: high. It is the same ports-and-adapters split the storage boundary already uses and the Architecture tests enforce; reversing it would only re-introduce Azure pricing into Core.

Before — pricing lived in Core and the handler built the calculator itself:

```csharp
// Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs
var pricing = PricingCatalog.LoadEmbedded().Resolve(opts.Region).Pricing;
var estimate = new RestoreCostCalculator(pricing).Compute(/* loose params */);
// AddArius(blobContainer, passphrase, account, container)
```

After — Core depends only on the port; Azure supplies the implementation:

```csharp
// Core handler
var estimate = costEstimator.EstimateRestoreCost(opts.Region, new RestoreCostRequest { … });
// AddArius(blobContainer, passphrase, account, container, costEstimator)   // estimator = AzureBlobCostEstimator
```

### Consequences and Tradeoffs

* Good — Arius.Core contains **zero** Azure pricing data or math; the Architecture test that forbids Core internals leaking across namespaces no longer has pricing types to police.
* Good — a new backend prices storage by implementing one interface, like it already implements `IBlobService`.
* Good — `StatisticsQuery`/`RestoreCommand`, the SignalR cost message, and the SPA are unchanged in shape — they consume Core canonical types.
* Bad — every `AddArius` caller (Api, CLI, Migration, Explorer, tests) must now supply an estimator (one extra argument).
* Bad — the **slim** estimate hides the per-component breakdown (retrieval/ops/storage/egress) from the host, so component-level cost assertions live only in `Arius.AzureBlob.Tests`, not in an integration test. Accepted: the host only renders totals, and the components are Azure-specific.

### Confirmation

`Arius.Architecture.Tests` keeps Core free of provider pricing internals; the pricing math + `pricing.json` are tested in `Arius.AzureBlob.Tests`; Core handler tests use a `FakeStorageCostEstimator`. The rates are regenerated from the Azure Retail Prices API by `src/Arius.AzureBlob/Pricing/update-pricing.py`.

## More Information

Design: [cost estimation](../design/core/shared/cost.md). Related: storage port/adapter split in [storage boundary](../design/core/shared/storage.md) and [ADR-0013](adr-0013-core-host-separation.md). The restore cost model and its earlier in-Core form: [restore-command](../design/core/features/restore-command.md) and the frozen [restore-cost-model](../history/openspec-archive/2026-03-25-restore-cost-model/design.md) spec.
