# Download collected files through the browser

**Date:** 2026-11-15
**Status:** Draft
**Scope:** Arius.Web (Files tab, Archive/Restore drawer, job page, job pill, Jobs list, Settings page) and
Arius.Api (a new `download` job kind, download/clean-up endpoints, clean-up service, one new setting).
Core, CLI and Explorer are untouched.
**Mockup:** [`2026-11-15-browser-download-mockup.html`](2026-11-15-browser-download-mockup.html) (v4, approved).

## Why

In the Web UI's Files tab you can collect files and **Restore** them, but a restore always writes on the
server, into the repository's local folder (or a temp folder when none is set). There is no way to get a
file onto the computer you're browsing from. The user wants a **Download** next to Restore: one file
arrives as itself, several arrive as one ZIP, and the flow behaves like Restore (same drawer, same cost
approval, same rehydration handling).

## Goals

- A **Download** action for collected files, beside **Restore**, at the snapshot being viewed.
- One file downloads under its own name; several download as one ZIP.
- The same flow as Restore: drawer → cost approval → (rehydration wait) → job page, with the same
  automatic re-checks while rehydrating.
- Get online files **now** and archive-tier files **later**: download what's ready while the rest is
  rehydrating, then download the remaining files, or everything again.
- Prepared files are cleaned up after a configurable number of days, and can be cleaned up on demand.

## Non-goals

- Downloading a whole repository or a folder (only collected files, like Restore collected).
- Resumable ZIP downloads, or a known total size for ZIPs (accepted trade-off of approach A).
- Any change to Core, the CLI or Explorer.
- Per-repository clean-up settings. The clean-up period is one server-wide setting.
- Letting a download run alongside another job on the same repository (see Constraints).
- Changing the Properties page. Only the Settings page is regrouped.

## Anti-goals (it fails even if it "works")

- Buffering a download in browser memory. Multi-GB ZIPs must stream straight to the browser's own download.
- Writing anything into the repository's local folder. A download never touches it.
- Starting paid work without the same cost approval Restore uses.
- Cleaning up files of a download that is still in progress (running, awaiting cost, rehydrating).

## Constraints (how the code works today)

- **Restore writes to a local folder.** `RestoreCommand` takes a `RootDirectory` and writes through
  `RelativeFileSystem`: it skips files already present and identical, copies duplicate content from an
  already-restored file, and sets timestamps. A later pass (resume after rehydration) skips what's
  already there. This is why downloads are staged rather than streamed.
- **Online chunks are restored immediately; archive-tier chunks are rehydrated and restored by later
  passes.** `JobRunner.RunRestoreAsync` / `ResumeRestoreAsync` persist the resume state (including the
  destination) in the job row; `RehydrationPollingService` re-drives `rehydrating` jobs with auto-resume
  on (default on). High priority: every 15 min. Standard: quiet ~10 h, then hourly. Every 15 min once
  a chunk has come back.
- **Cost approval appears for every restore that downloads anything.** `RestoreCommandHandler` asks when
  `TotalStandard > 0`; egress is billed from the first byte and every chunk read is a billed operation,
  so a download always shows it.
- **Builds on PR #238 (multi-target restore), merged into master.** `RestoreOptions.TargetPaths`
  takes the whole selection and `JobRunner` sends **one** `RestoreCommand`, so the cost estimate and the
  single prompt cover every collected file. (Before #238 it looped per target and only the first target's
  estimate was ever shown.) #238 also makes a target that matches nothing in the snapshot **fail the
  restore before the cost prompt**, so hosts must not send paths that aren't in the repository.
- **One active job per repository.** `ux_jobs_one_active_per_repo` covers `running`, `awaiting-cost` and
  `rehydrating`. A download that is rehydrating therefore blocks other jobs on that repository, including
  scheduled archives (the scheduler skips a repository with an active job), exactly as a rehydrating
  restore does today. Inherited, not changed here.
- **Hosts talk to Core only through `IMediator`.** The download job sends the existing `RestoreCommand`;
  serving and zipping staged files is host-side (Api) file I/O.
- **App state lives in one data directory** (`/data` in Docker): the SQLite DB, keys and logs sit next to
  each other, derived from the DB path in `AriusApiHost`.

## Approaches

| | Approach | Trade-off | Decision |
|---|---|---|---|
| **A** | New `download` job kind running the existing restore into a per-job staging folder; the ZIP is assembled while it streams to the browser. | No second copy on disk; ready / remaining / all are just selections. ZIP downloads can't resume and show no total size. Single files can resume. | **Chosen** |
| B | Same job, but build ZIP files on the server once files are ready. | Resumable downloads with a known size, but double the disk, a ZIP per selection rebuilt after every rehydration pass, and a wait before the button appears. | Rejected |
| C | Stream from blob storage through Core straight to the browser. | Needs a new output path through `RestoreCommandHandler` (skip/duplicate/timestamp logic is filesystem-based) and can't cover archive-tier files, which take hours. | Ruled out |

