# Archive & Restore Defaults Hierarchy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Configure the five archive/restore options at four levels (Factory → Global settings → Repository properties → Run), where the nearest level that set a value wins, and make scheduled archives use the repository's resolved values.

**Architecture:** Arius.Api stores only the values set at each persisted level, in two new SQLite tables (`global_defaults`, `repository_defaults`). It resolves them in one place (`OptionDefaults.Resolve`) and serves them over four REST endpoints. Arius.Web renders every option through one shared field component. That component is used by a shared defaults editor (Settings page and Properties tab) and by both drawers. The drawers send explicit values to the unchanged hub methods.

**Tech Stack:** .NET 11 minimal API + Microsoft.Data.Sqlite, TUnit (+ Shouldly in `Arius.Api.Tests`), Angular 22 standalone components with signals, Vitest (node environment), Playwright.

**Spec:** [`docs/history/superpowers/2026-11-14-archive-restore-defaults-design.md`](2026-11-14-archive-restore-defaults-design.md)

## Global Constraints

- Option names (wire JSON and DB rows): `tier`, `onDisk`, `fastHash`, `overwrite`, `noPointers`.
- Allowed values: tier `hot` | `cool` | `cold` | `archive`; onDisk `keep` | `keep-pointers` | `replace`; the other three are booleans.
- Factory defaults: tier `archive`, onDisk `keep`, fastHash `false`, overwrite `false`, noPointers `false`.
- Source names: `factory` | `settings` | `repository`.
- onDisk → hub flags: `keep` → removeLocal false, writePointers false; `keep-pointers` → false, true; `replace` → true, true.
- Pill copy, verbatim: "Inherited from factory defaults", "Inherited from global settings", "Inherited from repository properties", "Set in global settings", "Set for this repository", "Changed for this run". Reset button text: "Reset".
- At Settings and Properties, any explicit pick sets the value at that level, even one equal to the inherited value. Only Reset unsets it.
- Every yes/no control is `<input type="checkbox" role="switch" class="ar-switch">`. Keep the existing `data-testid`s `tier-seg` (+`data-tier`), `seg-on-disk` (+`data-on-disk`) and `toggle-fast-hash`.
- Core, CLI and Explorer are untouched. `--fast-hash` stays opt-in (ADR-0021).
- TUnit: run with `--treenode-filter`, never `--filter`. `Arius.Api.Tests` uses Shouldly; `Arius.Api.Integration.Tests` uses `await Assert.That(...)`.
- Run `/simplify` right before each code commit (not for docs- or test-only commits). Use conventional commit messages ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- `src/Arius.Web` has no `node_modules` in this worktree: run `npm ci` in `src/Arius.Web` once before the first Web task.

## Review Focus

1. **A PUT body with a misspelled or unknown option** (e.g. `{ "fasthash2": true }`) → 400, and the stored values are unchanged. It must not silently unset everything. *(Task 4)*
2. **An explicit `false`** (fastHash, overwrite or noPointers) set at a level is a set value, not "unset". It overrides a `true` above, round-trips through the DB, and shows "Set for this repository". *(Tasks 1, 2, 5)*
3. **Upgrading a database whose `default_tier` is capitalized, blank or unknown** (`Archive`, `''`, `premium`) → valid values migrate lower-cased as repository-level tiers, and anything else is skipped (the repository inherits). *(Task 3)*
4. **Deleting a repository that has defaults set** → succeeds despite `foreign_keys = on` and leaves no orphan rows. *(Task 2)*
5. **Opening the drawer for repository B right after A, or pressing Start before defaults have loaded** → Start never sends A's or stale values, and does nothing until the values are loaded. *(Task 6)*

---

### Task 1: Option model and resolution (API)

**Files:**
- Modify: `src/Arius.Api/AppData/Records.cs` (append records)
- Create: `src/Arius.Api/AppData/OptionDefaults.cs`
- Test: `src/Arius.Api.Tests/AppData/OptionDefaultsTests.cs`

**Interfaces:**
- Produces (namespace `Arius.Api.AppData`):
  - `[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public sealed record DefaultValues(string? Tier, string? OnDisk, bool? FastHash, bool? Overwrite, bool? NoPointers)` with `public static readonly DefaultValues None`. `null` means not set at this level.
  - `public sealed record ResolvedValue<T>(T Value, string Source);`
  - `public sealed record ResolvedDefaults(ResolvedValue<string> Tier, ResolvedValue<string> OnDisk, ResolvedValue<bool> FastHash, ResolvedValue<bool> Overwrite, ResolvedValue<bool> NoPointers);`
  - `internal static class OptionDefaults`:
    - constants `SourceFactory`/`SourceSettings`/`SourceRepository`
    - `DefaultValues Factory`
    - `IReadOnlyList<string> OptionNames`, `Tiers`, `OnDiskValues`
    - `ResolvedDefaults Resolve(DefaultValues global, DefaultValues repository)`: repository, then global, then factory
    - `string? Validate(DefaultValues values)`: null when valid; otherwise a message naming the option and the bad value
    - `(bool RemoveLocal, bool WritePointers) ToArchiveFlags(string onDisk)`
    - `IEnumerable<(string Option, string Value)> ToRows(DefaultValues values)`: non-null fields only; booleans as `"true"`/`"false"`
    - `DefaultValues FromRows(IEnumerable<(string Option, string Value)> rows)`

