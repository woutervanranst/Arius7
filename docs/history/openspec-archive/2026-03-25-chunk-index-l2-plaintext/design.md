## Context

The chunk index is a three-tier cache:
- **L1**: In-memory LRU (512 MB default)
- **L2**: Local disk at `~/.arius/{account}-{container}/chunk-index/{4-hex-prefix}`
- **L3**: Azure Blob Storage at `chunk-index/{4-hex-prefix}`

Currently `ShardSerializer.SerializeAsync` produces bytes in the order: plaintext lines → GZip → optional AES-256-CBC. These same bytes are written both to Azure (L3) and to the local disk cache (L2) verbatim via `SaveToL2`. On an L2 hit, `LoadShardAsync` calls `ShardSerializer.Deserialize(bytes, _encryption)` — decrypt then gunzip — even though no protection is needed locally.

## Goals / Non-Goals

**Goals:**
- L2 disk cache stores plaintext lines only — no gzip, no encryption
- L3 wire format (Azure) is unchanged: gzip + optional AES-256-CBC
- L2 cache is human-readable and trivially inspectable with standard tools
- Existing stale L2 files (encrypted bytes) are handled gracefully: treated as a cache miss, re-fetched from L3

**Non-Goals:**
- Changing the L3 (Azure) format in any way
- Migrating existing L2 cache files (self-healing on next run is sufficient)
- Adding compression to L2 (plaintext only, per decision below)

## Decisions

### Decision: Plaintext lines for L2, no compression

**Chosen**: Store raw shard lines (`<content-hash> <chunk-hash> <original-size> <compressed-size>\n`) directly to disk. No GZip, no encryption.

**Alternatives considered**:
- *GZip only (no encrypt)*: Smaller on disk, but adds CPU overhead on every L2 read with no meaningful benefit. Disk space is cheap locally and shard files are small.
- *Keep current format*: No benefit over plaintext for local files. Adds unnecessary decrypt+gunzip on every L2 hit.

**Rationale**: Simplest format. Human-readable and debuggable (`cat ~/.arius/.../chunk-index/a1b2`). No security value to encrypting a user's own local cache. No space pressure that warrants compression.

### Decision: Two separate serializer methods, not a flag

**Chosen**: Add `SerializeLocal(Shard)` → `string` (or `byte[]`) and `DeserializeLocal(byte[])` → `Shard` to `ShardSerializer`. These do not accept an `IEncryptionService` parameter.

**Alternatives considered**:
- *Pass `PlaintextPassthroughService` + skip GZip via flag*: Leaks local/remote distinction into the encryption abstraction. Confusing.
- *New `ILocalShardSerializer` interface*: Overkill for two small static methods.

**Rationale**: Clean separation. The local format is not a degenerate case of the wire format — it's intentionally different (no streams, no gzip). Static methods on the existing class keep it co-located without polluting the interface.

### Decision: Stale L2 files are self-healing, no migration needed

**Chosen**: On a deserialization failure of an L2 file (wrong format — encrypted bytes being read as plaintext), treat it as a cache miss and fall through to L3.

**Rationale**: L2 is a performance cache only. Falling back to L3 is safe and correct. After the first full archive run post-upgrade, all active shards will be in the new plaintext format. No explicit migration step needed.

## Risks / Trade-offs

- **Stale L2 files cause L3 round-trips on first run after upgrade** → Acceptable. Each shard is fetched once and re-cached in plaintext. After one run the cache is warm again.
- **Format ambiguity if old and new binaries share the same cache dir** → Not a concern: L2 is a best-effort cache and falling back to L3 is always correct.

## Migration Plan

No deployment steps needed. The change is self-healing:
1. Deploy new binary.
2. On next archive run, any L2 file that fails plaintext deserialization is treated as a miss and re-fetched from L3.
3. The re-fetched shard is saved to L2 in the new plaintext format.
4. Rollback: revert binary. Old binary will fail to parse new plaintext L2 files → cache miss → L3 fetch. Safe.