Within A, Download is its **own job kind** (`download`), not a flag on `restore`, so the Jobs list, job
page and pill can label it without special cases.

## The design

### The flow, screen by screen (see the mockup)

1. **Files tab.** The collected bar reads: `N files collected · size`, then **Clear** (outline),
   **Download** (primary, download icon) and **Restore** (primary, restore icon). "Restore collected"
   becomes **Restore**.
2. **Drawer.** Download opens the same right-hand drawer as Restore, titled **Download · ‹alias›**:
   - the target card: `N collected files · size` and the snapshot (`Latest snapshot` / `Snapshot ‹version›`),
   - **You'll receive**: the file name (the file's own name for one file, otherwise `‹alias›-‹yyyy-MM-dd›.zip`),
   - the same rehydration note as Restore,
   - **Close** / **Start download** (download icon).
   No overwrite or skip-pointers options (they don't apply). The **Restore** drawer gains the size in its
   target card (`N collected files · size`) and the restore icon on **Start restore**.
3. **Cost approval.** The existing modal on the job page, covering the whole selection (one estimate,
   thanks to #238), worded for a download: title "Confirm download cost", decline "Cancel download",
   approve "Rehydrate & download" / "Download". The body keeps the existing chunk-based sentence
   ("‹n› of ‹m› chunks (‹size›) are in Archive tier…"); the estimate has no per-file counts.
4. **Job page** (`/jobs/:id`, kind `download`, labelled **Download · ‹alias›**). Same layout as a restore:
   status badge, layered bar, rehydration wait card (copy: "Arius checks periodically and prepares the
   rest by itself. You can close this page."), stage list. New:
   - a green **files-ready card** with the ready count and size and its actions on one line, inside the card;
   - a **file table**: name, size, status (`Downloaded`, `Ready`, `Rehydrating`).
   - once completed, the card says "Kept on the server until ‹date›, then cleaned up" and offers
     **Clean up now** as a plain text action (no icon).
5. **Pill.** While rehydrating with files ready: "Rehydrating · 2 of 3 ready" + **Download ready**.
   When finished with files not yet downloaded: "Download ready" + **Download**. Both keep "View job ›".
   - An active job on the repository always wins, as today.
   - With no active job, the pill shows the repository's most recent finished download that still has
     ready files not yet downloaded and hasn't been cleaned up. Older ones are reachable from the Jobs list.
   - It goes away when everything ready has been downloaded, the files are cleaned up, or it's dismissed.
     Dismissing is remembered in this browser for that job.
6. **Settings** (see below).

Icons are one set everywhere: Restore = cloud with down arrow (`ki-cloud-download`), Download = arrow into
a tray. Real buttons carry icons; text actions don't.

### Ready, downloaded, remaining

A collected file is **ready** once Core has reported it restored (`FileRestoredEvent`, persisted with the
job), **downloaded** once the server has finished sending it to the browser at least once, otherwise
**pending** (still rehydrating or not yet restored). "Exists in the staging folder" is not used: Core
writes files in place, so a half-written file from an interrupted pass would otherwise count as ready.
The card's actions follow from those counts:

| Situation | Actions (left → right) |
|---|---|
| Some ready, nothing downloaded yet | **Download ready files** (job not finished) / **Download** (job finished) |
| Some downloaded, some ready but not downloaded | **Download all (n files)** (outline) · **Download remaining (n files)** (primary) |
| Everything ready has been downloaded | **Download all (n files)** |
| Job finished (completed, failed, cancelled or interrupted) | **Clean up now** (text) before the buttons above |

A download that ends without completing keeps its ready files downloadable until they're cleaned up, the
same as a completed one. Its page shows the same files card.

- "All" means every ready file; "remaining" means ready and not yet downloaded.
- Each download serves **one file as itself, several as a ZIP**, decided per click (so "remaining" with
  one file is that file, not a ZIP).
- ZIP names: `‹alias›-‹date›.zip` for everything, `‹alias›-‹date› (2 of 3).zip` for a partial set,
  `‹alias›-‹date› (remaining).zip` for the rest. `‹date›` is the job's start date.
- ZIP entries keep their repository-relative paths, so same-named files from different folders don't clash.
- A file counts as downloaded only when the response completed; an aborted transfer doesn't count.
- The browser downloads through a plain link to the endpoint (`<a href download>`), never via JS blobs.

### The download job (Api)

- A hub method `StartDownload(repositoryId, version, targetPaths)` mirrors `StartRestore` and returns the
  job id. It inserts a `download` job and runs the existing restore path with
  `RootDirectory = ‹data dir›/downloads/‹jobId›`, `Overwrite = false`, `NoPointers = true`.
  The resolved snapshot version is pinned when the job starts, so later passes restore the same snapshot.
- **Before every restore pass** (the first and each resume), staged files that aren't in the job's ready set
  are deleted. Ready files are then skipped as identical, and a half-written file is restored again rather
  than kept as "local differs".
- Cost approval, decline, cancel, auto-resume, "Restore now" and the rehydration poller work unchanged:
  they key on the job id and the persisted resume state (which carries the destination). The poller
  already re-drives every `rehydrating` job, whatever its kind.
- The job's file list (collected paths, sizes), its ready set and its downloaded set are persisted with
  the job so the job page survives restarts, and must not be overwritten when the job completes.
- The single-active-job rule applies to `download` like any other kind.
- **Interrupted downloads are not retried.** A restart during a pass marks the job `interrupted` (as for
  restore). Its ready files stay downloadable; for the rest the user starts a new download. Chunks that
  were already rehydrated are found and reused by the new job, so only egress is paid again.
- **Collected files that aren't in the repository** (local-only files on the latest view) are left out
  by the Web before it starts the job (it knows from each entry's state flags): the drawer says
  "‹n› collected file(s) aren't in the repository and are skipped", and they never reach the server, so
  counts can still reach "all ready" and #238's fail-fast check isn't tripped. **Restore gets the same
  treatment** in the same drawer: since #238 a local-only file in the selection would otherwise fail the
  whole restore.

### Temporary files and clean-up

- Staging lives in `‹data dir›/downloads/‹jobId›/`, next to the app DB, keys and logs (the `/data` volume
  in Docker). It survives restarts, which rehydration needs.
- **Retention:** when clean-up is on, a download job's staging folder is deleted `N` days after the job
  reaches a terminal status (completed, failed, cancelled, interrupted). Default: on, 7 days.
- Deleting a repository also deletes its download jobs' staging folders. Off keeps files until
  cleaned up by hand. Jobs that are still running, awaiting cost or rehydrating are never cleaned up.
- A background service sweeps periodically (hourly is enough), mirroring `StaleApprovalSweepService`.
- **Clean up now** (job page) deletes that job's folder. **Clean up all now** (Settings) deletes the folders
  of every finished download job. Both are immediate, not part of Save.
- After clean-up the job row stays in the Jobs history; its page shows the files as cleaned up and no
  download actions.

### Settings page

Regrouped into two sections, each with sub-sections:

```
# Settings
## Archive
### Defaults        — Upload tier, On disk after archive, Fast hash (unchanged fields and source pills)
## Restore
### Defaults        — Overwrite existing files, Skip pointer files (unchanged)
### Temporary files — "Downloading collected files saves them in a temporary location on the server
                       before your browser downloads them."
                      [switch] Clean up downloaded files after [7] days
                      hint: "Off keeps them on the server until you clean them up."
                      Currently on the server: ‹n› downloads · ‹size›   [Clean up all now]
[Discard] [Save settings]
```

- The old page subtitle ("Global defaults for every repository…") moves into each **Defaults**
  description: "Used when archiving, scheduled runs included. A repository can set its own in its
  Properties, and any run can change them." / "Used when restoring. A repository can set its own…".
- **Temporary files** is not part of the defaults hierarchy: no source pill, not shown in Properties,
  stored as its own setting in the app DB.
- One **Save settings** button saves the defaults and the clean-up period together.

### Docs

- **ADR** (next number): browser downloads are staged server-side by a `download` job that reuses the
  restore flow, instead of streaming through Core.
- `docs/guide/web-ui.md`: the Download flow, the regrouped Settings page, temporary files; also correct the
  egress note (the estimate bills egress from the first byte; the 100 GiB free allowance is not assumed).
- `docs/guide/deployment.md`: downloads take space on the `/data` volume.
- `docs/design/hosts/web.md` and the Api design doc: the `download` job kind and endpoints.
- `docs/glossary.md`: "download job" if a new term is introduced.

## Calls I made (check these)

- Download is its own job kind, not a restore flag.
- Staging sits in the data directory next to the DB; not separately configurable.
- No free-space check before starting (Restore has none either); a full disk fails the job like any restore I/O error.
- ZIP date is the job's start date, not the snapshot's date.
- Retention counts from when the job reaches a terminal status; default on, 7 days.
- "Downloaded" means the server finished sending it, not that the browser confirmed the save.
- The browser never starts a save by itself; the user always clicks.
- Properties keeps its current layout; only Settings is regrouped.
- "Ready" comes from Core's restored-file events, not from files on disk; unready staged files are deleted before each pass.
- Finished-but-not-completed downloads keep their ready files downloadable; interrupted downloads are not retried.
- The pill rules above (active job first, then the latest download with files left; dismiss remembered per browser).
- Collected files that aren't in the repository are skipped by Download **and Restore**, with a line in the drawer.

## Left to the builder

- Exact endpoint routes and DTO shapes, and where the file list and downloaded set are persisted
  (job `state_json` or a table).
- How the job pill discovers a completed download with files still to download after a page reload.
- How the hermetic e2e's scripted Core produces staged files.
- ZIP writing details (`ZipArchive` over the response stream; stored vs. deflate per entry).
- Splitting the shared defaults editor so Settings has one Save button covering defaults and the clean-up period.
- Updating the Web code that treats any job kind other than `restore` as `archive`.
