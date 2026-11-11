import { describe, expect, it } from 'vitest';
import { layoutSnapshotDots, MIN_DOT_GAP, DOT_INSET } from './snapshot-layout';

const day = 86_400_000;
const gaps = (x: number[]) => x.slice(1).map((v, i) => v - x[i]);

describe('layoutSnapshotDots', () => {
  it('returns nothing for no snapshots', () => {
    expect(layoutSnapshotDots([], 600)).toEqual([]);
  });

  it('puts a single snapshot at the right end', () => {
    expect(layoutSnapshotDots([1000], 600)).toEqual([600 - DOT_INSET]);
  });

  it('maps time linearly between the two insets', () => {
    // 0, 1, 10 days -> fractions 0, 0.1, 1 over [10, 590]
    const x = layoutSnapshotDots([0, day, 10 * day], 600);
    expect(x[0]).toBeCloseTo(10);
    expect(x[1]).toBeCloseTo(10 + 0.1 * 580);
    expect(x[2]).toBeCloseTo(590);
  });

  it('pushes snapshots from the same day apart by at least the minimum gap', () => {
    const t0 = Date.UTC(2025, 11, 14, 16, 34);
    const x = layoutSnapshotDots([t0 - 400 * day, t0, t0 + 2 * 3_600_000, t0 + 400 * day], 600);
    for (const g of gaps(x)) expect(g).toBeGreaterThanOrEqual(MIN_DOT_GAP - 1e-9);
  });

  it('pins the last dot to the right end and keeps the first at or after the left inset', () => {
    const t = Date.UTC(2025, 0, 1);
    // a crowded tail: the forward pass would overshoot the right end without the pin
    const x = layoutSnapshotDots([0, 100 * day, t, t + 1000, t + 2000], 600);
    expect(x[x.length - 1]).toBe(600 - DOT_INSET);
    expect(x[0]).toBeGreaterThanOrEqual(DOT_INSET - 1e-9);
    for (const g of gaps(x)) expect(g).toBeGreaterThanOrEqual(MIN_DOT_GAP - 1e-9);
  });

  it('keeps dots ordered and spaced when identical timestamps are given', () => {
    const x = layoutSnapshotDots([5, 5, 5], 300);
    expect(x[0]).toBeGreaterThanOrEqual(DOT_INSET);
    expect(x[2]).toBe(300 - DOT_INSET);
    for (const g of gaps(x)) expect(g).toBeGreaterThanOrEqual(MIN_DOT_GAP);
  });

  it('shrinks the gap evenly instead of overflowing when the track is too narrow', () => {
    const x = layoutSnapshotDots([0, day, 2 * day, 3 * day, 4 * day, 5 * day], 80);
    expect(x[0]).toBeCloseTo(DOT_INSET);
    expect(x[x.length - 1]).toBeCloseTo(80 - DOT_INSET);
    const g = gaps(x);
    for (const v of g) expect(v).toBeCloseTo(g[0]);
    expect(g[0]).toBeLessThan(MIN_DOT_GAP);
  });
});
