## Why

The chunk-index and filetree wire formats have redundancy and inconsistency. Large-file chunk-index entries repeat the same hash twice (content-hash == chunk-hash). Filetrees use JSON while chunk-index uses a space-separated text format. Aligning both to compact text formats reduces storage overhead and makes the system more uniform.

## What Changes

- **BREAKING**: Chunk-index shard entries for large files use 3 fields instead of 4 (omit redundant chunk-hash). Small-file entries remain 4 fields. Field count acts as the type discriminator.
- **BREAKING**: Filetree blobs switch from JSON to a space-separated text format. Each line is either `<hash> F <created> <modified> <name>` (file) or `<hash> D <name>` (directory). Entries sorted by name for deterministic hashing.
- Update README.md to reflect new filetree format.

## Capabilities

### New Capabilities

_(none)_

### Modified Capabilities

- `archive-pipeline`: Index shard entry format changes (3 fields for large, 4 for small). Merkle tree blob format changes from JSON to text.
- `restore-pipeline`: Must parse new chunk-index format (3 or 4 fields) and new filetree text format.
- `blob-storage`: Filetree content type changes from `application/json` to `text/plain; charset=utf-8`. Chunk-index shard entry format description changes.
- `ls-command`: Must parse new filetree text format during tree traversal.

## Impact

- **Shard.cs**: `TryParse` accepts 3 or 4 fields; `Serialize` omits chunk-hash when equal to content-hash. In-memory `ShardEntry` record unchanged (reconstruct on parse).
- **TreeBlobSerializer.cs**: Replace JSON serialization with space-separated text format. `TreeModels.cs` records unchanged.
- **TreeService.cs**: TreeBuilder writes new format. ManifestWriter unaffected (internal format).
- **RestorePipelineHandler.cs**: Tree traversal deserialization changes. Chunk resolution logic unchanged (still compares ContentHash == ChunkHash on the in-memory model).
- **LsHandler.cs**: Tree traversal deserialization changes.
- **BlobConstants.cs**: Filetree content type changes.
- **Tests**: Serialization tests need updating for both formats.
- **README.md**: Filetree format example needs updating.
