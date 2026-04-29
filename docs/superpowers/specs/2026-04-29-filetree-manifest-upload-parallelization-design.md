# Filetree Manifest Upload Parallelization Design

## Context

`ArchiveCommandHandler` spends part of the archive tail building filetree blobs from the sorted manifest and uploading each missing filetree before publishing the snapshot. The current `FileTreeBuilder.BuildAsync` name hides the upload side effect, validation is triggered both by the archive handler and the builder, and missing filetree uploads are awaited one at a time.

## Goals

- Add a scoped benchmark that measures one archive of the representative V1 repository after materialization is complete.
- Rename the `FileTreeBuilder` entry point to `SynchronizeAsync` so callers can see that it computes and uploads filetrees.
- Preserve archive durability: do not publish a snapshot until all referenced chunks, filetrees, and index updates are durable.
- Upload missing filetree blobs with bounded parallelism.
- Avoid duplicate filetree validation in the archive path.
- Keep the change scoped and avoid redesigning manifest sorting or the archive pipeline.

## Non-Goals

- Do not replace `ManifestSorter` with an external sort.
- Do not stream the full manifest-to-filetree algorithm in this change.
- Do not change filetree hash format, snapshot format, or cache semantics.

## Benchmark Design

Add a new benchmark in `Arius.Benchmarks` that prepares the representative V1 source repository during global setup and measures only the archive invocation.

The preferred backend is the existing in-memory blob container from `Arius.Tests.Shared.Storage` because it removes Azurite process and network variability while still exercising the repository service graph. If the benchmark exposes behavior that depends on real blob semantics not covered by the fake, use Azurite as the fallback.

Setup will:

- Create an isolated repository fixture and local temp root.
- Create the representative synthetic repository definition.
- Materialize V1 data outside the measured benchmark method.
- Ensure the source files are present in the fixture local root before the measured archive.

The measured method will:

- Run `ArchiveCommandHandler.Handle` once with normal archive options.
- Fail the benchmark if the archive result is unsuccessful.

The benchmark run before implementation is the baseline. The same benchmark will be run after implementation to compare archive-step mean time and allocation changes.

## FileTreeBuilder Design

Rename the public entry point from `BuildAsync` to `SynchronizeAsync` and update callers/tests accordingly. The method will still return `FileTreeHash?`, with `null` representing an empty manifest.

`ArchiveCommandHandler` will remain responsible for calling `FileTreeService.ValidateAsync` once before synchronization. `SynchronizeAsync` will not call validation again. Existing `FileTreeService.ExistsInRemote` validation guarding remains the fail-fast protection for direct callers that forget to validate.

Tree computation remains bottom-up and deterministic. While processing directories deepest-first, when a directory hash is computed, append that directory entry directly to its parent directory list. This replaces the current repeated scan of all computed directory hashes for each directory.

Missing filetrees will be uploaded through a bounded parallel stage. The synchronization method will queue upload work for hashes that are not already known locally/remotely and await all upload tasks before returning the root hash. This preserves the existing snapshot safety invariant: the caller cannot publish a snapshot that references filetrees whose uploads are still in flight.

## Error Handling And Cancellation

Any upload failure cancels or faults synchronization and prevents snapshot creation. Cancellation must be observed while reading the manifest, computing directory trees, queueing uploads, and awaiting upload completion.

Concurrent uploads may race with other archive runs. `FileTreeService.WriteAsync` already handles `BlobAlreadyExistsException`, so duplicate uploads remain safe.

## Testing And Verification

Update existing filetree builder tests to use `SynchronizeAsync`. Add or adjust tests so they verify:

- Empty manifests still return `null` and upload nothing.
- Existing root hash behavior remains stable.
- Already-cached filetrees are not reuploaded.
- `SynchronizeAsync` does not return until queued filetree uploads have completed.

Run the relevant verification commands:

- `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderTests/*"`
- `dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderIntegrationTests/*"` when Azurite is available.
- `dotnet build src/Arius.Benchmarks/Arius.Benchmarks.csproj`
- Baseline and post-change benchmark runs for the scoped archive-step benchmark.
