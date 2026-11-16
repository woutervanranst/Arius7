---
status: accepted
date: 2026-11-16
decision-makers: Wouter Van Ranst
consulted: Claude (Anthropic)
informed: Arius agent team
confidence: medium
---

# Stage browser downloads on the server with a download job that reuses the restore flow

## Context and Problem Statement

The Web UI can collect files in the Files tab and restore them, but a restore always writes on the server, into the repository's local folder. There is no way to get a file onto the computer the user is browsing from. The user wants a **Download** beside Restore: one file arrives as itself, several arrive as one ZIP, and the flow behaves like Restore (same cost approval, same rehydration handling). Archive-tier files take hours to rehydrate, so online files should be downloadable while the rest is still rehydrating.

`RestoreCommandHandler` is filesystem-based: it skips files that are already present and identical, copies duplicate content from an already-restored file, and sets timestamps, and a later pass after rehydration relies on what earlier passes left behind. Hosts reach Core only through `IMediator` ([ADR-0013](adr-0013-core-host-separation.md)), and a multi-GB selection must never be buffered in browser memory.

The question for this ADR is where the restored bytes live between Azure and the user's browser, and which flow produces them.

## Decision Drivers

* Reuse the restore flow unchanged (cost approval, rehydration, auto-resume, the rehydration poller, one active job per repository); Core, CLI and Explorer stay untouched.
* Archive-tier files must be downloadable "later", in batches, as they become ready.
* Multi-GB downloads stream to the browser; nothing is buffered in memory or a second copy on disk if avoidable.
* A download never writes into the repository's local folder.
* Correctness and durability: a half-written file must never be offered as ready, and files of an in-progress download must never be cleaned up.

## Considered Options

* A. A `download` job kind that runs the existing restore into a per-job staging folder; the ZIP is assembled while it streams to the browser.
* B. The same job, but build ZIP files on the server once files are ready.
* C. Stream from blob storage through Core straight to the browser.

## Decision Outcome

Chosen option: "A: a `download` job that restores into a staging folder and streams from it", because it reuses the whole restore flow as is, covers archive-tier files by the existing multi-pass rehydration, and keeps a single copy on disk: ready, remaining and all are just selections over the staged files.

Confidence: medium. The design is small and sits on existing machinery, but it adds disk use to the host's data volume and a retention policy that only real usage can tune; ZIP downloads cannot resume.

Before:

```text
Files tab → Restore → files written on the server, in the repository's local folder
```

After:

```text
Files tab → Download → download job → RestoreCommand into <data dir>/downloads/<jobId>
         → browser fetches one file, or a ZIP built on the fly, from that folder
         → folder cleaned up after N days (default 7) or on demand
```

The job is its own kind (`download`), not a flag on `restore`, so the Jobs list, job page and pill label it without special cases. It sends the existing `RestoreCommand` with `Overwrite = false`, `NoPointers = true` and a snapshot version pinned when the job starts. A file is **ready** once Core reports it restored (`FileRestoredEvent`), never because it exists on disk, and staged files that are not ready are deleted before each resume pass. Design: [Web host](../design/hosts/web.md#download-jobs).

### Consequences and Tradeoffs

* Good, because Core, the CLI and Explorer are unchanged, and cost approval, rehydration and auto-resume work as they do for a restore.
* Good, because there is one copy on disk: "ready", "remaining" and "all" are selections, and a single file downloads as itself with resume support.
* Bad, because downloads take disk space under `/data/downloads` until cleaned up. Retention (on, 7 days by default), **Clean up now** and **Clean up all now** bound it; there is no free-space check before starting, the same as Restore, and a full disk fails the job like any restore I/O error.
* Bad, because a ZIP is built while it streams: it cannot resume and has no known total size.
* Bad, because a rehydrating download holds the repository's single active-job slot, blocking other jobs including scheduled archives, exactly as a rehydrating restore does today.
* Bad, because an interrupted download is not retried: its ready files stay downloadable, and for the rest the user starts a new download (already rehydrated chunks are reused, so only egress is paid again).

### Confirmation

`Arius.Api.Tests` covers the staging folder and naming helpers, the `download_files` store and which transfers count as downloaded (aborted ones never do). `Arius.Api.Integration.Tests` covers the download job (restores only into its staging folder with fixed options; ready files survive a resume while unready staged files are pruned first), the content endpoint (one file, ZIP, a range past the end) and clean-up (the sweep, in-progress folders kept, repository delete). A hermetic Playwright spec (`downloads.spec.ts`) drives the cost approval, download and clean-up through the real UI.

## Pros and Cons of the Options

### A. Download job restoring into a staging folder

* Good, because it adds one job kind and host-side file serving; everything else is the restore flow.
* Good, because ready and remaining files are available while the rest rehydrates.
* Bad, because of the disk use, retention and non-resumable ZIPs recorded above.

### B. Build ZIP files on the server once files are ready

* Good, because ZIP downloads would be resumable with a known size.
* Bad, because it doubles the disk use, rebuilds a ZIP per selection after every rehydration pass, and makes the user wait before a button appears.

### C. Stream from blob storage through Core to the browser

* Good, because nothing would be staged.
* Bad, because it needs a new output path through `RestoreCommandHandler`, whose skip, duplicate and timestamp logic is filesystem-based, and it cannot cover archive-tier files, which take hours to become readable.

## More Information

- Design spec, frozen: [browser-download-design.md](../history/superpowers/2026-11-15-browser-download-design.md)
- Host design: [Web host](../design/hosts/web.md#download-jobs)
- User guide: [Downloading collected files](../guide/web-ui.md#downloading-collected-files)
- Related: [ADR-0013](adr-0013-core-host-separation.md) (hosts drive Core only through `IMediator`), [ADR-0025](adr-0025-archive-restore-defaults-hierarchy.md) (the Settings page)
