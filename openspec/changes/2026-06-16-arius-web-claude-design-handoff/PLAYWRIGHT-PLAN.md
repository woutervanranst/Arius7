# Playwright E2E coverage for Arius.Web

## Context
The Arius.Web design handoff is fully implemented (Phases 1–5, committed `c3e4d28a`…`c3f361b4`) and was verified **manually** with throwaway Playwright scripts in `/tmp` against the real `testexplorer` Azure repo. Those verifications are not committed and don't re-run. This task **codifies them as a committed Playwright E2E suite** so the implemented use cases stay covered.

**Decision (with the user): a live full-stack suite** — tests drive the real running stack (`Arius.Api` + Angular `ng serve`, proxying REST + SignalR) against a configured repository. No REST/SignalR mocking; highest fidelity, exactly mirroring the manual verification. The file browser, streaming archive/restore, container discovery and search all run over real SignalR, so only a live stack exercises them faithfully.

The suite is **non-destructive to Azure**: restore writes to a temp dir (repos with no `localPath`), archive is asserted only via its "no source folder" guard (no upload), discovery/search are read-only, and schedule rows are added+deleted within their test.

## Approach
A Playwright project under `src/Arius.Web/e2e/`, Playwright added as a devDependency, driven by `playwright.config.ts` in `src/Arius.Web/`.

- **`webServer` (array, `reuseExistingServer: true`)** starts both servers if not already up:
  1. `dotnet run --project ../Arius.Api` with `ASPNETCORE_URLS=http://localhost:5080`, a dedicated `Arius__AppDbPath` under `e2e/.state/` (isolated test DB), and the repo creds passed through; wait on `http://localhost:5080/api/health`.
  2. `npm start` (ng serve :4200, existing `proxy.conf.json` forwards `/api` + `/hubs` incl. `ws:true`); wait on `http://localhost:4200`.
  - `baseURL: http://localhost:4200`; `projects: [{ name: 'chromium' }]`; chromium already installed.
- **`globalSetup`** (`e2e/support/global-setup.ts`): wait for `/api/health`; `GET /api/repos` — if a repo exists, use it; else, if `ARIUS_E2E_ACCOUNT/KEY/CONTAINER/PASSPHRASE` env vars are present, seed an account + repo via `POST /api/accounts` + `/api/repos`; otherwise throw a clear "set ARIUS_E2E_* or pre-seed a repo" error so the suite never silently passes with no data.
- **`repo` fixture** (`e2e/support/fixtures.ts`): per test, fetches the first repo id from `/api/repos` (via Playwright `request`) and exposes `{ repoId, alias, container }` so specs aren't hardcoded to id 1.
- **`scripts`**: `"e2e": "playwright test"`, `"e2e:ui": "playwright test --ui"`.
- Secrets stay out of git: creds come from env (or a gitignored `e2e/.env`); add `e2e/.state/`, `e2e/test-results/`, `e2e/playwright-report/`, `e2e/.env` to `.gitignore`.

### Stable selectors
Add `data-testid` attributes to the key elements the specs target (additive, low-risk, touches the shell + feature templates). Representative set:
- Shell (`app.component.ts`): `rail-overview|rail-repos|rail-jobs|rail-settings`, `topbar-search`, `breadcrumb-current`.
- Overview (`overview.component.ts`): `kpi-card`, `repo-row`.
- Repo detail (`repo-detail.component.ts`): `repo-title`, `tab-files|tab-statistics|tab-properties`, `btn-archive|btn-restore`.
- Files (`files-tab.component.ts`): `snapshot-picker`, `scrubber-dot`, `tree-node`, `file-row`, `state-ring` (already a distinct `<arius-state-ring>` element), `file-filter`, `legend-button`, `legend-popover`, `collected-bar`, `restore-collected`.
- Drawer (`archive-restore-drawer.component.ts`): `drawer`, `drawer-title`, `tier-seg`, `toggle-remove-local|toggle-no-pointers`, `drawer-start`, `live-console`, `cost-modal`, `cost-approve|cost-decline`, `progress-bar`.
- Wizards: `wizard-step`, `account-radio`, `btn-discover`, `container-radio`, `btn-add`/`btn-create`, `tier-seg`, `passphrase`, `passphrase-confirm`.
- Jobs (`jobs.component.ts`): `job-row`, `job-status`, `live-console`.
- Search (`global-search-overlay.component.ts`): `search-input`, `search-result`.
- Properties (`properties-tab.component.ts`): `prop-alias`, `schedule-row`, `schedule-cron`, `schedule-add`, `schedule-delete`.

