import { request } from '@playwright/test';

/** Wait for the scripted Arius.Api.FakeTestHost host to become healthy on :5080 (no Azure seeding). */
export default async function globalSetup(): Promise<void> {
  const ctx = await request.newContext({ baseURL: 'http://localhost:5080' });
  try {
    for (let i = 0; i < 60; i++) {
      try {
        if ((await ctx.get('/api/health')).ok()) return;
      } catch {
        /* host not up yet */
      }
      await new Promise(r => setTimeout(r, 1000));
    }
    throw new Error('Arius.Api.FakeTestHost did not become healthy on :5080');
  } finally {
    await ctx.dispose();
  }
}
