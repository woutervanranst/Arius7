import type { Page } from '@playwright/test';

/** SignalR record separator (0x1e), used to frame messages on the wire. */
const RS = String.fromCharCode(0x1e);

interface MockEntry {
  relativePath: string;
  name: string;
  kind: 'file' | 'dir';
  state: number;
  stateFlags: {
    localPointer: boolean; localBinary: boolean; localDirectory: boolean;
    repository: boolean; repositoryHydrated: boolean; repositoryArchived: boolean; repositoryRehydrating: boolean;
  };
  contentHash: string | null;
  originalSize: number | null;
  created: string | null;
  modified: string | null;
}

const flags = (over: Partial<MockEntry['stateFlags']> = {}): MockEntry['stateFlags'] => ({
  localPointer: false, localBinary: true, localDirectory: false,
  repository: true, repositoryHydrated: true, repositoryArchived: false, repositoryRehydrating: false,
  ...over,
});

const file = (relativePath: string, name: string): MockEntry => ({
  relativePath, name, kind: 'file', state: 0, stateFlags: flags(),
  contentHash: 'hash-' + name, originalSize: 4096, created: null, modified: null,
});

const dir = (relativePath: string, name: string): MockEntry => ({
  relativePath, name, kind: 'dir', state: 0, stateFlags: flags(),
  contentHash: null, originalSize: null, created: null, modified: null,
});

/**
 * A small fixed repository (id 3) with a nested file `docs/guide.txt`, plus the search hit for it.
 * Mirrors the real DTO shapes (camelCase over the wire).
 */
export const REPO = { id: 3, alias: 'Mock Repo', container: 'mock-container', accountId: 1, account: 'mockacct', localPath: null, defaultTier: 'hot' };

const ENTRIES_BY_PREFIX: Record<string, MockEntry[]> = {
  '': [dir('docs', 'docs'), file('readme.txt', 'readme.txt')],
  'docs': [file('docs/guide.txt', 'guide.txt'), file('docs/notes.md', 'notes.md')],
};

const SEARCH_HIT = { repoId: REPO.id, repo: REPO.alias, entry: file('docs/guide.txt', 'guide.txt') };

/** Installs REST + SignalR network mocks so the real Angular app runs with no backend. */
export async function installMocks(page: Page): Promise<void> {
  // -- REST -------------------------------------------------------------------
  await page.route('**/api/health', route =>
    route.fulfill({ status: 200, headers: { 'X-Arius-Version': '0.0.0-mock' }, contentType: 'application/json', body: 'true' }));
  await page.route('**/api/jobs', route => route.fulfill({ json: [] }));
  await page.route('**/api/accounts', route => route.fulfill({ json: [] }));
  await page.route(/\/api\/repos\/\d+\/snapshots/, route => route.fulfill({ json: [] }));
  await page.route(/\/api\/repos\/\d+\/schedules/, route => route.fulfill({ json: [] }));
  await page.route(/\/api\/repos\/\d+\/stats.*/, route => route.fulfill({ json: { files: 0, originalSize: 0, deduplicatedSize: 0, storedSize: 0, uniqueChunks: 0, storedByTier: [] } }));
  await page.route(/\/api\/repos\/\d+$/, route => route.fulfill({ json: REPO }));
  await page.route('**/api/repos', route => route.fulfill({ json: [REPO] }));

  // -- SignalR hub ------------------------------------------------------------
  // Negotiate: advertise only WebSockets so the client opens a socket we intercept below.
  await page.route(/\/hubs\/arius\/negotiate/, route => route.fulfill({
    json: {
      negotiateVersion: 1,
      connectionId: 'mock-conn',
      connectionToken: 'mock-token',
      availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
    },
  }));

  await page.routeWebSocket(/\/hubs\/arius/, ws => {
    ws.onMessage(raw => {
      const text = typeof raw === 'string' ? raw : raw.toString();
      for (const frame of text.split(RS)) {
        if (!frame) continue;
        // Handshake request: {"protocol":"json","version":1} -> empty success response.
        if (frame.includes('"protocol"')) { ws.send('{}' + RS); continue; }

        let msg: { type: number; invocationId?: string; target?: string; arguments?: unknown[] };
        try { msg = JSON.parse(frame); } catch { continue; }
        if (msg.type !== 4) continue; // only StreamInvocation drives this app (SearchAll / StreamEntries)

        const id = msg.invocationId ?? '0';
        const items = streamItems(msg.target ?? '', msg.arguments ?? []);
        for (const item of items) ws.send(JSON.stringify({ type: 2, invocationId: id, item }) + RS);
        ws.send(JSON.stringify({ type: 3, invocationId: id }) + RS); // completion
      }
    });
  });
}

function streamItems(target: string, args: unknown[]): unknown[] {
  if (target === 'SearchAll') {
    const query = String(args[0] ?? '').toLowerCase();
    return 'docs/guide.txt'.includes(query) ? [SEARCH_HIT] : [];
  }
  if (target === 'StreamEntries') {
    // args: [repositoryId, version, prefix, filter, includeLocal]
    const prefix = (args[2] as string | null) ?? '';
    return ENTRIES_BY_PREFIX[prefix] ?? [];
  }
  return [];
}