- [ ] **Step 1: Write the failing tests** in `OptionDefaultsTests` (TUnit + Shouldly):
  - `Resolve_with_nothing_set_returns_factory_values_from_factory`: every value equals `Factory`, and every `Source == "factory"`.
  - `Resolve_prefers_repository_over_global_over_factory`: global `{Tier="cool", FastHash=true}`, repository `{Tier="hot"}` → Tier `hot`/`repository`, FastHash `true`/`settings`, OnDisk `keep`/`factory`.
  - `Explicit_false_at_repository_overrides_true_at_global`: global FastHash `true`, repository FastHash `false` → `false`/`repository`.
  - `Validate_rejects_unknown_tier_and_onDisk`: `Tier="premium"` → message contains `tier` and `premium`; `OnDisk="delete"` → message contains `onDisk`. `Validate(Factory)` and `Validate(None)` → null.
  - `ToArchiveFlags_maps_each_onDisk_value`: the three mappings from Global Constraints. An unknown value throws `ArgumentOutOfRangeException`.
  - `Rows_round_trip_including_false`: `FromRows(ToRows(v)) == v` for `v = new("cold", "replace", false, true, null)`, and `ToRows` emits exactly 4 rows.

- [ ] **Step 2: Run, expect a compile failure.** Run `dotnet test --project src/Arius.Api.Tests/Arius.Api.Tests.csproj --treenode-filter "/*/*/OptionDefaultsTests/*"`. It fails because `OptionDefaults` doesn't exist yet.

- [ ] **Step 3: Implement the records and `OptionDefaults`** with the signatures above. `Resolve` takes each field from the first non-null of repository, then global, then factory, and attaches the matching source constant.

- [ ] **Step 4: Run the Step 2 command, expect PASS.**

- [ ] **Step 5: Commit**: `feat(api): option defaults model and resolution order`.

---

### Task 2: Storage for global and repository defaults (API)

**Files:**
- Modify: `src/Arius.Api/AppData/AppDatabase.cs`: the schema `CREATE` block, `DeleteRepository`, `ResetAll`, and new methods
- Test: `src/Arius.Api.Tests/AppData/DefaultsStoreTests.cs`

**Interfaces:**
- Consumes: `DefaultValues`, `ResolvedDefaults`, `OptionDefaults.ToRows/FromRows/Resolve` (Task 1).
- Produces (on `AppDatabase`):
  - `DefaultValues GetGlobalDefaults()`
  - `void ReplaceGlobalDefaults(DefaultValues values)`: deletes every global row and inserts the non-null ones, in one transaction
  - `DefaultValues GetRepositoryDefaults(long repositoryId)`
  - `void ReplaceRepositoryDefaults(long repositoryId, DefaultValues values)`
  - `IReadOnlyList<string> ListAliasesInheriting(string option)`: aliases, ordered by alias, of repositories that have no `repository_defaults` row for `option`
  - `ResolvedDefaults ResolveRepositoryDefaults(long repositoryId)`: `OptionDefaults.Resolve(GetGlobalDefaults(), GetRepositoryDefaults(repositoryId))`
- Schema, added to the existing `CREATE` block:
  ```sql
  CREATE TABLE IF NOT EXISTS global_defaults (option TEXT PRIMARY KEY, value TEXT NOT NULL);
  CREATE TABLE IF NOT EXISTS repository_defaults (
      repo_id INTEGER NOT NULL REFERENCES repositories(id),
      option  TEXT NOT NULL,
      value   TEXT NOT NULL,
      PRIMARY KEY (repo_id, option));
  ```

