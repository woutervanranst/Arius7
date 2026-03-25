## Why

The restore pipeline has robust rehydration re-run detection logic (3 signals: blob existence check, ArchiveStatus property for pending rehydration, idempotent re-request). However, no test exercises this scenario. Integration tests use Azurite with Hot tier, so rehydration is never triggered. A dedicated test should capture the re-run detection behavior to prevent regressions — both with a mock for fast CI feedback and with a real Azure Archive tier E2E test for full confidence.

## What Changes

- **Add mock-based rehydration state machine test**: Create a unit test with a mock `IBlobStorageService` that simulates archive-tier behavior (returns `Tier: Archive`, `IsRehydrating: true/false`). Verify the restore pipeline correctly handles all three states: (1) chunk not yet rehydrated — initiates rehydration, (2) chunk rehydration pending — recognizes pending state and does not issue duplicate request, (3) chunk already rehydrated — downloads from `chunks-rehydrated/` directly.
- **Add real Azure E2E rehydration test**: Create an E2E test against real Azure Blob Storage that archives small files (< 1 KB) to Archive tier, then exercises the full rehydration and restore cycle. This test uses the existing `AzureFixture` pattern (gated by `ARIUS_E2E_ACCOUNT` / `ARIUS_E2E_KEY` env vars). The test should:
  - Archive 2-3 files of ~100-500 bytes each to Archive tier (keep costs minimal).
  - Verify blobs land in Archive tier via `GetMetadataAsync`.
  - Attempt restore — expect rehydration to be initiated (not immediate download).
  - Verify the cost estimate includes the correct chunk counts.
  - Verify re-running restore detects pending rehydration and does not duplicate requests.
  - **Sideload rehydrated chunks for full restore verification**: After verifying rehydration kick-off, bypass the ~15 hour rehydration wait by manually uploading the chunk content to `chunks-rehydrated/{hash}` in Hot tier. The test can either: (a) re-read the original file content locally, gzip+encrypt it, and upload to the rehydrated path, or (b) since the chunk content was just archived, download it from `chunks/{hash}` (which may still be accessible briefly before Azure fully transitions it to Archive tier — or use the local chunk index to reconstruct). Then re-run restore — the pipeline should detect the sideloaded blob in `chunks-rehydrated/`, download from there, and successfully restore the files. Verify byte-identical content.
  - Use a longer timeout (the Archive tier SetBlobTier call can take a few seconds per blob).

## Capabilities

### New Capabilities
_None_

### Modified Capabilities
_None_

## Impact

- **Arius.E2E.Tests**: New test class for rehydration scenarios, following the existing `E2ETests` / `E2EFixture` / `AzureFixture` pattern. Uses small files to keep Azure costs under control (a few cents at most).
- **Arius.Integration.Tests or unit test project**: New mock-based test for rehydration state machine logic.
- **No production code changes**.
- **Cost note**: Archive tier has a 180-day early deletion policy. The test container is deleted immediately after the test, so the archived blobs are deleted well before 180 days. Early deletion fees are prorated and negligible for tiny files (~pennies). The test should document this cost expectation.
