import { JobSnapshot } from '../core/api/api-models';

/** "~12 min left" / "estimating…" (null until totalNewBytes is known). */
export function formatEta(seconds: number | null | undefined): string {
  if (seconds == null) return 'estimating…';
  if (seconds < 60) return `~${Math.max(1, Math.round(seconds))} sec left`;
  if (seconds < 3600) return `~${Math.round(seconds / 60)} min left`;
  return `~${(seconds / 3600).toFixed(1)} h left`;
}

/** "11 min" / "1.4 h" / "48 s" — elapsed/duration display. */
export function formatDuration(seconds: number | null | undefined): string {
  if (seconds == null) return '—';
  if (seconds < 90) return `${Math.round(seconds)} s`;
  if (seconds < 5400) return `${Math.round(seconds / 60)} min`;
  return `${(seconds / 3600).toFixed(1)} h`;
}

/** "2.4 MB/s". */
export function formatThroughput(bytesPerSec: number | null | undefined): string {
  const b = bytesPerSec ?? 0;
  if (b >= 1e6) return `${(b / 1e6).toFixed(1)} MB/s`;
  if (b >= 1e3) return `${(b / 1e3).toFixed(0)} KB/s`;
  return `${Math.round(b)} B/s`;
}

/** "≈ hydrated by 03:40" from a rehydration start + the priority window (hours). */
export function hydratedByLabel(startedAtIso: string | null, windowHours: number): string {
  if (!startedAtIso) return '';
  const done = new Date(new Date(startedAtIso).getTime() + windowHours * 3600_000);
  return `≈ hydrated by ${done.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })}`;
}

export interface StatusMeta { label: string; color: string; bg: string; border: string; dot: string; icon: string; pulse: boolean; }

/** Chip styling per status (README §Screens; amber for waiting, blue running, etc.). */
export function statusMeta(status: string): StatusMeta {
  switch (status) {
    case 'running':      return { label: 'Running',      color: '#1d4ed8', bg: '#eff6ff', border: 'none', dot: '#3b82f6', icon: 'ki-loading',       pulse: true };
    case 'awaiting-cost':return { label: 'Review cost',  color: '#b45309', bg: '#fffbeb', border: '1px solid #fde68a', dot: '#b45309', icon: 'ki-dollar', pulse: false };
    case 'rehydrating':  return { label: 'Rehydrating',  color: '#b45309', bg: '#fffbeb', border: '1px solid #fde68a', dot: '#b45309', icon: 'ki-time',   pulse: true };
    case 'completed':    return { label: 'Completed',    color: '#15803d', bg: '#f0fdf4', border: 'none', dot: '#22c55e', icon: 'ki-check-circle',  pulse: false };
    case 'failed':       return { label: 'Failed',       color: '#dc2626', bg: '#fef2f2', border: 'none', dot: '#dc2626', icon: 'ki-cross-circle',  pulse: false };
    case 'cancelled':    return { label: 'Cancelled',    color: '#71717a', bg: '#f4f4f5', border: 'none', dot: '#a1a1aa', icon: 'ki-cross-circle',  pulse: false };
    case 'interrupted':  return { label: 'Interrupted',  color: '#a16207', bg: '#fefce8', border: 'none', dot: '#a16207', icon: 'ki-time',          pulse: false };
    default:             return { label: status,         color: '#52525b', bg: '#f4f4f5', border: 'none', dot: '#a1a1aa', icon: 'ki-time',          pulse: false };
  }
}

/** One phase sentence for the pill / overview row / detail header, e.g. "Uploading — 1.68 of 3.11 GB · 2.4 MB/s". */
export function phaseSentence(s: JobSnapshot, kind: string): string {
  const gb = (n: number) => (n / 1e9).toFixed(2) + ' GB';
  if (kind === 'restore') {
    if (s.chunksNeedingRehydration > 0 && s.bytesRestored === 0) return `Rehydrating — ${s.chunksNeedingRehydration} chunks from Archive tier`;
    return `Restoring — ${s.filesRestored} of ${s.restoreTotalFiles} files`;
  }
  if (s.totalNewBytes === 0) return 'Scanning & hashing — estimating…';
  return `Uploading — ${gb(s.uploadedBytes)} of ${gb(s.totalNewBytes)}`;
}