- [ ] **Step 1: Write the failing tests** in `DefaultsStoreTests`. Use the `NewDatabase()` helper pattern from `RepositoryRegionResolutionTests`, with a temporary DB file per test.
  - `Fresh_database_has_no_defaults_set`: `GetGlobalDefaults()` and `GetRepositoryDefaults(id)` both equal `DefaultValues.None`.
  - `Replace_global_round_trips_and_replaces_previous_set`: replace with `{Tier="cool", FastHash=false}`, then with `{OnDisk="replace"}`. The read-back equals exactly the second set, and Tier is null again.
  - `Replace_repository_round_trips_explicit_false`: `{Overwrite=false}` reads back `Overwrite == false` (not null).
  - `Resolve_repository_defaults_uses_both_levels`: global `{OnDisk="keep-pointers"}`, repository `{FastHash=true}` → OnDisk `keep-pointers`/`settings`, FastHash `true`/`repository`, Tier `archive`/`factory`.
  - `Aliases_inheriting_excludes_repositories_with_their_own_value`: repositories `music` and `work`, where `work` sets `onDisk`. `ListAliasesInheriting("onDisk")` → `["music"]`, and `ListAliasesInheriting("tier")` → `["music", "work"]`.
  - `Deleting_a_repository_with_defaults_removes_them`: set repository defaults, then `DeleteRepository(id)` doesn't throw. A raw `SELECT COUNT(*) FROM repository_defaults WHERE repo_id = $id` on the DB file then returns 0.

  *(Until Task 3, `InsertRepository` still takes a `defaultTier` argument. Pass `"archive"`.)*

- [ ] **Step 2: Run** `dotnet test --project src/Arius.Api.Tests/Arius.Api.Tests.csproj --treenode-filter "/*/*/DefaultsStoreTests/*"`. Expect FAIL (methods missing).

- [ ] **Step 3: Implement.**
  - Add the two tables and the six methods.
  - Add `"repository_defaults"` to `DeleteRepository`'s child-table loop.
  - Prepend `DELETE FROM repository_defaults; DELETE FROM global_defaults;` to `ResetAll`.

- [ ] **Step 4: Run the Step 2 command, expect PASS.** Then run the whole `Arius.Api.Tests` project, expect PASS.

- [ ] **Step 5: Commit**: `feat(api): persist global and repository defaults`.

---

### Task 3: Migrate `default_tier` and route the tier through resolution (API)

