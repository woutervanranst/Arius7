import '@angular/compiler';   // JIT fallback so partially-compiled Angular injectables load under Node Vitest
import { describe, expect, it, vi } from 'vitest';
import { Injector, runInInjectionContext } from '@angular/core';
import { Observable, Subject, of, throwError } from 'rxjs';
import { DrawerStore } from './drawer.store';
import { RealtimeService } from '../api/realtime.service';
import { JobPillStore } from './job-pill.store';
import { ApiService } from '../api/api.service';
import { RepositoryDefaultsDto } from '../api/api-models';
import { DefaultValues, SetValues } from '../defaults/defaults';

// DrawerStore uses inject() in field initialisers, so build it inside an injection context wired to
// stub RealtimeService/JobPillStore — no Angular TestBed/DOM needed (this suite runs under Node Vitest).
const FACTORY: DefaultValues = { tier: 'archive', onDisk: 'keep', fastHash: false, overwrite: false, noPointers: false };
const FACTORY_RESOLVED = Object.fromEntries(Object.entries(FACTORY).map(([k, value]) => [k, { value, source: 'factory' }]));

/** A defaults response: `set` values on the repository, everything else inherited from factory. */
function dto(set: SetValues): RepositoryDefaultsDto {
  return { set, inherited: FACTORY_RESOLVED } as unknown as RepositoryDefaultsDto;
}

function makeStore(realtime: Partial<RealtimeService> = {}, pill: Partial<JobPillStore> = {}, defaults: Observable<RepositoryDefaultsDto> = of(dto({}))) {
  const rt = { startArchive: vi.fn().mockResolvedValue('job'), startRestore: vi.fn().mockResolvedValue('job'), ...realtime } as unknown as RealtimeService;
  const pl = { show: vi.fn(), ...pill } as unknown as JobPillStore;
  const api = { getRepositoryDefaults: vi.fn().mockReturnValue(defaults) };
  const injector = Injector.create({ providers: [
    { provide: RealtimeService, useValue: rt },
    { provide: JobPillStore, useValue: pl },
    { provide: ApiService, useValue: api },
  ] });
  const store = runInInjectionContext(injector, () => new DrawerStore());
  return { store, realtime: rt, pill: pl, api };
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

  it('openArchive pre-fills run from the repository\'s resolved defaults', () => {
    const { store } = makeStore({}, {}, of(dto({ tier: 'cool', onDisk: 'replace' })));
    store.openArchive(7);
    expect(store.type()).toBe('archive');
    expect(store.repoId()).toBe(7);
    expect(store.run()).toEqual({ ...FACTORY, tier: 'cool', onDisk: 'replace' });
    expect(store.resolved()!.tier).toEqual({ value: 'cool', source: 'repository' });
    expect(store.resolved()!.fastHash).toEqual({ value: false, source: 'factory' });
  });

  it('a defaults response for a previously opened repository is ignored', () => {
    const a = new Subject<RepositoryDefaultsDto>();
    const b = new Subject<RepositoryDefaultsDto>();
    const { store, api } = makeStore();
    api.getRepositoryDefaults.mockImplementation((id: number) => (id === 1 ? a : b));
    store.openArchive(1);
    store.openArchive(2);
    b.next(dto({ tier: 'hot' }));
    a.next(dto({ tier: 'cold' }));
    expect(store.run()!.tier).toBe('hot');
  });

  it('a failed defaults load surfaces an error and leaves run empty', () => {
    const { store } = makeStore({}, {}, throwError(() => new Error('x')));
    store.openArchive(1);
    expect(store.error()).toBe('Could not load this repository\'s defaults.');
    expect(store.run()).toBeNull();
  });

  it('pick changes only the run value, resetOption restores the resolved value', () => {
    const { store } = makeStore({}, {}, of(dto({ tier: 'cool' })));
    store.openArchive(1);
    store.pick('tier', 'hot');
    expect(store.run()!.tier).toBe('hot');
    expect(store.resolved()!.tier.value).toBe('cool');
    store.resetOption('tier');
    expect(store.run()!.tier).toBe('cool');
  });

  it('openRestore captures version + collected paths and pre-fills overwrite and noPointers', () => {
    const { store } = makeStore({}, {}, of(dto({ overwrite: true, noPointers: true })));
    store.openRestore(2, 'v1', ['a', 'b']);
    expect(store.type()).toBe('restore');
    expect(store.repoId()).toBe(2);
    expect(store.version()).toBe('v1');
    expect(store.collectedPaths()).toEqual(['a', 'b']);
    expect(store.run()!.overwrite).toBe(true);
    expect(store.run()!.noPointers).toBe(true);
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
  it('start before defaults load sends nothing', async () => {
    const { store, realtime } = makeStore({}, {}, new Subject<RepositoryDefaultsDto>());
    store.openArchive(4);
    await store.start();
    expect(realtime.startArchive).not.toHaveBeenCalled();
    expect(store.type()).toBe('archive');
  });

  it('start maps replace to removeLocal and writePointers, hands off to the pill, and dismisses', async () => {
    const { store, realtime, pill } = makeStore({ startArchive: vi.fn().mockResolvedValue('job-a') }, {}, of(dto({ tier: 'cool' })));
    store.openArchive(4);
    store.pick('onDisk', 'replace');
    await store.start();
    expect(realtime.startArchive).toHaveBeenCalledWith(4, { tier: 'cool', removeLocal: true, writePointers: true, fastHash: false });
    expect(pill.show).toHaveBeenCalledWith('job-a', 'archive');
    expect(store.type()).toBeNull();
  });

  it('keep-pointers writes pointers without removing the local files', async () => {
    const { store, realtime } = makeStore({ startArchive: vi.fn().mockResolvedValue('job-a') });
    store.openArchive(4);
    store.pick('onDisk', 'keep-pointers');
    await store.start();
    expect(realtime.startArchive).toHaveBeenCalledWith(4, expect.objectContaining({ removeLocal: false, writePointers: true }));
  });

  it('starts a restore with the captured options and hands off to the pill', async () => {
    const { store, realtime, pill } = makeStore({ startRestore: vi.fn().mockResolvedValue('job-r') });
    store.openRestore(9, 'v2', ['x']);
    store.pick('overwrite', true);
    await store.start();
    expect(realtime.startRestore).toHaveBeenCalledWith(9, { version: 'v2', targetPaths: ['x'], overwrite: true, noPointers: false });
    expect(pill.show).toHaveBeenCalledWith('job-r', 'restore');
    expect(store.type()).toBeNull();
  });

  it('surfaces a start failure as an inline error and leaves the drawer open', async () => {
    const { store, pill } = makeStore({ startArchive: vi.fn().mockRejectedValue(new Error('A job is already running')) });
    store.openArchive(4);
    await store.start();
    expect(store.error()).toBe('A job is already running');
    expect(pill.show).not.toHaveBeenCalled();
    expect(store.type()).toBe('archive');   // NOT dismissed
  });
});
