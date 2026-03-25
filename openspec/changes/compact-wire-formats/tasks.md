## 1. Chunk-index: compact shard entry format

- [ ] 1.1 Update `ShardEntry.Serialize()` in `Shard.cs` to emit 3 fields when `ContentHash == ChunkHash`, 4 fields otherwise
- [ ] 1.2 Update `ShardEntry.TryParse()` in `Shard.cs` to accept 3-field lines (reconstruct chunk-hash = content-hash) and 4-field lines
- [ ] 1.3 Update `ShardTests.ShardEntry_Serialize_ThenParse_RoundTrips` to test both large-file (3-field) and small-file (4-field) roundtrips
- [ ] 1.4 Add test: `ShardEntry_Serialize_LargeFile_Emits3Fields` — verify output has exactly 3 space-separated fields when content-hash == chunk-hash
- [ ] 1.5 Add test: `ShardEntry_TryParse_3Fields_ReconstructsChunkHash` — verify parsed entry has ChunkHash == ContentHash
- [ ] 1.6 Update `ShardEntry_TryParse_InvalidLine_Throws` to verify that 1-field and 2-field and 5-field lines throw `FormatException`
- [ ] 1.7 Verify `ShardSerializerTests` and `ShardSerializerLocalTests` still pass (they use `ShardEntry` with different hashes — should now roundtrip as 4-field entries)

## 2. Filetree: text format serialization

- [ ] 2.1 Rewrite `TreeBlobSerializer.Serialize()` to produce text format: one line per entry, `<hash> F <created> <modified> <name>` for files, `<hash> D <name>` for directories, sorted by name, newline-terminated
- [ ] 2.2 Rewrite `TreeBlobSerializer.Deserialize(byte[])` to parse text format: split lines, detect `F`/`D` marker, split on first 4 spaces for `F` or first 2 spaces for `D`, extract fields
- [ ] 2.3 Rewrite `TreeBlobSerializer.DeserializeAsync(Stream)` to parse text format from a stream
- [ ] 2.4 Update `TreeBlobSerializer.ComputeHash()` — no logic change needed (already hashes the output of `Serialize()`), but verify it produces a different hash than before (format changed)
- [ ] 2.5 Update xmldoc on `TreeBlob` record in `TreeModels.cs` — change "Serialized as JSON" to "Serialized as text"

## 3. Filetree: update content type

- [ ] 3.1 Change `ContentTypes.FileTree` in `BlobConstants.cs` from `"application/json"` to `"text/plain; charset=utf-8"`

## 4. Filetree: update tests

- [ ] 4.1 Update `TreeBlobSerializerTests.Serialize_ThenDeserialize_RoundTrips` — verify roundtrip with new text format
- [ ] 4.2 Update `TreeBlobSerializerTests.Serialize_SortsEntriesByName` — verify sorted output in text format
- [ ] 4.3 Update `TreeBlobSerializerTests.Serialize_IsDeterministic_SameInputSameOutput` — verify byte-identical output
- [ ] 4.4 Update `TreeBlobSerializerTests.Serialize_NullTimestamps_OmittedFromJson` — rename and rewrite: verify `D` lines have no timestamps, `F` lines have timestamps
- [ ] 4.5 Add test: `Serialize_FileEntryWithSpacesInName_ParsesCorrectly` — verify a filename like `my vacation photo.jpg` roundtrips correctly
- [ ] 4.6 Add test: `Serialize_DirEntryWithSpacesInName_ParsesCorrectly` — verify a dirname like `2024 trip/` roundtrips correctly
- [ ] 4.7 Verify `ComputeHash` tests still pass (hash values will change but determinism and passphrase-sensitivity properties remain)
- [ ] 4.8 Verify `TreeBuilderTests` still pass (they exercise end-to-end build via `ManifestEntry` → `TreeBuilder` → serialization)

## 5. Documentation

- [ ] 5.1 Update `README.md` filetree format example from JSON to text format
