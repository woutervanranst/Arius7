## 1. BlobConstants content types

- [x] 1.1 Add `FileTreeEncrypted = "application/aes256cbc+gzip"` and `FileTreePlaintext = "application/gzip"` to `ContentTypes` in `BlobConstants.cs`; remove or deprecate the existing `FileTree = "text/plain; charset=utf-8"` constant

## 2. FileTreeBlobSerializer storage methods

- [x] 2.1 Add `SerializeForStorageAsync(FileTreeBlob, IEncryptionService)` method: serialize to text bytes, then gzip, then encrypt — mirroring `ShardSerializer.SerializeAsync`
- [x] 2.2 Add `DeserializeFromStorageAsync(Stream, IEncryptionService)` method: decrypt, gunzip, then deserialize — mirroring `ShardSerializer.DeserializeFromStream`

## 3. Upload path (TreeService)

- [x] 3.1 In `TreeService.EnsureUploadedAsync`, replace raw `FileTreeBlobSerializer.Serialize(tree)` with `FileTreeBlobSerializer.SerializeForStorageAsync(tree, _encryption)` for the upload byte array
- [x] 3.2 Set content type conditionally using `_encryption.IsEncrypted ? ContentTypes.FileTreeEncrypted : ContentTypes.FileTreePlaintext` (matching `ChunkIndexService` pattern)
- [x] 3.3 Keep disk cache writes using plaintext `FileTreeBlobSerializer.Serialize(tree)` (no change to cache format)

## 4. Download paths (Restore + Ls)

- [x] 4.1 In `RestorePipelineHandler.WalkTreeAsync`, replace `FileTreeBlobSerializer.DeserializeAsync(stream)` with `FileTreeBlobSerializer.DeserializeFromStorageAsync(stream, _encryption)`
- [x] 4.2 In `LsHandler.WalkTreeAsync`, replace `FileTreeBlobSerializer.DeserializeAsync(stream)` with `FileTreeBlobSerializer.DeserializeFromStorageAsync(stream, _encryption)`

## 5. Tests

- [x] 5.1 Add `FileTreeBlobSerializer` roundtrip test: serialize for storage with `PassphraseEncryptionService`, deserialize from storage with same passphrase, verify entries match
- [x] 5.2 Add `FileTreeBlobSerializer` roundtrip test: serialize for storage with `PlaintextPassthroughService`, deserialize from storage, verify entries match
- [x] 5.3 Verify encrypted output starts with `Salted__` prefix (confirming AES-256-CBC format)
- [x] 5.4 Run full test suite and verify all existing tree-related tests still pass
