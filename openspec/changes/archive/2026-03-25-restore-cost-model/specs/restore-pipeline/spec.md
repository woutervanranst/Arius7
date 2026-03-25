## MODIFIED Requirements

### Requirement: Cost estimation and user confirmation
The system SHALL display a detailed cost breakdown before restoring and require user confirmation. The cost model SHALL include four components: data retrieval cost, read operation cost, write operation cost, and storage cost for rehydrated copies. Rates SHALL be loaded from a pricing configuration file (JSON) with override capability. The breakdown SHALL show both Standard and High Priority costs per component. The chunk index entry's `compressed-size` field SHALL be used for size calculations. The default assumption for storage duration SHALL be 1 month.

The cost algorithm SHALL be:
- `retrievalCost = totalGB * archive.retrievalPerGB` (or `retrievalHighPerGB` for High Priority)
- `readOpsCost = ceil(numberOfBlobs / 10000) * archive.readOpsPer10000` (or `readOpsHighPer10000`)
- `writeOpsCost = ceil(numberOfBlobs / 10000) * targetTier.writeOpsPer10000`
- `storageCost = totalGB * targetTier.storagePerGBPerMonth * monthsStored`
- `totalCost = retrievalCost + readOpsCost + writeOpsCost + storageCost`

Where `targetTier` is Hot (matching `chunks-rehydrated/` convention) and `monthsStored` defaults to 1.

#### Scenario: Cost estimation with full breakdown
- **WHEN** a restore requires 200 archive-tier chunks totaling 20 GB compressed
- **THEN** the system SHALL compute retrieval cost, read ops cost, write ops cost, and storage cost using config-driven rates and display all four components

#### Scenario: User declines
- **WHEN** the user responds "N" to the confirmation prompt
- **THEN** the system SHALL exit without restoring or rehydrating

#### Scenario: Rehydration priority selection
- **WHEN** archive-tier chunks need rehydration
- **THEN** the system SHALL prompt the user to choose Standard or High Priority and display the cost difference per component

#### Scenario: Pricing config loaded from file
- **WHEN** a `pricing.json` exists in the working directory or `~/.arius/`
- **THEN** the system SHALL load rates from that file instead of the embedded defaults

#### Scenario: Default pricing used when no override
- **WHEN** no `pricing.json` override file exists
- **THEN** the system SHALL use the embedded EUR West Europe rates

### Requirement: Restore cost estimate model
The system SHALL use a `RestoreCostEstimate` record (renamed from `RehydrationCostEstimate`) with per-component cost fields: `RetrievalCostStandard`, `RetrievalCostHigh`, `ReadOpsCostStandard`, `ReadOpsCostHigh`, `WriteOpsCost`, `StorageCost`, plus computed `TotalStandard` and `TotalHigh` properties. The model SHALL also include chunk availability counts: `ChunksAvailable`, `ChunksAlreadyRehydrated`, `ChunksNeedingRehydration`, `ChunksPendingRehydration`, `RehydrationBytes`, and `DownloadBytes`.

#### Scenario: RestoreCostEstimate computed properties
- **WHEN** a `RestoreCostEstimate` is constructed with per-component costs
- **THEN** `TotalStandard` SHALL equal `RetrievalCostStandard + ReadOpsCostStandard + WriteOpsCost + StorageCost` and `TotalHigh` SHALL equal `RetrievalCostHigh + ReadOpsCostHigh + WriteOpsCost + StorageCost`

#### Scenario: Zero chunks needing rehydration
- **WHEN** all chunks are already available (Hot/Cool tier or already rehydrated)
- **THEN** `RestoreCostEstimate` SHALL have zero costs for all rehydration components and the system SHALL skip the rehydration confirmation prompt

### Requirement: Pricing configuration
The system SHALL load Azure pricing rates from a JSON configuration file. The file SHALL contain rate structures for: `archive` (retrievalPerGB, retrievalHighPerGB, readOpsPer10000, readOpsHighPer10000), `hot` (writeOpsPer10000, storagePerGBPerMonth), `cool` (writeOpsPer10000, storagePerGBPerMonth), `cold` (writeOpsPer10000, storagePerGBPerMonth). The embedded default SHALL use EUR West Europe rates. The pricing file SHALL be overridable by placing a file in the working directory or `~/.arius/` config path.

#### Scenario: Pricing file structure
- **WHEN** the pricing configuration is loaded
- **THEN** it SHALL contain archive retrieval rates, archive read operation rates, and target tier write/storage rates

#### Scenario: Override pricing file
- **WHEN** a `pricing.json` file is placed in the working directory
- **THEN** the system SHALL use those rates instead of the embedded defaults

#### Scenario: Malformed pricing file
- **WHEN** a pricing override file cannot be parsed as valid JSON
- **THEN** the system SHALL report an error and exit
