## 1. Mock-Based Rehydration Unit Tests

- [ ] 1.1 Create mock `IBlobStorageService` that simulates Archive tier (returns `Tier: Archive`, no rehydrated copy)
- [ ] 1.2 Test: chunk needs rehydration — verify pipeline initiates copy-to-rehydrate
- [ ] 1.3 Test: chunk rehydration pending — verify pipeline does not issue duplicate request
- [ ] 1.4 Test: chunk already rehydrated — verify pipeline downloads from `chunks-rehydrated/`

## 2. E2E Rehydration Test Setup

- [ ] 2.1 Create E2E test class following `E2ETests` / `AzureFixture` pattern, gated by `ARIUS_E2E_ACCOUNT` / `ARIUS_E2E_KEY`
- [ ] 2.2 Create 2-3 test files of ~100-500 bytes each
- [ ] 2.3 Archive test files with `--tier Archive`
- [ ] 2.4 Verify blobs are in Archive tier via `GetProperties` (poll until tier transition completes)

## 3. E2E Rehydration Flow

- [ ] 3.1 Attempt restore — verify rehydration is initiated and cost estimate includes correct chunk counts
- [ ] 3.2 Re-run restore — verify pending rehydration is detected and no duplicate requests issued
- [ ] 3.3 Sideload: upload chunk content to `chunks-rehydrated/{hash}` in Hot tier (gzip+encrypt matching original)
- [ ] 3.4 Re-run restore — verify files are restored from sideloaded blobs with byte-identical content

## 4. Documentation and Cleanup

- [ ] 4.1 Add test comments documenting expected Azure costs (prorated early deletion for tiny files)
- [ ] 4.2 Add test timeout of 60 seconds for Archive tier operations
- [ ] 4.3 Ensure test container cleanup in fixture teardown
