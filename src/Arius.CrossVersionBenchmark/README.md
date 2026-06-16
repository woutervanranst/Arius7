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
