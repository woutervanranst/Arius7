## 1. Streaming LsCommand Models

- [ ] 1.1 Replace `LsCommand : ICommand<LsResult>` with `LsCommand : IStreamQuery<RepositoryEntry>` in `LsModels.cs`. Add `Recursive` (default true) and `LocalPath` (optional string) properties to `LsOptions`.
- [ ] 1.2 Replace `LsEntry` with discriminated union: abstract `RepositoryEntry(string RelativePath)`, `RepositoryFileEntry(...)`, `RepositoryDirectoryEntry(...)`. Include cloud/local merge fields (`ExistsInCloud`, `ExistsLocally`, `HasPointerFile?`, `BinaryExists?`).
- [ ] 1.3 Remove `LsResult` record entirely (error handling via exceptions instead of `Success`/`ErrorMessage` pattern).

## 2. Streaming LsHandler

- [ ] 2.1 Rewrite `LsHandler` from `ICommandHandler<LsCommand, LsResult>` to `IStreamQueryHandler<LsCommand, RepositoryEntry>`. The `Handle` method returns `IAsyncEnumerable<RepositoryEntry>` with `[EnumeratorCancellation]`.
- [ ] 2.2 Implement prefix navigation: descend through tree blobs along the prefix path to reach the target directory, downloading only tree blobs on that path.
- [ ] 2.3 Implement cloud-only tree walk (`WalkTreeStreamingAsync`): for a given tree hash, download and deserialize the tree blob, yield directory and file entries. If `Recursive=true`, recurse into child directories. Apply filename substring filter to file entries.
- [ ] 2.4 Implement per-directory batch size lookup: collect all file content hashes in one directory, call `ChunkIndexService.LookupAsync` once, then yield `RepositoryFileEntry` records with sizes populated.
- [ ] 2.5 Implement two-phase merge (`WalkMergedAsync`): Phase 1 — iterate cloud entries, check local existence via `File.Exists`/`Directory.Exists`, yield cloud+local or cloud-only, track yielded names in `HashSet`. Phase 2 — iterate local filesystem entries, skip already-yielded, yield local-only. Use `LocalFileEnumerator` pointer-file pairing logic for local file entries.
- [ ] 2.6 Implement recursive descent for merged walk: cloud+local dir → recurse(childHash, childLocalPath), cloud-only dir → recurse(childHash, null), local-only dir → recurse(null, childLocalPath).

## 3. LsHandler Unit Tests

- [x] 3.1 Create `LsHandlerTests.cs` in `Arius.Core.Tests/Ls/` with mocked `IBlobStorageService`, `IEncryptionService`, `ChunkIndexService`. Use NSubstitute + Shouldly + TUnit.
- [x] 3.2 Test: cloud-only listing (no local path) — single directory with files and subdirs, verify both `RepositoryFileEntry` and `RepositoryDirectoryEntry` yielded with correct fields.
- [x] 3.3 Test: recursive vs non-recursive — same tree, verify `Recursive=false` yields only immediate children, `Recursive=true` yields all descendants.
- [x] 3.4 Test: prefix navigation — tree with nested dirs, verify only the target subtree is traversed (mock verifies only relevant tree blobs downloaded).
- [x] 3.5 Test: prefix + recursive=false — navigate to subdirectory, list only its immediate children.
- [x] 3.6 Test: filename substring filter — verify case-insensitive matching, directories not filtered.
- [x] 3.7 Test: two-phase merge — file in cloud+local, file in cloud-only, file in local-only. Verify correct `ExistsInCloud`/`ExistsLocally` flags and that local-only files appear in output.
- [x] 3.8 Test: directory merge — dir in cloud+local, dir in cloud-only, dir in local-only. Verify all three yielded as `RepositoryDirectoryEntry` with correct flags.
- [x] 3.9 Test: per-directory batch size lookup — verify `ChunkIndexService.LookupAsync` called once per directory (not per file). Verify size=null when hash not in index.
- [x] 3.10 Test: snapshot not found — verify exception thrown with descriptive message.
- [x] 3.11 Test: cancellation — verify enumeration stops when CancellationToken is cancelled.

