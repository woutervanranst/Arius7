# Arius Web UI — user guide

The Arius Web UI is a single-page app for archiving and restoring files to Azure Blob Storage
through your browser — no command line needed. It connects to a storage account, reads the
encrypted manifest already in your container, and lets you browse snapshots, run archive and restore
jobs, watch them live, and search across every repository you've added.

This guide is task-first: what you click and what happens. For *how* it's built (the Arius.Api +
SignalR backend behind the SPA), see [the Web host design doc](../design/hosts/web.md). For *how to
run* the server in Docker or in dev, see [the deployment guide](./deployment.md) and the
[repository README](https://github.com/woutervanranst/Arius7). Unfamiliar terms (snapshot, chunk, tier, pointer, rehydration)
are defined in the [glossary](../glossary.md).

> **Heads-up about the passphrase.** Every chunk is encrypted with your repository passphrase. Arius
> cannot recover it. If you lose it, the data in the container is unrecoverable. Store it somewhere
> safe before you archive anything.

---

## The layout

A narrow icon rail on the left switches between the four top-level areas:

| Rail item | What it is |
|---|---|
| **Overview** | KPI cards and the table of all repositories under management. Your home screen. |
| **Repos** | The repositories list — same data, plus the **Add existing** / **New repository** wizards. |
| **Jobs** | Every archive/restore run (one-off and scheduled) with a live output console. |
| **Settings** | Placeholder for now. |

The top bar shows a breadcrumb and — on every screen except Overview — a global search box. Press
**⌘K** (macOS) or **Ctrl+K** (Windows/Linux) anywhere to open search; **Esc** closes it.

---

## First run — add a repository

Open the app, go to **Overview** (or **Repos**), and choose one of the two buttons. Both are
two-step wizards; the difference is whether the container already exists.

- **Add existing** — point at a container that already holds an Arius archive. Arius reads the
  existing manifest and snapshots; **no files are uploaded**.
- **New repository** — create a brand-new container to archive into.

### Step 1 — the storage account (both wizards)

Pick **Use configured** to reuse a storage account you've already added (each row shows how many
repositories use it), or **Add new account** and enter:

- **Account name** — your Azure Storage account name.
- **Account key** — the account key. It's stored encrypted server-side (Data-Protection) and is
  never sent back to the browser.

In the **Add existing** wizard, the button is **Connect & discover**: Arius connects with that
account and *live-streams* the container names it finds. The **New repository** wizard uses
**Continue** (it doesn't need to list containers — it's making a new one).

### Step 2a — Add existing: pick the container

You'll see the discovered containers as a radio list. Select the one holding your archive, then fill
in:

- **Friendly alias** *(required)* — the display name used throughout the UI.
- **Passphrase** — the repository passphrase (needed to read/write encrypted chunks).
- **Local path** *(optional)* — a folder on the **server** that the Files view overlays against the
  archive (and the default source folder for archive runs). It must resolve on the machine running
  Arius.Api — in Docker that means a folder you've mounted into the container. See
  [deployment](./deployment.md) for how the mounts work.

Click **Add repository** — you land on the repository's Files view.

### Step 2b — New repository: create the container

- **Friendly alias** — as you type, a **container name** is auto-generated (`arius-<slug>-<hex>`,
  read-only). One container per repository.
- **Local path** *(optional)* — same meaning as above.
- **Default tier** — `Hot`, `Cool`, `Cold`, or `Archive`. This is the tier preselected for archive
  runs; you can still override per run. `Archive` is the cheapest to store and the slowest/most
  expensive to read back (it requires rehydration — see below).
- **Passphrase** + **Confirm passphrase** — must match. The form won't enable **Create repository**
  until the alias, passphrase, and confirmation are all filled and matching. A warning reminds you
  the passphrase cannot be recovered.

Click **Create repository** to land on its Files view.

---

## Browsing a repository

Selecting a repository (from a table row, or after a wizard) opens the **repository detail** screen:
a header with the alias, the container and local-path chips, and the **Archive** / **Restore**
buttons, over three tabs — **Files**, **Statistics**, **Properties**.

### Files tab — snapshots and the explorer

The Files tab is where you browse what's in the repository and pick files to restore.

**Snapshot / time-travel bar (top).** Each archive run produces a *snapshot*. The bar shows the
active snapshot with a **LATEST** pill, a dropdown picker listing every snapshot (version,
timestamp, file count), and a scrubber with one dot per snapshot. Click a dropdown item or a
scrubber dot to *time-travel* — the explorer then shows the repository exactly as it was at that
snapshot, and an amber **Historical view** badge appears. Pick the newest snapshot (or **LATEST**)
to return to the live working state.

**Explorer (folder tree + file list).** The left pane is the folder tree (expand folders to drill
in); the right pane lists the **files** in the selected folder. Above them: an **up** button, the
current path, and a **filter** box that matches file names in the current folder (case-insensitive
substring). Each file row shows:

- **State ring** + label — a small two-sided icon: the **left** half is your local disk, the
  **right** half is the repository. Outer ring = pointer/filetree entry; inner disc = the actual
  binary/chunk. Colors: dark = present, blue = binary-on-disk / chunk hydrated, light blue = chunk
  *not* hydrated (sitting in the archive tier), grey = absent. Click **State legend** in the footer
  for the full key. The text label summarises it: *In sync*, *Pointer only*, *In repository*, *Not
  archived*, *Archive tier*, *Rehydrating*.
