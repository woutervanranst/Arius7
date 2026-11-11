/** Minimum distance (px) between two dot centres on the snapshot scrubber. */
export const MIN_DOT_GAP = 20;
/** Inset (px) of the first/last dot centre from the track ends. */
export const DOT_INSET = 10;

/**
 * X positions (px) of the scrubber dots: linear in time from the first snapshot (left inset) to the
 * latest (right inset), then nudged so neighbours are at least MIN_DOT_GAP apart. A forward pass pushes
 * crowded dots right, the last dot is pinned to the right end, a backward pass pulls the overshoot back.
 * If the track is too narrow for MIN_DOT_GAP the dots are spread evenly instead of overflowing.
 * `times` are epoch milliseconds, oldest first.
 */
export function layoutSnapshotDots(times: number[], width: number): number[] {
  const n = times.length;
  const lo = DOT_INSET;
  const hi = width - DOT_INSET;
  if (n === 0) return [];
  if (n === 1) return [hi];

  const span = times[n - 1] - times[0];
  const x = times.map(t => (span > 0 ? lo + ((t - times[0]) / span) * (hi - lo) : lo));
  for (let i = 1; i < n; i++) x[i] = Math.max(x[i], x[i - 1] + MIN_DOT_GAP);
  x[n - 1] = hi;
  for (let i = n - 2; i >= 0; i--) x[i] = Math.min(x[i], x[i + 1] - MIN_DOT_GAP);

  return x[0] < lo ? times.map((_, i) => lo + (i / (n - 1)) * (hi - lo)) : x;
}
