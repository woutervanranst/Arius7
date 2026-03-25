## 1. ShardSerializer — local format

- [ ] 1.1 Add `SerializeLocal(Shard shard) -> byte[]` to `ShardSerializer`: writes plaintext lines directly (no GZip, no encryption)
- [ ] 1.2 Add `DeserializeLocal(byte[] data) -> Shard` to `ShardSerializer`: reads plaintext lines directly (no decrypt, no gunzip)

## 2. ChunkIndexService — use local format for L2

- [ ] 2.1 Update `SaveToL2` to call `ShardSerializer.SerializeLocal` instead of using the already-serialized wire bytes
- [ ] 2.2 Update the L2 hit branch in `LoadShardAsync` to call `ShardSerializer.DeserializeLocal` instead of `ShardSerializer.Deserialize(..., _encryption)`
- [ ] 2.3 Wrap the L2 deserialization in a try/catch so that a stale (old encrypted) L2 file is treated as a cache miss and falls through to L3

## 3. Tests

- [ ] 3.1 Add unit tests in `ShardTests.cs` (or a new `ShardSerializerTests.cs`) for `SerializeLocal` / `DeserializeLocal` roundtrip
- [ ] 3.2 Update any existing serializer roundtrip tests that assert on L2 byte content to expect plaintext
- [ ] 3.3 Add integration test scenario: L2 file containing old encrypted bytes is treated as a miss and shard is re-fetched from L3, then re-cached as plaintext
