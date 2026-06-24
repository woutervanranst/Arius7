---
status: "accepted"
date: 2026-06-23
decision-makers: ["Wouter Van Ranst"]
consulted: ["Claude Code"]
informed: ["Arius maintainers"]
confidence: "high"
---

# Centralize file/folder exclusion configuration in Arius.Core

## Context and Problem Statement

The archive walk enumerates every file and folder under the root and backs it up. Some entries are pure noise that must never enter a snapshot: NAS metadata folders (`@eaDir`, `eaDir`, `SynoResource`), OS junk files (`autorun.ini`, `thumbs.db`, `.ds_store`), and `System`/`Hidden`-attribute entries. The previous (v5) Arius skipped these with hardcoded `HashSet`s and a `FileAttributes.System` check; the rewritten enumerator dropped that behaviour and re-archived the noise.

Three hosts drive an archive — `Arius.Cli` (`ArchiveVerb`), `Arius.Api` (`JobRunner`), and `Arius.Migration` — all composing Core through `AddArius`. The CLI has **no** `appsettings.json` at all; only the Api and Explorer do.

The question for this ADR is **where the exclusion defaults should live and how they should be configured**, so the behaviour is adjustable (not hardcoded) yet identical across every host and unable to drift.

## Decision Drivers

* The exclusion list must be configurable, not hardcoded in the enumerator.
* Every host must see the **same** defaults — a per-host list would drift.
* The CLI (the primary archive path) has no config file, so the source of truth cannot be a host file.
* Excluded entries must never enter a snapshot, on first run or after a list change.
* Core ⊥ host separation ([ADR-0013](adr-0013-core-host-separation.md)): hosts compose Core, they don't reach into it.

## Considered Options

* **Hardcoded lists** in `LocalFileEnumerator` (port v5 as-is).
* **Per-host `appsettings.json`** — the CLI and Api each carry their own `Arius:Exclusions` section.
* **Central defaults in Arius.Core** — an embedded `appsettings.json` bound through the options pattern in `AddArius`, with an optional per-host `IConfiguration` override layered on top.

## Decision Outcome

Chosen option: **central defaults in Arius.Core**, because it gives one source of truth that every host inherits through the single `AddArius` composition call, keeps the list as editable JSON rather than buried `HashSet`s, and still leaves a documented per-host override seam — without making the CLI grow a config file.

`src/Arius.Core/appsettings.json` is an `<EmbeddedResource>` holding the `Arius:Exclusions` section. `AddArius` layers it as the base configuration, optionally over a host `IConfiguration`, binds it to `FileExclusionOptions` via the options pattern, and registers a `FileExclusionFilter` singleton consulted by both the archive enumeration (`LocalFileEnumerator.Enumerate`) and the `ls` local overlay (`LocalDirectoryReader.Read`), so a listing never shows an excluded file as local-only. Both types live in `Shared/FileSystem` (shared filesystem policy, not an archive-only concern):

Before — v7 enumerated everything (no exclusions); v5 hardcoded them:

```csharp
static readonly HashSet<string> ExcludedDirectories = new(...) { "@eaDir", "eaDir", "SynoResource" };
static readonly HashSet<string> ExcludedFiles       = new(...) { "autorun.ini", "thumbs.db", ".ds_store" };
```

After — central embedded `appsettings.json`, bound once in `AddArius`:

```jsonc
// src/Arius.Core/appsettings.json  (embedded; the single source of truth)
{ "Arius": { "Exclusions": {
  "ExcludedDirectoryNames": ["@eaDir", "eaDir", "SynoResource"],
  "ExcludedFileNames":      ["autorun.ini", "thumbs.db", ".ds_store"],
  "ExcludeSystemEntries":   true,
  "ExcludeHiddenEntries":   false } } }
```

```csharp
// ServiceCollectionExtensions.AddArius(..., IConfiguration? configuration = null)
var config = new ConfigurationBuilder()
    .AddConfiguration(FileExclusionOptions.EmbeddedDefaultConfiguration())   // central base
    .AddConfiguration(configuration ?? new ConfigurationBuilder().Build())   // optional host override
    .Build();
services.AddOptions<FileExclusionOptions>().Bind(config.GetSection(FileExclusionOptions.SectionName));
services.AddSingleton<FileExclusionFilter>(sp => new FileExclusionFilter(sp.GetRequiredService<IOptions<FileExclusionOptions>>().Value));
```

Confidence: high. This records an implemented decision: all three hosts call `AddArius` and inherit the embedded defaults with no host change.

### Consequences and Tradeoffs

* Good, because there is exactly one source of truth; CLI, Api, and Migration get identical exclusions through `AddArius` with zero host code.
* Good, because the list is editable JSON, not hardcoded collections, and a host may still override it by passing its own `IConfiguration` (host values win).
* Good, because directory exclusion **prunes the whole subtree** during the walk, so a NAS thumbnail folder's thousands of files are never enumerated.
* Bad, because the defaults are embedded in the assembly — changing them requires a rebuild (acceptable for *defaults*; per-deployment changes use the override seam).
* Bad, because the override is currently a **latent capability**: `AddArius` accepts an `IConfiguration`, but no shipped host passes one yet, so today the central defaults are effectively fixed for every host (see the design doc's open seams).
* Bad, because Arius.Core gains a dependency on `Microsoft.Extensions.Configuration.*` (Json + Binder + Options.ConfigurationExtensions).

### Confirmation

Covered by tests in `Arius.Core.Tests`:

* `FileExclusionFilterTests` — name matching (case-insensitive), `System`/`Hidden` attribute toggles, and that the embedded `appsettings.json` binds to the expected defaults.
* `ServiceCollectionExtensionsTests` — `AddArius` with no configuration yields the embedded defaults; a host `IConfiguration` overrides them.
* `ArchiveExclusionTests` — an end-to-end archive→restore proving excluded files/folders never enter the snapshot, and that a previously-archived file **disappears** from the new snapshot once excluded.

## Pros and Cons of the Options

### Hardcoded lists in the enumerator

* Good, because it is the least code and matches v5.
* Bad, because the list is not configurable without a code edit, and the rationale asks for configurability.

### Per-host `appsettings.json`

* Good, because each host owns its own config the conventional way, and the override is immediate.
* Bad, because the CLI has no `appsettings.json`, so it would need one created just for this.
* Bad, because two (or three) independent lists drift — the core risk this decision exists to prevent.

### Central defaults in Arius.Core (chosen)

* Good, because one embedded source feeds every host through `AddArius`; no drift.
* Good, because the options pattern still allows a per-host override when a host opts in.
* Neutral, because the override seam is built but not yet wired by any host.
* Bad, because defaults change with a rebuild, and Core takes on the configuration packages.

## More Information

* Design doc: [archive-command](../design/core/features/archive-command.md) (the Enumerate stage and the pruning walk) and [exclusion](../glossary.md#exclusion) in the glossary.
* Related: [ADR-0013](adr-0013-core-host-separation.md) (Core ⊥ host composition through `AddArius`).
* Frozen intent: [`history/agentic-plans/2026-06-23-file-folder-filter/`](../history/agentic-plans/2026-06-23-file-folder-filter/PLAN.md).
