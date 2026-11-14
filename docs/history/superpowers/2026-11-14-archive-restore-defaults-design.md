# Archive & restore defaults: a four-level hierarchy

**Date:** 2026-11-14
**Status:** Approved (2026-11-14)
**Scope:** Arius.Web (Settings page, Properties tab, New container wizard, Archive/Restore drawers) and
Arius.Api (storage, resolution, scheduler). Core, CLI and Explorer are untouched.

## Why

The Web UI's **Settings** page is a placeholder. The archive and restore options are re-chosen by hand on
every run, and only the upload tier has a per-repository default, which can only be set once, in the New
container wizard. Scheduled archives hard-code their options (`removeLocal: false, writePointers: false,
fastHash: false` in `SchedulerService`), so a repository's nightly run can't use fast hash or pointers
at all. The disabled "Fast-hash scheduled runs (coming soon)" checkbox in Properties is a placeholder for exactly this gap.

The goal is to configure each option once, at the right level, and have every run start from it while
still allowing any run to override any option.

## Goals

- One hierarchy for **all** archive and restore options: **Factory → Global settings → Repository
  properties → Run**. The nearest level that sets a value wins.
- Each option can be set or left inherited at the Global and Repository levels, and changed for a single run.
- Scheduled archives use the repository's resolved values.
- Every pre-filled field says where its value came from.
- A consistent UI: the same sections, options, order, controls and wording everywhere. Every yes/no option is a switch.

## Non-goals

- Overrides per schedule. A schedule runs with the repository's resolved archive values.
- CLI changes. The CLI flags are already its per-run level, and its defaults stay as they are
  (`--fast-hash` stays opt-in, per ADR-0021).
- Explorer changes.
- An "apply to all repositories" action in Settings. Inheritance already covers that.

## Anti-goals (it fails even if it "works")

- Changing a global setting must **never** change a repository that set its own value for that option.
- Changing a factory or global default must not silently become destructive. "Replace with pointers"
  deletes local files, and wherever it can be chosen it must say so, and say which runs it reaches.
- Fast hash must not become the factory default. ADR-0021's "the default must remain a full read" stands.

## The options

| Group | Option | Values | Factory default | Wire mapping today |
|---|---|---|---|---|
| Archive | Upload tier | `hot` · `cool` · `cold` · `archive` | `archive` | `StartArchive(tier)` |
| Archive | On disk after archive | `keep` · `keep-pointers` · `replace` | `keep` | `keep` → `removeLocal=false, writePointers=false`; `keep-pointers` → `false, true`; `replace` → `true, true` |
| Archive | Fast hash | on / off | **off** | `fastHash` |
| Restore | Overwrite existing files | on / off | off | `overwrite` |
| Restore | Skip pointer files | on / off | off | `noPointers` |

## The model

### Levels

| # | Level | Where it's edited | Stored | Holds |
|---|---|---|---|---|
| 0 | Factory defaults | — (shipped with Arius) | code constants | a value for every option |
| 1 | Global settings | Settings page | app DB | only the options set here |
| 2 | Repository properties | Properties tab | app DB, per repository | only the options set here |
| 3 | Run | Archive / Restore drawer | not stored | a full set of values, pre-filled from level 2's resolved values |
| — | Scheduled run | — | — | level 2's resolved archive values, unchanged |

**Resolution:** for each option, the value comes from the nearest level that set it, checked in order
Repository → Global → Factory. This is **live inheritance**: saving a global setting immediately
changes every repository that inherits that option. It affects their next drawer pre-fill and their
next scheduled run. Repositories that set their own value are unaffected.

**Set vs. inherited (levels 1 and 2):**
- Any explicit pick sets the value at this level, **including re-picking the value currently
  inherited** (for a switch: toggling it off and on again). This is how you pin a repository against a
  later global change. For example, a repository can keep "Keep files only" even if Settings later
  switches to "Replace with pointers".
- Once set, a value stays set even if it equals the inherited value, so it won't follow later changes
  from above. Only **Reset** clears it, and the option goes back to inheriting.
- Global settings start empty, so everything inherits the factory defaults. A consequence is
  that changing a factory default in a later Arius release changes every instance that never set that
  option. Treat a factory-default change as a deliberate, release-noted decision.

**Run level (3):** the drawer always sends a complete, explicit set of values. A field is marked
"changed for this run" by comparing its value with the resolved repository value, not by tracking state.

### Migration

