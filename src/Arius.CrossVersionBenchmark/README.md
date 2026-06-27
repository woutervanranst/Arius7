# Arius.CrossVersionBenchmark

Wall-clock comparison of the `archive` command between two builds of Arius with
**incompatible on-blob formats**:

- **v5** — the original [woutervanranst/Arius](https://github.com/woutervanranst/Arius)
- **v7** — the Arius7 rewrite (this repository)

It drives the already-built CLIs as child processes against a **real** Azure Storage
account, so the timings include hashing, compression, encryption and network upload —
the end-to-end cost a user actually experiences. Alongside time it reports the **peak
memory** of the CLI process, the **blob size** that landed in the container, and the
**source size** that was archived.

## What it measures

| Column | Source |
| --- | --- |
| Mean / Error / StdDev | BenchmarkDotNet timing of the `archive` process |
| Source | Bytes archived (real files only — constant per run) |
| Blob | Bytes stored in the container afterwards (compressed + encrypted) |
| Peak mem | Peak resident set size of the CLI process, via `/usr/bin/time -l` |

`MemoryDiagnoser` is deliberately **not** used: it measures the host's managed
allocations, not the external CLI, which would be misleading here.

## Results

Two runs to date, both on macOS (Apple M4, 10 cores), v5 = `v5.0.190`, Cool tier,
encryption on, 3 cold iterations per version, each into a fresh container.

**Dataset A — small (17 files, 25.3 MiB, mixed already-compressed media):**

| Version | Mean time | StdDev | Source | Blob stored | Peak mem |
| --- | --: | --: | --: | --: | --: |
| v5 | 16.06 s | 0.99 s | 25.3 MiB | 23.5 MiB | 200.9 MiB |
| v7 | 16.83 s | 0.23 s | 25.3 MiB | 23.3 MiB | 1058.2 MiB |

**Dataset B — larger (7,480 files, 724 MiB, compressible source code):**

| Version | Mean time | StdDev | Source | Blob stored | Peak mem |
| --- | --: | --: | --: | --: | --: |
| v5 | 4.42 min | 0.10 min | 724.1 MiB | 450.1 MiB | 237.7 MiB |
| v7 | 4.86 min | 0.04 min | 724.1 MiB | 429.0 MiB | 1030.7 MiB |

### Findings

- **Time — roughly a tie, lean v5.** On the small set the two are within noise; on the
  larger set v5's mean is ~10% lower (4.42 vs 4.86 min) but with only 3 runs the
  confidence intervals overlap (v5's margin is wide, driven by one slow run). The work is
  largely **network-bound** — wall-clock is dominated by upload to Azure, not by the
  implementation — so time does not cleanly isolate the two codebases. v7 is consistently
  the **steadier** of the two (much lower StdDev in both runs).
- **Blob size — essentially equal, v7 slightly tighter.** ~equal on the media set (little
  left to compress); on the code set v7 stores ~5% less (429 vs 450 MiB).
- **Peak memory — v7 uses ~4–5× more, and this reproduces.** ~1 GiB vs ~0.2 GiB on both
  datasets, measured apples-to-apples (both include the same `dotnet` host baseline).
  This is the one large, stable difference — plausibly v7's 64 MiB TAR bundling buffers
  plus zstd working set — and the result most worth investigating in the v7 pipeline.

Caveat: 3 cold runs give wide error bars; treat time as indicative, not conclusive.

## Memory investigation: why v7 peaks ~4–5× higher

The one large, reproducible difference is peak memory (~1 GiB v7 vs ~0.2 GiB v5,
**independent of dataset size**). It was traced to **unmanaged zstd memory**, not the
managed heap.

### Methodology

1. **Static read** of the v7 archive pipeline (`ArchiveCommandHandler`,
   `TarBuilder`, `ChunkStorageService`, `ZstdCompressionService`) to find buffers and
   per-stream allocations and the concurrency knobs.
2. **Micro-measurement** — a standalone probe creating the two compressors Arius uses,
   reading `ZSTD_estimateCCtxSize` and measuring working-set delta for 5 live streams
   (matching v7's ~5 concurrent uploads).
3. **Real-process profiling** — `dotnet-counters` (`System.Runtime`) over an actual v7
   archive, comparing peak working set against the GC heap size to locate the memory.

### Results

zstd compression-context size, and the working-set cost of keeping 5 alive:

| Compressor | Context size (`estimateCCtxSize`) | 5 live streams (Δ working set) |
| --- | --: | --: |
| gzip `SmallestSize` (v5) | ~0 | +2 MiB |
| zstd level 3 | 1.2 MiB | +18 MiB |
| **zstd level 19 (v7 default)** | **81.3 MiB** | **+415 MiB** |

Real v7 archive process (`dotnet-counters`):

| Metric | Value |
| --- | --: |
| Peak working set (RSS) | ~600 MiB sampled (`time -l` caught ~1058 MiB) |
| Managed GC heap (all gens incl. LOH) | ~29 MiB |
| GC committed memory | ~35 MiB |
| Allocation rate | ~24 MB/s |

Managed heap ~29 MiB while RSS is 600 MiB–1 GiB ⇒ ~95 % of the memory is **off the
managed heap** (ZstdSharp.Port allocates contexts in unmanaged memory). Not a managed
leak, not GC pressure.

### Findings

- **Main driver — zstd level 19.** `ZstdCompressionService.DefaultCompressionLevel = 19`
  gives an 81.3 MiB context **per compressor**, fixed-cost regardless of file size. The
  handler runs ~5 concurrently (`UploadWorkers = 4` + `TarUploadWorkers = 1`), each a
  fresh context ⇒ ~400 MiB. This is why even the 25 MiB dataset peaked at ~1 GiB.
- **Secondary — 64 MiB in-memory TAR bundles.** `TarBuilder` accumulates small files in a
  `MemoryStream` and seals a `TarTargetSize` (64 MiB) buffer; a few in flight ⇒ ~130–190 MiB
  (on the LOH).
- **Ruled out.** The round-trip verifier is bounded to ~1 MiB (a `Pipe`), and the upload
  path is fully streaming — neither buffers whole files.
- **v5 contrast.** BCL `GZipStream` has negligible per-stream cost, leaving v5 near the
  ~200 MiB runtime + Azure-SDK baseline.
- **Budget (v7 ≈ 1 GiB):** ~400 MiB zstd contexts + ~130–190 MiB TAR buffers + ~200 MiB
  runtime/SDK baseline.

### Levers (not implemented)

1. **Pool/reuse compressors** instead of `new` per chunk — caps total context memory
   regardless of throughput. Biggest structural win.
2. **Reconsider the level** — 19 is the one knob; ~12–15 keeps most ratio at a fraction of
   the context size (level 3 = 1.2 MiB). Tradeoff: compression ratio.
3. **Lower `UploadWorkers`** (4→2) halves concurrent contexts.
4. **Spool TAR bundles to a temp file** instead of `MemoryStream`. Tradeoff: disk I/O.

## Method

Because archive is content-addressable and deduplicated, a second run against the same
container uploads nothing. To keep every iteration a true from-scratch archive, the
benchmark:

1. Snapshots the source folder once, keeping only real files (drops `*.pointer.arius`
   and `.DS_Store`) so the source is never mutated and v5 never trips over v7 pointers.
2. For each iteration, copies that pristine snapshot to a temp working dir and archives
   it into a **brand-new container** (`<prefix>-<version>-<runid>-<n>`).
3. Times the `archive` child process with `RunStrategy.ColdStart` (3 iterations per
   version, no warmup — each run is a cold, from-scratch archive).
4. Gathers peak memory and blob size **outside** the timed region and writes them to
   per-version CSV files that the host reads for the custom columns.

### Why the out-of-process toolchain

BenchmarkDotNet's in-process toolchain has a watchdog that aborts multi-minute runs, so
this project uses the **default out-of-process toolchain**. The benchmark therefore runs
in a BDN-spawned child that never executes `Program.Main`, so configuration is passed as
**environment variables** (re-parsed in `[GlobalSetup]`) and the custom metrics cross the
process boundary as CSV files in the output directory.

## Build the two CLIs first

```bash
# v7 (this repo)
dotnet build src/Arius.Cli/Arius.Cli.csproj -c Release

# v5 (clone + checkout the release tag)
git clone https://github.com/woutervanranst/Arius /tmp/arius-v5
git -C /tmp/arius-v5 checkout v5.0.190
dotnet build /tmp/arius-v5/src/Arius.Cli/Arius.Cli.csproj -c Release
# v5's assembly is named arius.dll (AssemblyName=arius)
```

## Run

```bash
ARIUS_ACCOUNT=ariusci ARIUS_KEY='<account-key>' \
ARIUS_V5_CLI=/tmp/arius-v5/src/Arius.Cli/bin/Release/net10.0/arius.dll \
ARIUS_V7_CLI=src/Arius.Cli/bin/Release/net10.0/Arius.Cli.dll \
dotnet run -c Release --project src/Arius.CrossVersionBenchmark -- \
  --source "/Users/wouter/Downloads/REPOS NN/API" --tier Cool
```

| Option | Env var | Default | Meaning |
| --- | --- | --- | --- |
| `--v5-cli` | `ARIUS_V5_CLI` | — | Path to v5 `arius.dll` |
| `--v7-cli` | `ARIUS_V7_CLI` | — | Path to v7 `Arius.Cli.dll` |
| `--source` | `ARIUS_SOURCE` | `/Users/wouter/Downloads/AriusTest` | Folder to archive |
| `--account` | `ARIUS_ACCOUNT` | — | Azure Storage account name |
| `--key` | `ARIUS_KEY` | — | Azure Storage account key |
| `--passphrase` | `ARIUS_PASSPHRASE` | `ariusbench` | Encryption passphrase (same for both) |
| `--tier` | `ARIUS_TIER` | `Cool` | Blob tier |
| `--prefix` | `ARIUS_PREFIX` | `bench` | Container name prefix |
| `--output` | `ARIUS_OUTPUT` | `results/<runid>` | Output directory (logs, CSVs, reports) |
| `--keep` / `--delete-after` | `ARIUS_KEEP` (`1`/`0`) | **keep** | Keep or delete the benchmark containers afterwards |

Iteration count is fixed at 3 (`BenchmarkSettings.IterationCount`).

### Cleaning up kept containers

Containers are **kept by default**. To remove a run's containers later:

```bash
ARIUS_ACCOUNT=ariusci ARIUS_KEY='<account-key>' \
dotnet run -c Release --project src/Arius.CrossVersionBenchmark -- --delete-prefix bench
```

This deletes every container whose name starts with the given prefix, then exits.
