# Handoff: Arius.Web — repository management interface

## Overview
Arius.Web is the management UI for **Arius**, a deduplicating archival tool that stores local files as encrypted, chunked blobs in Azure Blob Storage (with `.pointer.arius` sidecars left on disk). The app manages **multiple repositories** (each = one blob container) and lets the user:

- See an **overview** of all repositories + a jobs summary.
- **Browse a repository** with a Windows-Explorer feel (folder tree + file detail pane), overlaying the local filesystem on top of the archive, with **time-travel** across snapshots.
- **Archive** local files and **restore** files/folders/whole repository, both **streaming live progress** from the backend.
- **Add an existing** repository or **create a new** one (two-step wizard: storage account → container).
- View per-repository **statistics** and edit **properties**.
- Manage **jobs** — one-off archive/restore runs and scheduled (cron) archive jobs.
- **Search files across all repositories**.

## About the design files
The files in this bundle are **design references authored in HTML** (a single-file prototype framework — see "Prototype tech" below). They are **not production code to copy**. The task is to **recreate these designs in the target stack** — **Angular + Metronic v9 (Tailwind) for the web**, **a minimal .NET `Arius.Api` over `Arius.Core`** for the backend — using each layer's established patterns. Treat the HTML as the source of truth for **layout, behavior, copy, and visual detail**.

### Prototype tech (so you can read the files)
`Arius.Web.dc.html` is a "Design Component": markup with `{{ }}` holes + a `class Component extends DCLogic` block at the bottom holding all state/handlers (read this class to understand interactions and data shapes). `Shells.dc.html` shows the three Metronic shell layouts that were evaluated — **demo8 was chosen** (icon rail + floating content card on a muted background). Open either file in a browser to view.

