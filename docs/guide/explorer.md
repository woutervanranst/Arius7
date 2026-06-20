# Arius Explorer — user guide

Arius Explorer is the **Windows desktop app** for *browsing* and *restoring* an Arius
repository. It gives you a familiar file-explorer view — a folder tree on the left, a file
list on the right — over a repository that lives in an Azure Blob Storage container.

Explorer is a **read-only / restore-only** client. You can look through everything that has
ever been archived and pull files back down to disk, but you **cannot archive (back up) new
files from Explorer** — archiving stays on the [command-line tool](https://github.com/woutervanranst/Arius7). Think of
Explorer as the window into a repository, and the CLI as the thing that fills it.

> **Platform:** Windows only. Explorer is built on WPF (`net10.0-windows`) and uses
> Windows-only facilities (DPAPI credential protection, the Windows user-settings store). On
> Linux/macOS, use the [CLI](https://github.com/woutervanranst/Arius7) or the [Web host](./web-ui.md) instead.

For how the app is wired internally (the per-repository service graph, streaming, etc.), see
the architecture note: [design/hosts/explorer.md](../design/hosts/explorer.md). This page is
just about *using* it.

---

## What Explorer can and cannot do

| | Explorer (this app) | [CLI](https://github.com/woutervanranst/Arius7) | [Web](./web-ui.md) |
|---|---|---|---|
| Browse a repository (folders + files) | ✅ | `arius ls` | ✅ |
| See per-file state (on disk / in cloud / archived) | ✅ (status dots) | partial | ✅ |
| Restore / download files to disk | ✅ (select + Download) | `arius restore` | ✅ |
| **Archive (back up) new files** | ❌ | `arius archive` | ❌ |
| Pick an older snapshot / version | ❌ (latest only) | `arius ls -v` / `arius restore` | partial |
| Repair the chunk index | ❌ | `arius repair-index` | — |
| Runs on | Windows only | Windows / Linux / macOS | any (browser) |

Explorer always opens the container **read-only** and always browses the **latest** state of
the repository. If you need an older version of a file, or you need to archive, use the CLI.

---

## Installing and launching

Explorer ships as a Windows application. Download the build from the project's
[releases](https://github.com/woutervanranst/Arius7/releases/latest), then launch it like any
other desktop app — there is no command line to learn.

On first run, Explorer migrates any settings (your recent-repository list) from a previously
installed version, so upgrading keeps your recent repositories.

When the window opens:

- If you have used Explorer before, it **re-opens your most recently used repository
  automatically** and starts loading it.
- If this is a fresh install (no recent repositories), the **Choose Repository** dialog opens
  so you can connect to one.

You can always open the dialog yourself from **File ▸ Open…**.

---

## Connecting to a repository

A repository is one Azure Blob **container** plus a **local folder** on your PC. The *Choose
Repository* dialog has two halves:

```
┌──────────────── Local ────────────────┐  ┌──────────────── Azure ────────────────┐
│ Local path:  [ C:\Photos        ] [..] │  │ Storage Account Name: [ mystorageacct ]│
│                                         │  │ Storage Account Key:  [ •••••••••••• ] │
│                                         │  │ Container:            [ photos ▾ ]     │
│                                         │  │ Passphrase:           [ •••••••••••• ] │
└─────────────────────────────────────────┘  └─────────────────────────────────────┘
                                                                            [ Open ]
```

**Local path** — the folder on your PC this repository maps to. Click **`...`** to browse for
it. This is where restored files are written, and Explorer overlays what is in this folder
onto the cloud listing so you can see what you already have locally (see
[Reading the status dots](#reading-the-status-dots)). Pick (or create) an empty folder if you
are restoring into a fresh location.

**Storage Account Name** and **Storage Account Key** — your Azure Storage account name and one
of its access keys. As soon as both are filled in (Explorer waits about half a second after
you stop typing), it contacts Azure and fills the **Container** dropdown with the containers it
finds on that account. If the credentials are wrong, the account fields are outlined in **red**
and the dropdown stays empty.

**Container** — pick the container that holds the repository from the dropdown. (It must be a
valid Azure container name; an invalid one will keep the **Open** button disabled.)

**Passphrase** — the passphrase the repository was created with on archive. This is what
decrypts the file index and the file contents. There is no recovery if you lose it.

The **Open** button stays disabled until **all** fields are filled and the container name is
valid. Click **Open** to connect.

> **Where your credentials are stored.** When you open a repository it is added to your recent
> list, and the account key and passphrase are saved **encrypted with Windows DPAPI** (tied to
> your Windows user account). The plaintext key/passphrase are never written to disk. Another
> Windows user — or the same files copied to another machine — cannot read them back.

If a connection fails (wrong key, wrong passphrase, wrong container), Explorer shows an error
message box telling you to re-check the Account Name, Account Key, Container, and Passphrase.
Re-open **File ▸ Open…** and correct them.

### Re-opening a recent repository

**File ▸ Open recent** lists repositories you have connected to before, most recent first
(up to 10). Pick one to reconnect instantly — the saved credentials are reused, so you do not
re-enter the key or passphrase. The title bar shows the active repository as
`<local path> on <account>:<container>`.

---

## Browsing the repository

Once connected, Explorer shows:

- a **folder tree** on the left (starting at **Root**), and
- a **file list** on the right showing the files in the selected folder.

Click a folder to load and show its contents. Folders load **one level at a time** — a folder
shows a "Loading…" placeholder until you select or expand it, and then Explorer fetches just
that level. This keeps large repositories responsive; you are never waiting on a full
recursive listing. Selecting or expanding a folder cancels any load still running for the
previously selected one.

Each file row shows:

- a **checkbox** (used to select files for download),
- the file **Name**,
- a **Status** dot (see below), and
- the file **Size**.

The status bar at the bottom-left summarizes the current folder — e.g.
`12 item(s), 4.3 GB`, or `3 of 12 item(s) selected, 800 MB of 4.3 GB` once you tick some
boxes.

### Reading the status dots

The **Status** column shows a small split circle for each file. It encodes, at a glance,
*where each file currently exists* — locally on your PC, and/or in the cloud, and whether the
cloud copy is immediately downloadable. Hover the dot for a tooltip.

The circle has a **left half (your local disk)** and a **right half (the cloud / repository)**:

| Half / ring | Meaning |
|---|---|
| Left outer ring, black | A [pointer file](../glossary.md#pointer-file) for this file exists locally |
| Left inner, blue | The actual file (the binary) exists locally |
| Right outer ring, black | The file exists in the repository (it has been archived) |
| Right inner, color | Cloud download status of the file's data (below) |

The **right inner** color tells you whether the cloud data is ready to pull down:

| Color | Tooltip | Meaning |
|---|---|---|
| Blue | "Cloud chunk is available for download" | Ready — restore downloads immediately |
| Light blue | "Cloud chunk is archived and must be rehydrated first" | On the Archive tier; restoring it first triggers a **rehydration** (can be slow and incur cost) |
| Purple | "Cloud chunk rehydration is already pending" | Already being rehydrated in Azure; not ready yet |
| (empty) | "Cloud chunk status not loaded yet" | Status not yet determined |

Explorer shows a quick estimate first (from the index) and then refines each file's cloud
status with a live check against Azure, so light-blue/purple/blue can update a moment after a
folder loads. The live check is authoritative — it reflects the file's real
[storage tier](../glossary.md#storage-tier-hint), not just the index hint.

---

## Restoring (downloading) files

1. **Tick the checkbox** next to each file you want in the file list. The status bar shows how
   many you have selected and their total size.
2. Choose **File ▸ Download**.
3. Explorer first confirms the **cloud status** of your selection, then shows a confirmation
   dialog that spells out what will happen, for example:
   - *"This will start hydration on N item(s) (X GB). This may incur a significant cost."* —
     some files are on the Archive tier and must be rehydrated first.
   - *"N item(s) (X GB) are already rehydrating in the cloud."* — already in progress.
   - *"This will download N item(s) (X GB)."* — files that are ready to pull down now.
4. Click **Yes** to proceed, or **No** to cancel.

Restored files are written under the repository's **Local path**. After the restore finishes,
the current folder reloads so the status dots update to reflect what is now on disk.

> **About rehydration and cost.** Files stored on the Azure **Archive** tier (light-blue dot)
> are not instantly readable. Downloading them starts an Azure *rehydration*, which takes time
> (hours) and can be billed by Azure. Explorer makes you confirm this before it starts, and it
> tells you the total size involved. If a file is already rehydrating (purple dot), restoring
> it again does not start a second rehydration. The actual cost and timing are governed by
> Azure and by how the repository was archived.

### Restore limitations

- **Latest version only.** Explorer always restores the file as it is in the latest state of
  the repository. To restore an older version of a file, use `arius restore` from the
  [CLI](https://github.com/woutervanranst/Arius7).
- **One file at a time, sequentially.** Explorer downloads selected files one after another.
  Restoring a large selection works but is not batched; for very large bulk restores the CLI
  may be faster.
- **No live progress bar.** During a restore the cursor shows "busy" and the menu is occupied;
  there is no per-file progress indicator yet. Watch the status bar and the dialog that
  appears when it finishes.

---

## Settings and where things live

Explorer keeps things simple — there is no settings screen. What it persists:

- **Recent repositories** — the list under **File ▸ Open recent** (most-recent-first, capped
  at 10). Each entry remembers its local path, account, container, and the **DPAPI-encrypted**
  account key and passphrase. Opening a repository again refreshes its saved credentials and
  bumps it to the top.
- These are stored in your Windows **user-scoped** application settings, so they are per
  Windows user and travel with your profile, not the machine.

**Logs.** Explorer writes a log file per session to
`%LocalAppData%\Arius\logs\arius-explorer-<timestamp>.log`. If something fails, that file has
the details — useful when reporting an issue.

**About.** **Help ▸ About** shows the Explorer and Arius Core version numbers.

**If something goes wrong.** Explorer is built to log-and-report rather than crash: unexpected
errors pop up a message box (and are written to the log) instead of closing the app. If a
repository fails to load, re-check your credentials via **File ▸ Open…**.

---

## See also

- [Arius CLI](https://github.com/woutervanranst/Arius7) — archiving, restoring older versions, listing, repairing the index.
- [Arius Web](./web-ui.md) — the browser-based host (cross-platform).
- [Glossary](../glossary.md) — snapshot, pointer file, chunk, storage tier, and other terms.
- [Explorer architecture](../design/hosts/explorer.md) — how the desktop app is wired (for maintainers).
