# Plan: Move Azure pricing into Arius.AzureBlob behind a cloud-agnostic cost interface

## Context

Cost/pricing currently lives in **Arius.Core** (`Shared/Pricing/*` + `Features/RestoreCommand/RestoreCostCalculator.cs`) with an embedded `pricing.json`. But the rates, tier names, retrieval/rehydration/egress model and the data are **Azure-specific**. Arius.Core is the cloud-agnostic core (ports); Azure specifics belong in the **Arius.AzureBlob** adapter (which already implements `IBlobService`/`IBlobContainerService`).

This refactor introduces a **canonical `IStorageCostEstimator`** interface in Arius.Core (provider-agnostic, scoped to our needs) with the **Azure implementation in Arius.AzureBlob**, so other providers (S3/B2/GCS) can implement the same contract. It also **commits the pricing.json generator script** so the rates can be regenerated.

Dependency graph is already clean ports-and-adapters: `Arius.Core` → (no Arius refs); `Arius.AzureBlob`/`Arius.Api`/`Arius.Cli`/`Arius.Migration`/`Arius.Explorer`/`Arius.Tests.Shared` → reference AzureBlob; `Arius.Core.Tests` does **not** reference AzureBlob (it references `Arius.Tests.Shared`).

### Decisions (confirmed with the user)
1. **Slim canonical `RestoreCostEstimate`** — counts/bytes + currency + `TotalStandard`/`TotalHigh`. The Azure-specific per-component breakdown (retrieval/ops/storage/download/egress) stays **internal** to the Azure implementation.
2. **Single `IStorageCostEstimator`** interface: `Regions` + `EstimateStorageCost` + `EstimateRestoreCost`.

---

## 1. Canonical model — `Arius.Core/Shared/Cost/`
New namespace `Arius.Core.Shared.Cost`, all **public**:
- `interface IStorageCostEstimator`
  - `IReadOnlyList<string> Regions { get; }`
  - `StorageCostEstimate EstimateStorageCost(string? region, IReadOnlyList<ChunkTierStatistic> storedByTier)`
  - `RestoreCostEstimate EstimateRestoreCost(string? region, RestoreCostRequest request)`
- `record StorageCostEstimate(string Region, string Currency, IReadOnlyList<TierStorageCost> Tiers, double TotalPerMonth)` — (was `StorageCostBreakdown`).
- `record TierStorageCost(BlobTier Tier, long UniqueChunks, long StoredSize, double CostPerMonth)` — moved unchanged.
- `record RestoreCostEstimate` **(slim)**: `ChunksAvailable, ChunksAlreadyRehydrated, ChunksNeedingRehydration, ChunksPendingRehydration, BytesNeedingRehydration, BytesPendingRehydration, DownloadBytes, Currency, TotalStandard, TotalHigh`. (Drop the per-component fields; `TotalStandard/High` become plain init properties set by the impl.)
- `record RestoreCostRequest`: the inputs the handler already accumulates — per-tier download (`Hot/Cool/Cold DownloadChunks/Bytes`), archive `ChunksNeedingRehydration/BytesNeedingRehydration`, `ChunksPendingRehydration/BytesPendingRehydration`, `ChunksAvailable`, `ChunksAlreadyRehydrated`, `DownloadBytes`, `MonthsStored`.
- Reuses existing Core types `ChunkTierStatistic` (`Shared/ChunkIndex`) + `BlobTier` (`Shared/Storage`) — `BlobTier` is already the canonical tier.