### Specs (`e2e/specs/*.spec.ts`) — one file per feature
1. **`shell.spec.ts`** — rail navigates Overview↔Repos↔Jobs↔Settings (URL + breadcrumb); top-bar search hidden on Overview, visible elsewhere; `⌘K`/click opens the search overlay, `Esc` closes.
2. **`overview.spec.ts`** — KPI cards render; Repositories KPI = repo count; repo row shows alias + mono container; clicking a row → `/repos/:id/files`.
3. **`files.spec.ts`** (core) — snapshot bar shows `LATEST` + a scrubber dot + "Live working state"; folder tree shows the root + ≥1 subfolder; selecting a folder lists file rows; each file row renders a `<arius-state-ring>` SVG (4 `path`s) + size; filter input narrows the list; legend button opens the popover with the 76px ring + colour key.
4. **`time-travel.spec.ts`** — open the snapshot picker, assert it lists snapshots (version/date/file-count); selecting a non-latest snapshot flips the indicator to "Historical view" and resets to root.
5. **`statistics.spec.ts`** — 4 KPI cards show real numbers (Files/Original/Stored/Unique chunks) > 0 for the seeded repo.
6. **`properties.spec.ts`** — alias/account/container/key(masked)/local fields populated; edit alias + Save → success; add a cron schedule → row appears → delete → row gone (cleanup).
7. **`restore.spec.ts`** — header Restore → drawer opens (`Restore · {alias}`) → Start restore → live console streams `→ … ✓` lines, progress advances, terminal "Restore complete." with stat grid (Restored N/N). (Whole-repo restore to a temp dir — non-destructive.)
8. **`archive.spec.ts`** — header Archive → drawer idle form: tier segments (default = repo tier), the two toggles are **mutually exclusive** (enabling one disables the other); Start → for the no-`localPath` repo asserts the graceful "No source folder configured" terminal (exercises the archive job path without uploading).
9. **`add-wizard.spec.ts`** — step 1 select configured account → Connect & discover → step 2 lists real containers (≥1); does **not** finalize (avoids duplicate-repo mutation).
10. **`create-wizard.spec.ts`** — step 1 → Continue → step 2: typing an alias auto-fills a mono container name; tier segments selectable; "Create" disabled until passphrase == confirm; does **not** submit.
11. **`jobs.spec.ts`** — after a restore (or relying on history), the jobs table shows a completed restore row with a green status pill + full progress; the live-output console is present.
12. **`search.spec.ts`** — open overlay → type a filename substring → a result appears with `<arius-state-ring>` + `{repo} · {path}` + size; clicking it navigates to that repo's Files tab.

Specs use `expect(...).toBeVisible()`/`toContainText()` with web-first auto-waiting; SignalR-streamed assertions use generous `expect.poll`/`toBeVisible({ timeout })` since they depend on live Azure latency.

## Critical files
- New: `src/Arius.Web/playwright.config.ts`, `src/Arius.Web/e2e/support/{global-setup,fixtures}.ts`, `src/Arius.Web/e2e/specs/*.spec.ts`, `e2e/.env.example`.
- Edit: `src/Arius.Web/package.json` (Playwright devDep + `e2e` scripts), `src/Arius.Web/.gitignore` (test artifacts + `.env` + `.state`), and the component templates listed above (add `data-testid`s). Reuse the existing `proxy.conf.json` (already SignalR-aware) and the `/api` + `/hubs` contract.
- Reference (the manual scripts to port): the flows captured in `/tmp/pw/*.mjs` during verification (overview/files/restore/jobs/add/search).

## Verification
- Provide a repo: either keep the already-seeded `e2e/.state` DB, or `export ARIUS_E2E_ACCOUNT=… ARIUS_E2E_KEY=… ARIUS_E2E_CONTAINER=… ARIUS_E2E_PASSPHRASE=…` (the user's `ariusci`/`testexplorer`).
- `cd src/Arius.Web && npm run e2e` — Playwright boots the API + ng serve (or reuses running ones), runs all specs headless on chromium; expect all green. `npm run e2e:ui` for the interactive runner; `npx playwright show-report` for the HTML report.
- Confirm non-destructiveness: after a run, the Azure container is unchanged (no new snapshot — restore-only + archive-no-source); the only writes are to the isolated `e2e/.state` app DB and the temp restore dir.
- CI note: add a `.github/workflows` job that runs `npm ci && npx playwright install --with-deps chromium && npm run e2e` with the `ARIUS_E2E_*` secrets — out of scope to wire now, but the suite is shaped for it (env-driven, auto-starting servers).
