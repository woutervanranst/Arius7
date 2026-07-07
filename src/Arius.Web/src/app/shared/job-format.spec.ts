import { archiveBarLayers, restoreBarLayers, phaseSentence, resolveRehydrationWindowHours } from './job-format';
import { CostEstimateMsg, JobSnapshot, ResumeInfo } from '../core/api/api-models';

function snap(p: Partial<JobSnapshot>): JobSnapshot {
  return {
    jobId: 'j', phase: 'x', status: 'running', totalBytes: 0, totalNewBytes: 0, scannedBytes: 0, hashedBytes: 0,
    uploadedBytes: 0, dedupedBytes: 0, dedupedFiles: 0, etaSeconds: null, throughputBytesPerSec: 0,
    pct: 0, warningCount: 0, stats: {}, restoreTotalFiles: 0, filesRestored: 0, restoreTotalBytes: 0,
    bytesRestored: 0, chunksAvailable: 0, chunksRehydrated: 0, chunksNeedingRehydration: 0,
    chunksPending: 0, chunksTotal: 0, ...p,
  };
}

describe('archiveBarLayers', () => {
  it('divides all three layers by totalBytes so uploaded never overtakes hashed', () => {
    const l = archiveBarLayers(snap({ totalBytes: 1000, scannedBytes: 1000, hashedBytes: 1000, uploadedBytes: 400, totalNewBytes: 500 }));
    expect(l.scanned).toBe(100);
    expect(l.middle).toBe(100);
    expect(l.top).toBe(40);          // 400/1000, NOT 400/500=80 (the old inconsistent denominator)
    expect(l.top).toBeLessThanOrEqual(l.middle);
  });
});

describe('restoreBarLayers', () => {
  it('uses the authoritative chunksTotal (including needs-rehydration) as the hydration denominator', () => {
    const l = restoreBarLayers(snap({ chunksTotal: 427, chunksAvailable: 145, chunksRehydrated: 0, chunksNeedingRehydration: 282, chunksPending: 0, restoreTotalBytes: 1000, bytesRestored: 250 }));
    expect(Math.round(l.middle)).toBe(34);   // 145/427, NOT 145/145=100 (old subset-sum omitted needs-rehydration)
    expect(l.top).toBe(25);
  });
});

describe('phaseSentence', () => {
  it('says estimating while still hashing', () => {
    expect(phaseSentence(snap({ totalBytes: 1000, hashedBytes: 400 }), 'archive')).toContain('estimating');
  });
  it('says "no new data" for a fully-deduped archive once hashing is done', () => {
    expect(phaseSentence(snap({ totalBytes: 1000, hashedBytes: 1000, totalNewBytes: 0 }), 'archive')).toContain('No new data');
  });
  it('shows the upload sentence when there is new data', () => {
    expect(phaseSentence(snap({ totalBytes: 1000, hashedBytes: 1000, totalNewBytes: 3_110_000_000, uploadedBytes: 1_680_000_000 }), 'archive')).toContain('Uploading');
  });
});

describe('resolveRehydrationWindowHours', () => {
  const cost = (p: Partial<CostEstimateMsg>): CostEstimateMsg => ({ jobId: 'j', chunksAvailable: 0, chunksNeedingRehydration: 0, bytesNeedingRehydration: 0, downloadBytes: 0, totalStandard: 0, totalHigh: 0, standardWaitHours: 15, highWaitHours: 1, ...p });
  const resume = (p: Partial<ResumeInfo>): ResumeInfo => ({ autoResume: true, rehydrationStartedAt: '2026-01-01T00:00:00Z', rehydrationWindowHours: 15, ...p });

  it('prefers the live cost estimate when present (priority-aware)', () => {
    expect(resolveRehydrationWindowHours(cost({ highWaitHours: 1 }), null, 'high')).toBe(1);
    expect(resolveRehydrationWindowHours(cost({ standardWaitHours: 15 }), null, 'standard')).toBe(15);
  });
  it('falls back to the persisted resume window when cost is null (rehydrating past approval)', () => {
    expect(resolveRehydrationWindowHours(null, resume({ rehydrationWindowHours: 15 }), 'standard')).toBe(15);
  });
  it('returns null when neither is available', () => {
    expect(resolveRehydrationWindowHours(null, null, 'high')).toBeNull();
  });
});
