# Storage accounts, region-aware cost calculator & provider-agnostic pricing (2026-06-27)

Frozen planning artifact for [PR #138](https://github.com/woutervanranst/Arius7/pull/138). `PLAN.md`
is the plan for the **final phase** (moving Azure pricing into `Arius.AzureBlob` behind a Core
interface); the branch delivered three connected things, all implemented in code:

1. **Storage-account management + region-aware cost calculator** — accounts get an Azure region, an
   Overview "Storage accounts" section + edit flyout, wizard changes (region dropdown, container-first
   create, optional alias, server-side local-path picker), and a cost-aware "stored size by tier"
   view on Statistics.
2. **Cost-model correction** — real Azure Retail Prices numbers for all standard public regions;
   per-tier read-ops and Cool/Cold data-retrieval added; restore now prices Cool/Cold retrieval +
   per-tier read-ops + internet egress (first 100 GiB/month free), is region-aware, and prompts on
   any non-zero cost. Confirmed Azure bills "GB" as binary GiB.
3. **Provider-agnostic pricing** — pricing moved out of `Arius.Core` behind `IStorageCostEstimator`,
   with the Azure implementation (+ `pricing.json` + `update-pricing.py`) in `Arius.AzureBlob`.

Where the durable intent landed: [ADR-0020](../../../decisions/adr-0020-provider-agnostic-cost-estimation.md)
· [cost estimation](../../../design/core/shared/cost.md) · [restore-command](../../../design/core/features/restore-command.md)
· [read-queries](../../../design/core/features/queries.md) · [web host](../../../design/hosts/web.md)
· [web-ui guide](../../../guide/web-ui.md).
