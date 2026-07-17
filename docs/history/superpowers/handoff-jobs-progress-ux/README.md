# Handoff: Arius archive/restore & Jobs progress UX

## Overview
Redesign of how Arius.Web presents running archive/restore operations, replacing the current terminal-style live console, the misleading count-based progress bar (which jumps to ~95% and hangs for the duration of the upload), and the unintuitive raw counters. The design converged over four iterations with the product owner; **Turn 3 (3a/3b) and Turn 4 (4a) are the agreed direction** — earlier turns (1a–2b) are retained in the mockup file as exploration history only.

Target repo: `woutervanranst/Arius7` (master). Affected app: `src/Arius.Web` (Angular 19+, standalone components, signals, Metronic/Tailwind) and `src/Arius.Api`. **Constraint: Api/Web-only change — do not modify Arius.Core** (its events and log output are already sufficient; see "Data sources").

## About the Design Files
The files in this bundle are **design references created in HTML** (Design Component mockups) — they show intended look and behavior, not production code. The task is to **recreate these designs inside Arius.Web's existing Angular environment** using its established patterns: standalone components with inline templates, signals + computed, `ChangeDetectionStrategy.OnPush`, Tailwind utility classes layered over Metronic, KeenIcons (`ki-filled` / `ki-outline`), and the existing SignalR `RealtimeService`.

## Fidelity
**High-fidelity.** Colors, spacing, typography and copy are final intent, expressed with the app's existing token vocabulary (zinc grays, `#3b82f6` blue, `#7c3aed`/`#6d28d9` purple, `#b45309` amber, Inter). Match visually; express values through the codebase's existing Tailwind classes rather than hard-coded styles where an equivalent exists.

## What to build (scope)

1. **Kill the live console** everywhere (`live-console.component.ts` and its usages in the drawer and Jobs page).
2. **Drawer → pill flow**: clicking *Start archive/restore* dismisses the drawer; a floating dark pill appears bottom-right **while the user is browsing that repository** and reconnects on revisit. No live per-file updates in the file browser itself.
3. **Job detail page** (new route, e.g. `/jobs/:id`) — same component drives archive and restore. Opened via the pill's "Open ›" or from the Jobs overview.
4. **Jobs overview redesign** — no global console; sectioned list with per-row mini progress bars and **Reattach ›**.
5. **Api additions**: progress aggregation (byte-weighted), ETA computation, warning tail endpoint, adaptive rehydration polling.

## Screens / Views

### 1. Floating progress pill (mockups `1c` and `2a` in Progress UX Options.dc.html)
- Position: fixed bottom-right of the content area, above everything (`right:18px; bottom:16px`).
- Dark pill: `background:#18181b`, white text, `border-radius:999px`, padding `9px 16px 9px 10px`, shadow `0 12px 32px rgba(9,9,11,.28)`.
- Left: circular SVG progress ring 30px (`stroke:#60a5fa` on `#3f3f46` track, stroke-width 4).
- Line 1 (12.5px/600): `Archiving photos · 62%`. Line 2 (11px, `#a1a1aa`): `~2 min left · 41 MB/s · 912 files to go`.
- Trailing link (12px/600, `#60a5fa`): `View job ›` → job detail page.
- Behavior: appears when a job is running **for the repo currently being viewed**; reconnects to the SignalR stream on page revisit; clicking the pill body may open a small glance popover (see `2a` popover mock: 360px card with % + bar + phase sentence + "Open job ›" / "Hide pill").

### 2. Archive job detail page (mockup `3a`)
Layout top-to-bottom inside the standard content card (breadcrumb `Arius › Jobs › Archive · <repo>`):

