# Arius.Web — agent contract

Cross-cutting rules (think-before-coding, simplicity, testing workflow, code style, the
documentation map) live in the [root AGENTS.md](../../AGENTS.md) — read it first; this file
only covers what is specific to this project.

- **What it is:** the Angular 21 SPA, served by Arius.Api. Architecture: [design/hosts/web.md](../../docs/design/hosts/web.md). User-facing tour: [guide/web-ui.md](../../docs/guide/web-ui.md).
- **Dev setup, Docker, e2e details:** [src/Arius.Web/README.md](./README.md) — keep it current when you change scripts, ports, env vars, or volumes. Deployment: [guide/deployment.md](../../docs/guide/deployment.md). Terms: [glossary.md](../../docs/glossary.md).

## This is a Node project, NOT in `Arius.slnx`

`dotnet build` / the .NET solution never see this folder. Run everything with `npm`/`ng`/`npx`
from `src/Arius.Web`. It ships in Docker as a static bundle (`dist/arius-web` → API `wwwroot`).

## Stack

Angular 21 (standalone components, signals), Metronic v9 themed with Tailwind v4 + KTUI,
`@microsoft/signalr` client, RxJS. No NgModules — wire DI via `providers` and `inject()`.

## Layout (vertical slices)

- `src/app/core/` — app-wide singletons, no UI: `api/api.service.ts` (REST `/api`),
  `api/realtime.service.ts` (SignalR), `api/api-models.ts` (DTOs), `state/*.store.ts`
  (signal stores), `ktui/`, `services/metronic-init.service.ts`.
- `src/app/features/` — one folder per route/feature (`overview`, `repos`, `repo`, `jobs`,
  `search`, `wizards`, `drawer`, `settings`). Keep a feature's logic inside its slice.
- `src/app/shared/` — reusable presentational components (`state-ring`, `state-legend`,
  `live-console`).

## SignalR — Core job events

`realtime.service.ts` connects to the API hub `/hubs/arius` and exposes the
`Log` / `Progress` / `CostEstimate` / `Done` streams (Arius.Core archive/restore job events +
the cost-approval handshake via `Approve`). The wire contract is the API's hub + `api-models.ts` —
when a Core job event changes, update both ends. `proxy.conf.json` forwards `/api` and `/hubs`
(WebSocket) to the API in dev.

## Conventions / gotchas

- Components carry `data-testid` hooks — e2e selectors depend on them; don't rename casually.
- Tailwind v4 via `.postcssrc.json` (`@tailwindcss/postcss`), not a JS config plugin chain.

## Run & test

```bash
npm start                          # ng serve → http://localhost:4200 (needs the API on :5080)
npm run build                      # production bundle → dist/arius-web

# Unit tests (Karma/Jasmine) — Karma needs a Chrome binary on this machine:
CHROME_BIN="/Applications/Brave Browser.app/Contents/MacOS/Brave Browser" \
  ng test --browsers=ChromeHeadless

# E2e (Playwright, live full-stack against a real repo — see README for env/.env.example):
npm run e2e                        # default: read-only / non-destructive specs
ARIUS_E2E_WRITE=1 npm run e2e      # also the destructive @write specs (real archive/restore)
```