**Files:**
- Modify: `src/Arius.Api/AppData/AppDatabase.cs`: remove `default_tier` from `CREATE TABLE repositories`, from both repository `SELECT`s, from `ReadRepository`, `InsertRepository` and `UpdateRepository`; add the migration
- Modify: `src/Arius.Api/AppData/Records.cs`: remove `DefaultTier` from `RepositoryRecord` and `RepositoryConnection`
- Modify: `src/Arius.Api/Contracts/Dtos.cs`: remove `DefaultTier` from `CreateRepositoryRequest` and `UpdateRepositoryRequest`. `RepositoryDto.DefaultTier` stays and now carries the **resolved** tier.
- Modify: `src/Arius.Api/Endpoints/RepositoryEndpoints.cs`: create/patch calls, `ToDto` tier, delete `NormalizeTier`
- Modify: `src/Arius.Api/Composition/RepositoryProviderRegistry.cs:~234`: drop `DefaultTier:`
- Modify: `src/Arius.Api/Jobs/SchedulerService.cs:~55`: resolved values
- Modify: `src/Arius.Api.FakeTestHost/TestingControlEndpoints.cs`: seed-repo
- Modify: the `InsertRepository(` callers in `src/Arius.Api.Integration.Tests/Harness/AriusApiFactory.cs` and `src/Arius.Api.Tests/**` (the 6 existing files plus Task 2's `DefaultsStoreTests`; drop the tier argument)
- Test: `src/Arius.Api.Tests/AppData/DefaultTierMigrationTests.cs`

**Interfaces:**
- Consumes: `ReplaceRepositoryDefaults`, `ResolveRepositoryDefaults` (Task 2), `OptionDefaults.ToArchiveFlags` (Task 1).
- Produces:
  - `long InsertRepository(string alias, string container, long accountId, string? localPath, string? encryptedPassphrase)`
  - `void UpdateRepository(long id, string? alias, string? localPath, string? encryptedPassphrase)`
  - private `MigrateDefaultTierToRepositoryDefaults(SqliteConnection)`, called in `CreateOrUpgradeSchema` after the `CREATE` block

- [ ] **Step 1: Write the failing tests** in `DefaultTierMigrationTests`. Seed an old-schema DB the same way `RepositoryRegionResolutionTests.Migration_AddsRegionHintColumnToAPreExistingDatabase` does (raw `CREATE TABLE repositories (… default_tier TEXT NOT NULL DEFAULT 'archive' …)`, then `SqliteConnection.ClearPool`), then construct `AppDatabase`.
  - `Existing_tiers_migrate_as_repository_level_values`: rows with `default_tier` `cool` and `Archive` → `GetRepositoryDefaults` gives Tier `cool` and `archive`, and `ResolveRepositoryDefaults(...).Tier.Source == "repository"` for both.
  - `Blank_or_unknown_tiers_are_not_migrated`: `''` and `premium` → Tier null (the repository inherits).
  - `Migration_drops_the_column_and_is_idempotent`: after the first `new AppDatabase(path)`, `pragma_table_info('repositories')` has no `default_tier`. A second `new AppDatabase(path)` doesn't throw, and the values are unchanged.
  - `Fresh_database_has_no_default_tier_column`.

- [ ] **Step 2: Run** `dotnet test --project src/Arius.Api.Tests/Arius.Api.Tests.csproj --treenode-filter "/*/*/DefaultTierMigrationTests/*"`. Expect FAIL.

- [ ] **Step 3: Implement the migration.** It runs only if the column exists (probe `pragma_table_info`, as `EnsureColumn` does), in one transaction:
  ```sql
  INSERT OR IGNORE INTO repository_defaults(repo_id, option, value)
    SELECT id, 'tier', lower(trim(default_tier)) FROM repositories
    WHERE lower(trim(default_tier)) IN ('hot','cool','cold','archive');
  ALTER TABLE repositories DROP COLUMN default_tier;
  ```

- [ ] **Step 4: Remove the `DefaultTier` plumbing** listed under Files.
  - `ToDto` passes `db.ResolveRepositoryDefaults(repository.Id).Tier.Value` as `RepositoryDto.DefaultTier`.
  - `SchedulerService` resolves `database.ResolveRepositoryDefaults(schedule.RepositoryId)`, maps `OnDisk` with `OptionDefaults.ToArchiveFlags`, and calls `RunArchiveAsync(id, jobId, d.Tier.Value, removeLocal, writePointers, d.FastHash.Value, trigger: "schedule")`.
  - In `TestingControlEndpoints` seed-repo, keep `SeedRepoRequest.DefaultTier`. When it isn't null, call `db.ReplaceRepositoryDefaults(repoId, DefaultValues.None with { Tier = req.DefaultTier.ToLowerInvariant() })`.

- [ ] **Step 5: Run** the Step 2 command, then both test projects in full:
  - `dotnet test --project src/Arius.Api.Tests/Arius.Api.Tests.csproj`
  - `dotnet test --project src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj`

  Expect PASS.

- [ ] **Step 6: Commit**: `feat(api): migrate default_tier into repository defaults; scheduler uses resolved defaults`.

---

### Task 4: Defaults endpoints (API)

**Files:**
- Create: `src/Arius.Api/Endpoints/DefaultsEndpoints.cs`
- Modify: `src/Arius.Api/Contracts/Dtos.cs` (two DTOs), `src/Arius.Api/AriusApiHost.cs:~89` (`api.MapDefaultsEndpoints();`)
- Test: `src/Arius.Api.Integration.Tests/DefaultsEndpointsTests.cs`

**Interfaces:**
- Consumes: the Task 2 methods; `OptionDefaults.Factory`, `OptionDefaults.Validate`, `OptionDefaults.OptionNames`.
- Produces:
  - `public sealed record GlobalDefaultsDto(DefaultValues Set, DefaultValues Factory, IReadOnlyDictionary<string, IReadOnlyList<string>> InheritedBy);`: `InheritedBy` is keyed by every option name, with values from `ListAliasesInheriting`
  - `public sealed record RepositoryDefaultsDto(DefaultValues Set, ResolvedDefaults Inherited);`: `Inherited = OptionDefaults.Resolve(db.GetGlobalDefaults(), DefaultValues.None)`
  - Routes (under `/api`):
    - `GET /settings/defaults` → `GlobalDefaultsDto`
    - `PUT /settings/defaults` (body `DefaultValues`) → `GlobalDefaultsDto`
    - `GET /repos/{id:long}/defaults` → `RepositoryDefaultsDto`
    - `PUT /repos/{id:long}/defaults` → `RepositoryDefaultsDto`
    - Status codes: 404 for an unknown repository; 400 when `Validate` returns a message (as `Results.BadRequest(message)`) or when the body has an unknown property (handled by the `Disallow` attribute)

- [ ] **Step 1: Write the failing tests** in `DefaultsEndpointsTests`, using `AriusApiFactory` and `factory.SeedRepository()`. For a second repository, insert directly through `AppDatabase` with a new account name, because account names are unique.
  - `Global_defaults_start_empty_and_report_factory_values`
  - `Global_change_reaches_an_inheriting_repository_with_settings_source`: PUT global `{onDisk:"keep-pointers"}` → repository `inherited.onDisk == {value:"keep-pointers", source:"settings"}`, and `inherited.tier.source == "factory"`.
  - `Repository_value_survives_a_global_change`: PUT repository `{fastHash:false}`, then PUT global `{fastHash:true}` → repository `set.fastHash == false`, and global `inheritedBy["fastHash"]` doesn't contain `"itest"`.
  - `Repository_dto_reports_the_resolved_tier`: PUT global `{tier:"cool"}` → `GET /api/repos/{id}` has `defaultTier == "cool"`.
  - `Unknown_option_is_rejected_and_nothing_changes`: PUT `{tier:"hot"}`, then PUT the raw JSON `{"fasthash2":true}` → 400, and a following GET still has `set.tier == "hot"`.
  - `Invalid_tier_is_rejected`: PUT `{tier:"premium"}` → 400.
  - `Unknown_repository_is_404`: both GET and PUT.

- [ ] **Step 2: Run** `dotnet test --project src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj --treenode-filter "/*/*/DefaultsEndpointsTests/*"`. Expect FAIL (404s).

- [ ] **Step 3: Implement** `DefaultsEndpoints.MapDefaultsEndpoints(this IEndpointRouteBuilder app)` (an `internal static class`, like `RepositoryEndpoints`) and the DTOs, and register it in `AriusApiHost`. Saving defaults calls neither `registry.Evict` nor `ClearStatisticsCache`.

- [ ] **Step 4: Run** the Step 2 command, then `dotnet test --project src/Arius.Architecture.Tests/Arius.Architecture.Tests.csproj`. Expect PASS.

- [ ] **Step 5: Commit**: `feat(api): global and repository defaults endpoints`.

---

### Task 5: Web defaults model and API client

**Files:**
- Create: `src/Arius.Web/src/app/core/defaults/defaults.ts`
- Test: `src/Arius.Web/src/app/core/defaults/defaults.spec.ts`
- Modify:
  - `src/Arius.Web/src/app/core/api/api-models.ts`: add the DTOs. `CreateRepositoryRequest.defaultTier` is removed in Task 7, together with the wizards that set it, so the build stays green.
  - `src/Arius.Web/src/app/core/api/api.service.ts`: four methods; drop `defaultTier` from `patchRepository`'s body type

**Interfaces:**
- Produces (`defaults.ts`):
  - `type Tier = 'hot'|'cool'|'cold'|'archive'`
  - `type OnDisk = 'keep'|'keep-pointers'|'replace'`
  - `interface DefaultValues { tier: Tier; onDisk: OnDisk; fastHash: boolean; overwrite: boolean; noPointers: boolean }`
  - `type OptionKey = keyof DefaultValues`
  - `type SetValues = { [K in OptionKey]?: DefaultValues[K] | null }`: absent or null means inherit
  - `type Source = 'factory'|'settings'|'repository'`
  - `type ResolvedDefaults = { [K in OptionKey]: { value: DefaultValues[K]; source: Source } }`
  - `type Level = 'settings'|'repository'|'run'`
  - `interface Pill { text: string; canReset: boolean }`
  - `const ARCHIVE_OPTIONS: OptionKey[] = ['tier','onDisk','fastHash']`, `RESTORE_OPTIONS: OptionKey[] = ['overwrite','noPointers']`
  - `const INHERITED_FROM: Record<Source,string>` and `SET_AT: Record<Level,string>` (Global Constraints copy)
  - `fromFactory(factory: DefaultValues): ResolvedDefaults`
  - `resolve(set: SetValues, inherited: ResolvedDefaults, source: Source): ResolvedDefaults`
  - `valuesOf(r: ResolvedDefaults): DefaultValues`
  - `levelPill(level: 'settings'|'repository', key: OptionKey, set: SetValues, inherited: ResolvedDefaults): Pill`
  - `runPill(key: OptionKey, run: DefaultValues, resolved: ResolvedDefaults): Pill`
  - `withPick(set: SetValues, key: OptionKey, value: DefaultValues[OptionKey]): SetValues`
  - `without(set: SetValues, key: OptionKey): SetValues`
  - `sameSet(a: SetValues, b: SetValues): boolean`
  - `archiveFlags(onDisk: OnDisk): { removeLocal: boolean; writePointers: boolean }`
  - `replaceNote(level: Level, inheritedBy?: string[]): string`
- Produces (`api-models.ts`):
  - `GlobalDefaultsDto { set: SetValues; factory: DefaultValues; inheritedBy: Record<OptionKey, string[]> }`
  - `RepositoryDefaultsDto { set: SetValues; inherited: ResolvedDefaults }`
- Produces (`ApiService`):
  - `getGlobalDefaults(): Observable<GlobalDefaultsDto>` (GET `/api/settings/defaults`)
  - `putGlobalDefaults(set: SetValues): Observable<GlobalDefaultsDto>`
  - `getRepositoryDefaults(repoId: number): Observable<RepositoryDefaultsDto>` (GET `/api/repos/${repoId}/defaults`)
  - `putRepositoryDefaults(repoId: number, set: SetValues): Observable<RepositoryDefaultsDto>`

- [ ] **Step 1: Write the failing tests** in `defaults.spec.ts`:
  - `fromFactory` marks every option `source: 'factory'`.
  - `resolve` lets set values win with the given source, and treats both null and absent as inherit.
  - An explicit `false` wins: `resolve({fastHash:false}, inheritedWithFastHashTrue, 'repository').fastHash` → `{value:false, source:'repository'}`.
  - `levelPill`:
    - inherited → `{text: INHERITED_FROM[source], canReset:false}`
    - set → `{text:'Set for this repository', canReset:true}`, even when the set value equals the inherited one
  - `runPill`: equal → the inherited text; different → `{text:'Changed for this run', canReset:true}`.
  - `withPick` adds the key even when the value equals the inherited one; `without` removes it.
  - `sameSet({tier:null}, {})` is true.
  - `archiveFlags`: the three mappings.
  - `replaceNote`:
    - `('settings', ['music','photos'])` → `'Reaches 2 repositories that inherit it (music, photos), scheduled runs included.'`
    - `('settings', ['music'])` → `'Reaches 1 repository that inherits it (music), scheduled runs included.'`
    - `('settings', [])` → `'No repository inherits it right now.'`
    - `('repository')` → `'Scheduled runs of this repository will do this unattended.'`
    - `('run')` → `''`

- [ ] **Step 2: Run** `cd src/Arius.Web && npx vitest run src/app/core/defaults/defaults.spec.ts`. Expect FAIL (module missing).

- [ ] **Step 3: Implement** `defaults.ts`, the DTO types and the `ApiService` methods.

- [ ] **Step 4: Run** the Step 2 command. Expect PASS.

- [ ] **Step 5: Commit**: `feat(web): defaults model and API client`.

---

### Task 6: Shared field component and pre-filled drawers (Web)

**Files:**
- Create: `src/Arius.Web/src/app/shared/default-field/default-field.component.ts`
- Modify:
  - `src/Arius.Web/src/app/core/state/drawer.store.ts`
  - `src/Arius.Web/src/app/features/drawer/archive-restore-drawer.component.ts`
  - `src/Arius.Web/src/app/features/repo/repo-detail.component.ts:36` (`drawer.openArchive(r.id)`)
- Test: `src/Arius.Web/src/app/core/state/drawer.store.spec.ts`

**Interfaces:**
- Consumes: everything from `defaults.ts`; `ApiService.getRepositoryDefaults` (Task 5).
- Produces:
  - `DefaultFieldComponent`, selector `arius-default-field`:
    - inputs: `option = input.required<OptionKey>()`, `value = input.required<DefaultValues[OptionKey]>()`, `pill = input.required<Pill>()`, `note = input('')`
    - outputs: `picked = output<DefaultValues[OptionKey]>()`, `reset = output<void>()`
    - host attribute `data-option`
    - the pill has `data-testid="default-source"`, and the Reset button `data-testid="default-reset"`
    - switches use testids `toggle-fast-hash`, `toggle-overwrite`, `toggle-no-pointers`
  - `DrawerStore`:
    - drops `archiveTier`, `archiveOnDisk`, `fastHash`, `overwrite` and `restoreNoPointers`
    - adds `resolved = signal<ResolvedDefaults | null>(null)` and `run = signal<DefaultValues | null>(null)`
    - `openArchive(repoId: number): void`
    - `openRestore(repoId: number, version: string | null, collectedPaths: string[]): void`
    - `pick(key: OptionKey, value: DefaultValues[OptionKey]): void`
    - `resetOption(key: OptionKey): void`
    - `start()` (unchanged signature)

The component holds a copy table: label, switch caption and the two hints per option. Use the exact strings from the spec's *Hints* section and these:
- Names: "Upload tier", "On disk after archive", "Fast hash", "Overwrite existing files", "Skip pointer files".
- Switch captions: "Skip re-reading unchanged files", "Replace local files that differ from the snapshot", "Don't write .pointer.arius files".
- Hints:
  - fast hash on: "Trusts the local hash cache for files whose size and timestamps are unchanged. Much faster on large folders."
  - fast hash off: "Every file is fully re-read and hashed. Slower, but the safest."
  - overwrite on: "Local files that differ are overwritten with the archived version."
  - overwrite off: "Local files that already exist are left untouched."
  - noPointers on: "Only the restored files are written."
  - noPointers off: "A pointer file is written next to each restored file."
- Warning for `replace`: **"Deletes local files."** followed by "Each file is removed once it is safely archived, leaving only its pointer file." and then `note`.

Segmented buttons reuse the existing `ar-seg` styles, which move into the component. Inherited pills are grey (`#f4f4f5`/`#71717a`); "set here" pills are amber (`#fef3c7`/`#92400e`).

- [ ] **Step 1: Rewrite the archive/restore tests in `drawer.store.spec.ts`.**
  - Add an `ApiService` stub with `getRepositoryDefaults` to `makeStore`.
  - Replace the old tier-fallback and flag-reset tests with:
    - `openArchive pre-fills run from the repository's resolved defaults`
    - `a defaults response for a previously opened repository is ignored`: use two `Subject`s; emit B, then A, and expect B's values
    - `start before defaults load sends nothing`: `realtime.startArchive` is not called
    - `start maps replace to removeLocal and writePointers`
    - `resetOption restores the resolved value`
    - `openRestore pre-fills overwrite and noPointers`

- [ ] **Step 2: Run** `cd src/Arius.Web && npx vitest run src/app/core/state/drawer.store.spec.ts`. Expect FAIL.

- [ ] **Step 3: Implement `DrawerStore`.**
  - `open*` clears `resolved`/`run`, bumps a private open sequence number, and fetches the defaults.
  - On response (only if the sequence still matches): `resolved = resolve(dto.set, dto.inherited, 'repository')`, `run = valuesOf(resolved)`.
  - On error: `error.set('Could not load this repository\'s defaults.')`.
  - `start()` returns early while `run()` is null, and maps via `archiveFlags`.

- [ ] **Step 4: Implement `DefaultFieldComponent` and rewire the drawer.**
  - The archive drawer shows Source folder plus `@for (key of ARCHIVE_OPTIONS)`, with `pill = runPill(key, run, resolved)` and `note = replaceNote('run')`.
  - The restore drawer shows the same for `RESTORE_OPTIONS`.
  - Disable `drawer-start` while `run()` is null.
  - Remove the drawer's now-unused `.ar-seg`/`.ar-toggle` styles and the `tiers` field.

- [ ] **Step 5: Run** `npx vitest run` (the whole suite) and `npm run build`. Expect PASS and a clean build.

- [ ] **Step 6: Commit**: `feat(web): drawers pre-fill from repository defaults with provenance pills`.

---

### Task 7: Settings page, Properties defaults and wizard (Web)

**Files:**
- Create: `src/Arius.Web/src/app/shared/defaults-editor/defaults-editor.component.ts`
- Modify:
  - `src/Arius.Web/src/app/features/settings/settings.component.ts`: replace the placeholder
  - `src/Arius.Web/src/app/features/repo/properties/properties-tab.component.ts`
  - `src/Arius.Web/src/app/features/wizards/create/create-repo-wizard.component.ts`: remove the `tier` signal, `tiers`, the Default tier field, `defaultTier` in `create()`, and the unused `TitleCasePipe`
  - `src/Arius.Web/src/app/features/wizards/add/add-repo-wizard.component.ts:143`: remove `defaultTier: null`
  - `src/Arius.Web/src/app/core/api/api-models.ts`: remove `defaultTier` from `CreateRepositoryRequest`
- Test: `src/Arius.Web/e2e/hermetic/specs/defaults.spec.ts`

**Interfaces:**
- Consumes: `DefaultFieldComponent` (Task 6); `levelPill`, `withPick`, `without`, `sameSet`, `fromFactory`, `replaceNote`, `ARCHIVE_OPTIONS`, `RESTORE_OPTIONS`, and the `ApiService` defaults methods (Task 5).
- Produces: `DefaultsEditorComponent`, selector `arius-defaults-editor`:
  - inputs: `level = input.required<'settings'|'repository'>()`, `saved = input.required<SetValues>()`, `inherited = input.required<ResolvedDefaults>()`, `inheritedBy = input<Record<OptionKey,string[]>>()`
  - output: `save = output<SetValues>()`
  - It keeps a `draft` signal, reset from `saved` whenever `saved` changes.
  - It renders two cards, `data-testid="defaults-archive"` ("Archive defaults") and `data-testid="defaults-restore"` ("Restore defaults"), plus one bar with `defaults-discard` / `defaults-save`, both disabled when `sameSet(draft, saved)`, and "Unsaved changes" text when dirty.
  - Card descriptions:
    - settings: "Used when archiving, scheduled runs included." / "Used when restoring."
    - repository: "For this repository's archives, scheduled runs included." / "For restores from this repository."
  - The onDisk `note` is `replaceNote(level, inheritedBy()?.onDisk)`.

- [ ] **Step 1: Write the failing hermetic e2e test** `defaults.spec.ts`, using `control.seedRepo({ alias: 'photos' })` (it sets no tier):
  1. `/settings`: in `defaults-archive`, click `seg-on-disk[data-on-disk="keep-pointers"]`. The onDisk `default-source` reads "Set in global settings". Click `defaults-save`; it becomes disabled.
  2. `/repos/{id}` → `btn-archive`. In `drawer`:
     - keep-pointers has class `on`
     - onDisk source reads "Inherited from global settings"
     - tier source reads "Inherited from factory defaults"

     Close the drawer.
  3. `/repos/{id}/properties`: in `defaults-archive`, click `keep`. The source reads "Set for this repository". Save.
  4. Reopen the archive drawer:
     - `keep` is on, and its source reads "Inherited from repository properties"
     - check `toggle-fast-hash` → its source reads "Changed for this run"
     - click its `default-reset` → unchecked, and the source reads "Inherited from factory defaults"

- [ ] **Step 2: Run** `cd src/Arius.Web && npm run e2e:hermetic -- e2e/hermetic/specs/defaults.spec.ts`. Expect FAIL (the Settings placeholder has no `defaults-archive`).

- [ ] **Step 3: Implement `DefaultsEditorComponent` and the two screens.**
  - **Settings:**
    - Title "Settings"; subtitle "Global defaults for every repository. A repository can set its own in its Properties, and any run can change them."
    - Load `getGlobalDefaults()`. Pass `inherited = fromFactory(dto.factory)`, `saved = dto.set`, `inheritedBy`.
    - On `save`, call `putGlobalDefaults`, then refresh from the response.
  - **Properties:**
    - Below the existing card, add `<arius-defaults-editor level="repository">` fed by `getRepositoryDefaults`.
    - On save, call `putRepositoryDefaults`.
    - In Scheduled archives:
      - description "Cron schedules fire archive runs automatically (UTC) with this repository's archive defaults."
      - each row shows `data-testid="schedule-summary"`: `${Tier} · ${on-disk label lower-case} · fast hash ${on|off}`, from the resolved values
      - delete the disabled `schedule-fast-hash` checkbox and its comment

- [ ] **Step 4: Run** the Step 2 command, `npx vitest run`, `npm run build`, and the full `npm run e2e:hermetic`. Expect PASS.

- [ ] **Step 5: Commit**: `feat(web): settings page and repository defaults editor; drop wizard tier`.

---

### Task 8: Live e2e suite follows the new tier API (Web)

**Files:**
- Modify:
  - `src/Arius.Web/e2e/support/fixtures.ts`: add `setRepoDefaults(id: number, body: Record<string, unknown>): Promise<void>` (PUT `/api/repos/${id}/defaults`, asserting `ok()`), next to `patchRepo`
  - `src/Arius.Web/e2e/support/global-setup.ts:~53`: read the created id and PUT `{ tier: process.env.ARIUS_E2E_TIER ?? 'hot' }`
  - Specs:
    - Move `defaultTier: 'x'` from the POST body to `setRepoDefaults(created.id, { tier: 'x' })`, keeping the same tier: `cost-approval` (archive), `job-detail` (hot), `pill` (hot), `snapshot-order` (cold), `statistics-tiers` (cold), `archive` write test (hot), `jobs` (hot), `restore-roundtrip` (hot).
    - Just delete the field in `delete-repo` and `properties`, where the tier isn't used.

- [ ] **Step 1: Make the edits.**
- [ ] **Step 2: Run** `cd src/Arius.Web && npx playwright test --list`. Expect every spec to be listed with no TypeScript errors, and `grep -rn "defaultTier:" e2e` to find nothing in request bodies.
- [ ] **Step 3: Commit** (test-only, no `/simplify`): `test(web-e2e): set repository tier through the defaults endpoint`.

---

### Task 9: Docs

**Files:**
- Create: `docs/decisions/adr-0025-archive-restore-defaults-hierarchy.md` (from `adr-template.md`; status `accepted`, date `2026-11-14`, decision-maker Wouter Van Ranst, consulted Claude)
- Modify: `docs/decisions/README.md` (row 0025), `docs/guide/web-ui.md`, `docs/design/hosts/web.md`, `src/Arius.Api/AGENTS.md` (the AppDatabase table list)

- [ ] **Step 1: ADR-0025.**
  - Context: per-run options, the hard-coded scheduler flags.
  - Drivers: configure once; never override a repository's own choice; nothing silently destructive.
  - Options: copy-down, global-only for on-disk/fast hash, live inheritance.
  - Decision: live inheritance, nearest set level wins; scheduled runs use the repository level; factory fast hash off (consistent with ADR-0021); existing `default_tier` migrated as set at the repository level.
  - Consequences: a factory-default change in a release reaches instances that never set the option; "Replace with pointers" can reach many repositories' scheduled runs, which the Settings warning names.

- [ ] **Step 2: `web-ui.md`.**
  - Overview table: replace "Settings — Placeholder for now." with a one-line description.
  - Step 2b: delete the **Default tier** bullet.
  - Properties: add Archive defaults / Restore defaults, and that schedules run with them.
  - Archiving / Restoring: the fields are pre-filled from the repository's defaults, show where each value came from, and can be changed for one run; restore options are switches.
  - New short section "Defaults: where values come from": the four levels, the pills, Reset.

- [ ] **Step 3: `design/hosts/web.md`.**
  - AppData section: "five tables" → seven. In the ER diagram, drop `default_tier` from `repositories` and add `global_defaults` and `repository_defaults`.
  - One paragraph: resolution lives in `OptionDefaults.Resolve`, is used by the endpoints, `RepositoryDto.DefaultTier` and `SchedulerService`. Link ADR-0025.

- [ ] **Step 4: Verify** that links resolve: `grep -n "adr-0025" docs -r` shows the README row and the web.md link. If the repo's docs build is available, run `mkdocs build --strict`; expect no warnings for the changed files.

- [ ] **Step 5: Commit**: `docs: defaults hierarchy ADR-0025, web UI guide and host design`.
