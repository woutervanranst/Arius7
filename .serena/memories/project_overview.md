# Arius7 - Project Overview

## Purpose
Arius is a content-addressable archival tool that backs up files to Azure Blob Storage with deduplication, encryption, and compression.

## Tech Stack
- C# / .NET (latest)
- Azure Blob Storage backend
- Mediator pattern for event-driven pipeline
- TUnit for testing (NOT xUnit/NUnit)

## Key Architecture
- `src/Arius.Core/` - Core library with features and shared services
- `src/Arius.Cli/` - CLI frontend
- `src/Arius.AzureBlob/` - Azure Blob Storage implementation
- `src/Arius.Explorer/` - Explorer/browser UI

### Archive Pipeline (Features/ArchiveCommand)
Multi-stage channel-based pipeline:
1. Enumerate files
2. Hash (parallel)
3. Dedup + Route (large vs small)
4a. Large file upload (parallel)
4b. Tar builder (small files bundled)
4c. Tar upload (parallel)
5. Index flush (sequential)
6. Tree build (sequential)
7. Snapshot creation

### Key Shared Services
- **ChunkIndex** - 3-tier cache (L1=memory LRU, L2=disk, L3=Azure). Sharded by 4-char hex prefix (65,536 shards)
- **FileTree** - Merkle tree of directory blobs, one blob per directory
- **Encryption** - AES-256-GCM optional encryption layer
- **Snapshot** - Point-in-time manifest referencing root tree hash

## Solution File
`src/Arius.slnx`