## 4. Update CLI LsVerb

- [x] 4.1 Update `LsVerb.cs` to consume `mediator.CreateStream(command)` via `await foreach` instead of batch `LsResult`. Handle `RepositoryFileEntry` for table output, ignore `RepositoryDirectoryEntry` (CLI only shows files).
- [x] 4.2 Update error handling: catch exceptions from the stream instead of checking `LsResult.Success`.
- [x] 4.3 Update CLI argument-parsing tests in `CliTests.cs` — mock now uses `IStreamQueryHandler<LsCommand, RepositoryEntry>` instead of `ICommandHandler<LsCommand, LsResult>`.

## 5. Update Integration Tests

- [x] 5.1 Update `LsIntegrationTests.cs` to consume streaming API (`CreateStream` or direct handler). Adapt existing 5 tests to work with `IAsyncEnumerable<RepositoryEntry>` instead of `LsResult.Entries`.
- [x] 5.2 Update `PipelineFixture.cs` — replace `CreateLsHandler()` and `LsAsync()` helpers to work with the streaming handler signature.

## 6. ContainerNamesQuery

- [x] 6.1 Create `ContainerNamesQuery : IStreamQuery<string>` and `ContainerNamesQueryHandler : IStreamQueryHandler<ContainerNamesQuery, string>` in `Arius.AzureBlob`. Accept `BlobServiceClient` (or account connection params). List containers, filter by checking for `snapshots/` prefix blob.
- [ ] 6.2 Register handler in DI (verify Mediator source generator picks it up, or add manual registration).
- [x] 6.3 Write unit test for `ContainerNamesQueryHandler` with mocked `BlobServiceClient`.

## 7. Wire Explorer

- [ ] 7.1 Update `Arius.Explorer.csproj` — ensure project references to new Arius.Core and Arius.AzureBlob compile. Verify Mediator packages are aligned.
- [ ] 7.2 Update `Program.cs` DI setup — replace old `services.AddArius()` with new signature. Implement per-repository `ServiceProvider` rebuild pattern.
- [ ] 7.3 Update `ChooseRepositoryViewModel.cs` — replace old `ContainerNamesQuery` usage with new `IStreamQuery<string>` via `mediator.CreateStream(...)`.
- [ ] 7.4 Update `RepositoryExplorerViewModel.cs` — replace old `PointerFileEntriesQuery` with new `LsCommand` (streaming). Use `Prefix` for current directory, `Recursive=false` for tree expand.
- [ ] 7.5 Update `FileItemViewModel.cs` and `TreeNodeViewModel.cs` — adapt to new `RepositoryFileEntry` / `RepositoryDirectoryEntry` types instead of old `PointerFileEntriesQueryFileResult` / `PointerFileEntriesQueryDirectoryResult`.
- [ ] 7.6 Wire Archive and Restore commands in Explorer to new Arius.Core `ArchiveCommand` and `RestoreCommand`.

## 8. Explorer Tests

- [ ] 8.1 Convert `Arius.Explorer.Tests.csproj` from xUnit v3 to TUnit + Shouldly + NSubstitute (match Arius.Core.Tests patterns).
- [ ] 8.2 Rewrite ChooseRepository VM tests against new `ContainerNamesQuery` streaming API.
- [ ] 8.3 Rewrite Settings tests using TUnit conventions.

## 9. ServiceCollectionExtensions

- [ ] 9.1 Update `ServiceCollectionExtensions.cs` in Arius.Core — verify Mediator source generator auto-registers `LsHandler` as `IStreamQueryHandler`. Remove any manual handler registration for old batch `ICommandHandler<LsCommand, LsResult>` if present.
