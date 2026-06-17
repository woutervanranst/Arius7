# Arius.Web

Angular 20 + Metronic v9 (Tailwind/KTUI) frontend for Arius. It talks to **Arius.Api** over REST
(`/api`) and SignalR (`/hubs/arius`) — the file browser, archive/restore streaming, container
discovery and global search all run over the hub.

## Development

```bash
# 1. start the API (from the repo root)
dotnet run --project src/Arius.Api          # http://localhost:5080

# 2. start the web dev server (from here)
cd src/Arius.Web
npm install
npm start                                   # http://localhost:4200
```

`proxy.conf.json` forwards `/api` and `/hubs` (WebSocket) to the API, so the SPA is same-origin in
dev. Build a production bundle with `npm run build` (output in `dist/arius-web`); in Docker the API
serves that bundle from `wwwroot`.

### Local state (when running the API with `dotnet run`)

`ContentRootPath` is the API project dir, so by default state lands under `src/Arius.Api/.appstate`
(gitignored). Override the first two with `Arius__AppDbPath` / `Arius__DataProtectionKeysPath`.

| What | Default path |
|---|---|
| App SQLite (storage accounts, repositories, jobs, schedules) | `src/Arius.Api/.appstate/arius-app.sqlite` |
| Data-Protection keys (encrypt account keys + passphrases at rest) | `src/Arius.Api/.appstate/keys/` |
| Arius.Core caches (chunk index, filetree, snapshots) | `~/.arius/<account>-<container>/` |
| Default restore destination (repositories with no local path) | `<temp>/arius-restore/<repoId>` (e.g. `/var/folders/.../arius-restore/1`) |
| Isolated e2e app DB | `src/Arius.Web/e2e/.state/` |

## Docker (single-container deployment)

One image serves the built Angular SPA (from `wwwroot`) **and** the API (`/api` + `/hubs`) on port
`8080`. The `Dockerfile`, `.dockerignore` and `docker-compose.yml` live at the **repository root**,
and the build context is the repo root — run the commands below from there (not from `src/Arius.Web`).

### Build the container

```bash
# from the repo root
docker build -t arius-web:latest .
```

Multi-stage: builds the Angular bundle (`node`), publishes `Arius.Api` (`dotnet/sdk`), then assembles
the SPA + API into a `dotnet/aspnet` runtime image.

### Run the container

```bash
docker run -d --name arius -p 8080:8080 \
  -v /host/arius/data:/data \
  -v /host/photos:/repos/photos \
  arius-web:latest
# → http://localhost:8080
```

`-v …:/data` persists the app SQLite, the Data-Protection keys, and Arius.Core's caches. Add one
`-v host-share:/container-path` mount per repository local-overlay folder (a repository's local path,
set in Properties / the wizards, must resolve **inside** the container — i.e. one of these mounts).

### Run with Docker Compose

```bash
# from the repo root
docker compose up -d --build      # build the image and start (detached)
docker compose logs -f            # follow logs
docker compose down               # stop and remove
```

Edit the `volumes:` in `docker-compose.yml` to match your host shares — the defaults target Synology
paths (`/volume1/...`).

### Paths inside the container

| What | Path |
|---|---|
| App SQLite | `/data/arius-app.sqlite` (`Arius__AppDbPath`) |
| Data-Protection keys | `/data/keys` (`Arius__DataProtectionKeysPath`) |
| Arius.Core caches (`~/.arius`) | `/data/.arius` (`HOME=/data`) |
| Repository local-overlay folders | the host shares you mount (e.g. `/repos/photos`) |

## End-to-end tests (Playwright)

A **live full-stack** suite (`e2e/`): Playwright boots the real `Arius.Api` + `ng serve` and drives
the app against a configured repository — no REST/SignalR mocking. The browser, time-travel,
streaming archive/restore and search are exercised against live data.

### Prerequisites — a repository

The suite needs one repository. Either pre-seed one in the app DB, or let `globalSetup` create it
from environment variables (copy `e2e/.env.example` and export these):

| Variable | Purpose |
|---|---|
| `ARIUS_E2E_ACCOUNT` | Azure Storage account name |
| `ARIUS_E2E_KEY` | account key (omit to use Azure CLI auth) |
| `ARIUS_E2E_CONTAINER` | container the suite targets (the `repo` fixture selects this) |
| `ARIUS_E2E_PASSPHRASE` | repository passphrase |
| `ARIUS_E2E_ALIAS` / `ARIUS_E2E_TIER` | optional alias / default tier |
| `ARIUS_E2E_WRITE` | set to `1` to also run the destructive `@write` specs |
| `ARIUS_E2E_SEARCH` | optional override for the global-search term (default: a real filename from the repo) |

### Running

```bash
cd src/Arius.Web

# default suite — read-only / non-destructive (browser, tabs, wizards, drawer UI, search, jobs)
npm run e2e

# also the destructive @write specs (real archive, restore round-trip, cost-approval modal)
ARIUS_E2E_WRITE=1 npm run e2e

npm run e2e:ui                 # interactive runner
npx playwright show-report e2e/playwright-report
```

`playwright.config.ts` starts both servers (with `reuseExistingServer`, so already-running dev
servers are reused) and runs serially (`workers: 1`) since the specs share one backend + repository.

### What's covered

- **Default (non-destructive):** shell navigation + ⌘K search, Overview KPIs + repos table, the
  Files tab (snapshot bar, folder tree, **state rings**, filter, legend), time-travel, Statistics,
  Properties + schedule add/delete, the restore drawer, the archive drawer (tier + mutually-exclusive
  toggles), the Add wizard's live container discovery, Create-wizard form gating, the Jobs table, and
  global search → navigate.
- **`@write` (destructive, opt-in):** a real archive; a restore round-trip (files restored to an
  empty destination, and skipped when already present, identical); and the archive-tier
  cost-approval modal. These create dedicated `e2e-arius-*` containers in the account and clean up
  their app-DB rows afterwards (the Azure containers persist — the suite assumes the account is
  yours to use).

### How it's wired

- `e2e/support/global-setup.ts` waits for the API, purges leftover `e2e-arius-*` scratch repos, and
  seeds/uses a repository.
- `e2e/support/fixtures.ts` exposes a `repo` fixture (the configured container) so specs aren't tied
  to a specific id.
- Components carry `data-testid` hooks for stable selectors.
- The isolated test app-DB lives under `e2e/.state` (gitignored — the encrypted account key never
  gets committed).

## Unit tests

```bash
ng test        # Karma/Jasmine
```
