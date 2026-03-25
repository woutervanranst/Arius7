## Context

Arius uses two wire formats for persistent data:

1. **Chunk-index shards** — space-separated text, one line per entry with 4 fields: `<content-hash> <chunk-hash> <original-size> <compressed-size>`. Stored across 65,536 shard files keyed by 4-hex-char prefix. Three-tier cache: L1 in-memory LRU, L2 local disk (plaintext), L3 Azure (gzip + optional encryption).

2. **Filetree blobs** — compact JSON with entries array. Each entry has name, type, hash, and optional created/modified timestamps. Content-addressed via SHA256 of the canonical JSON bytes (optionally passphrase-seeded). Stored at `filetrees/<tree-hash>`, cached on local disk indefinitely.

Both formats are read during restore, ls, and archive (dedup). The chunk-index is the hot path for dedup checks; filetrees are traversed for restore and ls.

## Goals / Non-Goals

**Goals:**
- Eliminate redundant chunk-hash in chunk-index entries for large files (~65 bytes saved per entry)
- Unify wire format style: both chunk-index and filetrees use space-separated text
- Keep the in-memory data model (`ShardEntry`, `TreeEntry`, `TreeBlob`) unchanged
- Keep all parse/serialize changes localized to `Shard.cs` and `TreeBlobSerializer.cs`

**Non-Goals:**
- Backward compatibility with existing repositories (still in development)
- Format versioning (can be added later via `#`-prefixed comment line if needed)
- Changing the tree hash algorithm (still SHA256 of serialized bytes, passphrase-seeded)
- Changing the chunk-index shard structure (65,536 shards, 4-hex prefix)
- Changing the L3 wire format envelope (gzip + optional AES-256-CBC for chunk-index shards)

## Decisions

### Decision 1: Field-count convention for chunk-index entries

**Choice**: 3 fields = large file, 4 fields = small file.

Large file entries omit the chunk-hash (which equals the content-hash). Small file entries retain the explicit chunk-hash (which is the tar-hash).

```
Large:  <content-hash> <original-size> <compressed-size>
Small:  <content-hash> <chunk-hash> <original-size> <compressed-size>
```

**Rationale**: The chunk-hash for large files is pure redundancy — it always equals the content-hash. Dropping it saves 65 bytes per large-file entry. The field count is an unambiguous discriminator: exactly 3 numeric-incompatible tokens (hash + two numbers) vs 4 (two hashes + two numbers).

**Alternative considered**: Sentinel value (e.g., `*` or `-` for the chunk-hash field). Rejected because it still occupies space and adds a magic value with no benefit over field count.

### Decision 2: Reconstruct chunk-hash on parse (not model change)

**Choice**: `ShardEntry.TryParse()` detects 3-field lines and constructs `ShardEntry(hash, hash, size, compressed)`. `ShardEntry.Serialize()` checks `ContentHash == ChunkHash` and emits 3 fields when equal.

The `ShardEntry` record keeps all 4 properties. All consumers remain unchanged.

**Rationale**: The optimization is a wire-format concern. The semantic model ("large files have chunk-hash equal to content-hash") is correct and used by the restore pipeline. Changing the model to nullable `ChunkHash` would ripple through `RestorePipelineHandler`, `LsHandler`, `ArchivePipelineHandler`, and tests — with no functional benefit.

### Decision 3: Filetree text format — hash first, type marker, name last

**Choice**: Each line follows one of two patterns:

```
<hash> F <created> <modified> <name>
<hash> D <name>
```

- `F` = file entry (5+ tokens: hash, type, created, modified, name...)
- `D` = directory entry (3+ tokens: hash, type, name...)
- Name is always the last field and may contain spaces
- Directory names retain trailing `/` convention
- Entries sorted by name (ordinal, case-sensitive) before serialization
- No header line, no blank lines between entries
- Lines terminated by `\n`

**Parse strategy**: Split on first N spaces based on type marker. For `F`: split on first 4 spaces, remainder is name. For `D`: split on first 2 spaces, remainder is name.

**Rationale**: Putting the variable-length, user-controlled field (name) last eliminates the need for escaping or quoting. Hash first is consistent with chunk-index format. The `F`/`D` single-character marker is compact and unambiguous; it doubles as a field-count discriminator (similar to the chunk-index 3/4 convention).

**Alternative considered**: Tab-separated with name first (like the internal manifest format). Rejected because (a) tabs are invisible and harder to debug, (b) name-first requires the parser to handle variable-width data before reaching fixed fields.

**Alternative considered**: `file`/`dir` spelled out instead of `F`/`D`. Rejected as slightly more verbose with no clarity benefit — F and D are standard filesystem conventions.

### Decision 4: Tree hash computation unchanged

**Choice**: Tree hash remains SHA256 of the serialized bytes (optionally passphrase-seeded via `IEncryptionService.ComputeHash`). The input bytes change (text instead of JSON) but the algorithm is identical.

**Rationale**: This is a breaking change anyway (all existing tree hashes become invalid). No reason to also change the hash algorithm.

### Decision 5: Timestamp format

**Choice**: Keep ISO-8601 round-trip format (`"O"` format specifier in C#) for created/modified timestamps, same as the current JSON output.

Example: `2026-03-25T10:00:00.0000000+00:00`

**Rationale**: Round-trip format preserves full precision and is unambiguous. It contains no spaces, so it works as a space-delimited field.

## Risks / Trade-offs

**[Risk: Filenames with newlines]** → Newlines in filenames would break line-based parsing. Mitigation: newlines in filenames are extremely rare (prohibited on Windows, unusual on Linux/macOS). The current JSON format also doesn't handle embedded newlines in compact mode. Accept this as a known limitation — same as git.

**[Risk: Empty names or edge-case filenames]** → Names like `.` or names starting with spaces could cause parse ambiguity. Mitigation: split-on-first-N-spaces with name as remainder handles leading spaces correctly. Empty names are impossible in practice (filesystems require non-empty names).

**[Trade-off: Less human-readable than JSON]** → The text format is less self-documenting than JSON for someone encountering a filetree blob for the first time. Acceptable because filetree blobs are content-addressed binary data — they're not meant to be hand-edited. The format is straightforward once documented.

**[Trade-off: No format version marker]** → If we ever need a v3 format, we'll need content-sniffing or a `#v3` header line. Acceptable risk — the `#`-comment convention provides a clean escape hatch, and we're still in development.