1. **Header row**: 44px rounded icon tile (`#eff6ff`/`#3b82f6`, `ki-cloud-add`), title 20px/700, status chip (pill, `#eff6ff`/`#1d4ed8`, pulsing 7px dot), meta line 12.5px `#a1a1aa` (`Started 15:47 · elapsed 11 min · Cold tier · <account>/<container>`). Right-aligned: **ETA 24px/700** (`~12 min left`) + throughput 12.5px (`2.4 MB/s sustained`).
2. **Layered progress bar** — the core idea. ONE track (height 14px, `#eef0f3`, fully rounded) with three absolutely-positioned overlapping fills, each a *subset* of the previous, all measured in **bytes of the same dataset**:
   - Scanned: `#dbeafe` (lightest)
   - Hashed & routed: `#93c5fd`
   - Uploaded: `#2563eb` (darkest)
   - There is **no snapshot segment** — snapshot/finalize is not byte-progress (and only ~30 s); it is represented by the pulsing bullet in the stage list.
   - Tar bundling is **not** a layer either: tar-build/tar-upload bytes count toward *Uploaded* as each tar seals and uploads.
   - Legend below the bar (12.5px, swatch squares 10px radius 3px): `Scanned · 3,122 files (3.16 GB) — done in 96 s` / `Hashed & routed · 100%` / `Uploaded · 1.68 of 3.11 GB`.
3. **KPI tiles** — 4-up grid, `#fafafb` bg, `#f0f0f2` border, radius 11px, padding 13px 15px. Label 11px uppercase `#a1a1aa`, value 19px/700, sub 11.5px `#a1a1aa`. Archive tiles: Uploaded (`1.68 GB` / `of 3.11 GB new data`), Deduplicated (`81 files` / `48 MB not re-uploaded`, value in purple `#6d28d9`), Throughput (`2.4 MB/s` / `16 parallel workers`), Est. finish (`16:09` / `upload + ~30 s snapshot`).
4. **Stage summary list** — bordered card (`#ececef`, radius 12px), one row per stage: 9px status dot (done `#22c55e`, running `#3b82f6` with 1.4s pulse animation, pending `#e4e4e7`), stage name 13px/600 (fixed 130px column), one summary sentence 12.5px `#71717a`, right-aligned timing 12px `#a1a1aa`. Archive stages: Scan, Hash & route, Upload (includes tar bundles), Snapshot (`chunk index + filetree + snapshot — took 32 s in the last run`).
5. **Footer row**: `Cancel job` (13px/600 `#dc2626`) · `There are N warnings` (13px/600 `#b45309`, info icon) — clicking expands the warnings panel:
6. **Warnings panel** — amber-bordered card: header strip `#fffbeb` with "Warnings — raw log messages" + Copy button; body `#fffdf5`, monospace 11.5px `#78350f`, `white-space:pre`, horizontal scroll. Shows the Serilog `[WRN]` lines **verbatim** (they are not events and not UI-shaped — display raw).
7. Optional footer note ("On completion: snapshot …") exists behind a `showFooterNotes` flag — default hidden.

### 3. Restore job detail page (mockup `3b`)
Same skeleton as 3a with purple palette and restore-specific pieces:

1. Status chip variant: `Waiting — rehydration` (amber `#fffbeb`/`#b45309` + `#fde68a` border, `ki-time` icon). Right side: `resumes ~03:40` (24px/700 `#b45309`) + `then ~20 min download at 2.4 MB/s`.
2. **Layered bar (purple)**: Planned `#ede9fe` (100%) → Hydrated & ready `#c4b5fd` → Restored to disk `#7c3aed`. Legend: `Planned · 427 chunks resolved (2.76 GB)` / `Hydrated & ready · 145 chunks` / `Restored to disk · 811 files (0.72 GB)`.
3. **Rehydration wait card** (amber, `ki-moon` icon): title `282 chunks are being moved out of Archive tier by Azure (~13 h)`; sub `Status is checked periodically — often at first for High priority, backed off for Standard. You can close this page.`; right side a **toggle**: `Automatically restore as chunks become available` (on = downloads resume automatically; off = job parks and waits for user action).
4. KPI tiles: Restored (`811 / 3,122` files · GB on disk), Ready to download (chunks hydrated), Rehydrating (count + size + priority, amber value), Cost approved (`€4.31` · priority + time).
5. Stage summary: Plan (snapshot resolved · files → chunks · size), Confirm cost, Rehydrate (available/pending · checked periodically), Download (partial — resumes as chunks arrive), Verify (hash check + pointer cleanup).
6. Footer: Cancel job · `There is 1 warning` · optional note `Rehydration already paid — cancelling does not refund it` (behind `showFooterNotes`).