## 2. Azure implementation — `Arius.AzureBlob/Pricing/`
- **Move** `pricing.json` here; embed in `Arius.AzureBlob.csproj` `<EmbeddedResource>`, remove from `Arius.Core.csproj`.
- **Move** `PricingCatalog`→`AzurePricingCatalog`, plus `RegionPricing` + `TierRates` (stay **internal** to AzureBlob; drop the `[SharedWithinAssembly]` once they're in one namespace).
- **Move** the math from `StorageCostCalculator` + `RestoreCostCalculator` into AzureBlob as internal helpers. Keep an **internal rich `AzureRestoreCost`** record (with the component breakdown) so the math stays unit-testable; the estimator maps it → slim canonical `RestoreCostEstimate`.
- `public sealed class AzureBlobCostEstimator : IStorageCostEstimator` — loads the embedded catalog once; implements the three methods (region resolution incl. "Unknown"→default stays here).
- `InternalsVisibleTo("Arius.AzureBlob.Tests")` (check/add in AzureBlob's `AssemblyMarker.cs`).

## 3. Wiring
- `Arius.Core/ServiceCollectionExtensions.cs` (`AddArius`): add a parameter `IStorageCostEstimator costEstimator`; `services.AddSingleton(costEstimator);`; the **StatisticsQuery** (line ~181) and **RestoreCommand** (line ~134) handler factories resolve `IStorageCostEstimator` from `sp`. Remove the `StorageCostCalculator` registration (line ~179).
- `StatisticsQueryHandler` ctor: `StorageCostCalculator` → `IStorageCostEstimator`; call `EstimateStorageCost`, map to `RepositoryStatistics`.
- `RestoreCommandHandler` ctor: add `IStorageCostEstimator`; replace the internal `new RestoreCostCalculator(PricingCatalog.LoadEmbedded()…)` with `costEstimator.EstimateRestoreCost(opts.Region, request)`; keep the `TotalStandard > 0` gate. **JobRunner + frontend unchanged** (slim estimate keeps every field they read).
- Update the **6 `AddArius` call sites** to pass the estimator: `RepositoryProviderRegistry` (Api — inject the app singleton via ctor), `CliBuilder`, `Arius.Migration/Program.cs`, `Arius.Explorer/Infrastructure/RepositorySession.cs`, and the two test call sites. Production callers already reference AzureBlob → pass `new AzureBlobCostEstimator()` (or the injected singleton).
- `Arius.Api/Program.cs`: register `IStorageCostEstimator` → `AzureBlobCostEstimator` singleton. `PricingEndpoints` injects it for `Regions` (replaces the static `PricingCatalog.LoadEmbedded().RegionNames`).

## 4. Tests
- **Move** `RestoreCostCalculatorTests` + `PricingConfigTests` from `Arius.Core.Tests` → **`Arius.AzureBlob.Tests`**, retargeted to `AzureBlobCostEstimator` + its internal `AzureRestoreCost`/catalog (real `pricing.json`). Keeps the component-level + egress + per-tier assertions, now in the adapter that owns them.
- Add `FakeStorageCostEstimator` to **`Arius.Tests.Shared`** (deterministic `IStorageCostEstimator`).
- `RepositoryTestFixture` (`Arius.Tests.Shared`): expose `IStorageCostEstimator CostEstimator` (default = `new AzureBlobCostEstimator()`) and thread it into `CreateRestoreHandler()`.
- `StatisticsQueryHandlerTests` (Core.Tests): construct with `FakeStorageCostEstimator` (Azure-free, deterministic) and assert the handler maps per-tier sizes→costs + total + currency/region.
- `RestoreCostModelTests` (Integration, refs AzureBlob): assert **slim** fields (`TotalStandard > 0`, `TotalHigh > TotalStandard`, counts) instead of per-component (component asserts now live in AzureBlob.Tests).
- `RestoreCommandHandlerTests` (Core.Tests): unchanged except handlers come from the fixture (estimator injected); the captured-estimate test asserts the slim fields it already uses (counts/bytes).

## 5. Generator script
Commit the Azure-Retail-Prices generator as **`src/Arius.AzureBlob/Pricing/update-pricing.py`** (next to `pricing.json`), with a usage docstring. It queries `prices.azure.com` (EUR, LRS, `General Block Blob v2` + `Bandwidth`), covers all standard public commercial regions (Gov/MEC/sovereign excluded), and writes the sibling `pricing.json`. Run: `python3 src/Arius.AzureBlob/Pricing/update-pricing.py`.

## Files at a glance
| Area | Create | Move/Modify |
|---|---|---|
| Core abstraction | `Shared/Cost/IStorageCostEstimator.cs`, `Shared/Cost/CostModels.cs` (StorageCostEstimate, TierStorageCost, RestoreCostEstimate, RestoreCostRequest) | delete `Shared/Pricing/*`; `Features/RestoreCommand/RestoreCostCalculator.cs` (removed → AzureBlob); `Features/StatisticsQuery/StatisticsQuery.cs` (handler ctor); `ServiceCollectionExtensions.cs`; `Features/RestoreCommand/RestoreCommandHandler.cs` + `RestoreCommand.cs` (RestoreCostEstimate usings); `Arius.Core.csproj` (drop embedded resource) |
| Azure adapter | `Pricing/AzureBlobCostEstimator.cs`, `Pricing/AzurePricingCatalog.cs`, `Pricing/pricing.json` (moved), `Pricing/update-pricing.py` | `Arius.AzureBlob.csproj` (embed resource), `AssemblyMarker.cs` (InternalsVisibleTo) |
| API | — | `Program.cs` (register estimator), `Composition/RepositoryProviderRegistry.cs` (inject + pass to AddArius), `Endpoints/PricingEndpoints.cs` (use estimator) |
| Other callers | — | `Cli/CliBuilder.cs`, `Migration/Program.cs`, `Explorer/Infrastructure/RepositorySession.cs` |
| Tests | `Arius.AzureBlob.Tests/Pricing/*` (moved calc tests), `Arius.Tests.Shared/.../FakeStorageCostEstimator.cs` | `Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`, `Arius.Core.Tests/.../StatisticsQueryHandlerTests.cs`, `Arius.Integration.Tests/Pipeline/RestoreCostModelTests.cs`, the 2 test AddArius call sites; **delete** the Core.Tests pricing tests |

## Reuse (don't reinvent)
- Ports-and-adapters precedent: `IBlobServiceFactory`/`IBlobContainerService` (Core) ↔ `AzureBlobServiceFactory` (AzureBlob), passed into `AddArius` — mirror this for the estimator.
- `BlobTier` + `ChunkTierStatistic` as the canonical tier/stat inputs.
- The existing cost math (just relocated) — no formula changes.

## Verification
- `dotnet build src/Arius.slnx` (0 errors). `dotnet test` for **Arius.Core.Tests, Arius.AzureBlob.Tests, Arius.Api.Tests, Arius.Cli.Tests, Arius.Architecture.Tests** — all green (Architecture test no longer sees the moved internal pricing types in Core).
- `npm --prefix src/Arius.Web run build` — no change expected (DTO/SignalR shapes unchanged).
- Run `python3 src/Arius.AzureBlob/Pricing/update-pricing.py` → regenerates `pricing.json` with no diff.
- Smoke: `/api/pricing/regions` still returns the region list; Statistics cost-by-tier + restore cost still computed (now via `AzureBlobCostEstimator`).
