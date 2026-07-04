# Arius hosts — choosing CLI, Web, or Explorer

Arius is one repository format with three front-ends (*hosts*). They all read and write the same
encrypted Azure Blob container, so you can point any of them at the same repository — they differ
only in **where they run** and **how much they can do**. Pick the one that fits the task.

| Host | What it's for | Runs on |
|---|---|---|
| **[CLI](./cli.md)** (`arius`) | The full toolset, scriptable — archive, restore, list, snapshot list/diff, repair. The only host that can archive headless (a server, a NAS). | Windows · Linux · macOS |
| **[Web](./web-ui.md)** | A self-hosted browser UI (Arius.Api + Angular, Docker) to archive, restore, browse, time-travel across snapshots, and read cost/statistics. | any (browser) |
| **[Explorer](./explorer.md)** | A Windows desktop app for *browsing and restoring* the latest state of a repository, with a familiar folder tree. | Windows only |

## Feature comparison

| Capability | [Explorer](./explorer.md) | [CLI](./cli.md) | [Web](./web-ui.md) |
|---|---|---|---|
| Browse a repository (folders + files) | ✅ | `arius ls` | ✅ |
| See per-file state (on disk / in cloud / archived) | ✅ (status dots) | partial | ✅ |
| Restore / download files to disk | ✅ | `arius restore` | ✅ |
| Archive (back up) new files | ❌ | `arius archive` | ✅ |
| Pick an older snapshot / version | ❌ (latest only) | `arius snapshot list`, `restore -v` | ✅ (snapshot bar) |
| Compare two snapshots (what changed) | ❌ | `arius snapshot diff` | ❌ |
| Repair the chunk index | ❌ | `arius repair-index` | — |
| Runs on | Windows only | Windows / Linux / macOS | any (browser) |

A few load-bearing distinctions behind the grid:

- **Only the CLI and Web archive.** Explorer is browse + restore only and always opens the
  container **read-only**; archiving from a desktop client was removed. Use the CLI for headless or
  scripted backups (servers, NAS), and either the CLI or Web for interactive ones.
- **Snapshots and versions.** Every archive run records a [*snapshot*](../glossary.md#snapshot) — a
  point-in-time view. The CLI lists them with `arius snapshot list` and shows what changed between
  any two with `arius snapshot diff`; the Web has a snapshot/time-travel bar; **Explorer only ever
  shows the latest**. To restore an older version, use `arius restore -v` or the Web snapshot bar.
- **Repository repair is CLI-only.** `arius repair-index` rebuilds a corrupt or incomplete chunk
  index; there is no UI equivalent.

## See also

- [Arius CLI](./cli.md) — the full command-line toolset (all platforms).
- [Arius Web](./web-ui.md) — the self-hosted browser host.
- [Arius Explorer](./explorer.md) — the Windows browse/restore desktop app.
- [Glossary](../glossary.md) — snapshot, pointer file, chunk, storage tier, and other terms.
