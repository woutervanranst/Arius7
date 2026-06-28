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
| **Overview** | KPI cards, a **Storage accounts** table, and the **Repositories** table — whose header carries the **Add existing** / **New repository** buttons. Your home screen. |
| **Repos** | The repositories list — same data. |
| **Jobs** | Every archive/restore run (one-off and scheduled) with a live output console. |
| **Settings** | Placeholder for now. |

The top bar shows a breadcrumb and — on every screen except Overview — a global search box. Press
**⌘K** (macOS) or **Ctrl+K** (Windows/Linux) anywhere to open search; **Esc** closes it.

---

## First run — add a repository

Open the app, go to **Overview** (or **Repos**), and choose one of the two buttons in the
**Repositories** card header. Both are two-step wizards; the difference is whether the container
already exists.

- **Add existing** — point at a container that already holds an Arius archive. Arius reads the
  existing manifest and snapshots; **no files are uploaded**.
- **New repository** — create a brand-new container to archive into.

### Step 1 — the storage account (both wizards)

Pick **Use configured** to reuse a storage account you've already added (each row shows how many
repositories use it), or **Add new account** and enter:

- **Account name** — your Azure Storage account name.
- **Account key** — the account key. It's stored encrypted server-side (Data-Protection) and is
  never sent back to the browser.

