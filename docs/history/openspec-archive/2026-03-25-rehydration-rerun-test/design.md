## Context

The restore pipeline implements rehydration logic for Archive-tier blobs: it checks availability, initiates copy-to-rehydrate, detects pending rehydration on re-run, and downloads from `chunks-rehydrated/` when ready. This logic handles three states: (1) not yet rehydrated, (2) rehydration pending, (3) already rehydrated. However, no test exercises any of these states. Integration tests use Azurite with Hot tier, so rehydration is never triggered.

## Goals / Non-Goals

**Goals:**
- Mock-based unit test covering all 3 rehydration states (initiate, pending, ready)
- Real Azure E2E test with Archive-tier blobs confirming full rehydration flow
- Sideload trick to bypass the ~15 hour rehydration wait for the "ready" state test
- Verify byte-identical restore content

**Non-Goals:**
- Changing production restore/rehydration logic
- Testing rehydration with actual Azure rehydration (15 hours is impractical for CI)
- Load testing or performance benchmarking of rehydration

## Decisions

### 1. Mock-based unit test for state machine

**Decision**: Create a unit test with a mock `IBlobStorageService` that returns controlled responses for each rehydration state. The mock simulates: HEAD returns `Tier: Archive` (needs rehydration), HEAD returns `IsRehydrating: true` (pending), `chunks-rehydrated/<hash>` exists (ready). Test verifies the pipeline calls the correct methods in each state.

**Rationale**: Fast CI feedback (runs in milliseconds). Tests the decision logic without Azure dependencies. Covers edge cases like duplicate rehydration prevention.

### 2. Real Azure E2E test with small files

**Decision**: Archive 2-3 files of ~100-500 bytes each to Archive tier. Use the existing `AzureFixture` pattern (gated by `ARIUS_E2E_ACCOUNT` / `ARIUS_E2E_KEY`). Keep files tiny to minimize Azure costs (early deletion fees for Archive tier are prorated and negligible for tiny files).

**Rationale**: Real Azure confirms the SDK calls work end-to-end. Small files keep costs under control (pennies at most).

### 3. Sideload trick for full restore verification

**Decision**: After verifying that restore correctly initiates rehydration, bypass the wait by manually uploading chunk content to `chunks-rehydrated/{hash}` in Hot tier. The test re-reads the archived chunk content (from the local file that was just archived), gzip+encrypts it identically, and uploads to the rehydrated path. Then re-runs restore — the pipeline detects the sideloaded blob and downloads from there. Verify byte-identical restored content.

**Rationale**: This tests the full restore download path without the 15-hour wait. The sideloaded blob is indistinguishable from a genuinely rehydrated one — same content, same path, Hot tier. The restore pipeline doesn't (and shouldn't) verify provenance.

**Alternative considered**: Waiting for actual rehydration. Rejected — 15 hours is impractical for any test suite.

### 4. Longer timeout and cost documentation

**Decision**: Use a test timeout of 60 seconds (Archive tier SetBlobTier can take a few seconds per blob). Document the expected Azure costs in a test comment (prorated early deletion for tiny blobs = negligible).

**Rationale**: Explicit timeout and cost documentation prevent surprises for developers running the E2E suite.

## Risks / Trade-offs

- **Archive tier 180-day early deletion fee** → The test container is deleted after the test. For tiny files (~500 bytes), the early deletion cost is negligible (fractions of a cent). → Mitigation: documented in test comments.
- **Sideload may not exactly match genuine rehydration** → The sideloaded blob is uploaded fresh, not copied from Archive. In practice, the restore pipeline doesn't differentiate. → Mitigation: the test verifies the same code path (download from `chunks-rehydrated/`).
- **Brief window where Archive blob might still be readable** → After `SetBlobTier(Archive)`, Azure may take seconds to minutes to fully transition. The test should wait and verify the tier before proceeding. → Mitigation: poll `GetProperties()` until `AccessTier == Archive` before attempting restore.
