## 1. BlobConstants content types

- [ ] 1.1 Add `FileTreeEncrypted = "application/aes256cbc+gzip"` and `FileTreePlaintext = "application/gzip"` to `ContentTypes` in `BlobConstants.cs`; remove or deprecate the existing `FileTree = "text/plain; charset=utf-8"` constant

## 2. TreeBlobSerializer storage methods

- [ ] 2.1 Add `SerializeForStorageAsync(TreeBlob, IEncryptionService)` method: serialize to text bytes, then gzip, then encrypt — mirroring `ShardSerializer.SerializeAsync`
- [ ] 2.2 Add `DeserializeFromStorageAsync(Stream, IEncryptionService)` method: decrypt, gunzip, then deserialize — mirroring `ShardSerializer.DeserializeFromStream`

## 3. Upload path (TreeService)

- [ ] 3.1 In `TreeService.EnsureUploadedAsync`, replace raw `TreeBlobSerializer.Serialize(tree)` with `TreeBlobSerializer.SerializeForStorageAsync(tree, _encryption)` for the upload byte array
- [ ] 3.2 Set content type conditionally using `_encryption.IsEncrypted ? ContentTypes.FileTreeEncrypted : ContentTypes.FileTreePlaintext` (matching `ChunkIndexService` pattern)
- [ ] 3.3 Keep disk cache writes using plaintext `TreeBlobSerializer.Serialize(tree)` (no change to cache format)

## 4. Download paths (Restore + Ls)

- [ ] 4.1 In `RestorePipelineHandler.WalkTreeAsync`, replace `TreeBlobSerializer.DeserializeAsync(stream)` with `TreeBlobSerializer.DeserializeFromStorageAsync(stream, _encryption)`
- [ ] 4.2 In `LsHandler.WalkTreeAsync`, replace `TreeBlobSerializer.DeserializeAsync(stream)` with `TreeBlobSerializer.DeserializeFromStorageAsync(stream, _encryption)`

## 5. Tests

- [ ] 5.1 Add `TreeBlobSerializer` roundtrip test: serialize for storage with `PassphraseEncryptionService`, deserialize from storage with same passphrase, verify entries match
- [ ] 5.2 Add `TreeBlobSerializer` roundtrip test: serialize for storage with `PlaintextPassthroughService`, deserialize from storage, verify entries match
- [ ] 5.3 Verify encrypted output starts with `Salted__` prefix (confirming AES-256-CBC format)
- [ ] 5.4 Run full test suite and verify all existing tree-related tests still pass
