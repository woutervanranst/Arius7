# Browser Download Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a Web UI user download collected files to their browser (one file as itself, several as one ZIP) through a `download` job that reuses the restore flow (drawer → cost approval → rehydration → job page).

**Architecture:** A new `download` job kind in Arius.Api runs the existing `RestoreCommand` into a per-job staging folder `‹data dir›/downloads/‹jobId›`. Ready/downloaded state per file lives in a new `download_files` table; download endpoints stream single files or an on-the-fly ZIP; a background sweep cleans up staging folders after a configurable period. The Angular Web adds Download beside Restore, a Download drawer, a files card on the job page, pill states, and a regrouped Settings page.

**Tech Stack:** .NET (net11.0) minimal APIs + SignalR, SQLite (raw `SqliteConnection`), TUnit; Angular 22 standalone + signals, Vitest (node env), Playwright hermetic e2e.

**Spec:** [`2026-11-15-browser-download-design.md`](2026-11-15-browser-download-design.md) (approved) and its mockup [`2026-11-15-browser-download-mockup.html`](2026-11-15-browser-download-mockup.html). Read both before any task.

## Global Constraints

- Core, CLI and Explorer are not changed. Arius.Api talks to Core only through `IMediator` (`RestoreCommand`); `RelativePath` (a value type) may be used.
- A download never writes into the repository's local folder; staging is `Path.Combine(Path.GetDirectoryName(dbPath)!, "downloads", jobId)`.
- Download jobs always restore with `Overwrite = false`, `NoPointers = true`, and an explicit (pinned) snapshot version.
- Job kind string is exactly `download`. Statuses are unchanged; the single-active-job rule applies to downloads.
- Retention default: clean-up **on**, **7** days, counted from the job's `finished_at`. Valid days: 1–3650.
- Never delete the staging folder of a job in `running`, `awaiting-cost` or `rehydrating`.
- Icons: Restore = `ki-cloud-download`, Download = `ki-exit-down`. Real buttons carry icons; text actions (Clean up now) don't.
- Exact copy (from the spec): buttons "Download", "Restore", "Start download", "Start restore", "Download ready files", "Download all (n files)", "Download remaining (n files)", "Download", "Clean up now", "Clean up all now", "Save settings"; modal "Confirm download cost", "Cancel download", "Rehydrate & download" / "Download"; wait-card line "Arius checks periodically and prepares the rest by itself. You can close this page."; completed line "Kept on the server until ‹d MMM›, then cleaned up"; Settings intro "Downloading collected files saves them in a temporary location on the server before your browser downloads them."; switch "Clean up downloaded files after [n] days"; hint "Off keeps them on the server until you clean them up."; drawer note "‹n› collected file(s) aren't in the repository and are skipped" (singular: "1 collected file isn't in the repository and is skipped").
- ZIP names: `‹alias›-‹yyyy-MM-dd›.zip`, `‹alias›-‹date› (k of n).zip`, `‹alias›-‹date› (remaining).zip`; date = job `started_at`. One selected file is served as itself under its own file name.
- Non-test C# classes `internal` unless another non-test assembly or a public constructor signature needs them; one top-level type per file; local functions over private statics for single-use helpers.
- TUnit: run with `dotnet test --project <csproj>`; filter with `--treenode-filter "/*/*/<Class>/*"` (never `--filter`).
- Keep existing `data-testid`s; add the new ones named in the tasks.
- Commit after each task with a conventional message ending in `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Every collected file is local-only.** Targets become empty; Start must be disabled, never turning into a whole-repository restore or download (test in Task 7).
2. **Paths with spaces, non-ASCII characters, or the same file name in two folders.** ZIP entries keep their repository-relative paths and the single-file name survives Content-Disposition (tests in Tasks 2 and 4).
3. **The browser aborts a ZIP or single-file transfer.** Nothing is marked downloaded (test of the decision function in Task 4).
4. **A start request with a path that escapes the repository (`../x`)** is rejected at the hub, and serving never resolves outside the job's folder (tests in Tasks 2 and 3).
5. **Clean-up while a download is still in progress** (Clean up now, Clean up all now, the sweep) never deletes its folder (tests in Tasks 4 and 5).

---

### Task 1: App database storage for download files and settings

**Files:**
- Modify: `src/Arius.Api/AppData/AppDatabase.cs` (schema in `CreateOrUpgradeSchema`, `DeleteRepository`, `ResetAll`, new section `// ── Downloads ──`)
- Modify: `src/Arius.Api/AppData/Records.cs`
- Test: `src/Arius.Api.Tests/AppData/DownloadStoreTests.cs`

**Interfaces:**
- Produces (Records.cs, `public`):
  - `sealed record DownloadFile(string Path, long Size, bool Ready = false, bool Downloaded = false);`
  - `sealed record DownloadSettings(bool CleanupEnabled, int CleanupDays) { public static readonly DownloadSettings Default = new(true, 7); }`
- Produces (AppDatabase):
  - `void InsertDownloadFiles(string jobId, IReadOnlyList<DownloadFile> files)` — one transaction.
  - `IReadOnlyList<DownloadFile> ListDownloadFiles(string jobId)` — ordered by path.
  - `void MarkDownloadFileReady(string jobId, string path)`
  - `void MarkDownloadFilesDownloaded(string jobId, IReadOnlyCollection<string> paths)`
  - `IReadOnlyList<JobRecord> ListDownloadJobs(long? repositoryId = null)` — `kind = 'download'`, newest `started_at` first, any status.
  - `DownloadSettings GetDownloadSettings()` (absent rows → `DownloadSettings.Default`), `void SaveDownloadSettings(DownloadSettings settings)`.

Schema (add to the `CREATE TABLE IF NOT EXISTS` block):

```sql
CREATE TABLE IF NOT EXISTS download_files (
    job_id     TEXT    NOT NULL REFERENCES jobs(id),
    path       TEXT    NOT NULL,
    size       INTEGER NOT NULL,
    ready      INTEGER NOT NULL DEFAULT 0,
    downloaded INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (job_id, path)
);
-- Server-wide app settings that are not part of the defaults hierarchy (e.g. download clean-up).
CREATE TABLE IF NOT EXISTS app_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
```