Mandatory fields are marked with a red asterisk. (There is no region to pick here — the region used
for cost estimates is read from the container's own metadata; see
[Managing storage accounts](#managing-storage-accounts).)

In the **Add existing** wizard, the button is **Connect & discover**: Arius connects with that
account and *live-streams* the container names it finds. The **New repository** wizard uses
**Continue** (it doesn't need to list containers — it's making a new one).

### Step 2a — Add existing: pick the container

You'll see the discovered containers as a radio list. Select the one holding your archive, then fill
in:

- **Friendly alias** *(optional)* — the display name used throughout the UI; leave it blank to
  default to the container name.
- **Passphrase** — the repository passphrase (needed to read/write encrypted chunks).
- **Local path** *(optional)* — a folder on the **server** that the Files view overlays against the
  archive (and the default source folder for archive runs). It must resolve on the machine running
  Arius.Api — in Docker that means a folder you've mounted into the container. Click the **[…]**
  button to browse the folders the server can see, or type the path. See
  [deployment](./deployment.md) for how the mounts work.

Click **Add repository** — you land on the repository's Files view.

### Step 2b — New repository: create the container

- **Container name** *(required)* — the blob container to create, typed by you (e.g.
  `arius-photos`). One container per repository. It's the first field you fill.
- **Friendly alias** *(optional)* — display name; defaults to the container name when left blank.
- **Local path** *(optional)* — same meaning as above, with the same **[…]** server-folder browser.
- **Default tier** — `Hot`, `Cool`, `Cold`, or `Archive`. This is the tier preselected for archive
  runs; you can still override per run. `Archive` is the cheapest to store and the slowest/most
  expensive to read back (it requires rehydration — see below).
- **Passphrase** + **Confirm passphrase** — must match. The form won't enable **Create repository**
  until the container name, passphrase, and confirmation are all filled and matching. Mandatory
  fields are marked with a red asterisk, and a warning reminds you the passphrase cannot be recovered.

Click **Create repository** to land on its Files view.

---

## Managing storage accounts

The **Overview** lists every storage account under management in its own table — its name and how
many repositories use it. Click an account row to open its **edit flyout** (a right-hand drawer):

- **Account key** — paste a new key to rotate it (stored encrypted; leave blank to keep the current
  one). The key belongs to the *account*, so this is the one place to change it — it is no longer on
  a repository's Properties.
- **Delete account** *(danger zone)* — removes an account that has **no** repositories.

New accounts are added from the **Add new account** step of either wizard (above).

> **Where does the pricing region come from?** Cost estimates are priced for an Azure **region**, but
> Arius reads that region from each **container's own metadata** — it is not chosen in the UI. Set the
> container's `region` metadata (e.g. `westeurope`) in **Azure Storage Explorer** for an accurate
> estimate; if it is unset, Arius prices against a default region (`northeurope`). The region is used
> only to pick rates; the resolved region is shown (read-only) in the repository list, flagged
> `(default)` when the container's metadata is unset.

---

## Browsing a repository

Selecting a repository (from a table row, or after a wizard) opens the **repository detail** screen:
a header with the alias, the container and local-path chips, and the **Restore**, **Archive**, and
**Properties** buttons, then the **snapshot bar**, then two tabs — **Files** and **Statistics**.
(Restore and Archive open right-hand drawers; **Properties** opens a drawer too — see *Properties*
below.)

**Snapshot / time-travel bar.** Each archive run produces a *snapshot*. The bar sits above the tabs
and applies to whatever tab you're on. It shows the active snapshot with a **LATEST** pill, a
dropdown picker listing every snapshot **newest-first** (version + timestamp), and a scrubber with
one dot per snapshot. Versions are numbered oldest-first — **v1** is the first archive run, the
highest number is the latest. Click a dropdown item or a scrubber dot to *time-travel* — the Files
explorer then shows the repository exactly as it was at that snapshot, and an amber **Historical
view** badge appears. Pick the latest snapshot (the top of the dropdown, or **LATEST**) to return to
the live working state.

### Files tab — the explorer

The Files tab is where you browse what's in the repository and pick files to restore, scoped to the
snapshot selected in the bar above.

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

Five KPI cards in one row, in two scoped sections. **This snapshot** (follows the snapshot bar)
shows **Files** and **Original size** — the logical, uncompressed size of the selected snapshot,
what you would restore. **Repository storage · across all snapshots** shows **Deduplicated size**
(unique data before compression), **Stored size** (the actual cloud footprint after dedup +
compression), and **Unique chunks**. Below the cards, a **Stored size by tier** breakdown shows each
tier's share as a 100%-stacked bar and, per tier, its stored size, chunk count, and **estimated
monthly cost**, with a grand-total monthly cost — priced for the repository's region (read from the
container's metadata; see [Managing storage accounts](#managing-storage-accounts)).

The two snapshot cards load immediately; the three repository-storage cards (and the cost breakdown)
are **calculated across all snapshots** and load separately. The result is cached serverside per
snapshot.

### Properties

**Properties** is a right-hand drawer, opened from the button in the repository header.

- **Friendly alias** and **Local folder** — editable (the local folder has a **[…]** server-folder
  browser); **Save changes** persists them.
- **Storage account** and **Container** — read-only. The account **key** belongs to the *account*,
  not the repository — rotate it from the account's flyout on the Overview (see
  [Managing storage accounts](#managing-storage-accounts)).
- **Passphrase** — type a new passphrase to rotate it; a **Confirm passphrase** field appears and
  must match before **Save changes** is enabled. Leaving it blank keeps the current one. Note that
  existing snapshots stay readable with the passphrase they were written with — rotating affects
  future writes, it does not re-encrypt the archive.
- **Scheduled archives** — add a cron expression (e.g. `0 2 * * *`, interpreted in **UTC**) to fire
  archive runs automatically. Existing schedules show their next run time and a trash button to
  delete them.
- **Delete repository** *(danger zone)* — removes the repository from Arius after a confirm step.
  The Azure container and its blobs are **not** deleted; you can re-add it later with **Add
  existing**.

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

### The restore cost confirmation

Restoring can cost money: reading from the **Cool** or **Cold** tiers incurs a per-GB data-retrieval
charge, **Archive** chunks must be *rehydrated* first (this takes hours and is pricier), and
downloading data out of Azure incurs egress. Whenever a restore has a non-zero estimated cost, Arius
pauses **before downloading anything** and shows a cost-approval modal. The estimate includes data
retrieval, operations, and internet egress (the first 100 GB/month of egress is free), priced for
the repository's region (read from the container's metadata).

The modal adapts to what's being restored:

- **Archive restore** — shows how many chunks must be rehydrated and the size from archive, and asks
  you to choose a **rehydration priority**: **Standard** (~15 h, cheaper) or **High** (~1 h,
  pricier), each with its estimated total.
- **Online restore** (Hot/Cool/Cold only) — no rehydration; shows the chunk count, download size,
  and a single estimated total.

Click **Approve & restore** to proceed, or **Decline** to cancel. Closing the drawer (or losing the
connection — e.g. closing the tab) while the modal is up also declines and cancels the restore. A
genuinely free restore skips the modal entirely.

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
