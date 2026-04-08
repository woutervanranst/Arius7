# Shared Services

                     snapshot truth / coordination
                               │
                               ▼
                    ┌─────────────────────┐
                    │   SnapshotService   │
                    │ disk JSON <-> blob  │
                    └─────────┬───────────┘
                              │
               mismatch invalidates chunk-index L2/L1
                              │
        immutable tree blobs  │                  mutable shard state
                              ▼
                    ┌─────────────────────┐
                    │  TreeCacheService   │
                    │   disk <-> blob     │
                    │ exists via File.Exists
                    └─────────┬───────────┘
                              │
                              ▼
                    ┌─────────────────────┐
                    │     TreeBuilder      │
                    │ consumes tree cache  │
                    └─────────────────────┘
                    ┌─────────────────────┐
                    │ ChunkIndexService   │
                    │ L1 mem -> L2 disk -> blob
                    └─────────────────────┘

# Component Overview
| Component | Role | Cache model |
| -- | -- | -- |
| ChunkIndexService | content-hash to chunk metadata lookup and end-of-run flush | L1 memory LRU, L2 disk, L3 blob |
| TreeCacheService | tree blob read-through/write-through and remote existence checks | disk, then blob; snapshot-aware validation
| SnapshotService | snapshot create/resolve/list | local JSON copy plus remote listing |
| TreeBlobSerializer | serialization for tree blobs | not a cache itself |
| TreeBuilder | builds Merkle tree blobs from manifest | uses TreeCacheService; temp manifest files are staging, not cache
| ManifestWriter / ManifestSorter | archive temp-file staging | transient disk staging |
| Storage, Encryption, Streaming, LocalFileEnumerator | infrastructure/support | none |