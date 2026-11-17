import '@angular/compiler';   // JIT fallback so partially-compiled Angular injectables load under Node Vitest
import { describe, expect, it, vi } from 'vitest';
import { Injector, runInInjectionContext } from '@angular/core';
import { of, Subject } from 'rxjs';
import { JobPillStore } from './job-pill.store';
import { ApiService } from '../api/api.service';
import { RealtimeService } from '../api/realtime.service';
import { DownloadFileDto, JobAttachState, JobSnapshot, PendingDownloadDto } from '../api/api-models';

function snap(p: Partial<JobSnapshot> = {}): JobSnapshot {
  return {
    jobId: 'j1', phase: 'x', status: 'running', totalBytes: 0, totalNewBytes: 0, scannedBytes: 0, scannedFiles: 0, hashedBytes: 0,
    uploadedBytes: 0, dedupedBytes: 0, dedupedFiles: 0, etaSeconds: null, throughputBytesPerSec: 0,
    hashThroughputBytesPerSec: 0, uploadThroughputBytesPerSec: 0, etaIsProvisional: false,
    pct: 0, warningCount: 0, stats: {}, restoreTotalFiles: 0, filesRestored: 0, restoreTotalBytes: 0,
    bytesRestored: 0, chunksAvailable: 0, chunksRehydrated: 0, chunksNeedingRehydration: 0,
    chunksPending: 0, chunksTotal: 0, ...p,
  };
}

// JobPillStore uses inject() in field initialisers → build it inside an injection context with stubs.
// attachToJob defaults to a never-resolving promise so the reattach reconciliation only runs in the
// tests that opt into it (by resolving it); progress/done are Subjects the test pushes through.
function makeStore(api: Partial<ApiService> = {}, realtime: Partial<RealtimeService> = {}) {
  const progress = new Subject<JobSnapshot>();
  const done = new Subject<{ status: string }>();
  const rt = {
    attachToJob: vi.fn().mockReturnValue(new Promise<JobAttachState | null>(() => {})),
    detachFromJob: vi.fn().mockResolvedValue(undefined),
    jobProgress: vi.fn().mockReturnValue(progress),
    jobDone: vi.fn().mockReturnValue(done),
    ...realtime,
  } as unknown as RealtimeService;
  const ap = {
    getJobs: vi.fn().mockReturnValue(of([])),
    getPendingDownload: vi.fn().mockReturnValue(of(null)),
    getDownload: vi.fn().mockReturnValue(of({ files: [], cleanedUp: false, cleanupAt: null })),
    ...api,
  } as unknown as ApiService;
  const injector = Injector.create({ providers: [
    { provide: ApiService, useValue: ap },
    { provide: RealtimeService, useValue: rt },
  ] });
  const store = runInInjectionContext(injector, () => new JobPillStore());
  return { store, api: ap, realtime: rt, progress, done };
}

describe('JobPillStore visibility', () => {
  it('show attaches a job and makes the pill visible', () => {
    const { store, realtime } = makeStore();
    store.show('j1', 'restore');
    expect(store.jobId()).toBe('j1');
    expect(store.kind()).toBe('restore');
    expect(store.status()).toBe('running');
    expect(store.visible()).toBe(true);
    expect(realtime.attachToJob).toHaveBeenCalledWith('j1');
  });

  it('dismiss hides the pill without dropping the job', () => {
    const { store } = makeStore();
    store.show('j1', 'archive');
    store.dismiss();
    expect(store.visible()).toBe(false);
    expect(store.jobId()).toBe('j1');
  });

  it('detach drops the job and tears down the realtime attachment', () => {
    const { store, realtime } = makeStore();
    store.show('j1', 'archive');
    store.detach();
    expect(realtime.detachFromJob).toHaveBeenCalledWith('j1');
    expect(store.jobId()).toBeNull();
    expect(store.visible()).toBe(false);
  });
});

