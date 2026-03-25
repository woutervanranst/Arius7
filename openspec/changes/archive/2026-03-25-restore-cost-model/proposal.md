## Why

The current restore cost estimation only includes rehydration priority cost (hardcoded USD rates: $0.01/GB Standard, $0.025/GB High). It omits significant cost components: transaction costs (read operations), data retrieval costs, and the write + storage costs for the rehydrated copies in the target tier. Users deserve an accurate cost estimate before committing to a restore operation. Pricing should be configurable per-region via a config file rather than hardcoded.

## What Changes

- **Add Azure pricing config file**: Create a pricing configuration file (JSON) with actual Azure rates. The initial values are EUR prices for West Europe:
  ```json
  {
    "archive": {
      "retrievalPerGB": 0.0204,
      "retrievalHighPerGB": 0.1102,
      "readOpsPer10000": 6.6094,
      "readOpsHighPer10000": 71.6011
    },
    "hot": {
      "writeOpsPer10000": 0.05,
      "storagePerGBPerMonth": 0.018
    },
    "cool": {
      "writeOpsPer10000": 0.10,
      "storagePerGBPerMonth": 0.01
    },
    "cold": {
      "writeOpsPer10000": 0.08,
      "storagePerGBPerMonth": 0.0036
    }
  }
  ```
  The config file is loaded at startup and passed to the cost estimation logic. Currency and region are documented in the file.

- **Implement the copy cost algorithm**: The cost calculation models the full cost of copying archive-tier blobs to a rehydrated tier. Given `numberOfBlobs`, `totalGB`, `targetTier` (hot/cool/cold — currently always hot for `chunks-rehydrated/`), `monthsStored` (how long the rehydrated copies will exist before cleanup), and `isHighPriority`:
  ```
  retrievalCost = totalGB * archive.retrievalPerGB (or retrievalHighPerGB)
  readOpsCost   = (numberOfBlobs / 10000) * archive.readOpsPer10000 (or readOpsHighPer10000)
  writeOpsCost  = (numberOfBlobs / 10000) * targetTier.writeOpsPer10000
  storageCost   = totalGB * targetTier.storagePerGBPerMonth * monthsStored
  totalCost     = retrievalCost + readOpsCost + writeOpsCost + storageCost
  ```
  The `monthsStored` parameter accounts for the ongoing storage cost of rehydrated copies. Default assumption: 1 month (user is prompted to clean up after restore). The CLI should display this assumption and allow override.

- **Replace hardcoded USD rates**: Remove the hardcoded `0.01` and `0.025` USD/GB constants in `RehydrationCostEstimate`. Replace with the algorithm above using config-driven rates.

- **Expand `RehydrationCostEstimate` model**: Add fields for retrieval cost, read operation cost, write operation cost, and storage cost. Rename to `RestoreCostEstimate`. Provide both Standard and High Priority totals with per-component breakdown.

- **Update CLI cost table**: Expand the Spectre.Console cost table in `CliBuilder.cs` to show all cost components:
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

## Capabilities

### New Capabilities
_None_

### Modified Capabilities
- `restore-pipeline`: Update cost estimation requirement to use the copy cost algorithm with retrieval, read ops, write ops, and storage components. Rates loaded from config file instead of hardcoded. Target tier for rehydrated copies is always Hot (matching `chunks-rehydrated/` convention).
- `cli`: Update restore cost confirmation display to show the full cost breakdown table with all cost components and both priority levels.

## Impact

- **New config file**: Pricing config file (e.g., `pricing.json` or embedded resource) with Azure rates. EUR West Europe as initial values.
- **Arius.Core/Restore**: `RestorePipelineHandler` cost estimation logic replaced with the copy cost algorithm. `RehydrationCostEstimate` in `RestoreModels.cs` renamed to `RestoreCostEstimate` with per-component cost fields. The `ConfirmRehydration` callback signature changes to take the expanded estimate.
- **Arius.Cli**: `CliBuilder.cs` cost table display (L322-361) needs rewriting for the new table layout.
- **No new dependencies**.
