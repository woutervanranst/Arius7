import { archiveBarLayers, restoreBarLayers, phaseSentence, resolveRehydrationWindowHours,
  formatEta, formatDuration, formatThroughput, hydratedByLabel, statusMeta } from './job-format';
import { CostEstimateMsg, JobSnapshot, ResumeInfo } from '../core/api/api-models';

function snap(p: Partial<JobSnapshot>): JobSnapshot {
  return {
    jobId: 'j', phase: 'x', status: 'running', totalBytes: 0, totalNewBytes: 0, scannedBytes: 0, scannedFiles: 0, hashedBytes: 0,
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
    expect(l.top).toBe(40);          // 400/1000, NOT 400/500=80
    expect(l.top).toBeLessThanOrEqual(l.middle);
  });
});

describe('restoreBarLayers', () => {
  it('uses the authoritative chunksTotal (including needs-rehydration) as the hydration denominator', () => {
    const l = restoreBarLayers(snap({ chunksTotal: 427, chunksAvailable: 145, chunksRehydrated: 0, chunksNeedingRehydration: 282, chunksPending: 0, restoreTotalBytes: 1000, bytesRestored: 250 }));
    expect(Math.round(l.middle)).toBe(34);   // 145/427, NOT 145/145=100
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
  it('reports rehydration for a restore whose archive-tier chunks have not started downloading', () => {
    expect(phaseSentence(snap({ chunksNeedingRehydration: 5, bytesRestored: 0 }), 'restore')).toBe('Rehydrating — 5 chunks from Archive tier');
  });
  it('reports the restored-file progress once a restore is downloading', () => {
    expect(phaseSentence(snap({ chunksNeedingRehydration: 5, bytesRestored: 10, filesRestored: 2, restoreTotalFiles: 7 }), 'restore'))
      .toBe('Restoring — 2 of 7 files');
  });
});

describe('formatEta', () => {
  it('is "estimating…" until the total is known', () => expect(formatEta(null)).toBe('estimating…'));
  it('renders seconds under a minute (never below 1)', () => {
    expect(formatEta(42)).toBe('~42 sec left');
    expect(formatEta(0)).toBe('~1 sec left');
  });
  it('renders minutes under an hour', () => expect(formatEta(150)).toBe('~3 min left'));
  it('renders hours to one decimal at/above an hour', () => expect(formatEta(5400)).toBe('~1.5 h left'));
});

describe('formatDuration', () => {
  it('is an em dash when unknown', () => expect(formatDuration(null)).toBe('—'));
  it('renders seconds under 90s', () => expect(formatDuration(48)).toBe('48 s'));
  it('renders minutes under 90 min', () => expect(formatDuration(660)).toBe('11 min'));
  it('renders hours at/above 90 min', () => expect(formatDuration(5400)).toBe('1.5 h'));
});

describe('formatThroughput', () => {
  it('treats null as zero B/s', () => expect(formatThroughput(null)).toBe('0 B/s'));
  it('renders B/s below 1 KB/s', () => expect(formatThroughput(512)).toBe('512 B/s'));
  it('renders whole KB/s below 1 MB/s', () => expect(formatThroughput(2400)).toBe('2 KB/s'));
  it('renders MB/s to one decimal at/above 1 MB/s', () => expect(formatThroughput(2_400_000)).toBe('2.4 MB/s'));
});

describe('hydratedByLabel', () => {
  it('is empty without a start time', () => expect(hydratedByLabel(null, 15)).toBe(''));
  it('adds the window to the start and formats a wall-clock ETA', () => {
    const label = hydratedByLabel('2026-01-01T00:00:00Z', 2);
    expect(label).toMatch(/^≈ hydrated by \d{2}:\d{2}$/);   // exact time is locale/TZ-dependent; shape is stable
  });
});

describe('statusMeta', () => {
  it('maps each known status to its own label', () => {
    expect(statusMeta('running').label).toBe('Running');
    expect(statusMeta('awaiting-cost').label).toBe('Review cost');
    expect(statusMeta('rehydrating').label).toBe('Rehydrating');
    expect(statusMeta('completed').label).toBe('Completed');
    expect(statusMeta('failed').label).toBe('Failed');
    expect(statusMeta('cancelled').label).toBe('Cancelled');
    expect(statusMeta('interrupted').label).toBe('Interrupted');
  });
  it('pulses only while actively working (running/rehydrating)', () => {
    expect(statusMeta('running').pulse).toBe(true);
    expect(statusMeta('rehydrating').pulse).toBe(true);
    expect(statusMeta('completed').pulse).toBe(false);
  });
  it('echoes an unknown status verbatim as its own label', () => {
    expect(statusMeta('queued').label).toBe('queued');
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
