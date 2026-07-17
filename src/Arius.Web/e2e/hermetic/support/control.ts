import { APIRequestContext } from '@playwright/test';

/** Thin client for the Arius.Api.FakeTestHost control endpoints (reached through the ng proxy at /api/testing). */
export class Control {
  constructor(private readonly request: APIRequestContext) {}

  async reset(): Promise<void> { await this.request.post('/api/testing/reset'); }

  async seedRepo(body: { alias?: string; defaultTier?: string } = {}): Promise<number> {
    const res = await this.request.post('/api/testing/seed-repo', { data: body });
    return (await res.json()).repoId as number;
  }

  async scenario(repoId: number, name: string, gated = false): Promise<void> {
    await this.request.post('/api/testing/scenario', { data: { repoId, name, gated } });
  }

  async release(repoId: number): Promise<void> { await this.request.post(`/api/testing/release/${repoId}`); }
}
