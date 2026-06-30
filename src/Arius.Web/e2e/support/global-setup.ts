import { request, type APIRequestContext } from '@playwright/test';
import * as signalR from '@microsoft/signalr';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { SCRATCH_PREFIX } from './scratch';

const API = 'http://localhost:5080';
const HUB = `${API}/hubs/arius`;

/**
 * Ensures a repository exists *and has at least one snapshot* before the suite runs. If the app DB
 * already has a populated repo (e.g. a reused dev DB), it's used as-is. Otherwise, when the
 * ARIUS_E2E_* env vars are present, an account + repository are seeded; and if the source container
 * is still empty, a small fixture tree is archived into it so the read-only specs (files,
 * statistics, search, time-travel) have real data to assert against. With no configuration and no
 * existing data, the run fails fast rather than passing against nothing.
 */
export default async function globalSetup() {
  const ctx = await request.newContext();

  // Defensive health wait (webServer normally already ensured this).
  for (let i = 0; i < 60; i++) {
    try { if ((await ctx.get(`${API}/api/health`)).ok()) break; } catch { /* not up yet */ }
    await new Promise(r => setTimeout(r, 1000));
  }

  // Purge leftover scratch repos from previous (possibly crashed) @write runs.
  const existing = await (await ctx.get(`${API}/api/repos`)).json();
  if (Array.isArray(existing)) {
    for (const r of existing.filter((x: { container: string }) => x.container.startsWith(SCRATCH_PREFIX)))
      await ctx.delete(`${API}/api/repos/${r.id}`);
  }

  // Ensure a repository exists (seed account + repo from env when the DB is empty).
  let repos = (await (await ctx.get(`${API}/api/repos`)).json()) as { id: number; container: string }[];
  if (!Array.isArray(repos) || repos.length === 0) {
    const account = process.env.ARIUS_E2E_ACCOUNT;
    const container = process.env.ARIUS_E2E_CONTAINER;
    if (!account || !container) {
      await ctx.dispose();
      throw new Error(
        'No repository configured. Set ARIUS_E2E_ACCOUNT / ARIUS_E2E_KEY / ARIUS_E2E_CONTAINER / ' +
        'ARIUS_E2E_PASSPHRASE, or pre-seed a repository in the app DB before running the e2e suite.',
      );
    }

    const created = await ctx.post(`${API}/api/accounts`, {
      data: { name: account, accountKey: process.env.ARIUS_E2E_KEY ?? null },
    });
    const accountId = (await created.json()).id;

    await ctx.post(`${API}/api/repos`, {
      data: {
        accountId,
        container,
        alias: process.env.ARIUS_E2E_ALIAS ?? container,
        passphrase: process.env.ARIUS_E2E_PASSPHRASE ?? null,
        localPath: '',
        defaultTier: process.env.ARIUS_E2E_TIER ?? 'hot',
      },
    });

    repos = (await (await ctx.get(`${API}/api/repos`)).json()) as { id: number; container: string }[];
  }

  // Identify the source repo the suite reads from (mirrors the `repo` fixture's selection), then
  // make sure it actually contains a snapshot — seeding one if the container is still empty.
  const wanted = process.env.ARIUS_E2E_CONTAINER;
  const source =
    repos.find(r => r.container === wanted) ??
    repos.find(r => !r.container.startsWith(SCRATCH_PREFIX)) ??
    repos[0];
  if (source) await seedSnapshotIfEmpty(ctx, source.id);

  await ctx.dispose();
}

/**
 * Archives a tiny fixture tree into the source repository when it has no snapshot yet, so the
 * read-only specs have folders, files and stats to assert against. Idempotent: a repo that already
 * has a snapshot (a reused dev DB, or a previous CI run against the same container) is left alone.
 */
async function seedSnapshotIfEmpty(ctx: APIRequestContext, repoId: number) {
  // A missing container makes the read-only snapshots endpoint 500 (ContainerNotFound); treat any
  // non-OK / non-array response as "no snapshot yet". The seed archive below (ReadWrite preflight)
  // creates the container.
  const res = await ctx.get(`${API}/api/repos/${repoId}/snapshots`);
  if (res.ok()) {
    const snapshots = await res.json();
    if (Array.isArray(snapshots) && snapshots.length > 0) return;
  }

  // Build a small on-disk tree (a root file + a subfolder so the file browser shows a folder node).
  const seedDir = fs.mkdtempSync(path.join(os.tmpdir(), 'arius-e2e-seed-'));
  fs.writeFileSync(path.join(seedDir, 'readme.txt'), `arius e2e seed ${Date.now()}\n`);
  fs.mkdirSync(path.join(seedDir, 'docs'));
  fs.writeFileSync(path.join(seedDir, 'docs', 'notes.md'), '# notes\n'.repeat(50));
  fs.writeFileSync(path.join(seedDir, 'docs', 'guide.txt'), 'arius e2e guide file\n');

  // Point the repo at the fixture and archive it through the hub (same path the UI uses).
  await ctx.patch(`${API}/api/repos/${repoId}`, { data: { localPath: seedDir } });

  // LongPolling avoids needing a WebSocket implementation in the Node setup process.
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(HUB, { transport: signalR.HttpTransportType.LongPolling })
    .build();
  await connection.start();
  let jobId: string;
  try {
    // StartArchive(repositoryId, tier, removeLocal, writePointers, fastHash)
    jobId = await connection.invoke<string>('StartArchive', repoId, 'hot', false, false, false);
  } finally {
    await connection.stop(); // the server job runs independently of the connection
  }

  // Wait for the archive job to finish (it writes the first snapshot).
  const deadline = Date.now() + 180_000;
  while (Date.now() < deadline) {
    const jobs = await (await ctx.get(`${API}/api/jobs`)).json();
    const job = Array.isArray(jobs) ? jobs.find((j: { id: string }) => j.id === jobId) : undefined;
    if (job?.status === 'completed') return;
    if (job?.status === 'failed') throw new Error(`Seed archive failed: ${job.detail ?? 'unknown error'}`);
    await new Promise(r => setTimeout(r, 2000));
  }
  throw new Error('Seed archive did not complete within 180s');
}
