# Arius.CrossVersionBenchmark

Wall-clock comparison of the `archive` command between two builds of Arius with
**incompatible on-blob formats**:

- **v5** — the original [woutervanranst/Arius](https://github.com/woutervanranst/Arius)
- **v7** — the Arius7 rewrite (this repository)

It drives the already-built CLIs as child processes against a **real** Azure Storage
account, so the timings include hashing, compression, encryption and network upload.

## Method

Because archive is content-addressable and deduplicated, a second run against the same
container uploads nothing. To keep every iteration a true from-scratch archive, the
benchmark:

1. Snapshots the source folder once, keeping only real files (drops `*.pointer.arius`
   and `.DS_Store`) so the source is never mutated and v5 never trips over v7 pointers.
2. For each `(version, iteration)` pair, copies that pristine snapshot to a temp working
   dir and archives it into a **brand-new container** (`<prefix>-<version>-<runid>-<n>`).
3. Times the child process end-to-end with a `Stopwatch`.
4. Writes `report.md`, `results.json` and per-run `.log` files to the output dir.
5. Deletes the throwaway containers (unless `--no-cleanup`).

## Build the two CLIs first

```bash
# v7 (this repo)
dotnet build src/Arius.Cli/Arius.Cli.csproj -c Release

# v5 (clone + checkout the release tag)
git clone https://github.com/woutervanranst/Arius /tmp/arius-v5
git -C /tmp/arius-v5 checkout v5.0.190
dotnet build /tmp/arius-v5/src/Arius.Cli/Arius.Cli.csproj -c Release
```

## Run

```bash
ARIUS_ACCOUNT=ariusci ARIUS_KEY='<account-key>' \
ARIUS_V5_CLI=/tmp/arius-v5/src/Arius.Cli/bin/Release/net10.0/arius.dll \
ARIUS_V7_CLI=src/Arius.Cli/bin/Release/net10.0/Arius.Cli.dll \
dotnet run -c Release --project src/Arius.CrossVersionBenchmark -- \
  --source /Users/wouter/Downloads/AriusTest \
  --iterations 5 --tier Cool
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--v5-cli` / `ARIUS_V5_CLI` | — | Path to v5 `arius.dll` |
| `--v7-cli` / `ARIUS_V7_CLI` | — | Path to v7 `Arius.Cli.dll` |
| `--source` | `/Users/wouter/Downloads/AriusTest` | Folder to archive |
| `--account` / `ARIUS_ACCOUNT` | — | Azure Storage account name |
| `--key` / `ARIUS_KEY` | — | Azure Storage account key |
| `--passphrase` / `ARIUS_PASSPHRASE` | `ariusbench` | Encryption passphrase (same for both) |
| `--tier` | `Cool` | Blob tier |
| `--iterations` | `5` | Runs per version |
| `--prefix` | `bench` | Container name prefix |
| `--output` | `results/<runid>` | Output directory |
| `--no-cleanup` | (off) | Keep the benchmark containers instead of deleting them |