Settings rows: `downloads.cleanupEnabled` = `"true"|"false"`, `downloads.cleanupDays` = integer. `DeleteRepository` deletes `download_files WHERE job_id IN (SELECT id FROM jobs WHERE repo_id = $id)` before the existing cascade. `ResetAll` also clears `download_files` (before `jobs`) and `app_settings`.

- [ ] **Step 1: Write the failing tests** (construct `AppDatabase` on a temp path like `JobLifecycleDbTests`; seed account/repo/job with `InsertAccount`/`InsertRepository`/`InsertJob`)

```csharp
[Test] public async Task Inserted_download_files_list_by_path_not_ready_and_not_downloaded()
// InsertDownloadFiles(job, [new("photos/b.jpg", 2), new("a.txt", 1)]) → ListDownloadFiles == [("a.txt",1,false,false), ("photos/b.jpg",2,false,false)]

[Test] public async Task Marking_ready_and_downloaded_changes_only_the_named_files()
// MarkDownloadFileReady(job, "a.txt"); MarkDownloadFilesDownloaded(job, ["a.txt"]) → a.txt (true,true), photos/b.jpg (false,false)

[Test] public async Task Download_settings_default_to_clean_up_after_7_days_and_round_trip()
// GetDownloadSettings() == DownloadSettings.Default; Save(new(false, 3)) → Get() == new(false, 3)

[Test] public async Task ListDownloadJobs_returns_only_download_jobs_of_the_repository_newest_first()

[Test] public async Task Deleting_a_repository_removes_its_download_files()
```

- [ ] **Step 2: Run** `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/DownloadStoreTests/*"` — expect compile failure / FAIL.
- [ ] **Step 3: Implement** the schema, records and methods above, matching the file's existing raw-SQL idiom.
- [ ] **Step 4: Run** the same command — expect PASS; then run all of `src/Arius.Api.Tests` (JobGuardTests and DefaultsStoreTests must stay green).
- [ ] **Step 5: Commit** `feat(api): store download files and download clean-up settings`

---

### Task 2: Staging folder and download names

**Files:**
- Create: `src/Arius.Api/Downloads/DownloadStaging.cs`, `src/Arius.Api/Downloads/DownloadNaming.cs`
- Modify: `src/Arius.Api/AriusApiHost.cs` (register the staging singleton)
- Test: `src/Arius.Api.Tests/Downloads/DownloadStagingTests.cs`, `src/Arius.Api.Tests/Downloads/DownloadNamingTests.cs`