### 4. Cost confirmation dialog (mockup `3b`, lower card)
Blocks the restore at the `confirm-cost` phase. 480px modal:
- Header: amber `ki-dollar` tile + "Confirm restore cost" 15.5px/700.
- Body copy: `282 of 427 chunks (2.1 GB) are in Archive tier and need rehydration before download.`
- Line-item table (bordered, radius 11px): download cost, early-deletion fee, **Total excl. rehydration** row bold on `#fafafb`.
- Two selectable priority cards side-by-side: `Standard · €0.71 / up to 15 h wait` (neutral border) vs `High priority · €4.31 / under 1 h` (selected: 2px `#7c3aed` border, `#f5f3ff` bg).
- Footer on `#fafafb`: `Cancel restore` (ghost) + `Rehydrate & restore` (solid `#7c3aed`).

### 5. Jobs overview page (mockup `4a`)
Replaces the current Jobs page. **The global "Live output" console is removed.** Sections:

1. **Heading** with count chips: `1 running` (blue), `2 waiting` (amber outline), `1 scheduled` (purple).
2. **"Needs your attention"** section — amber banner rows for blocked jobs (e.g. waiting for cost confirmation) with a solid amber CTA (`Review cost ›`).
3. **"Active"** section — bordered list, grid `260px 1fr 190px 110px`: job identity (icon tile + name + trigger/start meta), **layered mini-bar** (8px, same color logic as detail pages) + one phase sentence (`Uploading — 1.68 of 3.11 GB · 2.4 MB/s`), status chip + ETA line, and **`Reattach ›`** button — navigates to the job detail page and re-subscribes to the SignalR group, exactly as if the user never left.
4. **"Scheduled & history"** section — same grid; running bar column replaced by a one-line outcome summary (`3,412 files · 1.2 GB uploaded · 5.7 GB deduped · snapshot v12 · 14 min`); actions `Edit ›` / `Report ›`; history rows at 75% opacity.

## Interactions & Behavior
- Start archive/restore → drawer dismisses → pill appears. Pill is repo-scoped and reconnects on revisit.
- Pill "Open ›" / Jobs "Reattach ›" → job detail route; the page subscribes to the job's SignalR stream and renders current aggregate state immediately (state must be replayable from the Api, not only incremental — send a full snapshot on subscribe, then deltas).
- Running-state pulse: `@keyframes pulse { 50% { opacity:.45 } }`, 1.4s infinite, applied to status-chip dots and running stage bullets. This is the only animation; **no per-file row flashing anywhere** (explicit product decision — per-file detail is too much).
- Bar fills use `transition: width .3s`.
- Warnings link toggles the verbatim log panel.
- Cost dialog blocks the job at `confirm-cost`; job shows in Jobs "Needs your attention" until answered.
- Rehydration toggle: on = auto-resume downloads as chunks hydrate; off = job stays parked awaiting user action.

## State Management & Data sources (Api work)
All derivable from what Arius.Core already emits — **no Core changes**:

