---
status: accepted
date: 2026-11-14
decision-makers: Wouter Van Ranst
consulted: Claude (Anthropic)
informed: Arius agent team
confidence: high
---

# Resolve archive and restore defaults by live inheritance across four levels

## Context and Problem Statement

The Web UI re-asks for every archive and restore option on every run. Only the upload tier had a per-repository default, and it could be set exactly once, in the New container wizard. Scheduled archives hard-coded their options (`removeLocal: false, writePointers: false, fastHash: false`), so a repository's nightly run could never use fast hash or pointer sidecars, and the Settings page was an empty placeholder.

Each option should be configured once, at the right level, and every run should start from it while still allowing any single run to override any option. Two risks constrain the design: a change made at a higher level must not overrun a choice a repository made for itself, and a changed default must not silently become destructive — "replace with pointers" deletes local files.

The question for this ADR is how the five archive/restore options (upload tier, on-disk-after-archive, fast hash, overwrite existing, skip pointer files) obtain their value for a run.

## Decision Drivers

* Configure an option once; do not re-choose it per run.
* A global change must never change a repository that set its own value for that option.
* Nothing becomes silently destructive: "replace with pointers" must say, wherever it can be chosen, that it deletes local files and which runs it reaches.
* Fast hash stays opt-in ([ADR-0021](adr-0021-opt-in-change-detection-hashcache.md)): the default remains a full read.
* Every option follows the same rule, so the UI can show the same fields, and say where each value came from, everywhere.
* Scheduled (unattended) runs must be able to use the configured values.

## Considered Options

* Copy-down: Settings values are copied into a repository when it is created; levels are independent afterwards.
* Global-only for on-disk and fast hash, per-repository only for the tier.
* Live inheritance: Factory → Global settings → Repository properties → Run, the nearest level that sets a value wins.

## Decision Outcome

Chosen option: "live inheritance, nearest set level wins", because it is one uniform rule for every option, it satisfies "a global change never overrides a repository's own value" by construction, and provenance pills make the source of every pre-filled value visible.

Confidence: high. The rule is small and pure (`OptionDefaults.Resolve`), and the risks it carries (factory changes propagating, an inherited destructive value) are mitigated in the UI rather than by weakening the model.

The levels:

| Level | Edited in | Holds |
|---|---|---|
| Factory defaults | shipped with Arius | a value for every option |
| Global settings | Settings page | only the options set here |
| Repository properties | Properties drawer | only the options set here |
| Run | Archive / Restore drawer | a full set of values, pre-filled from the repository's resolved values; not stored |

Rules:

* **Nearest set level wins.** Per option, the value comes from Repository, else Global, else Factory. Saving a global setting immediately changes every repository that inherits that option.
* **Set vs. inherited.** At the Global and Repository levels any explicit pick sets the value — including re-picking the currently inherited one, which is how a repository pins itself against a later global change. A value stays set until **Reset** clears it. Global settings start empty.
* **Scheduled runs use the repository level**: the repository's resolved archive values. There are no per-schedule overrides.
* **Factory defaults:** tier `archive`, on-disk `keep`, fast hash **off**, overwrite off, skip pointer files off. Fast hash is not a factory default, consistent with ADR-0021; the CLI is untouched and its flags remain its own run level.
* **Migration.** The existing `repositories.default_tier` of every repository is carried over as **set at the repository level**, so existing repositories behave exactly as before. Intent is never guessed; a repository can Reset to start inheriting. Starting `archive` tiers out as inherited was rejected because it would make deliberately chosen Archive tiers follow Settings. New repositories, created or added, set nothing.

Before:

```text
Default tier:  chosen once in the New container wizard, per repository
Other options: re-chosen on every manual run; hard-coded off for scheduled runs
```

After:

```text
Settings → Properties → Archive/Restore drawer, each option inherits from the level above
Scheduled runs: the repository's resolved tier, on-disk value and fast hash
```

### Consequences and Tradeoffs

* Good, because one rule covers all five options, in Settings, Properties and both drawers.
* Good, because a repository that set its own value is immune to global changes, and can pin a value equal to the inherited one.
* Good, because scheduled archives can finally use fast hash, pointers and a chosen tier.
* Bad, because a factory-default change in a later Arius release reaches every instance that never set that option. A factory-default change is a deliberate, release-noted decision.
* Bad, because "Replace with pointers" set globally can reach many repositories' scheduled runs, which then delete local files unattended. The Settings warning names the repositories that inherit it; the Properties warning says scheduled runs do it unattended.
* Bad, because the upgrade drops `repositories.default_tier` after carrying it into `repository_defaults`, so opening an upgraded app database with an older Arius is not supported; a downgrade requires restoring a pre-upgrade copy of the app database.
* Neutral, because the New container wizard loses its Default tier field: it only existed because Properties could not edit the tier.

### Confirmation

`Arius.Api.Tests` covers the resolution order, explicit-false-overrides-inherited, the `onDisk` to archive-flag mapping the scheduler uses, the defaults store (round-trips, resolving both levels, delete cascade) and the `default_tier` migration. `Arius.Api.Integration.Tests` covers the defaults endpoints: global changes reaching inheriting repositories, a repository's own value surviving a global change, the resolved tier in the repository DTO, and rejection of unknown options and invalid tiers. The scheduler's tick loop is not exercised by a test; the options a scheduled run uses (resolution plus flag mapping) are covered. Vitest covers the pure resolution and pill logic (`defaults.spec.ts`) and the drawer store's pre-fill from resolved defaults, per-run changes and Reset, and the flags sent on start (`drawer.store.spec.ts`). A hermetic Playwright e2e saves a global setting and checks that an inheriting repository pre-fills its drawer with it while a repository with its own value does not.

## Pros and Cons of the Options

### Copy-down

* Good, because it is the safest against surprise propagation.
* Bad, because a global change never reaches repositories that have not set their own value, so Settings stops being a place to configure anything once repositories exist.

### Global-only for on-disk and fast hash

* Good, because it is simpler.
* Bad, because a repository cannot opt out of a global on-disk or fast-hash choice, and the options would not follow the same hierarchy as the tier.

### Live inheritance

* Good, because it is a uniform rule with visible provenance.
* Bad, because it propagates factory and global changes, which needs the explicit warnings above.

## More Information

- Design spec, frozen: [archive-restore-defaults-design.md](../history/superpowers/2026-11-14-archive-restore-defaults-design.md)
- Host design (storage, resolution, scheduler): [Web host](../design/hosts/web.md#archive-and-restore-defaults)
- User guide: [Defaults: where values come from](../guide/web-ui.md#defaults-where-values-come-from)
- Related: [ADR-0021](adr-0021-opt-in-change-detection-hashcache.md) (fast hash stays opt-in)
