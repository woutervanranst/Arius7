## ADDED Requirements

### Requirement: Rehydration state machine test coverage
The system SHALL have test coverage for all three rehydration states in the restore pipeline: (1) chunk needs rehydration (initiates copy-to-rehydrate), (2) chunk rehydration is pending (recognizes pending state, no duplicate request), (3) chunk is already rehydrated (downloads from `chunks-rehydrated/`). Both mock-based unit tests and real Azure E2E tests SHALL exercise these states.

#### Scenario: Mock test - initiate rehydration
- **WHEN** a unit test runs with a mock `IBlobStorageService` returning `Tier: Archive` and no rehydrated copy exists
- **THEN** the restore pipeline SHALL call the copy-to-rehydrate method for the chunk

#### Scenario: Mock test - pending rehydration detected
- **WHEN** a unit test runs with a mock returning pending rehydration state for a chunk
- **THEN** the restore pipeline SHALL NOT issue a duplicate rehydration request

#### Scenario: Mock test - already rehydrated
- **WHEN** a unit test runs with a mock where `chunks-rehydrated/<hash>` exists
- **THEN** the restore pipeline SHALL download from the rehydrated path

### Requirement: E2E rehydration test with sideload
The system SHALL have an E2E test against real Azure Blob Storage that: archives small files (<1 KB) to Archive tier, verifies blobs land in Archive tier, attempts restore (expects rehydration initiation), verifies re-run detects pending rehydration, then sideloads rehydrated content to `chunks-rehydrated/{hash}` in Hot tier and verifies full restore with byte-identical content. The test SHALL be gated by `ARIUS_E2E_ACCOUNT` / `ARIUS_E2E_KEY` environment variables.

#### Scenario: Archive to Archive tier
- **WHEN** the E2E test archives 2-3 files of ~100-500 bytes with `--tier Archive`
- **THEN** the blobs SHALL be verified as Archive tier via `GetProperties`

#### Scenario: Restore initiates rehydration
- **WHEN** restore is run against Archive-tier blobs
- **THEN** the system SHALL initiate rehydration and report chunks pending

#### Scenario: Re-run detects pending rehydration
- **WHEN** restore is re-run while rehydration is pending
- **THEN** the system SHALL recognize the pending state and not duplicate rehydration requests

#### Scenario: Sideloaded rehydration enables full restore
- **WHEN** chunk content is manually uploaded to `chunks-rehydrated/{hash}` in Hot tier
- **THEN** re-running restore SHALL detect the sideloaded blob, download from it, and restore files with byte-identical content

#### Scenario: Test cost documentation
- **WHEN** the E2E test completes and the container is deleted
- **THEN** the Azure cost SHALL be negligible (prorated early deletion for tiny files = fractions of a cent), documented in test comments
