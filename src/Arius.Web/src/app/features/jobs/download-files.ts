import { DownloadFileDto, isNonTerminal } from '../../core/api/api-models';
import { formatBytes } from '../../shared/format';

/** Counts behind the download card: ready = restored into the staging folder, downloaded = already sent to the browser. */
export interface DownloadSummary {
  total: number;
  ready: number;
  readyBytes: number;
  /** Ready files that were already downloaded. */
  downloaded: number;
  /** Ready and not yet downloaded. */
  remaining: number;
  remainingBytes: number;
  /** Not ready (still rehydrating, waiting, or never prepared). */
  pending: number;
}

export interface DownloadAction {
  label: string;
  set: 'all' | 'remaining';
  primary: boolean;
  testid: string;
}

export type FileStatus = 'Cleaned up' | 'Downloaded' | 'Ready' | 'Rehydrating' | 'Pending' | 'Not prepared';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

const files = (n: number): string => `${n} ${n === 1 ? 'file' : 'files'}`;

export function summarize(list: DownloadFileDto[]): DownloadSummary {
  const s: DownloadSummary = { total: list.length, ready: 0, readyBytes: 0, downloaded: 0, remaining: 0, remainingBytes: 0, pending: 0 };
  for (const f of list) {
    if (!f.ready) { s.pending++; continue; }
    s.ready++; s.readyBytes += f.size;
    if (f.downloaded) s.downloaded++;
    else { s.remaining++; s.remainingBytes += f.size; }
  }
  return s;
}

/** The download links the card offers, left to right (see the design's "Ready, downloaded, remaining" table). */
export function downloadActions(s: DownloadSummary, finished: boolean): DownloadAction[] {
  if (s.ready === 0) return [];
  if (s.downloaded === 0)
    return [finished
      ? { label: 'Download', set: 'all', primary: true, testid: 'download-all' }
      : { label: 'Download ready files', set: 'all', primary: true, testid: 'download-ready' }];
  const all = (primary: boolean): DownloadAction => ({ label: `Download all (${files(s.ready)})`, set: 'all', primary, testid: 'download-all' });
  if (s.remaining > 0)
    return [all(false), { label: `Download remaining (${files(s.remaining)})`, set: 'remaining', primary: true, testid: 'download-remaining' }];
  return [all(true)];
}

export function fileStatus(f: DownloadFileDto, jobStatus: string, cleanedUp: boolean): FileStatus {
  if (cleanedUp && f.ready) return 'Cleaned up';
  if (f.downloaded) return 'Downloaded';
  if (f.ready) return 'Ready';
  if (jobStatus === 'rehydrating') return 'Rehydrating';
  return isNonTerminal(jobStatus) ? 'Pending' : 'Not prepared';
}

export function readyHeadline(s: DownloadSummary, finished: boolean): string {
  const size = formatBytes(s.readyBytes);
  return finished && s.ready === s.total ? `All ${files(s.total)} ready · ${size}` : `${s.ready} of ${files(s.total)} ready · ${size}`;
}

/** Cleanup date as "d MMM" (e.g. "22 Nov"). */
export function readySubline(s: DownloadSummary, status: string, cleanupAt: string | null): string {
  if (status === 'rehydrating' && s.pending > 0) return `${files(s.pending)} still being rehydrated`;
  if (isNonTerminal(status)) return '';
  if (!cleanupAt) return 'Kept on the server until you clean them up';
  const d = new Date(cleanupAt);
  const date = `${d.getDate()} ${MONTHS[d.getMonth()]}`;
  return `Kept on the server until ${date}, then cleaned up`;
}
