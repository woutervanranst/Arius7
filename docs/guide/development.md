# Development

How to build, run, and test Arius locally. This is the contributor *how-to*; for **why** the test suite is shaped the way it is (the representative workflow, fixture boundaries, the coverage floor), see [Testing](../design/cross-cutting/testing.md). For using or deploying the built artifacts, see the other [guides](cli.md) and the [README](https://github.com/woutervanranst/Arius7).

## Prerequisites

- **.NET SDK 10** — the projects target `net10.0` (Explorer: `net10.0-windows`).
- **Node 22** — for the Angular web app (`src/Arius.Web`).
- **Docker** — optional; only needed to run the Azurite-backed integration and E2E tests locally.

## The solution

`Arius.slnx` holds the .NET projects (`Arius.Core`, `Arius.AzureBlob`, `Arius.Cli`, `Arius.Api`, `Arius.Explorer`, the test projects, and the benchmarks). `src/Arius.Web` (Angular) is a **Node** project, is **not** part of `Arius.slnx`, and is built with npm — its bundle is served by `Arius.Api`.

```bash
dotnet build        # builds the whole solution
```

Build the *full* solution after changing a shared `Arius.Core` record or interface — `Arius.Cli`, `Arius.Api`, `Arius.Explorer`, and the test projects all consume Core.

## Running each host locally

### CLI

```bash
dotnet run --project src/Arius.Cli -- <verb> ...
```

See the [CLI guide](cli.md) for the verbs and options.

### Web app (API + Angular SPA)

The web UI is the Angular SPA served by `Arius.Api` over REST (`/api`) and SignalR (`/hubs/arius`). In development, run the two halves separately:

```bash
# 1. start the API (from the repo root)
dotnet run --project src/Arius.Api          # http://localhost:5080

# 2. start the web dev server
cd src/Arius.Web
npm install
npm start                                   # http://localhost:4200
```

`proxy.conf.json` forwards `/api` and `/hubs` (WebSocket) to the API, so the SPA is same-origin in dev. Dev state lands under `src/Arius.Api/.appstate/` (gitignored). Full web dev / Docker / Playwright detail is in [`src/Arius.Web/README.md`](https://github.com/woutervanranst/Arius7/blob/master/src/Arius.Web/README.md); to run the packaged single container, see [Deployment](deployment.md).

### Explorer (Windows)

```bash
dotnet run --project src/Arius.Explorer      # Windows only (WPF, net10.0-windows)
```

See the [Explorer guide](explorer.md).

## Tests

| Test project | Purpose | Real Azure | Azurite |
|---|---|:---:|:---:|
| `src/Arius.Core.Tests` | Fast unit and feature-level tests for archive, restore, list, snapshot, chunk, and tree behavior without a storage emulator. | N | N |
| `src/Arius.AzureBlob.Tests` | The Azure Blob adapter and Azure-specific storage boundary, in isolation. | N | N |
| `src/Arius.Cli.Tests` | Command-line parsing, option wiring, and CLI-facing behavior. | N | N |
| `src/Arius.Api.Tests` | Web-host logic in isolation — e.g. the app-database statistics cache (hit/miss, fingerprint pruning). | N | N |
| `src/Arius.Architecture.Tests` | Enforces repository structure and architectural boundaries. | N | N |
| `src/Arius.Explorer.Tests` | Windows-only tests for the Explorer application. | N | N |
| `src/Arius.Integration.Tests` | Pipelines and shared services against an emulator-backed repository (archive, restore, list, chunk-index, filetree, crash recovery). | N | Y |
| `src/Arius.E2E.Tests` | End-to-end behavior across representative archive/restore scenarios — Azurite for shared coverage, live Azure for opt-in real-service coverage. | Y | Y |

`src/Arius.Tests.Shared` is **not** a test project — it is the reusable test infrastructure shared by the integration and E2E suites (see [Testing](../design/cross-cutting/testing.md)). Azurite-backed integration and E2E tests **skip at runtime** when Docker is unavailable, reported as skipped rather than filtered out.

### Running tests

```bash
dotnet test                                   # whole solution
dotnet test --project src/Arius.Core.Tests    # one project
```

- These projects use **TUnit** on the Microsoft Testing Platform. Filter by class/test with `--treenode-filter "/*/*/<ClassName>/*"` — the standard `--filter` flag silently runs zero tests.
- Web tests: the Angular SPA carries no unit-test harness — its behaviour is covered end-to-end by **Playwright** specs under `src/Arius.Web/e2e/`, documented in [`src/Arius.Web/README.md`](https://github.com/woutervanranst/Arius7/blob/master/src/Arius.Web/README.md).

### Setup (credentials for live tests)

The test suites read the same user secrets / environment variables. To set up:

```bash
dotnet user-secrets set "ARIUS_E2E_ACCOUNT" <name> --project src/Arius.E2E.Tests
dotnet user-secrets set "ARIUS_E2E_KEY"     <key>  --project src/Arius.E2E.Tests
```

### End-to-end tests

`src/Arius.E2E.Tests/` contains the actual end-to-end coverage:

- `RepresentativeArchiveRestoreTests.cs` runs one canonical representative workflow on Azurite and, when credentials are available, live Azure.
- The workflow exercises one evolving archive history (not isolated one-off scenarios): incremental archive, warm and cold restore, previous-version restore, no-op re-archive, `--write-pointers`, `--remove-local`, `--fast-hash`, conflict handling, and archive-tier pending-versus-ready behavior when the backend supports it.
- No-op archive runs preserve the current latest snapshot, so snapshot history reflects repository state changes, not repeated invocations.
- The synthetic repository size is one explicit constant in `SyntheticRepositoryDefinitionFactory` — tune it upward deliberately.
- `E2ETests.cs` keeps the live Azure credential sanity check plus narrow hot-tier pointer-file and large-file probes the representative workflow does not cover directly.

### Mutation testing

Stryker.NET is configured for local mutation testing of `Arius.Core`:

```bash
dotnet tool install --global dotnet-stryker     # once
dotnet stryker --config-file stryker-config.json
```

### Benchmarks

```bash
dotnet run -c Release --project src/Arius.Benchmarks
```

Runs the canonical representative workflow with BenchmarkDotNet. Raw output goes under `src/Arius.Benchmarks/raw/`, and each run appends a line to `src/Arius.Benchmarks/benchmark-tail.log`.