- **Progress events** (`src/Arius.Core/Features/ArchiveCommand/Events.cs`, `RestoreCommand/Events.cs`): FileScanned/ScanComplete, FileHashed, FileRouted (dedup), TarEntryAdded/TarBundleSealing, ChunkUploading/ChunkUploaded, SnapshotCreated; restore: SnapshotResolved, chunk resolution, RehydrationStatus (available/rehydrated/pending + sizes), ChunkDownloadCompleted, FileRestored. Events carry byte sizes — aggregate server-side into: `scannedBytes/totalBytes`, `hashedBytes`, `uploadedBytes`, per-stage counters, dedup savings.
- **ETA (Api-computed — Core does not provide it)**: rate = uploaded bytes ÷ elapsed over a **sliding window (~60 s)**; ETA = remaining bytes ÷ rate. Total bytes are only known once hash/dedup-route completes (~90 s on the reference run) — show "estimating…" until then. Rehydration ETA is a heuristic (Standard ≤15 h / High ≤1 h minus elapsed).
- **Warnings**: not events — they are Serilog `[WRN]` lines. Api exposes a filtered tail of the job's log sink (`GET /jobs/{id}/warnings` → verbatim strings + count).
- **Rehydration polling**: hosted `BackgroundService` in the Api (Hangfire is overkill for this alone). Adaptive schedule: High priority → poll every 15 min from start; Standard → first poll after ~10 h, then hourly; optionally tighten to 15 min once the first chunk flips available. Timers re-hydrate from the job store on Api restart.
- **SignalR**: reuse `RealtimeService` patterns; per-job groups; on subscribe send full aggregate snapshot, then deltas (enables reattach).

Reference timings (from a real 3,122-file / 3.16 GB run, log `uploads/arius-20260628-archive - reperesentative log.txt` in the repo project): scan 96 s, upload ~22 min at ~2.4 MB/s, finalize (chunk-index + filetree + snapshot) 32 s. Restore phases: resolve-snapshot → classify → chunk resolution → rehydration status → confirm-cost (blocks) → download.

## Design Tokens
- Grays: `#18181b` (headings/pill), `#27272a`, `#3f3f46`, `#52525b`, `#71717a`, `#a1a1aa`, borders `#e4e4e7`/`#ececef`/`#f0f0f2`/`#f6f6f7`, surfaces `#fafafb`/`#f7f7f8`/`#f4f4f5`, track `#eef0f3`.
- Archive blues: `#dbeafe` → `#93c5fd` → `#2563eb`; accents `#3b82f6`, `#1d4ed8`, chip bg `#eff6ff`.
- Restore purples: `#ede9fe` → `#c4b5fd` → `#7c3aed`; accents `#6d28d9`, chip bg `#f5f3ff`.
- Amber (waiting/warnings): `#b45309`, `#92400e`, `#a16207`, `#78350f`; bg `#fffbeb`/`#fffdf5`; border `#fde68a`.
- Green (done): `#22c55e`, `#15803d`, bg `#f0fdf4`. Red (destructive/failed): `#dc2626`, bg `#fef2f2`.
- Type: Inter; page title 20–22px/700 tracking -0.02em; ETA display 24px/700; KPI value 19px/700; body 13–13.5px; meta 12–12.5px; micro-labels 11–11.5px uppercase tracking .03–.04em; monospace for log lines and path chips (`ui-monospace` stack).
- Radii: cards 12–16px, tiles 9–12px, chips/pills 999px. Bars: detail 14px tall, overview mini 8px, all fully rounded.
- Shadows: cards `0 1px 2px rgba(0,0,0,.04)`; pill/popover `0 12px 32px rgba(9,9,11,.28)` / `0 20px 50px rgba(9,9,11,.22)`.

## Assets
- KeenIcons (already in the repo at `src/Arius.Web/public/assets/vendors/keenicons/`). Icons used: `ki-cloud-add`, `ki-cloud-download`, `ki-time`, `ki-moon`, `ki-dollar`, `ki-information-2`, `ki-check-circle`, `ki-cross-circle`, `ki-calendar-tick`, `ki-folder`, `ki-flash-circle` (note: `ki-flash` does not exist in the font).
- Arius iceberg logo (already in repo). No new assets required.

## Files
- `Progress UX Options.dc.html` — the design canvas. **Implement turns 4a, 3a, 3b** (top two sections). Turns 1a–2b are exploration history: the pill originates in `1c`, the glance popover in `2a`, the collapsible-panels idea in `2b`.
- `Current — Files + Archive Drawer.dc.html`, `Current — Jobs.dc.html` — recreations of today's UI, for before/after context only.
- `assets/keenicons/`, `assets/arius-iceberg.svg` — copied from the repo so the mockups render standalone.