**Interfaces:**
- Produces `public sealed class DownloadStaging(string root)` (public because `JobRunner`'s public constructor takes it):
  - `string PathFor(string jobId)` — `Path.Combine(root, jobId)`.
  - `string FilePathFor(string jobId, string relativePath)` — full path of a staged file; throws `ArgumentException` when the resolved full path is not inside `PathFor(jobId)`.
  - `bool Exists(string jobId)`
  - `void PruneUnready(string jobId, IReadOnlySet<string> readyPaths)` — deletes every file under the job folder whose `/`-separated relative path is not in `readyPaths`; no-op when the folder is missing.
  - `void Delete(string jobId)` — recursive; no-op when missing.
  - `IReadOnlyList<string> ListJobIds()` — folder names under `root`; empty when `root` is missing.
  - `long SizeOf(string jobId)` — total bytes of the folder; 0 when missing.
- Produces `internal static class DownloadNaming`:
  - `string ArchiveName(string alias, DateTimeOffset startedAt, int selected, int total, bool remaining)` — `remaining` → `"{alias}-{date} (remaining).zip"`; else `selected < total` → `"{alias}-{date} ({selected} of {total}).zip"`; else `"{alias}-{date}.zip"`; `date = startedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)`.
- Registration in `AddAriusApi`: `builder.Services.AddSingleton(new DownloadStaging(Path.Combine(Path.GetDirectoryName(dbPath)!, "downloads")));`

- [ ] **Step 1: Write the failing tests**

```csharp
// DownloadNamingTests
[Test] public async Task Everything_is_alias_and_start_date()        // ("Photos", 2026-11-15T22:00Z, 3, 3, false) == "Photos-2026-11-15.zip"
[Test] public async Task A_partial_set_says_k_of_n()                  // (…, 2, 3, false) == "Photos-2026-11-15 (2 of 3).zip"
[Test] public async Task The_rest_says_remaining()                    // (…, 1, 3, true)  == "Photos-2026-11-15 (remaining).zip"

// DownloadStagingTests (root = unique temp dir, deleted after)
[Test] public async Task PruneUnready_keeps_ready_files_and_deletes_the_rest()
// stage "photos/a.jpg", "photos/b.jpg", "docs/ü c.txt"; PruneUnready(job, {"photos/a.jpg", "docs/ü c.txt"}) → only b.jpg gone
[Test] public async Task FilePathFor_rejects_a_path_outside_the_job_folder()   // "../other/x" → ArgumentException
[Test] public async Task Delete_ListJobIds_and_SizeOf_reflect_the_folders_on_disk()
[Test] public async Task A_missing_folder_is_not_an_error()                     // PruneUnready/Delete/SizeOf on unknown job: no throw, SizeOf 0
```

- [ ] **Step 2: Run** `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/DownloadStagingTests/*"` and `…/DownloadNamingTests/*` — expect FAIL.
- [ ] **Step 3: Implement** both types and the registration. Compare relative paths with `/` separators (`Path.GetRelativePath(jobFolder, file).Replace('\\', '/')`). Containment check: `Path.GetFullPath(Path.Combine(jobFolder, relativePath))` must start with `Path.GetFullPath(jobFolder) + Path.DirectorySeparatorChar`.
- [ ] **Step 4: Run** both filters — expect PASS.
- [ ] **Step 5: Commit** `feat(api): add download staging folder and download names`

---

### Task 3: The download job

**Files:**
- Modify: `src/Arius.Api/Jobs/JobRunner.cs`, `src/Arius.Api/Jobs/JobSink.cs`, `src/Arius.Api/Hubs/JobsHub.cs`
- Create: `src/Arius.Api/Contracts/DownloadDtos.cs`
- Modify (test host): `src/Arius.Api.FakeTestHost/ScenarioRegistry.cs`, `src/Arius.Api.FakeTestHost/ScriptedRestoreHandler.cs`, `src/Arius.Api.FakeTestHost/CanonicalScenarios.cs`
- Test: `src/Arius.Api.Integration.Tests/DownloadJobTests.cs`

**Interfaces:**
- Consumes: Task 1 `DownloadFile`, `InsertDownloadFiles`, `MarkDownloadFileReady`, `ListDownloadFiles`; Task 2 `DownloadStaging`.
- Produces:
  - `DownloadDtos.cs`: `public sealed record DownloadFileRequest(string Path, long Size);` (later tasks add their DTOs to this file).
  - `JobSink`: `public Action<string>? FileRestored { get; set; }` — invoked by `AddRestored(key, size)` with `key` after crediting.
  - `JobRunner` constructor gains `DownloadStaging staging`.
  - `public Task RunDownloadAsync(long repositoryId, string jobId, string version, IReadOnlyList<DownloadFile> files)` — inserts the job with kind `download` (same unique-index handling as restore), inserts the files, sets `sink.FileRestored = path => database.MarkDownloadFileReady(jobId, path)`, then runs the shared restore body with `destination = staging.PathFor(jobId)`, `targetPaths = files.Select(f => f.Path)`, `overwrite: false`, `noPointers: true`.
  - Refactor: the part of `RunRestoreAsync` after the insert becomes `private async Task RunRestoreJobAsync(JobSink sink, long repositoryId, string jobId, DateTimeOffset startedAt, string? version, IReadOnlyList<string> targetPaths, string destination, bool overwrite, bool noPointers, string noun)`; `noun` is `"Restore"` or `"Download"` and replaces "Restore" in the summaries/log lines (`"{noun} complete."`, `"{noun} declined."`, `"{noun} declined at cost approval."`, `"{noun} failed."`).
  - `ResumeRestoreAsync`: when `job.Kind == "download"`, before `RunRestoreOnceAsync`: `staging.PruneUnready(jobId, ready paths from ListDownloadFiles)` and set `sink.FileRestored` as above; the completion summary uses the noun for the job's kind.
  - `JobsHub.StartDownload(long repositoryId, string? version, DownloadFileRequest[]? files) : Task<string>` — throws `HubException` with:
    - `"Choose a snapshot to download from."` when `version` is null/whitespace,
    - `"Collect at least one file to download."` when `files` is null/empty,
    - `"'{path}' is not a valid repository path."` when `RelativePath.Parse(path)` throws,
    - the existing busy message when `HasActiveJob`.
    Paths are normalized with `RelativePath.Parse(p).ToString()` and de-duplicated, then `_ = jobRunner.RunDownloadAsync(...)`; the caller joins the job group first (as `StartRestore`).
  - Test host: `RestoreScenario` gains a trailing `bool WritesFiles = false`. When true, `ScriptedRestoreHandler` writes each `FileRestoredEvent` file (in both `PreCostEvents` and `PostApproveEvents`) to `Path.Combine(command.Options.RootDirectory, evt.RelativePath.ToString())` with UTF-8 content `$"scripted:{evt.RelativePath}"` **before** publishing the event.
  - `CanonicalScenarios.OnlineDownload(bool gated = false)` — `OnlineRestore` shape with post-approve `FileRestoredEvent`s for `photos/a.jpg` (1000) and `docs/b.txt` (500), `WritesFiles: true`, result 2 restored, 0 pending.
  - `CanonicalScenarios.RehydratingDownloadStaysPending()` — `RehydratingRestore` cost prompt; post-approve restores only `photos/a.jpg`; result `ChunksPendingRehydration = 1`; `WritesFiles: true`.

- [ ] **Step 1: Write the failing tests** (pattern: `RestoreCostHandshakeTests`; resolve `JobRunner`, `RestoreApprovalRegistry`, `AppDatabase`, `DownloadStaging` from `factory.Services`)

```csharp
[Test] public async Task A_download_restores_only_into_its_staging_folder_with_fixed_options()
// RunDownloadAsync(repo, job, "v1", [new("photos/a.jpg",1000), new("docs/b.txt",500)]); await awaiting-cost; approve Standard; await run
// Received.Single().Options: RootDirectory == staging.PathFor(job), Overwrite false, NoPointers true, Version "v1",
//   TargetPaths == [photos/a.jpg, docs/b.txt]
// GetJob(job): Kind "download", Status "completed"; ListDownloadFiles all Ready; both files exist under PathFor(job)
// the repository's local folder contains no files

[Test] public async Task Ready_files_survive_a_resume_and_unready_staged_files_are_pruned_first()
// RehydratingDownloadStaysPending → status "rehydrating"; a.jpg Ready, b.txt not
// write a stray staging file docs/b.txt ("half"); set scenario to a pass with no events and 1 pending; ResumeRestoreAsync(job)
// → docs/b.txt deleted, photos/a.jpg kept, a.jpg still Ready

[Test] public async Task StartDownload_rejects_a_missing_version_no_files_and_a_path_outside_the_repository()
// over the hub (HubConnection pattern from ArchiveHubTests): (repo, null, [a]) / (repo, "v1", []) / (repo, "v1", [("../x", 1)])
// each throws HubException with the message above; no job row is created
```

- [ ] **Step 2: Run** `dotnet test --project src/Arius.Api.Integration.Tests --treenode-filter "/*/*/DownloadJobTests/*"` — expect FAIL.
- [ ] **Step 3: Implement** the interfaces above. Keep `RunRestoreAsync`'s public signature unchanged.
- [ ] **Step 4: Run** the filter — expect PASS; then all of `src/Arius.Api.Integration.Tests` and `src/Arius.Api.Tests` (restore handshake, reattach, concurrent resume must stay green).
- [ ] **Step 5: Commit** `feat(api): add the download job kind staging restores per job`

---

### Task 4: Download endpoints (state, content, clean up one, pending)

**Files:**
- Create: `src/Arius.Api/Endpoints/DownloadEndpoints.cs`
- Modify: `src/Arius.Api/Contracts/DownloadDtos.cs`, `src/Arius.Api/AriusApiHost.cs` (`api.MapDownloadEndpoints();`)
- Test: `src/Arius.Api.Integration.Tests/DownloadEndpointsTests.cs`, `src/Arius.Api.Tests/Endpoints/DownloadCompletionTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces DTOs (camelCase on the wire):
  - `DownloadFileDto(string Path, long Size, bool Ready, bool Downloaded)`
  - `DownloadStateDto(IReadOnlyList<DownloadFileDto> Files, bool CleanedUp, DateTimeOffset? CleanupAt)`
  - `PendingDownloadDto(string JobId, int ReadyFiles, int TotalFiles, int RemainingFiles, long RemainingBytes)`
- Produces endpoints (all under `/api`; any job that is missing or not `kind == "download"` → 404):
  - `GET /jobs/{id}/download` → `DownloadStateDto`. `CleanedUp` = terminal status && `!staging.Exists(id)`. `CleanupAt` = `finished_at + CleanupDays` when clean-up is enabled, the job is terminal and not cleaned up; otherwise null.
  - `GET /jobs/{id}/download/content?set=all|remaining` → `all` = ready files, `remaining` = ready && !downloaded. Unknown `set` → 400; cleaned up → 410; empty selection → 404.
    - One file: `Results.File(staging.FilePathFor(id, path), "application/octet-stream", Path.GetFileName(path), enableRangeProcessing: true)` executed in the handler; afterwards mark it downloaded when `CountsAsDownloaded(...)`.
    - Several: `Content-Type: application/zip`, `Content-Disposition: attachment` with `DownloadNaming.ArchiveName(alias, startedAt, selected, total, remaining: set == "remaining" && files.Any(f => f.Downloaded))` (use `ContentDispositionHeaderValue.SetHttpFileName`). Write the ZIP straight to `Response.Body` with the async ZIP APIs (`ZipArchive.CreateAsync` / `entry.OpenAsync` / `DisposeAsync`), `CompressionLevel.NoCompression`, entry name = repository-relative path, entry `LastWriteTime` = file's last write time. After the archive is disposed and flushed, mark the selection downloaded only if `!RequestAborted.IsCancellationRequested`. (If the async ZIP APIs are missing in the SDK, set `IHttpBodyControlFeature.AllowSynchronousIO = true` for this response instead.)
  - `POST /jobs/{id}/download/cleanup` → 409 while non-terminal; else `staging.Delete(id)` → 204.
  - `GET /repos/{id:long}/downloads/pending` → the newest download job of the repository that is terminal, still staged, and has ≥ 1 ready-and-not-downloaded file → `PendingDownloadDto`; otherwise 204.
  - `internal static bool CountsAsDownloaded(bool aborted, RangeHeaderValue? range, long length)` — false when aborted; true without a range; with a range, true only if some range reaches the last byte (`To is null || To >= length - 1`).

- [ ] **Step 1: Write the failing tests**

```csharp
// DownloadCompletionTests (Arius.Api.Tests)
[Test] public async Task An_aborted_transfer_never_counts()                  // (true, null, 10) == false
[Test] public async Task A_full_transfer_counts()                            // (false, null, 10) == true
[Test] public async Task Only_a_range_that_reaches_the_end_counts()          // bytes=0-4 → false; bytes=5- → true; bytes=5-9 → true

// DownloadEndpointsTests (Arius.Api.Integration.Tests) — helper: complete an OnlineDownload via RunDownloadAsync + approve
[Test] public async Task State_lists_each_file_with_ready_and_downloaded_and_the_clean_up_date()
// cleanupAt == finishedAt + 7 days; after PUT-free SaveDownloadSettings(new(false, 7)) cleanupAt is null
[Test] public async Task Several_files_download_as_one_zip_with_repository_relative_entries()
// GET content?set=all → application/zip; filename "itest-{started yyyy-MM-dd}.zip";
// entries "docs/b.txt","photos/a.jpg" with contents "scripted:…"; afterwards both Downloaded
[Test] public async Task Remaining_with_one_file_left_serves_that_file_under_its_own_name()
// MarkDownloadFilesDownloaded(job, ["photos/a.jpg"]); GET set=remaining → filename "b.txt", body "scripted:docs/b.txt", not a zip
[Test] public async Task Cleaning_up_an_in_progress_download_is_refused_and_keeps_its_files()
// gated OnlineDownload, approved, still running → POST cleanup 409; folder still exists
[Test] public async Task A_cleaned_up_download_reports_cleaned_up_and_its_content_is_gone()
// POST cleanup 204 → state CleanedUp true; GET content 410
[Test] public async Task Pending_returns_the_latest_finished_download_with_files_left_until_all_are_downloaded()
// 200 with RemainingFiles 2 → after GET content?set=all → 204
```

- [ ] **Step 2: Run** both test filters — expect FAIL.
- [ ] **Step 3: Implement** the endpoints and DTOs.
- [ ] **Step 4: Run** both filters — expect PASS.
- [ ] **Step 5: Commit** `feat(api): serve staged download files and zips`

---

### Task 5: Clean-up settings, Clean up all, sweep, repository delete

**Files:**
- Create: `src/Arius.Api/Downloads/DownloadCleaner.cs`, `src/Arius.Api/Jobs/DownloadCleanupService.cs`
- Modify: `src/Arius.Api/Endpoints/DownloadEndpoints.cs`, `src/Arius.Api/Endpoints/RepositoryEndpoints.cs` (delete), `src/Arius.Api/Contracts/DownloadDtos.cs`, `src/Arius.Api/AriusApiHost.cs`
- Test: `src/Arius.Api.Integration.Tests/DownloadCleanupTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2, 4.
- Produces:
  - `DownloadUsageDto(int Downloads, long Bytes)` — folders on disk and their total size.
  - `internal sealed class DownloadCleaner(AppDatabase database, DownloadStaging staging, ILogger<DownloadCleaner> logger)`:
    - `int CleanUpAll()` — deletes every staged folder whose job is missing or terminal; returns the count.
    - `int CleanUpExpired(DateTimeOffset now)` — no-op when clean-up is disabled; deletes folders whose job is missing, or terminal with `FinishedAt <= now - CleanupDays`. Per-folder try/catch + log, like `StaleApprovalSweepService`.
  - `internal sealed class DownloadCleanupService(IServiceProvider services, ILogger<DownloadCleanupService> logger) : BackgroundService` — mirrors `StaleApprovalSweepService` (30 s initial delay, hourly `PeriodicTimer`, `SafeWaitForNextTickAsync`), each tick calls `CleanUpExpired(DateTimeOffset.UtcNow)`.
  - Endpoints: `GET /settings/downloads` → `DownloadSettings`; `PUT /settings/downloads` (body `DownloadSettings`) → 400 `"Clean-up days must be between 1 and 3650."` when out of range, else saves and returns it; `GET /downloads/usage` → `DownloadUsageDto`; `POST /downloads/cleanup` → `CleanUpAll()` then `DownloadUsageDto`.
  - `DELETE /repos/{id}`: after the existing active-job guard, `staging.Delete(job.Id)` for each `db.ListDownloadJobs(id)` before `db.DeleteRepository(id)`.
  - Register `AddSingleton<DownloadCleaner>()` and `AddHostedService<DownloadCleanupService>()`.

- [ ] **Step 1: Write the failing tests** (create job rows with `InsertJob` + `CompleteJob` and folders with a file via `staging.PathFor`)

```csharp
[Test] public async Task The_sweep_deletes_finished_downloads_after_the_period_and_keeps_newer_and_in_progress_ones()
// completed job A, rehydrating job B, orphan folder C (no row): CleanUpExpired(UtcNow + 6 days) → only C gone;
// CleanUpExpired(UtcNow + 8 days) → A gone, B kept
[Test] public async Task The_sweep_does_nothing_when_clean_up_is_off()
[Test] public async Task Clean_up_all_keeps_folders_of_downloads_still_in_progress()
// POST /api/downloads/cleanup → completed gone, running/rehydrating kept; usage reports the kept ones
[Test] public async Task Download_settings_round_trip_and_reject_zero_days()
[Test] public async Task Deleting_a_repository_deletes_its_download_folders()
```

- [ ] **Step 2: Run** `dotnet test --project src/Arius.Api.Integration.Tests --treenode-filter "/*/*/DownloadCleanupTests/*"` — expect FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter, then the whole `Arius.Api.Integration.Tests`, `Arius.Api.Tests` and `Arius.Architecture.Tests` — expect PASS.
- [ ] **Step 5: Commit** `feat(api): clean up download folders after a period or on demand`

---

### Task 6: Web models, clients and job-kind helpers

**Files:**
- Modify: `src/Arius.Web/src/app/core/api/api-models.ts`, `src/Arius.Web/src/app/core/api/api.service.ts`, `src/Arius.Web/src/app/core/api/realtime.service.ts`, `src/Arius.Web/src/app/shared/job-format.ts`, `src/Arius.Web/src/app/features/jobs/jobs.component.ts`
- Test: `src/Arius.Web/src/app/shared/job-format.spec.ts`

**Interfaces:**
- Produces (api-models.ts): `DownloadFileRequest {path; size}`, `DownloadFileDto {path; size; ready; downloaded}`, `DownloadStateDto {files; cleanedUp; cleanupAt: string | null}`, `PendingDownloadDto {jobId; readyFiles; totalFiles; remainingFiles; remainingBytes}`, `DownloadSettingsDto {cleanupEnabled; cleanupDays}`, `DownloadUsageDto {downloads; bytes}`.
- Produces (api.service.ts): `getDownload(jobId): Observable<DownloadStateDto>`, `cleanUpDownload(jobId): Observable<void>`, `getPendingDownload(repoId): Observable<PendingDownloadDto | null>` (204 → null), `getDownloadSettings()`, `putDownloadSettings(s)`, `getDownloadUsage()`, `cleanUpAllDownloads(): Observable<DownloadUsageDto>`; and exported pure `downloadContentUrl(jobId: string, set: 'all' | 'remaining'): string` → `/api/jobs/${jobId}/download/content?set=${set}`.
- Produces (realtime.service.ts): `startDownload(repositoryId: number, opts: { version: string; files: DownloadFileRequest[] }): Promise<string>` → hub `StartDownload`.
- Produces (job-format.ts): `export type JobKind = 'archive' | 'restore' | 'download'`; `toJobKind(kind: string): JobKind` (unknown → `'archive'`); `isRestoreFamily(kind: JobKind): boolean` (restore or download); `jobKindLabel(kind): 'Archive' | 'Restore' | 'Download'`; `jobKindIcon(kind): 'ki-cloud-add' | 'ki-cloud-download' | 'ki-exit-down'`. `phaseSentence(s, kind)` treats `download` like `restore`.
- `jobs.component.ts`: replace every `kind === 'restore'` branch with these helpers (label, icon, violet palette and `[kind]` of the layered bar for the restore family).

- [ ] **Step 1: Write the failing tests** in `job-format.spec.ts`: `toJobKind keeps download and maps unknown kinds to archive`; `jobKindLabel and jobKindIcon name Download with ki-exit-down`; `isRestoreFamily is true for restore and download only`; `phaseSentence describes a download like a restore`.
- [ ] **Step 2: Run** `cd src/Arius.Web && npx vitest run src/app/shared/job-format.spec.ts` — expect FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `npm test` and `npm run build` — expect PASS.
- [ ] **Step 5: Commit** `feat(web): add download job kind, models and clients`

---

### Task 7: Files tab Download button and the Download drawer

**Files:**
- Modify: `src/Arius.Web/src/app/features/repo/files/files-tab.component.ts`, `src/Arius.Web/src/app/core/state/drawer.store.ts`, `src/Arius.Web/src/app/features/drawer/archive-restore-drawer.component.ts`, `src/Arius.Web/src/app/features/repo/repo-detail.component.ts` (`openRestore(r.id, null, [])` keeps working)
- Create: `src/Arius.Web/src/app/core/downloads/download-name.ts`
- Test: `src/Arius.Web/src/app/core/state/drawer.store.spec.ts`, `src/Arius.Web/src/app/core/downloads/download-name.spec.ts`

**Interfaces:**
- Consumes: Task 6 `startDownload`, `DownloadFileRequest`.
- Produces (drawer.store.ts):
  - `export interface CollectedFile { path: string; size: number; inRepository: boolean }`
  - `DrawerType` adds `'download'`.
  - `collected = signal<CollectedFile[]>([])` replaces `collectedPaths`; computed `targets` (inRepository only), `skippedCount`, `targetBytes`, `canStart`.
  - `openRestore(repoId: number, version: string | null, collected: CollectedFile[])`, `openDownload(repoId: number, version: string | null, collected: CollectedFile[])` (download loads no defaults).
  - `canStart`: download → `targets().length > 0`; restore → `run() !== null && (collected().length === 0 || targets().length > 0)`; archive → `run() !== null`.
  - `start()` for download: `realtime.startDownload(repoId, { version: version() ?? snapshots.snapshots().at(-1)?.version ?? '', files: targets().map(({ path, size }) => ({ path, size })) })` then `pill.show(id, 'download')`. Restore sends `targetPaths: targets().map(t => t.path)`. `DrawerStore` injects `SnapshotStore` for the latest version (the pin).
- Produces (download-name.ts): `downloadFileName(alias: string, date: Date, paths: string[]): string` — one path → its last segment; several → `${alias}-${yyyy-MM-dd}.zip` (local date).
- Files tab: `collected` becomes `Map<string, CollectedFile>` (`inRepository = f.stateFlags.repository`, also for the revealed file); bar: `Clear` (outline) · `Download` (`ar-btn-primary`, `ki-exit-down`, `data-testid="download-collected"`) · `Restore` (`ar-btn-primary`, `ki-cloud-download`, keeps `data-testid="restore-collected"`).
- Drawer (`arType` also accepts `'download'`): header icon/title `Download · ‹alias›`; target card `‹n› collected files · ‹size›` + `Latest snapshot`/`Snapshot ‹version›` for both Download and Restore (Restore keeps "Whole repository" when nothing is collected); skipped note `data-testid="drawer-skipped"` (copy in Global Constraints) for both; Download shows `You'll receive` (`data-testid="drawer-receive"`) with `downloadFileName(alias, new Date(), targets)`, the same rehydration note, and no option fields; footer button `Start download` with `ki-exit-down`; Start disabled by `!store.canStart()`.

- [ ] **Step 1: Write the failing tests** (extend `makeStore` with a `SnapshotStore` stub `{ snapshots: signal([{ version: 'v1' }, { version: 'v2' }]) }` and `startDownload` mock; update existing `openRestore` tests to pass `CollectedFile[]`)

```ts
it('a download sends only repository files, pinned to the latest snapshot, and hands off to the pill')
// openDownload(9, null, [{path:'a',size:1,inRepository:true},{path:'b',size:2,inRepository:false}]);
// skippedCount() === 1; await start(); startDownload called with (9, {version:'v2', files:[{path:'a',size:1}]}); pill.show(id,'download')
it('a download keeps the snapshot being viewed')                         // openDownload(9, 'v1', …) → version 'v1'
it('a restore of collected files leaves out files that are not in the repository') // targetPaths ['a']
it('when every collected file is local-only, Start is disabled instead of restoring the whole repository')
// openRestore(9, null, [{…inRepository:false}]) after defaults load → canStart() false; start() sends nothing
// openDownload(9, null, [{…inRepository:false}]) → canStart() false
// download-name.spec
it('one file keeps its own name')            // ('Photos', d, ['x/IMG 1.HEIC']) === 'IMG 1.HEIC'
it('several files become alias-date.zip')    // ('Photos', new Date(2026,10,15), ['a','b']) === 'Photos-2026-11-15.zip'
```

- [ ] **Step 2: Run** `npx vitest run src/app/core/state/drawer.store.spec.ts src/app/core/downloads/download-name.spec.ts` — expect FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `npm test` and `npm run build` — expect PASS.
- [ ] **Step 5: Commit** `feat(web): add Download for collected files and skip local-only files`

---

### Task 8: Job page — download files card and download copy

**Files:**
- Create: `src/Arius.Web/src/app/features/jobs/download-files.ts`, `src/Arius.Web/src/app/features/jobs/download-files-card.component.ts`
- Modify: `src/Arius.Web/src/app/features/jobs/job-detail.component.ts`
- Test: `src/Arius.Web/src/app/features/jobs/download-files.spec.ts`

**Interfaces:**
- Consumes: Task 6 (`getDownload`, `cleanUpDownload`, `downloadContentUrl`, `JobKind` helpers).
- Produces (download-files.ts, pure):
  - `interface DownloadSummary { total; ready; readyBytes; downloaded; remaining; remainingBytes; pending }` — `downloaded` counts ready files already downloaded; `remaining` = ready && !downloaded; `pending` = !ready.
  - `summarize(files: DownloadFileDto[]): DownloadSummary`
  - `interface DownloadAction { label: string; set: 'all' | 'remaining'; primary: boolean; testid: string }`
  - `downloadActions(s: DownloadSummary, finished: boolean): DownloadAction[]` — `ready === 0` → `[]`; `downloaded === 0` → `[finished ? {'Download','all',true,'download-all'} : {'Download ready files','all',true,'download-ready'}]`; `remaining > 0` → `[{'Download all (n files)','all',false,'download-all'}, {'Download remaining (n file|files)','remaining',true,'download-remaining'}]`; otherwise `[{'Download all (n files)','all',true,'download-all'}]`.
  - `fileStatus(f: DownloadFileDto, jobStatus: string, cleanedUp: boolean): 'Cleaned up' | 'Downloaded' | 'Ready' | 'Rehydrating' | 'Pending' | 'Not prepared'` — in that precedence; `Rehydrating` while the job is `rehydrating`, `Pending` while otherwise non-terminal, `Not prepared` for a finished job.
  - `readyHeadline(s, finished): string` — `All ‹n› files ready · ‹size›` when `ready === total` and finished, else `‹ready› of ‹total› files ready · ‹size›`.
  - `readySubline(s, status, cleanupAt: string | null): string` — rehydrating with pending → `‹n› file(s) still being rehydrated`; finished with `cleanupAt` → `Kept on the server until ‹d MMM›, then cleaned up`; finished without → `Kept on the server until you clean them up`; else `''`.
- Produces `arius-download-files-card` (inputs `jobId: string`, `status: string`, `filesRestored: number`): loads `getDownload` on init and whenever `status` or `filesRestored` changes; renders the green card (`data-testid="download-card"`) per mockup state a/b with actions on one line inside the card — `Clean up now` text button (`data-testid="download-cleanup"`, finished && !cleanedUp, calls `cleanUpDownload` then reloads) before the download links; each action is `<a [href]="downloadContentUrl(jobId, a.set)" download [attr.data-testid]="a.testid">` styled violet (primary) / violet outline with `ki-exit-down`. Clicking marks the selection downloaded locally (optimistic; the server is the truth on next load). Cleaned up → the card reads `Files cleaned up` with no actions. File table rows `data-testid="download-file-row"`: name (path), size, `fileStatus`.
- job-detail.component.ts:
  - `kind` stays the restore-family value used by the bar/stages/tiles (`isRestoreFamily(toJobKind(detail.kind)) ? 'restore' : 'archive'`); new `isDownload` computed; breadcrumb/title/icon use `jobKindLabel`/`jobKindIcon`.
  - When `isDownload()`: render `<arius-download-files-card>` right after the rehydration wait card; wait-card second line = the spec line (keep `hydratedBy`), toggle label `Automatically prepare files as chunks become available`, button text `Prepare now` (testids unchanged); first KPI tile title `Prepared`, sub `on the server`; the awaiting-cost banner says `This download is paused…`.
  - Cost modal: title `Confirm download cost`, decline `Cancel download`, approve `Rehydrate & download` / `Download`, online sentence ends `Confirm the download cost to download.` (restore wording unchanged).

- [ ] **Step 1: Write the failing tests** in `download-files.spec.ts`, one per row of the spec's actions table plus statuses:

```ts
it('offers "Download ready files" while running with nothing downloaded')
it('offers "Download" once finished with nothing downloaded')
it('offers outline "Download all (3 files)" and primary "Download remaining (1 file)" after a partial download')
it('offers only "Download all (n files)" when everything ready was downloaded')
it('offers nothing when no file is ready')
it('labels files Downloaded, Ready, Rehydrating, Pending, Not prepared and Cleaned up')
it('headline says "All 3 files ready · 1.4 GB" when finished and "2 of 3 files ready · 20.5 MB" otherwise')
it('subline shows the clean-up date as d MMM')
```

- [ ] **Step 2: Run** `npx vitest run src/app/features/jobs/download-files.spec.ts` — expect FAIL.
- [ ] **Step 3: Implement** the pure module, the card, and the job-detail changes.
- [ ] **Step 4: Run** `npm test` and `npm run build` — expect PASS.
- [ ] **Step 5: Commit** `feat(web): show download files and actions on the job page`

---

### Task 9: Job pill for downloads

**Files:**
- Modify: `src/Arius.Web/src/app/core/state/job-pill.store.ts`, `src/Arius.Web/src/app/features/pill/job-pill.component.ts`
- Test: `src/Arius.Web/src/app/core/state/job-pill.store.spec.ts`

**Interfaces:**
- Consumes: Task 6 (`getPendingDownload`, `getDownload`, `downloadContentUrl`, `toJobKind`), Task 8 (`summarize`).
- Produces (JobPillStore):
  - `kind = signal<JobKind>('archive')`; `show(jobId: string, kind: JobKind)`.
  - `mode = signal<'progress' | 'download-ready'>('progress')`; `download = signal<DownloadSummary | null>(null)`; `resume` from the attach state (for the "~… for the rest" line).
  - Discovery: an active job always wins (`toJobKind(job.kind)`); with none, `getPendingDownload(repoId)` → when present and not dismissed, show it in `download-ready` mode.
  - An attached `download` job loads `getDownload` on attach and when `filesRestored` changes.
  - When a `download` job finishes, re-run pending discovery instead of the 4 s auto-hide (archive/restore keep the auto-hide).
  - `dismiss()` in `download-ready` mode also stores `arius.pill.dismissed.<jobId>` in `localStorage` (every access wrapped in try/catch); discovery skips dismissed job ids.
- Component: `progress` mode unchanged, except a rehydrating download with `ready > 0` shows `Rehydrating · ‹ready› of ‹total› ready` / `‹hydrated-by label›` and a `Download ready` link (`data-testid="pill-download"`, `ki-exit-down`, `href = downloadContentUrl(jobId, 'remaining')`, `download`). `download-ready` mode: green check circle, `Download ready` / `‹n› remaining file(s) · ‹size›`, link `Download` (`pill-download`, same href), then `View job ›`.

- [ ] **Step 1: Write the failing tests** (stub `globalThis.localStorage` with a Map-backed object in the tests that need it)

```ts
it('with no active job, shows the latest finished download with files left')
it('an active job wins over a pending download')
it('a dismissed download-ready pill stays hidden for that job when discovered again')
it('a finished download job switches to download-ready instead of hiding')
it('a rehydrating download reports how many files are ready')
```

- [ ] **Step 2: Run** `npx vitest run src/app/core/state/job-pill.store.spec.ts` — expect FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `npm test` and `npm run build` — expect PASS.
- [ ] **Step 5: Commit** `feat(web): show download-ready states in the job pill`

---

### Task 10: Settings page regroup and Temporary files

**Files:**
- Modify: `src/Arius.Web/src/app/shared/defaults-editor/defaults-editor.component.ts`, `src/Arius.Web/src/app/features/settings/settings.component.ts`
- Create: `src/Arius.Web/src/app/features/settings/temporary-files-card.component.ts`
- Test: covered by the hermetic spec in Task 11 (components aren't unit-tested under node Vitest); the existing `e2e/hermetic/specs/defaults.spec.ts` must keep passing.

**Interfaces:**
- Consumes: Task 6 (`getDownloadSettings`, `putDownloadSettings`, `getDownloadUsage`, `cleanUpAllDownloads`).
- Produces (DefaultsEditorComponent; Properties keeps its exact look):
  - `level === 'settings'`: `<h2>Archive</h2>` before the archive card and `<h2>Restore</h2>` before the restore card (styled as section headings), card titles `Defaults`, descriptions `Used when archiving, scheduled runs included. A repository can set its own in its Properties, and any run can change them.` / `Used when restoring. A repository can set its own in its Properties, and any run can change them.`; save label `Save settings`.
  - `level === 'repository'`: unchanged titles, copy and `Save defaults`.
  - `<ng-content />` right after the restore card; `extraDirty = input(false)`; `discarded = output<void>()` emitted by Discard; Save/Discard enabled when `dirty() || extraDirty()`; `save` still emits the draft. Testids unchanged.
- Produces `arius-temporary-files-card` (inputs `value: DownloadSettingsDto`; output `changed: DownloadSettingsDto`): `ar-card` titled `Temporary files`, intro copy, switch (`data-testid="cleanup-toggle"`) + `Clean up downloaded files after [n] days` (`data-testid="cleanup-days"`, number, min 1, disabled when off), hint copy, a usage row `Currently on the server` / `‹n› downloads · ‹size›` (`data-testid="download-usage"`) and `Clean up all now` (`data-testid="cleanup-all"`, danger-outline button with `ki-trash`) which calls `cleanUpAllDownloads()` immediately and shows the returned usage. No source pill.
- SettingsComponent: drops the page subtitle; loads defaults and download settings; keeps a download-settings draft; passes `extraDirty`; on `save` PUTs the defaults and (when changed) the download settings, error text `Could not save settings — please try again.`; on `discarded` resets the draft.

- [ ] **Step 1: Implement** the editor changes, the card, and the Settings host.
- [ ] **Step 2: Run** `npm test`, `npm run build`, and `npm run e2e:hermetic -- defaults` — expect PASS.
- [ ] **Step 3: Commit** `feat(web): regroup Settings and add temporary files clean-up`

---

### Task 11: Hermetic end-to-end coverage

**Files:**
- Modify: `src/Arius.Api.FakeTestHost/TestingControlEndpoints.cs`, `src/Arius.Web/e2e/hermetic/support/control.ts`
- Create: `src/Arius.Web/e2e/hermetic/specs/downloads.spec.ts`

**Interfaces:**
- Consumes: Tasks 3–10.
- Produces:
  - Scenario names `onlineDownload` and `rehydratingDownloadStaysPending` (Task 3's canonical scenarios).
  - `POST /api/testing/start-download` body `{ repoId, version, files: [{ path, size }] }` → calls `JobRunner.RunDownloadAsync` (fire-and-forget, new GUID) and returns `{ jobId }`. Document in the class summary that this is the one start that bypasses the hub: the scripted Core can't list entries, so the Files tab can't collect files in this suite.
  - `Control.startDownload(repoId: number, files: { path: string; size: number }[], version = 'v1'): Promise<string>`.

- [ ] **Step 1: Write the specs**

```ts
test('a finished download offers its files as one zip and cleans up on demand')
// seedRepo({alias:'dl'}); scenario(repo,'onlineDownload'); id = startDownload(repo, [photos/a.jpg, docs/b.txt])
// poll GET /api/jobs/{id} → awaiting-cost; goto /jobs/{id}; cost-modal contains 'Confirm download cost'; cost-approve has text 'Download'
// click → download-card contains 'All 2 files ready'; const dl = page.waitForEvent('download'); click download-all;
// (await dl).suggestedFilename() matches /^dl-\d{4}-\d{2}-\d{2}\.zip$/; rows show 'Downloaded';
// click download-cleanup → card contains 'Files cleaned up'
test('a rehydrating download offers the ready file now')
// scenario 'rehydratingDownloadStaysPending'; approve; status rehydrating; download-ready visible;
// suggestedFilename === 'a.jpg'; wait card contains 'prepares the rest by itself'
test('settings saves the download clean-up period and cleans up all now')
// goto /settings; headings 'Archive' and 'Restore' visible; cleanup-days fill '3'; defaults-save enabled → click;
// reload → cleanup-days has value '3'; cleanup-all click → download-usage contains '0 downloads'
```

- [ ] **Step 2: Run** `cd src/Arius.Web && npm run e2e:hermetic` — expect all specs (old and new) PASS.
- [ ] **Step 3: Commit** `test(web): hermetic e2e for browser downloads`

---

### Task 12: Documentation

**Files:**
- Create: `docs/decisions/adr-0026-browser-downloads-staged-by-a-download-job.md` (follow `adr-template.md`)
- Modify: `docs/guide/web-ui.md`, `docs/guide/deployment.md`, `docs/design/hosts/web.md`, `docs/glossary.md`, `docs/design/cross-cutting/testing.md`, `src/Arius.Api/AGENTS.md`, `docs/history/superpowers/2026-11-15-browser-download-design.md` (Status → Approved)

**Interfaces:** none (docs only; no code).

- [ ] **Step 1: Write**
  - ADR-0026: decision = browser downloads run a `download` job that reuses the restore flow and stages into `‹data dir›/downloads/‹jobId›`, streamed to the browser (ZIP built on the fly); alternatives B and C from the spec's Approaches table; consequences: disk use on `/data`, retention + clean-up, non-resumable ZIPs, Core unchanged.
  - web-ui.md: the Download flow (collected bar, drawer, cost approval, job page card, pill), skipped local-only files for Download and Restore, regrouped Settings with Temporary files; fix the egress note — the estimate bills egress from the first byte; the 100 GiB free allowance is not assumed.
  - deployment.md: downloads take space under `/data/downloads` until cleaned up.
  - design/hosts/web.md: the `download` job kind, its endpoints, the `download_files`/`app_settings` tables, `DownloadCleanupService`; invariants (ready from `FileRestoredEvent`, prune before each pass, in-progress folders never deleted).
  - glossary.md: **download job** → `JobRunner.RunDownloadAsync` / kind `download`.
  - testing.md: `downloads.spec.ts` and the `start-download` control endpoint exception.
  - Api AGENTS.md: `Downloads/` in Layout; `DownloadCleanupService` among the hosted services.
- [ ] **Step 2: Run** the docs gate if the repo has one (`mkdocs build --strict` when `mkdocs.yml` exists and mkdocs is installed); otherwise check links by hand.
- [ ] **Step 3: Commit** `docs: document browser downloads`

---

## Final verification

- [ ] `dotnet build` (solution) with no new warnings.
- [ ] `dotnet test --project src/Arius.Api.Tests`, `src/Arius.Api.Integration.Tests`, `src/Arius.Architecture.Tests`, `src/Arius.Core.Tests` — all PASS.
- [ ] `cd src/Arius.Web && npm test && npm run build && npm run e2e:hermetic` — all PASS.
- [ ] Whole-branch review against the spec and this plan's Review Focus.