describe('JobPillStore live stream', () => {
  it('follows the status carried by progress snapshots, so running becomes rehydrating without a reattach', () => {
    const { store, progress } = makeStore();
    store.show('j1', 'restore');
    expect(store.status()).toBe('running');
    progress.next(snap({ status: 'rehydrating' }));
    expect(store.status()).toBe('rehydrating');
  });

  it('streams live progress into the snapshot', () => {
    const { store, progress } = makeStore();
    store.show('j1', 'archive');
    const s = snap({ jobId: 'j1', pct: 42 });
    progress.next(s);
    expect(store.snapshot()).toEqual(s);
  });

  it('a done event sets the terminal status, then auto-hides after the grace period', () => {
    vi.useFakeTimers();
    try {
      const { store, done } = makeStore();
      store.show('j1', 'archive');
      done.next({ status: 'completed' });
      expect(store.status()).toBe('completed');
      expect(store.jobId()).toBe('j1');       // still shown briefly
      vi.advanceTimersByTime(4000);
      expect(store.jobId()).toBeNull();        // auto-hidden
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('JobPillStore reattach reconciliation', () => {
  it('adopts the reattached snapshot + status for a still-live job', async () => {
    const state: JobAttachState = { status: 'rehydrating', snapshot: snap({ jobId: 'j1' }), cost: null, warningCount: 0, resume: null };
    const { store } = makeStore({}, { attachToJob: vi.fn().mockResolvedValue(state) });
    store.show('j1', 'restore');
    await Promise.resolve(); await Promise.resolve();   // flush the attachToJob then-callback
    expect(store.snapshot()).toEqual(state.snapshot);
    expect(store.status()).toBe('rehydrating');
  });

  it('marks the pill failed when the job no longer exists at attach time', async () => {
    const { store } = makeStore({}, { attachToJob: vi.fn().mockResolvedValue(null) });
    store.show('j1', 'restore');
    await Promise.resolve(); await Promise.resolve();
    expect(store.status()).toBe('failed');
  });
});

describe('JobPillStore.discover', () => {
  it('attaches the repository\'s active job', () => {
    const getJobs = vi.fn().mockReturnValue(of([{ id: 'j9', kind: 'restore', status: 'running' }]));
    const { store, api } = makeStore({ getJobs });
    store.discover(11);
    expect(api.getJobs).toHaveBeenCalledWith({ repositoryId: 11, status: 'active' });
    expect(store.jobId()).toBe('j9');
    expect(store.kind()).toBe('restore');
  });

  it('leaves the pill hidden when the repository has no active job', () => {
    const { store } = makeStore({ getJobs: vi.fn().mockReturnValue(of([])) });
    store.discover(11);
    expect(store.jobId()).toBeNull();
    expect(store.visible()).toBe(false);
  });

  it('does not re-poll a repo it is already showing a job for', () => {
    const getJobs = vi.fn().mockReturnValue(of([{ id: 'j9', kind: 'archive', status: 'running' }]));
    const { store, api } = makeStore({ getJobs });
    store.discover(11);
    store.discover(11);   // revisit while a pill is live → no second poll
    expect(api.getJobs).toHaveBeenCalledTimes(1);
  });
});


// localStorage does not exist in the node test environment: stub a Map-backed one.
function stubLocalStorage(): Map<string, string> {
  const m = new Map<string, string>();
  vi.stubGlobal('localStorage', {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  });
  return m;
}

const pending = (p: Partial<PendingDownloadDto> = {}): PendingDownloadDto =>
  ({ jobId: 'd1', readyFiles: 3, totalFiles: 3, remainingFiles: 2, remainingBytes: 1200, ...p });

const file = (path: string, ready: boolean, downloaded = false, size = 100): DownloadFileDto => ({ path, size, ready, downloaded });

describe('JobPillStore download states', () => {
  it('with no active job, shows the latest finished download with files left', () => {
    stubLocalStorage();
    try {
      const { store, api } = makeStore({ getPendingDownload: vi.fn().mockReturnValue(of(pending())) });
      store.discover(11);
      expect(api.getPendingDownload).toHaveBeenCalledWith(11);
      expect(store.visible()).toBe(true);
      expect(store.jobId()).toBe('d1');
      expect(store.kind()).toBe('download');
      expect(store.mode()).toBe('download-ready');
      expect(store.download()).toMatchObject({ total: 3, ready: 3, remaining: 2, remainingBytes: 1200 });
    } finally { vi.unstubAllGlobals(); }
  });

  it('an active job wins over a pending download', () => {
    stubLocalStorage();
    try {
      const getPendingDownload = vi.fn().mockReturnValue(of(pending()));
      const getJobs = vi.fn().mockReturnValue(of([{ id: 'j9', kind: 'download', status: 'running' }]));
      const { store } = makeStore({ getJobs, getPendingDownload });
      store.discover(11);
      expect(store.jobId()).toBe('j9');
      expect(store.kind()).toBe('download');
      expect(store.mode()).toBe('progress');
      expect(getPendingDownload).not.toHaveBeenCalled();
    } finally { vi.unstubAllGlobals(); }
  });

  it('a dismissed download-ready pill stays hidden for that job when discovered again', () => {
    stubLocalStorage();
    try {
      const getPendingDownload = vi.fn().mockReturnValue(of(pending()));
      const first = makeStore({ getPendingDownload });
      first.store.discover(11);
      first.store.dismiss();
      expect(first.store.visible()).toBe(false);

      const second = makeStore({ getPendingDownload });   // a later visit, same browser
      second.store.discover(11);
      expect(second.store.visible()).toBe(false);
      expect(second.store.jobId()).toBeNull();

      const other = makeStore({ getPendingDownload: vi.fn().mockReturnValue(of(pending({ jobId: 'd2' }))) });
      other.store.discover(11);                           // a different job is not affected
      expect(other.store.visible()).toBe(true);
    } finally { vi.unstubAllGlobals(); }
  });

  it('dismiss still works when localStorage is unavailable', () => {
    const { store } = makeStore({ getPendingDownload: vi.fn().mockReturnValue(of(pending())) });
    store.discover(11);
    expect(() => store.dismiss()).not.toThrow();
    expect(store.visible()).toBe(false);
  });

  it('a finished download job switches to download-ready instead of hiding', () => {
    vi.useFakeTimers();
    stubLocalStorage();
    try {
      const getPendingDownload = vi.fn().mockReturnValueOnce(of(null)).mockReturnValue(of(pending({ jobId: 'd1' })));
      const { store, done, api } = makeStore({ getPendingDownload });
      store.discover(11);          // nothing pending yet; a running download arrives via the drawer hand-off
      store.show('d1', 'download');
      expect(store.mode()).toBe('progress');
      done.next({ status: 'completed' });
      expect(api.getPendingDownload).toHaveBeenCalledWith(11);
      vi.advanceTimersByTime(4000);
      expect(store.jobId()).toBe('d1');
      expect(store.mode()).toBe('download-ready');
    } finally { vi.useRealTimers(); vi.unstubAllGlobals(); }
  });

  it('a finished download with nothing left to download auto-hides like other jobs', () => {
    vi.useFakeTimers();
    stubLocalStorage();
    try {
      const { store, done } = makeStore();
      store.discover(11);
      store.show('d1', 'download');
      done.next({ status: 'completed' });
      vi.advanceTimersByTime(4000);
      expect(store.jobId()).toBeNull();
    } finally { vi.useRealTimers(); vi.unstubAllGlobals(); }
  });

  it('archive and restore jobs still auto-hide without a pending-download lookup', () => {
    vi.useFakeTimers();
    try {
      const { store, done, api } = makeStore();
      store.discover(11);
      (api.getPendingDownload as ReturnType<typeof vi.fn>).mockClear();
      store.show('r1', 'restore');
      done.next({ status: 'completed' });
      vi.advanceTimersByTime(4000);
      expect(store.jobId()).toBeNull();
      expect(api.getPendingDownload).not.toHaveBeenCalled();
    } finally { vi.useRealTimers(); }
  });

  it('a rehydrating download reports how many files are ready', async () => {
    const files = [file('a', true), file('b', true), file('c', false)];
    const state: JobAttachState = {
      status: 'rehydrating', snapshot: snap({ jobId: 'd1', filesRestored: 2 }), cost: null, warningCount: 0,
      resume: { autoResume: true, priority: 'Standard', rehydrationStartedAt: '2026-11-16T10:00:00Z', rehydrationWindowHours: 15, lastCheckedAt: '2026-11-16T10:00:00Z' },
    };
    const { store, progress, api } = makeStore(
      { getDownload: vi.fn().mockReturnValue(of({ files, cleanedUp: false, cleanupAt: null })) },
      { attachToJob: vi.fn().mockResolvedValue(state) });
    store.show('d1', 'download');
    await Promise.resolve(); await Promise.resolve();
    expect(api.getDownload).toHaveBeenCalledWith('d1');
    expect(store.mode()).toBe('progress');
    expect(store.download()).toMatchObject({ total: 3, ready: 2 });
    expect(store.resume()).toEqual(state.resume);

    // more files restored → the summary is reloaded
    (api.getDownload as ReturnType<typeof vi.fn>).mockReturnValue(of({ files: files.map(f => ({ ...f, ready: true })), cleanedUp: false, cleanupAt: null }));
    progress.next(snap({ jobId: 'd1', filesRestored: 3 }));
    expect(store.download()).toMatchObject({ total: 3, ready: 3 });
  });

  it('downloading from a download-ready pill hides it without remembering a dismissal', () => {
    vi.useFakeTimers();
    const ls = stubLocalStorage();
    try {
      const { store } = makeStore({ getPendingDownload: vi.fn().mockReturnValue(of(pending())) });
      store.discover(11);
      store.markDownloaded();
      expect(store.visible()).toBe(true);        // deferred so the clicked link's default download still runs
      vi.advanceTimersByTime(0);
      expect(store.visible()).toBe(false);
      expect(ls.size).toBe(0);
    } finally { vi.useRealTimers(); vi.unstubAllGlobals(); }
  });

  it('downloading from a rehydrating pill treats the ready files as downloaded until more become ready', async () => {
    vi.useFakeTimers();
    try {
      const files = [file('a', true, false, 100), file('b', true, false, 50), file('c', false)];
      const getDownload = vi.fn().mockReturnValue(of({ files, cleanedUp: false, cleanupAt: null }));
      const { store, progress } = makeStore({ getDownload });
      store.show('d1', 'download');
      expect(store.download()).toMatchObject({ ready: 2, remaining: 2, remainingBytes: 150 });
      store.markDownloaded();
      expect(store.download()!.remaining).toBe(2);
      vi.advanceTimersByTime(0);
      expect(store.download()).toMatchObject({ ready: 2, downloaded: 2, remaining: 0, remainingBytes: 0 });
      expect(store.visible()).toBe(true);

      getDownload.mockReturnValue(of({ files: [...files.slice(0, 2).map(f => ({ ...f, downloaded: true })), file('c', true, false, 70)], cleanedUp: false, cleanupAt: null }));
      progress.next(snap({ jobId: 'd1', filesRestored: 3 }));
      expect(store.download()).toMatchObject({ ready: 3, remaining: 1, remainingBytes: 70 });
    } finally { vi.useRealTimers(); }
  });
});
