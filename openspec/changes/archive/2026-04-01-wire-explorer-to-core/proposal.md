## Why

The Arius.Explorer WPF app and its test project were copied verbatim from the previous version of Arius but still reference the old Arius.Core API (SQLite-backed `IStreamQuery`, `ContainerNamesQuery`, `PointerFileEntriesQuery`). The new Arius.Core uses a Merkle tree architecture with a batch `LsCommand`. The Explorer cannot compile or function until these APIs are aligned. Additionally, the Explorer needs a streaming Ls (not batch) to progressively populate its tree view, and the CLI `ls` verb would also benefit from streaming output.

## What Changes

- **BREAKING**: Replace the batch `LsCommand`/`LsResult` with a streaming `LsCommand` that returns `IAsyncEnumerable<LsEntry>`, emitting both directory and file entries as a discriminated union.
- Add an optional `LocalPath` parameter to `LsCommand`. When provided, the handler merges Merkle tree (cloud) state with local filesystem state at the directory boundary.
- Add a `Recursive` flag to `LsCommand` (`true` = full tree walk for CLI, `false` = single directory listing for Explorer tree expand).
- Add a `ContainerNamesQuery` (or service) so the Explorer can list available repositories from an Azure storage account.
- Update the CLI `ls` verb to consume the new streaming `LsCommand`.
- Wire Arius.Explorer's ViewModels to the new streaming Ls, ArchiveCommand, and RestoreCommand from Arius.Core.
- Switch Arius.Explorer.Tests from xUnit v3 to TUnit (scrap existing tests, rewrite to match Core.Tests patterns).

## Capabilities

### New Capabilities
- `container-names`: Query to list available Arius container (repository) names from an Azure storage account, used by the Explorer's repository picker.

### Modified Capabilities
- `ls-command`: Replace batch execution with streaming (`IAsyncEnumerable`). Add `LocalPath` parameter for local/cloud merge. Add `Recursive` flag. Emit directory entries alongside file entries. Merge algorithm operates at the directory boundary (one dir at a time).

## Impact

- **Arius.Core**: `LsHandler`, `LsModels`, `ServiceCollectionExtensions` — streaming rewrite, new entry types, new parameters.
- **Arius.Cli**: `LsVerb` — consume streaming Ls instead of batch `LsResult`.
- **Arius.AzureBlob**: New container listing capability (possibly in `BlobServiceFactory` or a new service).
- **Arius.Explorer**: `Program.cs` (DI setup), `ChooseRepositoryViewModel` (container names), `RepositoryExplorerViewModel` (streaming Ls, archive/restore commands), `FileItemViewModel`/`TreeNodeViewModel` (new entry types).
- **Arius.Explorer.Tests**: Complete rewrite from xUnit v3 to TUnit.
- **Breaking for any consumer** of the current batch `LsCommand`/`LsResult` API (only the CLI `ls` verb).
