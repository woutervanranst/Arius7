import '@angular/compiler';   // JIT fallback so partially-compiled Angular injectables load under Node Vitest
import { describe, expect, it, vi } from 'vitest';
import { Injector, runInInjectionContext } from '@angular/core';
import { of, Subject } from 'rxjs';
import { JobPillStore } from './job-pill.store';
import { ApiService } from '../api/api.service';
import { RealtimeService } from '../api/realtime.service';
import { JobAttachState, JobSnapshot } from '../api/api-models';

function snap(p: Partial<JobSnapshot> = {}): JobSnapshot {
  return {
    jobId: 'j1', phase: 'x', status: 'running', totalBytes: 0, totalNewBytes: 0, scannedBytes: 0, scannedFiles: 0, hashedBytes: 0,
    uploadedBytes: 0, dedupedBytes: 0, dedupedFiles: 0, etaSeconds: null, throughputBytesPerSec: 0, etaIsUpperBound: false,
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
  const ap = { getJobs: vi.fn().mockReturnValue(of([])), ...api } as unknown as ApiService;
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
