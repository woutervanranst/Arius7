# Resolve CodeRabbit comments on PR #103 (streaming list rewrite)

## Context

PR #103 has 6 unresolved CodeRabbit inline threads + 4 nitpicks in the review body. Assessment (verified against code) and user decisions:

| # | Comment | Verdict |
|---|---------|---------|
| 1 | `ListQuery.cs` — tier flags don't encode "implies Repository" | **Valid — fix** |
| 2 | `ChunkIndexService.cs` — repair synthesizes `Hot` for missing parent tar | **Valid — fix (throw)** |
| 3 | `FileItemViewModel.cs` — `Unknown` hydration presentation never initialized | **Valid (cosmetic) — fix** |
| 4 | `LsVerb.cs` — legend missing `~` and `?` | **Valid (minor) — fix** |
| 5 | `ListQueryHandler.cs` — overlay matching is case-sensitive | **Dismiss** (user decision: case-sensitive is correct for a listing; clients decide presentation; consistent with pointer↔binary pairing which is also case-sensitive; ignore-case elsewhere is only user-typed conveniences). Document the policy. |
| 6 | `ChunkIndexLocalStore.cs` — old cache.sqlite missing column fails late | **Dismiss** (user decision: dev phase, deletes `~/.arius` manually) |
| 7 | README.md fence missing language | Nitpick — apply (` ```text`) |
| 8 | openspec PLAN.MD fence missing language | Nitpick — apply (` ```text`) |
| 9 | `LsStateFormatterTests` — no `ToMarkup` coverage for `~`/`?` | Nitpick — apply |
| 10 | `AzureBlobContainerService` — tier gated behind traits / null handling | Dismiss — already satisfied (mapping is unconditional; `FromAzureTier` accepts null→null; `BlobListItem.Tier` nullable) |

## Changes

1. **`src/Arius.Core/Features/ListQuery/ListQuery.cs`** — composite flag values so `HasFlag(Repository)` is true for tier states (behavior-neutral for all current emitters/tests since the handler always sets both bits):
   - `RepositoryHydrated = Repository | (1 << 4)`
   - `RepositoryArchived = Repository | (1 << 5)`
   - `RepositoryRehydrating = RepositoryArchived | (1 << 6)`

2. **`src/Arius.Core/Shared/ChunkIndex/ChunkIndexService.cs`** — in `CreateThinRepairEntry`, replace `tarTiers.GetValueOrDefault(parentChunkHash, BlobTier.Hot)` with a lookup that throws `ChunkIndexRepairException(item.Name, "parent tar chunk <hash> not found in repository listing")` when absent — consistent with the existing missing-metadata throws. Keep `item.Tier ?? BlobTier.Hot` for blobs that report no tier (Azure's effective default is hot/account default).

3. **`src/Arius.Explorer/RepositoryExplorer/FileItemViewModel.cs`** — call `OnHydrationStatusChanged(HydrationStatus);` at the end of the constructor so the `Unknown` presentation (Transparent brush + "Cloud chunk status not loaded yet" tooltip) is applied when the initial switch lands on `Unknown` (property setter no-ops on same value).

4. **`src/Arius.Cli/Commands/Ls/LsVerb.cs`** — legend gains `~=rehydrating  ?=tier unknown`.

5. **`src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`** — doc-header line documenting the case policy: overlay name matching is case-sensitive (exact tree names; distinct case-variant files each get a row); `--prefix`/`--filter` remain case-insensitive user conveniences. No code change.

6. **`README.md`** + **`openspec/changes/2026-06-10-rewrite/PLAN.MD`** — label the fenced blocks ` ```text`.

7. **`src/Arius.Cli.Tests/Commands/Ls/LsStateFormatterTests.cs`** — add `ToMarkup` tests for rehydrating (`[purple]~[/]`) and unknown-tier (`?`) per CodeRabbit's suggested cases.

8. **Tests for #2**: `ChunkIndexServiceRepairTests.cs` — add: repair over a thin chunk whose parent tar blob is absent from the listing → throws `ChunkIndexRepairException`.

9. **GitHub thread hygiene** — reply briefly to the two dismissed threads (#5 case-sensitivity rationale, #6 dev-phase rationale) and the already-satisfied AzureBlob nitpick is body-level (no thread to resolve). Resolve threads for items fixed by commits as they land (or leave for CodeRabbit auto-resolve on push).

## Verification

1. `dotnet build src/Arius.slnx`
2. `dotnet test --project src/Arius.Core.Tests` (×2), `src/Arius.Cli.Tests`, `src/Arius.Integration.Tests`
3. Confirm no test asserts raw enum bit values that the composite-flag change would break (grep `1 <<` in tests; value-equality assertions like `Repository | RepositoryHydrated` remain value-equal).
