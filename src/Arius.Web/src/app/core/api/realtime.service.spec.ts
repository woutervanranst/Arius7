import { describe, expect, it } from 'vitest';
import { RealtimeService } from './realtime.service';
import { CostEstimateMsg, JobAttachState, JobSnapshot } from './api-models';

function snapshot(jobId: string): JobSnapshot {
  return {
    jobId, phase: 'restore', totalBytes: 0, totalNewBytes: 0, scannedBytes: 0, hashedBytes: 0,
    uploadedBytes: 0, dedupedBytes: 0, dedupedFiles: 0, etaSeconds: null, throughputBytesPerSec: 0,
    pct: 0, warningCount: 0, stats: {}, restoreTotalFiles: 0, filesRestored: 0, restoreTotalBytes: 0,
    bytesRestored: 0, chunksAvailable: 0, chunksRehydrated: 0, chunksNeedingRehydration: 0,
    chunksPending: 0, chunksTotal: 0, chunkBytesTotal: 0,
  };
}

const cost: CostEstimateMsg = {
  jobId: 'j1', chunksAvailable: 3, chunksNeedingRehydration: 2, bytesNeedingRehydration: 1200,
  downloadBytes: 3000, totalStandard: 0.71, totalHigh: 4.31, standardWaitHours: 15, highWaitHours: 1,
};

describe('RealtimeService.forwardReattach', () => {
  it('re-emits the cost estimate for a non-terminal job', () => {
    const svc = new RealtimeService();
    const costs: CostEstimateMsg[] = [];
    const snaps: JobSnapshot[] = [];
    svc.cost$.subscribe(c => costs.push(c));
    svc.progress$.subscribe(s => snaps.push(s));

    const state: JobAttachState = { status: 'awaiting-cost', snapshot: snapshot('j1'), cost, warningCount: 0, resume: null };
    (svc as unknown as { forwardReattach(id: string, s: JobAttachState | null): void }).forwardReattach('j1', state);

    expect(snaps).toHaveLength(1);
    expect(costs).toEqual([cost]);
  });

  it('emits done and not cost for a terminal job', () => {
    const svc = new RealtimeService();
    const costs: CostEstimateMsg[] = [];
    const dones: string[] = [];
    svc.cost$.subscribe(c => costs.push(c));
    svc.done$.subscribe(d => dones.push(d.status));

    const state: JobAttachState = { status: 'completed', snapshot: snapshot('j1'), cost: null, warningCount: 0, resume: null };
    (svc as unknown as { forwardReattach(id: string, s: JobAttachState | null): void }).forwardReattach('j1', state);

    expect(dones).toEqual(['completed']);
    expect(costs).toEqual([]);
  });
});
