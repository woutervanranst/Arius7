import { describe, expect, it } from 'vitest';
import { reattachEmissions } from './realtime.service';
import { CostEstimateMsg, JobAttachState, JobSnapshot } from './api-models';

function snapshot(jobId: string): JobSnapshot {
  return {
    jobId, phase: 'restore', status: 'awaiting-cost', totalBytes: 0, totalNewBytes: 0, scannedBytes: 0, scannedFiles: 0, hashedBytes: 0,
    uploadedBytes: 0, dedupedBytes: 0, dedupedFiles: 0, etaSeconds: null, throughputBytesPerSec: 0,
    pct: 0, warningCount: 0, stats: {}, restoreTotalFiles: 0, filesRestored: 0, restoreTotalBytes: 0,
    bytesRestored: 0, chunksAvailable: 0, chunksRehydrated: 0, chunksNeedingRehydration: 0,
    chunksPending: 0, chunksTotal: 0,
  };
}

const cost: CostEstimateMsg = {
  jobId: 'j1', chunksAvailable: 3, chunksNeedingRehydration: 2, bytesNeedingRehydration: 1200,
  downloadBytes: 3000, totalStandard: 0.71, totalHigh: 4.31, standardWaitHours: 15, highWaitHours: 1,
};

describe('reattachEmissions', () => {
  it('re-emits progress and the cost estimate for a non-terminal job', () => {
    const state: JobAttachState = { status: 'awaiting-cost', snapshot: snapshot('j1'), cost, warningCount: 0, resume: null };

    const out = reattachEmissions('j1', state);

    expect(out.snapshot).toEqual(snapshot('j1'));
    expect(out.cost).toEqual(cost);
    expect(out.done).toBeUndefined();
  });

  it('emits done, not cost, for a terminal job', () => {
    const state: JobAttachState = { status: 'completed', snapshot: snapshot('j1'), cost: null, warningCount: 0, resume: null };

    const out = reattachEmissions('j1', state);

    expect(out.done?.status).toBe('completed');
    expect(out.cost).toBeUndefined();
  });

  it('emits nothing for a null reattach state', () => {
    expect(reattachEmissions('j1', null)).toEqual({});
  });
});
