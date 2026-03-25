## Context

The current restore cost estimation in `RestoreModels.cs` (`RehydrationCostEstimate`) uses hardcoded USD rates ($0.01/GB Standard, $0.025/GB High) and only covers rehydration priority cost. It omits transaction costs (read/write operations), data retrieval costs, and storage costs for rehydrated copies. The CLI (`CliBuilder.cs` L322-361) displays a simple two-line cost estimate. Users need accurate, region-specific cost breakdowns before committing to potentially expensive restore operations.

The rehydrated copies go to `chunks-rehydrated/` in Hot tier. The cost of a restore includes: (1) data retrieval from Archive, (2) read operations on Archive blobs, (3) write operations to Hot tier, and (4) ongoing storage of Hot-tier copies until cleanup.

## Goals / Non-Goals

**Goals:**
- Configurable pricing via a JSON config file (initial: EUR West Europe rates)
- Full cost model: retrieval + read ops + write ops + storage
- Per-component cost breakdown in CLI table with Standard vs High Priority columns
- Rename `RehydrationCostEstimate` to `RestoreCostEstimate` with expanded fields
- Replace hardcoded USD rates with config-driven EUR rates

**Non-Goals:**
- Dynamic pricing fetched from Azure APIs (too complex, rates change infrequently)
- Multi-currency support (file documents the currency, user can edit)
- Egress/bandwidth costs (restore downloads to the same Azure region in typical use)
- Cost estimation for the archive operation itself

## Decisions

### 1. Pricing config as embedded JSON resource

**Decision**: Ship pricing as an embedded JSON resource in Arius.Core with a well-known filename (e.g., `pricing.json`). The file is loadable/overridable by placing a `pricing.json` in the working directory or `~/.arius/` config path.

**Rationale**: Embedded resource means it works out-of-the-box with no setup. File override enables region customization without recompilation. JSON is human-readable and editable.

**Alternative considered**: CLI parameters for rates. Rejected — too many parameters (8+ rates) would clutter the CLI. A config file is more maintainable.

### 2. Cost algorithm with 4 components

**Decision**: The cost calculation is:
```
retrievalCost = totalGB * archive.retrievalPerGB (or retrievalHighPerGB)
readOpsCost   = ceil(numberOfBlobs / 10000) * archive.readOpsPer10000 (or readOpsHighPer10000)
writeOpsCost  = ceil(numberOfBlobs / 10000) * targetTier.writeOpsPer10000
storageCost   = totalGB * targetTier.storagePerGBPerMonth * monthsStored
totalCost     = retrievalCost + readOpsCost + writeOpsCost + storageCost
```
Where `numberOfBlobs` is the count of chunks needing rehydration, `totalGB` is the sum of their compressed sizes, `targetTier` is Hot (matching `chunks-rehydrated/`), and `monthsStored` defaults to 1.

**Rationale**: This models the actual Azure billing components for a copy-blob-from-Archive operation. Each component maps directly to an Azure billing meter. Using `ceil(N/10000)` for operations correctly rounds up partial batches (Azure charges per 10,000 operations).

### 3. Rename `RehydrationCostEstimate` to `RestoreCostEstimate`

**Decision**: Rename the record and expand it with per-component fields: `RetrievalCost`, `ReadOpsCost`, `WriteOpsCost`, `StorageCost`, computed properties for `TotalStandard` and `TotalHigh`.

**Rationale**: "Rehydration" is only one aspect of restore cost. "RestoreCostEstimate" better reflects the full scope. The expanded fields enable the CLI to display a per-component breakdown table.

### 4. Storage cost assumption: 1 month

**Decision**: Default `monthsStored` to 1 month. The CLI displays this assumption in the cost table header (e.g., "Storage (1 month)"). The user is reminded to clean up rehydrated blobs after restore.

**Rationale**: Most users restore and clean up within days. 1 month is a conservative assumption that avoids surprising users with a "free" line item that actually costs money if forgotten.

**Alternative considered**: Prompting the user for months stored. Rejected — adds friction to an already multi-step confirmation flow. 1 month is clearly documented.

### 5. CLI cost table with per-component breakdown

**Decision**: Replace the simple two-line cost display with a Spectre.Console table:
```
┌──────────────────────┬───────────┬───────────────┐
│ Cost Component       │ Standard  │ High Priority │
├──────────────────────┼───────────┼───────────────┤
│ Data retrieval       │ € 0.42   │ € 2.26       │
│ Read operations      │ € 0.13   │ € 1.43       │
│ Write operations     │ € 0.001  │ € 0.001      │
│ Storage (1 month)    │ € 0.37   │ € 0.37       │
├──────────────────────┼───────────┼───────────────┤
│ Total                │ € 0.92   │ € 4.06       │
└──────────────────────┴───────────┴───────────────┘
```

**Rationale**: Users can see which cost component dominates and make informed decisions about priority selection. The two-column layout makes Standard vs High comparison easy.

## Risks / Trade-offs

- **Stale pricing** → Azure rates change occasionally. The embedded JSON becomes stale over time. → Mitigation: the file override mechanism lets users update rates. The pricing file includes a comment with the source URL and date.
- **EUR-only defaults** → Users in other regions pay different rates. → Mitigation: the pricing file is human-editable and clearly documented as EUR West Europe. Users in other regions can override.
- **Storage cost is an estimate** → `monthsStored = 1` may not match actual cleanup timing. → Mitigation: the table header explicitly shows "(1 month)" and the cleanup prompt reminds users to delete rehydrated blobs.
