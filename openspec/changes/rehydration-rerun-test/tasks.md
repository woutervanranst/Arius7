## 1. Mock-Based Rehydration Unit Tests

- [x] 1.1 Create mock `IBlobStorageService` that simulates Archive tier (returns `Tier: Archive`, no rehydrated copy)
- [x] 1.2 Test: chunk needs rehydration — verify pipeline initiates copy-to-rehydrate
- [x] 1.3 Test: chunk rehydration pending — verify pipeline does not issue duplicate request
- [x] 1.4 Test: chunk already rehydrated — verify pipeline downloads from `chunks-rehydrated/`

## 2. E2E Rehydration Test Setup

- [x] 2.1 Create E2E test class following `E2ETests` / `AzureFixture` pattern, gated by `ARIUS_E2E_ACCOUNT` / `ARIUS_E2E_KEY`
- [x] 2.2 Create 2-3 test files of ~100-500 bytes each
- [x] 2.3 Archive test files with `--tier Archive`
- [x] 2.4 Verify blobs are in Archive tier via `GetProperties` (poll until tier transition completes)

## 3. E2E Rehydration Flow

- [x] 3.1 Attempt restore — verify rehydration is initiated and cost estimate includes correct chunk counts
- [x] 3.2 Re-run restore — verify pending rehydration is detected and no duplicate requests issued
- [x] 3.3 Sideload: upload chunk content to `chunks-rehydrated/{hash}` in Hot tier (gzip+encrypt matching original)
- [x] 3.4 Re-run restore — verify files are restored from sideloaded blobs with byte-identical content

## 4. Documentation and Cleanup

- [x] 4.1 Add test comments documenting expected Azure costs (prorated early deletion for tiny files)
- [x] 4.2 Add test timeout of 60 seconds for Archive tier operations
- [x] 4.3 Ensure test container cleanup in fixture teardown
