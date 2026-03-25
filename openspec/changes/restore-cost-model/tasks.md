## 1. Pricing Configuration

- [x] 1.1 Create `pricing.json` embedded resource with EUR West Europe rates (archive retrieval, read ops, hot/cool/cold write ops, storage)
- [x] 1.2 Create `PricingConfig` model class with deserialization from JSON
- [x] 1.3 Implement pricing file loading: embedded default with override from working directory or `~/.arius/`
- [x] 1.4 Unit test pricing config loading: default, override, malformed file error

## 2. Cost Estimate Model

- [x] 2.1 Rename `RehydrationCostEstimate` to `RestoreCostEstimate` in `RestoreModels.cs`
- [x] 2.2 Add per-component cost fields: `RetrievalCostStandard`, `RetrievalCostHigh`, `ReadOpsCostStandard`, `ReadOpsCostHigh`, `WriteOpsCost`, `StorageCost`
- [x] 2.3 Add computed properties `TotalStandard` and `TotalHigh`
- [x] 2.4 Update all references from `RehydrationCostEstimate` to `RestoreCostEstimate`

## 3. Cost Algorithm

- [x] 3.1 Implement the 4-component cost calculation: retrieval + readOps + writeOps + storage
- [x] 3.2 Wire pricing config into `RestorePipelineHandler` cost estimation
- [x] 3.3 Replace hardcoded USD rates with config-driven rates
- [x] 3.4 Unit test cost algorithm: verify each component calculation, ceil rounding for ops, zero-chunk edge case

## 4. CLI Cost Table

- [x] 4.1 Rewrite cost table in `CliBuilder.cs` to show per-component breakdown with Standard and High Priority columns
- [x] 4.2 Add total row to cost table
- [x] 4.3 Label storage row as "Storage (1 month)" to indicate assumption
- [x] 4.4 Update `ConfirmRehydration` callback signature to accept `RestoreCostEstimate`

## 5. Integration

- [x] 5.1 Integration test: verify cost estimate includes all 4 components with non-zero values for archive-tier restore
- [x] 5.2 Integration test: verify zero costs when no rehydration needed (Hot tier)