- Every existing repository has a `default_tier` today: the wizard's choice, or `archive` from
  `NormalizeTier`'s fallback for repositories added through the Add-existing wizard. *(Decided by
  Wouter.)* Migrate that value as **set at the repository level**, so existing repositories behave
  exactly as before. The migration never guesses intent, and users can Reset a repository to start
  inheriting. The alternative, starting `archive` tiers out as inherited, was rejected because it would
  make deliberately chosen Archive tiers follow Settings.
- No other option has a stored value today, so every repository starts out inheriting the rest.
- New repositories, whether created or added, set nothing and inherit everything.

## UI (Arius.Web)

### One field component, used everywhere

Every option is rendered by a single shared presentational component, in `src/app/shared/`. It
renders the same label, control, hint and warning wherever the option appears. Controls:

- **Upload tier** and **On disk after archive**: the existing `ar-seg` segmented buttons.
- **Fast hash**, **Overwrite existing files** and **Skip pointer files**: a **switch**, the one yes/no
  control across the app. It replaces every checkbox for these options, the restore drawer's included.

Hints (shown under the control, with text that depends on the current value):
- Tier: `hot` "Instant reads · highest storage cost · no minimum retention"; `cool` "Instant reads ·
  lower storage cost · 30-day minimum"; `cold` "Instant reads · lower still · 90-day minimum";
  `archive` "Cheapest storage · restores need rehydration (hours) · 180-day minimum".
- On disk: `keep` "Local files stay as they are."; `keep-pointers` "Also writes a .pointer.arius file
  next to each archived file."; `replace` shows a warning box instead (below).
- Fast hash, Overwrite and Skip pointer files: one line each describing the on or off behaviour.

**"Replace with pointers" warning:** "**Deletes local files.** Each file is removed once it is safely
archived, leaving only its pointer file." followed by a sentence that depends on the level:
- Settings: "Reaches N repositories that inherit it (alias, alias), scheduled runs included." If none
  do: "No repository inherits it right now."
- Properties: "Scheduled runs of this repository will do this unattended."
- Drawer: no extra sentence.

### Provenance pills

Each field's label row carries one pill:

| Situation | Pill | Plus |
|---|---|---|
| Value inherited from level 0 | Inherited from factory defaults | — |
| Value inherited from level 1 | Inherited from global settings | — |
| Value inherited from level 2 | Inherited from repository properties | — |
| Set at Settings | Set in global settings | **Reset** |
| Set at Properties | Set for this repository | **Reset** |
| Changed in the drawer | Changed for this run | **Reset** |

Inherited pills are neutral grey. "Set here" pills are amber. In the drawer, the pill names the level the
resolved value actually came from, which can be any of the three.

### Screens

- **Settings** (`features/settings`, replacing the placeholder): title "Settings", subtitle "Global
  defaults for every repository. A repository can set its own in its Properties, and any run can
  change them." Two section cards:
  - **Archive defaults** ("Used when archiving, scheduled runs included."): Upload tier, On disk after
    archive, Fast hash.
  - **Restore defaults** ("Used when restoring."): Overwrite existing files, Skip pointer files.
  - One **Discard / Save changes** bar under both cards, showing "Unsaved changes" when dirty.
- **Properties tab:** the existing card (alias, account, container, passphrase, local folder) and its Save
  stay as they are. Below it, the same two section cards as Settings, with descriptions "For this
  repository's archives, scheduled runs included." and "For restores from this repository.", and their
  own Discard / Save bar. In the **Scheduled archives** card, the description becomes "Cron
  schedules fire archive runs automatically (UTC) with this repository's archive defaults.", each schedule
  row shows a one-line summary of the resolved values (e.g. "Cool · keep files only · fast hash off"),
  and the disabled "Fast-hash scheduled runs (coming soon)" checkbox is removed.
- **New container wizard:** remove the **Default tier** field. The Add-existing wizard has none already.
- **Archive drawer:** Source folder, then the three archive fields, pre-filled with the repository's
  resolved values and showing their pills. `DrawerStore.openArchive` stops taking a tier argument.
- **Restore drawer:** the two restore fields, as switches, pre-filled and with pills the same way.
- **Repos list, Overview and repository header** keep showing a tier, which is now the **resolved** tier.

Keep the existing `data-testid` hooks (`tier-seg`, `seg-on-disk`, `toggle-fast-hash`, …) on the new
controls. The e2e specs depend on them.

## API (Arius.Api)

