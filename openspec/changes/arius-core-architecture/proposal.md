## Why

We need a deduplicated, encrypted backup tool purpose-built for Azure Blob Storage's Archive tier. Restic provides excellent backup semantics (content-defined chunking, snapshots, encryption) but supports nine backends with none optimized for archive-tier economics. Archive tier introduces unique constraints — rehydration delays, per-operation pricing, minimum storage durations — that require first-class architectural treatment, not bolt-on workarounds.

## What Changes

- **New CLI tool** (`arius`) with restic-like commands built on Spectre.Console
- **New core library** implementing repository format, content-defined chunking (Gear hash CDC), AES-256-CBC encryption (OpenSSL-compatible), pack file management, snapshot/tree/index management
- **Azure Blob backend** with tiered storage (Cold for metadata, Archive for data packs), blob lease-based locking, and multi-phase rehydration-aware restore
- **ASP.NET Core API** with Minimal APIs + SignalR for streaming progress to web clients
- **Vue + TypeScript web UI** as a Docker container providing File Explorer-style repository browsing
- **Mediator pattern** (using Mediator, not MediatR) for shared command/handler logic between CLI and API
- **Local SQLite cache** as an optional performance optimization, fully rebuildable from remote — Azure is the sole source of truth
- **IAsyncEnumerable throughout** for streaming operations across CLI, API, and handlers

## Capabilities

### New Capabilities
- `repository-format`: Repository structure, pack files, content-addressable storage, config, versioning
- `chunking`: Content-defined chunking via Gear hash CDC with configurable min/avg/max sizes
- `encryption`: AES-256-CBC OpenSSL-compatible encryption with PBKDF2 key derivation, multi-key support
- `azure-backend`: Azure Blob Storage integration — upload, download, rehydrate, tiering (cold/archive), blob lease locking
- `backup`: Create snapshots from local files — scan, chunk, dedup, pack, encrypt, upload
- `restore`: Multi-phase restore — plan, cost estimate, rehydrate, download, decrypt, reassemble (overlapping phases)
- `snapshot-management`: List, forget, tag, diff snapshots; retention policies (keep-last, keep-daily, etc.)
- `repository-maintenance`: Prune unreferenced data, check integrity, repair index/snapshots
- `tree-browsing`: Walk directory trees from snapshots, ls, find — served from cold tier (no rehydration)
- `local-cache`: Optional SQLite cache for index/tree/snapshot data, delta-synced, fully rebuildable from remote
- `cli`: Spectre.Console CLI with restic-like command surface
- `api`: ASP.NET Core Minimal APIs + SignalR hubs for web frontend
- `web-ui`: Vue + TypeScript File Explorer interface with streaming progress updates

### Modified Capabilities

_(none — greenfield project)_

## Impact

- **New solution**: Entire .NET solution with multiple projects (Core, Azure, CLI, API, Web)
- **Dependencies**: Azure.Storage.Blobs SDK, Spectre.Console, Mediator, Microsoft.Data.Sqlite, SignalR, Vue 3, TypeScript
- **Docker**: Web UI + API packaged as a single container
- **Deployment**: CLI as standalone tool; web UI as Docker image
