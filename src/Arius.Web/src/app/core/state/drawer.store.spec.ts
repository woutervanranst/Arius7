import '@angular/compiler';   // JIT fallback so partially-compiled Angular injectables load under Node Vitest
import { describe, expect, it, vi } from 'vitest';
import { Injector, runInInjectionContext } from '@angular/core';
import { DrawerStore } from './drawer.store';
import { RealtimeService } from '../api/realtime.service';
import { JobPillStore } from './job-pill.store';

// DrawerStore uses inject() in field initialisers, so build it inside an injection context wired to
// stub RealtimeService/JobPillStore — no Angular TestBed/DOM needed (this suite runs under Node Vitest).
function makeStore(realtime: Partial<RealtimeService> = {}, pill: Partial<JobPillStore> = {}) {
  const rt = { startArchive: vi.fn().mockResolvedValue('job'), startRestore: vi.fn().mockResolvedValue('job'), ...realtime } as unknown as RealtimeService;
  const pl = { show: vi.fn(), ...pill } as unknown as JobPillStore;
  const injector = Injector.create({ providers: [
    { provide: RealtimeService, useValue: rt },
    { provide: JobPillStore, useValue: pl },
  ] });
  const store = runInInjectionContext(injector, () => new DrawerStore());
  return { store, realtime: rt, pill: pl };
}

describe('DrawerStore open/close', () => {
  it('openProperties opens the properties drawer for a repo and clears any prior error', () => {
    const { store } = makeStore();
    store.error.set('boom');
    store.openProperties(5);
    expect(store.type()).toBe('properties');
    expect(store.repoId()).toBe(5);
    expect(store.error()).toBeNull();
  });

  it('openAccount targets an account drawer', () => {
    const { store } = makeStore();
    store.openAccount(3);
    expect(store.type()).toBe('account');
    expect(store.accountId()).toBe(3);
  });

  it('openArchive resets the archive form and defaults an empty tier to "archive"', () => {
    const { store } = makeStore();
    store.archiveOnDisk.set('replace');
    store.fastHash.set(true);
    store.openArchive(7, '');
    expect(store.type()).toBe('archive');
    expect(store.repoId()).toBe(7);
    expect(store.archiveTier()).toBe('archive');   // '' falls back
    expect(store.archiveOnDisk()).toBe('keep');
    expect(store.fastHash()).toBe(false);
  });

  it('openArchive keeps an explicitly chosen tier', () => {
    const { store } = makeStore();
    store.openArchive(7, 'hot');
    expect(store.archiveTier()).toBe('hot');
  });

  it('openRestore captures version + collected paths and resets the flags', () => {
    const { store } = makeStore();
    store.overwrite.set(true);
    store.restoreNoPointers.set(true);
    store.openRestore(2, 'v1', ['a', 'b']);
    expect(store.type()).toBe('restore');
    expect(store.repoId()).toBe(2);
    expect(store.version()).toBe('v1');
    expect(store.collectedPaths()).toEqual(['a', 'b']);
    expect(store.overwrite()).toBe(false);
    expect(store.restoreNoPointers()).toBe(false);
  });

  it('close clears the drawer type', () => {
    const { store } = makeStore();
    store.openProperties(1);
    store.close();
    expect(store.type()).toBeNull();
  });

  it('bumpAccounts increments the accounts revision', () => {
    const { store } = makeStore();
    expect(store.accountsRevision()).toBe(0);
    store.bumpAccounts();
    store.bumpAccounts();
    expect(store.accountsRevision()).toBe(2);
  });
});

describe('DrawerStore.start', () => {
  it('starts an archive with the mapped on-disk flags, hands off to the pill, and dismisses', async () => {
    const { store, realtime, pill } = makeStore({ startArchive: vi.fn().mockResolvedValue('job-a') });
    store.openArchive(4, 'cool');
    store.archiveOnDisk.set('replace');   // replace → removeLocal + writePointers
    await store.start();
    expect(realtime.startArchive).toHaveBeenCalledWith(4, { tier: 'cool', removeLocal: true, writePointers: true, fastHash: false });
    expect(pill.show).toHaveBeenCalledWith('job-a', 'archive');
    expect(store.type()).toBeNull();
  });

  it('keep-pointers writes pointers without removing the local files', async () => {
    const { store, realtime } = makeStore({ startArchive: vi.fn().mockResolvedValue('job-a') });
    store.openArchive(4, 'hot');
    store.archiveOnDisk.set('keep-pointers');
    await store.start();
    expect(realtime.startArchive).toHaveBeenCalledWith(4, expect.objectContaining({ removeLocal: false, writePointers: true }));
  });

  it('starts a restore with the captured options and hands off to the pill', async () => {
    const { store, realtime, pill } = makeStore({ startRestore: vi.fn().mockResolvedValue('job-r') });
    store.openRestore(9, 'v2', ['x']);
    store.overwrite.set(true);
    await store.start();
    expect(realtime.startRestore).toHaveBeenCalledWith(9, { version: 'v2', targetPaths: ['x'], overwrite: true, noPointers: false });
    expect(pill.show).toHaveBeenCalledWith('job-r', 'restore');
    expect(store.type()).toBeNull();
  });

  it('surfaces a start failure as an inline error and leaves the drawer open', async () => {
    const { store, pill } = makeStore({ startArchive: vi.fn().mockRejectedValue(new Error('A job is already running')) });
    store.openArchive(4, 'hot');
    await store.start();
    expect(store.error()).toBe('A job is already running');
    expect(pill.show).not.toHaveBeenCalled();
    expect(store.type()).toBe('archive');   // NOT dismissed
  });
});