- **Storage.** Levels 1 and 2 must be able to say "not set" for every option.
  `repositories.default_tier` is currently `TEXT NOT NULL DEFAULT 'archive'`, and SQLite can't relax a
  NOT NULL constraint in place. So pick a storage shape that represents "unset", such as a small
  key/value table keyed by (scope = global | repository id, option), or nullable columns plus a global
  row. The migration must be idempotent, following `AppDatabase`'s existing `EnsureColumn` /
  `user_version` style, and must carry each existing `default_tier` over as a repository-level value.
  Deleting a repository removes its values.
- **Resolution lives in one place on the server.** It is used by the endpoints that feed Properties and
  the drawers, by `SchedulerService`, and by `RepositoryDto`'s tier. Whatever the endpoints return must
  let the UI show, for every option, the effective value, whether it is set at this level, and which
  level it came from.
- **Endpoints:** read and write global settings, and read and write a repository's defaults. Validate at
  the boundary: tier must be one of the four tiers, on-disk one of the three values, and an unknown
  option is rejected.
- **`CreateRepositoryRequest.DefaultTier`** goes away, since new repositories set nothing.
  **`UpdateRepositoryRequest.DefaultTier`** is superseded by the repository-defaults endpoint.
- **`SchedulerService`** fires `RunArchiveAsync` with the repository's resolved tier, on-disk value
  (mapped to `removeLocal` / `writePointers`) and fast hash.
- **`JobsHub.StartArchive` / `StartRestore`** stay unchanged, because the drawer sends explicit values.
- A change to global settings only changes resolution, so it needs no `registry.Evict`. A change to a
  repository's defaults doesn't touch its connection material either, so it needs no evict.

## Approaches considered

1. **Copy-down:** Settings values are copied into a repository when it is created, and each level is
   independent after that. It is the safest against surprise propagation. *Rejected by Wouter:* a global
   change should reach repositories that haven't set their own value.
2. **Global-only for on-disk and fast hash** (only the tier per repository). Simpler. *Rejected by
   Wouter:* every option should follow the same hierarchy as the tier.
3. **Live inheritance, nearest set level wins.** *Chosen.* It is a clean, uniform rule, and the pills
   make the source of every value visible.

The New container wizard's tier field was also considered. It was *dropped by Wouter*: it only existed
because Properties couldn't edit the tier, and keeping it would be a second, inconsistent place to set it.

## Decided in conversation (Wouter)

- Four levels: Factory → Global settings → Repository properties → Run, with any option overridable per run.
- Scheduled archives use the defaults (the repository's resolved values).
- Factory defaults: Archive tier, Keep files only, Fast hash **off**.
- Every option follows the hierarchy, and a global change never overrides a value set at a repository.
- The tier is edited in Properties and dropped from the New container wizard.
- Fast hash uses a switch everywhere.
- Pills name their source explicitly ("Inherited from global settings", "Inherited from repository properties", …).
- Approved together with the final interactive mockup ("perfect, go ahead"):
  - Restore options follow the same hierarchy, with factory defaults off / off (today's behaviour).
  - Settings and Properties both have two sections, Archive defaults and Restore defaults. The earlier
    "New repository" section was merged into Archive defaults.
  - Overwrite and Skip pointer files also become switches.
  - The Settings warning on "Replace with pointers" names the repositories it reaches.
  - The defaults sections in Properties get their own Save bar.
  - Set stays set until Reset, and global settings start empty.
- Any explicit pick sets the value, including re-picking the inherited one. This allows locking a
  repository against a later global change, and replaces the mockup's "picking the inherited value is a
  no-op". It was raised by the builder check.
- Existing repositories' tier is migrated as set at the repository level.

## Docs to update with the change

- **ADR-0025**: the defaults hierarchy (four levels, nearest set level wins, live inheritance, scheduled
  runs use the repository level, factory fast hash off in line with ADR-0021).
- `docs/guide/web-ui.md`: Settings (no longer a placeholder), Properties, the wizard and the drawers.
- `docs/design/hosts/web.md`: where defaults are stored and resolved, and the scheduler.

## Testing

- Api tests for resolution order, set vs. inherited, Reset, migration of an existing `default_tier`,
  boundary validation, and the scheduler firing with resolved values.
- Vitest for any client-side logic (pill selection, drawer pre-fill and "changed for this run").
- Hermetic Playwright e2e: save a global setting → a repository that inherits it pre-fills the drawer
  with it, and a repository that set its own value does not.

## Left to the builder

The storage shape, endpoint routes and payloads, component and file names, whether pill sources are
computed on the server or the client, and how the shared field component is split.