## Fidelity
**High-fidelity.** Final colors, typography, spacing, component design, interactions, and copy. Recreate pixel-accurately using **Metronic v9 Angular** components and tokens (the prototype already uses Metronic's design tokens and Keenicons). Where the prototype hand-rolls a control that Metronic ships (buttons, inputs, tabs, toggles, dropdowns, drawers/sheets, modals), prefer the Metronic component with the same visual result.

---

## Target architecture

```
┌─────────────────────────┐     SSE / SignalR      ┌──────────────────────────┐
│  Arius.Web (Angular,     │ ◀────streaming────────▶│  Arius.Api (.NET minimal  │
│  Metronic v9 Tailwind)   │     REST (JSON)         │  API over MediatR)        │
└─────────────────────────┘                         │   ├─ relays Arius.Core    │
                                                     │   │  command/query handlers│
                                                     │   └─ app SQLite (accounts, │
                                                     │      repos, jobs, schedules)│
                                                     └──────────────────────────┘
                                                          │            │
                                                   Arius.Core      Azure Blob
                                                  (chunk index,     Storage
                                                   filetree;        (containers)
                                                   own SQLite)
```

Runs in a **Docker container on Synology**; app SQLite + the local overlay folders live on **mounted volumes**.

### API contract (Arius.Web ↔ Arius.Api ↔ Arius.Core)
The prototype maps 1:1 to existing `Arius.Core` features. Suggested endpoints:

| UI surface | Endpoint (suggested) | Arius.Core handler | Notes |
|---|---|---|---|
| File browser, time-travel | `GET /repos/{id}/entries?version=&prefix=&filter=&local=true` **(stream)** | `ListQuery : IStreamQuery<RepositoryEntry>` | `version`→`ListQueryOptions.Version` (null=latest); `prefix`→`Prefix`; `filter`→`Filter`; `local`→`LocalPath` merge. Emits `RepositoryFileEntry` / `RepositoryDirectoryEntry` with `RepositoryEntryState` flags. |
| Archive | `POST /repos/{id}/archive` **(stream)** | `ArchiveCommand` + notification events | Body = `ArchiveCommandOptions` (see Archive screen). Stream `FileScanned/Hashing/Hashed`, `ChunkUploading/Uploaded`, `TarBundle*`, `SnapshotCreated` → live log. Result = `ArchiveResult`. |
| Restore | `POST /repos/{id}/restore` **(stream + callback)** | `RestoreCommand` + events | `RestoreOptions{Version, TargetPath, Overwrite, NoPointers}`. The **cost approval** uses `ConfirmRehydration(RestoreCostEstimate) → RehydratePriority?` — surfaced as the cost modal (see Restore). Stream `RehydrationStatus`, `ChunkDownloadStarted/Completed`, `FileRestored`. Result = `RestoreResult`. |
| Add existing — discover containers | `GET /accounts/{accountId}/containers` **(stream)** | `ContainerNamesQuery(AccountName, AccountKey)` | Returns names only. |
| Snapshot list (picker + scrubber) | `GET /repos/{id}/snapshots` | **NEW `SnapshotsQuery`** | Small new feature: enumerate snapshot filetree blobs + timestamps via `IBlobContainerService.ListAsync`. Returns `{version, timestamp, fileCount}`. |
| Statistics | `GET /repos/{id}/stats` | **NEW aggregation** | Files, original size, stored size, unique chunks — aggregate `ChunkIndexService` + `FileTreeService`. Values are "pending" until the local cache finishes downloading. |
| Accounts / repos / jobs / schedules | REST CRUD | **Arius.Api app DB** | Not in Arius.Core — see "App state". |

**Only two real Core additions**: `SnapshotsQuery` and the stats aggregation. Everything else exposes existing handlers.

### Streaming model
`Arius.Core` handlers are already `IStreamQuery` / emit `INotification`. Wrap long-running commands in **SSE** for one-way logs (archive, list). **Restore needs a mid-stream request→response** (cost approval), so use **SignalR** (or WebSocket) for restore, or SSE + a `POST /restore/{jobId}/approve {priority}` side-channel. Each emitted event maps to a console log line and/or a progress update.

### App state (Arius.Api owns a small SQLite on the mounted volume)
Separate from Arius.Core's chunk-index DB. Tables:
- **storage_accounts** `(id, name, account_key [encrypted], created_at)`
- **repositories** `(id, alias, container, account_id FK, local_path, default_tier, created_at)`
- **jobs** `(id, repo_id, kind[archive|restore], trigger[one-off|schedule], status, pct, detail, started_at, finished_at)`
- **schedules** `(id, repo_id, cron, kind, enabled, next_run)`

A **scheduler** (Quartz.NET or hosted `BackgroundService`) fires cron archive jobs. The Overview cross-repo totals and the Jobs screen read from this DB.

---

## Global shell (demo8)
Every screen lives in this chrome:

- **Page background** `#f4f4f5` (muted). Root is `display:flex; height:100vh; overflow:hidden`.
- **Icon rail** — left, `86px` wide, transparent on the muted bg. Logo (38×38, radius 10) at top. Nav items are `64×62` rounded-13 tiles, icon (21px) over a `10.5px` label: **Overview, Repos, Jobs, Settings**. Active item = white fill, `1px #ececef` border, accent text/icon, subtle shadow; inactive = transparent, `#71717a`. Bottom: notification bell + a `40px` avatar circle (gradient `135deg,#0091e1,#5bd6fd`) with a green presence dot.
- **Content card** — fills the rest, `background:#fff`, `1px #e4e4e7` border, `border-radius:16px`, `margin:14px 14px 14px 0`, soft shadow, `overflow:hidden`, internal scroll.
  - **Top bar** inside the card: `64px` tall, `1px #f0f0f2` bottom border. Left = breadcrumb (`{parent} › {current}`, parent `#a1a1aa`, current `#27272a` 600). Right = **global search box** (`300px`, `#f4f4f5`, radius 9, magnifier + "Search files across repositories…" + a `⌘K` chip) — **hidden on the Overview screen**; opens the global search overlay.
  - **Main** = `flex:1; overflow-y:auto; padding:24px 26px 36px`.

Icons throughout are **Keenicons** (`ki-outline` / `ki-filled` / `ki-solid` classes); Metronic Angular ships these.

---

## Screens / Views

### 1. Overview (`screen='overview'`)
- **Header row**: `<h1>` "Overview" (22px/700/-.02em/#18181b) + sub "3 repositories · 2.82 TB archived · last sync 2h ago" (13.5px #71717a). Right: **Refresh** (outline btn), **Add existing** (outline btn, `ki-data`), **New repository** (primary btn `#3b82f6`, `ki-plus`). Buttons 40px tall, radius 9.
- **KPI grid**: 4 columns, gap 18. Each card `#fff`, `1px #ececef`, radius 13, padding 19/20. Top row = a 42×42 radius-11 tinted icon chip + a small trend pill; below = value (25px/700) + label (13px #71717a). The four: **Total archived 2.82 TB** (+86 GB), **Repositories 3** (all healthy), **Deduplicated 38%** (1.5 TB saved), **Est. monthly storage €4.12**.
- **Two-column row** (`1.75fr 1fr`, gap 18):
  - **Repositories table** card. Header "Repositories" + "Blob containers under management" + a filter icon button. Column header row (`grid 2.4fr .9fr .7fr`, uppercase 11px #a1a1aa): **Repository · Size · Snapshots**. Each row: 38×38 tinted folder chip + alias (14/600) over mono container name (#a1a1aa); size (14/600) over "{n} files"; snapshot count. Whole row hover `#fafafb`, click → repo detail. *(No status or tier-distribution columns — removed intentionally.)*
  - **Jobs summary** card. Header "Jobs" + "View all" link → Jobs screen. Three stat chips (Running `#eff6ff/#1d4ed8`, Scheduled `#f5f3ff/#6d28d9`, Done `#f0fdf4/#15803d`). Below: list of active+scheduled jobs (icon chip, "Archive · Media Library", when-text, status pill).

### 2. Repository detail (`screen='repo'`)
Header block: 48×48 tinted folder chip + alias `<h1>` (21px) + a status pill. Meta row = mono **container chip** (`ki-data`) + mono **local-folder chip** (`ki-folder`) + free text ("Cold tier · West Europe · last archive 2h ago"). Right: **Restore** (outline, `ki-cloud-download`) + **Archive** (primary, `ki-cloud-add`). Below: **tab bar** (underline style, 2px accent): **Files · Statistics · Properties**.

#### 2a. Files tab
- **Snapshot / time-travel bar** (card, padding 13/18): a **snapshot picker** button (`ki-time`, "Snapshot **v28** 12 Mar 2024", a green `LATEST` pill, chevron) opening a dropdown of snapshots (version, date·time, file count, LATEST marker); a **horizontal scrubber** (track `#eef0f3`, an accent range fill from start to the active dot, one dot per snapshot — active dot accent 15px, past dots `#bcd3f5`, future `#d8dce2`; click a dot to time-travel); and a right-side indicator — "Live working state" (latest) or an amber "Historical view" pill. Selecting a snapshot re-runs `ListQuery` with that `version` and resets to root.
- **Collected action bar** (only when ≥1 file collected): accent-tinted bar — `ki-check-square`, "**N** files collected · **size**", a **Clear** button, a **Restore collected** primary button.
- **Explorer card** (`height: calc(100vh - 360px)`, min 420):
  - **Toolbar**: back button, up button, **path chip** (`flex:1`, `#f7f7f8`, mono, shows `/{container}/{folder}`), and a **filter input** (right, 240px, magnifier, "Filter files in this folder…" → `ListQuery.Filter`).
  - **Body** = `flex` row: **folder tree** (left, 288px, `1px #f4f4f5` right border) — a root row ("{alias}", `ki-data`) then `treeRows` with indentation (`8 + depth*18`px), a rotating caret for expandable nodes, yellow folder icon, name, child count. **Detail pane** (right, `flex:1`) — *files only* (directories live in the tree).
    - Detail header grid: `34px 2fr 1.2fr .7fr .7fr .9fr .55fr` → **[checkbox] · Name · State · Size · Tier · Modified · Snapshot**.
    - Each row: a **collect checkbox** (18×18 radius-5; checked = accent fill + white `ki-check`; collected rows get `#f7f9ff` bg), file-type icon + name, the **state ring** (see below) + a state label, size, tier (colored), modified date (#a1a1aa), snapshot pill (mono, `#f4f4f5`). Clicking a row toggles collect. Row height = `--row-h` (46 comfortable / 38 compact).
  - **Footer status bar**: left = "**N files · {total size} shown**" (sum of currently-shown file sizes); right = **State legend** button (with a mini ring) opening an upward popover that explains the ring anatomy + color key.

#### 2b. Statistics tab
4 KPI cards — **Files**, **Original size**, **Stored size**, **Unique chunks** (icon chip + label, 24px value, sub-text). Below: an info banner — "Counts are derived from the file-tree and chunk index. Figures finalise once the local cache has fully downloaded — until then some values may read as *pending*." *(Cost-by-tier, dedup gauge, composition, and activity timeline were intentionally cut for v1.)*

#### 2c. Properties tab
Form (max-width 680) in a card: **Friendly alias** (text), **Storage account** + **Container** (read-only mono fields), **Account key** (password, mono; note "Stored encrypted in the Arius.Api SQLite. Replace to rotate the key."), **Local folder** (text mono; note "Folder the Files view overlays against the archive, and the default source for archive runs."). Footer: **Discard** (outline) + **Save changes** (primary, `ki-check`). → `PATCH /repos/{id}`.

### 3. Jobs (`screen='console'`)
Header "Jobs" + sub + two pills (**N running**, **N scheduled**). **Jobs table** (`grid 2.2fr 1.5fr 1fr 1.5fr`): **Job** (kind icon chip + repo name + kind label), **Trigger** ("One-off" with `ki-flash`, or schedule label "Daily · 02:00" with `ki-calendar` + the **cron** expression in mono below), **Status** pill (Running = blue spinner `ki-loading`; Rehydrating = amber pulsing `ki-time`; Scheduled = violet `ki-calendar-tick`; Queued = gray; Completed = green `ki-check-circle`), **Progress** (bar + when-text, e.g. "started 2h ago" / "ETA several hours" / "next run tonight 02:00"; completed rows dimmed to .66). Below: a **dark live-output console** (`#0b0b0f`, mono, 300px, auto-scroll) showing the streaming unified job feed with timestamp + source + colored line.

### 4. Add existing repository (`screen='add'`) — 2-step wizard
Stepper: "Step **1** of 2 · Storage account" + a 2-segment progress bar (second segment accent only on step 2).
- **Step 1 — Storage account**: a segmented toggle **Use configured** / **Add new account**. Configured = radio list of accounts (mono name + "{n} repositories", selected = accent border + filled radio). New = **Account name** + **Account key** inputs. Info banner. Footer: **Cancel** (→ Overview) + **Connect & discover** (`ki-arrow-right`) → step 2.
- **Step 2 — Repository**: **Select container** radio list (from `ContainerNamesQuery`; each shows mono container name + "{n} snapshots" — *snapshot count only; size/tier intentionally omitted as not cheap to fetch*), **Friendly alias**, **Passphrase**, **Local path** (mono; note about the overlay), info banner ("Arius reads the existing manifest and snapshots — no files are uploaded"). Footer: **Back** + **Add repository**.

### 5. New repository (`screen='create'`) — 2-step wizard
Step 1 identical (storage account select/new). **Step 2 — New container**: **Friendly alias** + auto-generated **Container name** (read-only mono), **Local path**, **Default tier** (4 segmented options Hot/Cool/Cold/Archive), **Passphrase** + **Confirm passphrase**, amber warning ("the passphrase encrypts every chunk and **cannot be recovered** if lost"). Footer: **Back** + **Create repository**.

### 6. Archive drawer (slide-over from right, 494px)
Header = `ki-cloud-add` chip + "Archive · {repo}". Body (idle state) mirrors the **CLI `archive` defaults exactly**:
- **Source folder** (read-only mono path).
- **Upload tier · default Archive** — 4 segmented options (Hot/Cool/Cold/Archive), **Archive preselected**.
- Toggles: **Remove local binaries** (`--remove-local`, default **off**) and **Skip pointer files** (`--no-pointers`, default **off**). **Mutually exclusive** — enabling one disables the other.
- Info note. Footer: **Close** + **Start archive**.
- On start → progress view: a percent bar, a 4-stat grid (Files, Uploaded, Deduped, Throughput), and the dark streaming log. (Fixed: SmallFileThreshold 1 MB, TarTargetSize 64 MB — not user-exposed.)

### 7. Restore drawer + cost approval
Header = `ki-cloud-download` chip + "Restore · {repo}". Idle body: **Restore target** summary card ("Whole repository · {size}" or "**N** collected files · {size}"), restore options (Overwrite existing files; Skip pointer files), info note. Footer: **Close** + **Start restore**.
- **Start restore → `analyzing`**: log streams classification lines; primary button shows "Estimating cost…" spinner.
- **→ `cost` (the approval modal)**: an amber "Rehydration required" banner, a 3-stat grid (Ready now / Rehydrate / From-archive GB), and a **Rehydration priority** picker — two cards **Standard (~15 h, €0.02)** and **High (~1 h, €0.21)**. Footer switches to **Decline** + **Approve & restore**. This is `RestoreOptions.ConfirmRehydration` returning the chosen `RehydratePriority`.
- **Approve → `running` → `done`**: rehydration + download log streams; stats show ETA per chosen priority. ETAs are **hardcoded** ("Standard ~15 h", "High ~1 h").

### 8. Global file search overlay
Triggered by the top-bar search (or ⌘K). Centered panel (640px, top 72px) over a scrim: a big search input ("Search files across all repositories…", live filter), a scrollable result list — each result = **state ring** + file-type icon + name + "**{repo}** · {path}" + size + state label, click → opens that repo. Footer: "Searches file names and paths across every repository · backed by ListQuery filter".

---

## The State Ring (critical — port faithfully)
Mirrors `Arius.Explorer`'s `StateCircle.xaml` + `FileItemViewModel`. One disc per file, split **left = local disk / right = repository**, each half an **outer ring + inner disc**:

| Segment | Meaning | Filled when |
|---|---|---|
| **Left outer** | pointer file on disk | `RepositoryEntryState.LocalPointer` |
| **Left inner** | binary file on disk | `RepositoryEntryState.LocalBinary` |
| **Right outer** | filetree entry exists in repo | `RepositoryEntryState.Repository` |
| **Right inner** | chunk availability | `RepositoryHydrated` → **hydrated**; `RepositoryArchived`/`RepositoryRehydrating` → **not hydrated** |

**Colors**: present (pointer/filetree) = `#27272a`; binary-on-disk **and** chunk-hydrated = `#2563eb` (blue); chunk **not** hydrated (archive tier) = `#9cc4f5` (light blue); **empty/absent** = `#e4e7ec`. *(We deliberately collapsed chunk state to just hydrated/not-hydrated — do not reintroduce a separate "rehydration pending" color.)*

**SVG geometry** (viewBox `0 0 24 24`, center 12,12, outer r=11, inner r=7.4), rendered ~19px in rows, 15px in the legend button, 76px in the legend diagram:
```html
<svg viewBox="0 0 24 24">
  <path d="M12,12 L1,12 A11,11 0 0,1 12,1 Z M12,12 L1,12 A11,11 0 0,0 12,23 Z"  fill="{leftOuter}"/>
  <path d="M12,12 L23,12 A11,11 0 0,0 12,1 Z M12,12 L23,12 A11,11 0 0,1 12,23 Z" fill="{rightOuter}"/>
  <path d="M12,12 L4.6,12 A7.4,7.4 0 0,1 12,4.6 Z M12,12 L4.6,12 A7.4,7.4 0 0,0 12,19.4 Z"   fill="{leftInner}"/>
  <path d="M12,12 L19.4,12 A7.4,7.4 0 0,0 12,4.6 Z M12,12 L19.4,12 A7.4,7.4 0 0,1 12,19.4 Z"  fill="{rightInner}"/>
  <line x1="12" y1="0.6" x2="12" y2="23.4" stroke="#fff" stroke-width="1.6"/>  <!-- left/right gap -->
  <circle cx="12" cy="12" r="7.4" fill="none" stroke="#fff" stroke-width="1.4"/> <!-- inner/outer gap -->
</svg>
```
Build it as a small standalone Angular component taking the four colors (or the `RepositoryEntryState` flags directly). Provide a tooltip with the human state label.

---

## Interactions & behavior
- **Navigation**: icon-rail items switch top-level screens; Repos opens the active repository; clicking a repo row in Overview opens it. Breadcrumb reflects location.
- **Time-travel**: clicking a scrubber dot or a picker row sets the viewed snapshot and re-lists; non-latest shows the "Historical view" pill.
- **Collect**: clicking a file row (or its checkbox) toggles membership in the collected set; the collected bar + the Restore flow read from it; Restore with an empty set targets the whole repository.
- **Archive/Restore drawers**: slide in from the right (`translateX(100%)→0`, `.26s cubic-bezier(.4,0,.2,1)`); scrim fade `.2s`. State machine — Archive: `idle → running → done`; Restore: `idle → analyzing → cost → running → done`.
- **Streaming logs**: append-only, auto-scroll to bottom; line colors encode severity (green ok, amber warning, violet dedup, gray meta, blue info).
- **Global search**: opens centered modal; typing filters live; Esc/scrim closes.
- **Filter input** (Files): live-filters the shown files by name substring.
- Hover: rows `#fafafb`; tree rows `#f7f9ff`.

## State management
Prototype state (see the `Component` class) → Angular services + component state:
- **Routing/nav**: `screen`, `activeRepoId`, `repoTab` → Angular Router.
- **Explorer**: `expanded` (tree), `selectedFolder`, `fileFilter`, `viewSnap` (snapshot), `collected` (map of file key → size).
- **Drawer**: `drawerType` (archive|restore), `streamState` (idle|analyzing|cost|running|done), `streamLines[]`, `progress`, `restorePriority`, `archiveTier`, `archiveRemoveLocal`, `archiveNoPointers`.
- **Wizards**: `addStep`/`createStep`, `addAccountMode`/`createAccountMode`, `selectedAccount`.
- **Global**: `searchOpen`, `searchQuery`, jobs feed, console feed.
- **Data fetching**: `ListQuery` (stream) for entries; REST for repos/accounts/snapshots/stats/jobs; SSE/SignalR for archive/restore/job streams.

## Design tokens (Metronic v9 — `config.ktui.css`)
- **Primary / accent**: `--primary: blue-500` = **`#3b82f6`** (`--accent-soft` ≈ 10% tint `#eff4ff`/`#eff6ff`, `--accent-softer` ≈ 5% `#f7f9ff`).
- **Foreground**: zinc-950 `#09090b` / titles `#18181b` / body `#27272a`–`#3f3f46`. **Muted text**: zinc-500 `#71717a`, lighter `#a1a1aa`.
- **Surfaces**: page muted `#f4f4f5`; card `#fff`; subtle fills `#fafafa`/`#f7f7f8`; **borders** `#ececef` (cards) / `#e4e4e7` (inputs) / `#f4f4f5`,`#f0f0f2` (dividers).
- **State colors**: green `#16a34a`/`#15803d` on `#f0fdf4`; amber `#b45309`/`#d97706` on `#fffbeb`; violet `#6d28d9`/`#8b5cf6` on `#f5f3ff`; sky `#0ea5e9` on `#f0f9ff`; red `#dc2626`. Tier colors: Hot `#d97706`, Cool `#0ea5e9`, Cold `#3b82f6`, Archive `#8b5cf6`.
- **Radius**: base `--radius: 0.5rem` (8px); buttons/inputs 8–9px; cards 13px; the floating content card 16px; pills 999px.
- **Type**: **Inter** (400/500/600/700). Scale used: h1 22 / section 15.5 / body 13.5–14 / labels 12.5 / meta 11–12; KPI numbers 24–25. Headings `letter-spacing:-.02em`.
- **Spacing**: 18px grid gaps; 24–26px main padding; control heights 32 (compact) / 38–40 / 44 (form). Shadows are soft: cards `0 1px 2px rgba(0,0,0,.04)`; overlays `0 12–24px 32–60px rgba(9,9,11,.14–.24)`.
- Mono font for paths/containers/cron/keys: `ui-monospace, SFMono-Regular, Menlo, monospace`.

## Assets
- **Logo**: `assets/arius-iceberg.svg` (the Arius iceberg).
- **Icons**: **Keenicons** (bundled here under `assets/keenicons/`, but use the copy shipped with your Metronic Angular package). Classes used: `ki-outline`/`ki-filled`/`ki-solid` + name (e.g. `ki-folder`, `ki-cloud-add`, `ki-cloud-download`, `ki-data`, `ki-time`, `ki-element-11`, `ki-chart-simple`, `ki-setting-2`, `ki-magnifier`, `ki-check`, `ki-calendar-tick`, `ki-loading`).
- **Font**: Inter (Google Fonts in the prototype; self-host in production).
- Sample data in the prototype is an audiobook library — replace with live data.

## Files
- `Arius.Web.dc.html` — the full prototype (all screens + the `Component` logic class with state shapes, handlers, sample data, and the exact `RepositoryEntryState`→ring mapping). **Primary reference.**
- `Shells.dc.html` — the three Metronic shells evaluated (demo1/5/8); **demo8 chosen**.
- `assets/` — logo + Keenicons.
- `support.js` — prototype runtime only; **not** needed in production.

## Build sequence (suggested)
1. `Arius.Core`: add `SnapshotsQuery` + the stats aggregation query.
2. `Arius.Api`: read-only first — repos, entries (stream), snapshots, stats → wire the **file browser + time-travel** end-to-end in Angular.
3. Add streaming **archive** + **restore** with the **cost-approval handshake**.
4. **Accounts/repos CRUD** + the **cron scheduler** + Jobs feed.
5. **Dockerize** (API serves built Angular or two containers behind a reverse proxy) + `docker-compose` on Synology with mounted volumes for the app SQLite and local overlay folders.