- **Size**, **Tier** (`Online` or `Archive`), and **Modified** date.

**Collecting files to restore.** Click any file row to *collect* it (a checkmark fills in, the row
highlights). A blue **collected bar** appears showing the count and total size, with **Clear** and
**Restore collected** buttons. Collecting respects the snapshot you're viewing — collect from a
historical snapshot to restore that older version. Use **Restore collected** to open the Restore
drawer scoped to exactly those files.

### Statistics tab

KPI cards in two scoped sections. **This snapshot** shows **Files** and **Original size** (the
logical, uncompressed size of the selected snapshot — what you would restore). **Repository
storage · across all snapshots** shows **Deduplicated size** (unique data before compression),
**Stored size** (the actual cloud footprint after dedup + compression), and **Unique chunks**.
A green **savings** banner expresses the original→stored reduction as a percentage, and a
**Stored size by tier** breakdown bar follows. A note explains the figures are derived from the
file-tree and chunk index and finalise once the local cache has fully downloaded, so they may
read low right after you add a large repository.

### Properties tab

- **Friendly alias** and **Local folder** — editable; **Save changes** persists them.
- **Storage account** and **Container** — read-only.
- **Account key** — type a new value to rotate it (stored encrypted; left blank means unchanged).
- **Scheduled archives** — add a cron expression (e.g. `0 2 * * *`, interpreted in **UTC**) to fire
  archive runs automatically. Existing schedules show their next run time and a trash button to
  delete them.

---

## Archiving (backing up)

Click **Archive** in the repository header to open the Archive drawer (a right slide-over).

1. **Source folder** — shown read-only; it's the repository's local path. If it's empty, set one in
   **Properties** first (there's nothing local to archive otherwise).
2. **Upload tier** — `Hot` / `Cool` / `Cold` / `Archive` (defaults to the repository's default
   tier). This is where the newly uploaded chunks land.
3. **Toggles** (mutually exclusive — turning one on turns the other off):
   - **Remove local binaries** — after a successful upload, replace local files with pointer files
     to reclaim disk space (`--remove-local`).
   - **Skip pointer files** — don't write pointer files at all (`--no-pointers`).
   - A note reminds you small files are bundled (1 MB threshold, ~64 MB tar target) automatically.
4. Click **Start archive**. The drawer switches to the live stream (see *Watching live progress*).

---

## Restoring (downloading)

Open the Restore drawer in one of two ways:

- **Restore** in the repository header → restores the **whole repository** at the latest snapshot.
- **Restore collected** in the Files tab → restores only the files you collected, at the snapshot
  you were viewing.

The drawer shows the **target** (whole repository or *N* collected files) and the snapshot, then two
options:

- **Overwrite existing files** — re-restore files even if they're already present at the
  destination. Off by default (identical files are skipped).
- **Skip pointer files** — don't write pointers.

Click **Start restore**.

### The rehydration cost confirmation

Chunks in the **Archive** tier are offline and must be *rehydrated* before they can be downloaded —
this takes hours and costs money. If your restore touches any archive-tier chunks, Arius pauses
**before downloading anything** and shows a cost-approval modal:

- **Ready now** — chunks already online.
- **Rehydrate** — how many chunks must be rehydrated, and the total size from archive.
- **Rehydration priority** — choose **Standard** (~15 h, cheaper) or **High** (~1 h, pricier). Each
  shows its estimated cost.

Click **Approve & restore** to start rehydration with your chosen priority, or **Decline** to
cancel. Closing the drawer (or losing the connection — e.g. closing the tab) while the modal is up
also declines and cancels the restore. Restores that touch only online chunks skip this modal
entirely.

---

## Watching live progress

Once an archive or restore starts, the drawer streams progress over a live WebSocket connection:

- A **progress bar** with a percentage and a status line (*Archiving… / Restoring… / Awaiting cost
  approval / Done*).
- A **stats grid** of live counters (files, bytes, chunks — whatever the run reports).
- A **live console** of log lines, scrolling as the job runs.
- On completion, a green check with a one-line summary.

You can **Hide** the drawer while a job runs (it keeps going) and **Close** it once it's done.

### The Jobs screen

The **Jobs** rail item shows every run across all repositories in one table — repository, kind
(archive/restore), trigger (*manual* or *schedule*), status, and a progress bar. Status badges:
**Running**, **Rehydrating**, **Scheduled**, **Queued**, **Completed**, **Failed**. Counters at the
top show how many are running and scheduled. Below the table, a **Live output** console aggregates
log lines from active jobs — handy for keeping an eye on a scheduled archive that fired without you.

---

## Global search

Press **⌘K** / **Ctrl+K** (or click the search box in the top bar) to open the search overlay. Type
to search **file names and paths across every repository** you've added — matching is a recursive
filename filter run per repository, so one unreachable repository won't break the rest of the
results. Each hit shows the file's state ring, name, owning repository, path, and size. Click a
result to jump to that repository's Files view. **Esc** closes the overlay.

---

## Where to go next

- [Deployment guide](./deployment.md) — run the server (Docker single-container, Compose, dev).
- [Web host design](../design/hosts/web.md) — the Arius.Api + SignalR architecture behind this UI.
- [Glossary](../glossary.md) — snapshot, chunk, dedup, tier, pointer, rehydration.
- [Repository README](https://github.com/woutervanranst/Arius7) — project overview and quick start.
