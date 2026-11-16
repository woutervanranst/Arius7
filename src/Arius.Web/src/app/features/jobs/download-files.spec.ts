import { describe, expect, it } from 'vitest';
import { DownloadFileDto } from '../../core/api/api-models';
import { downloadActions, fileStatus, readyHeadline, readySubline, summarize } from './download-files';

const file = (over: Partial<DownloadFileDto> = {}): DownloadFileDto => ({ path: 'a.txt', size: 100, ready: true, downloaded: false, ...over });

describe('summarize', () => {
  it('counts ready, downloaded, remaining and pending files with their bytes', () => {
    const s = summarize([
      file({ size: 10, downloaded: true }),
      file({ size: 20 }),
      file({ size: 30 }),
      file({ size: 40, ready: false }),
    ]);
    expect(s).toEqual({ total: 4, ready: 3, readyBytes: 60, downloaded: 1, remaining: 2, remainingBytes: 50, pending: 1 });
  });
});

describe('downloadActions', () => {
  it('offers "Download ready files" while running with nothing downloaded', () => {
    const s = summarize([file(), file({ ready: false })]);
    expect(downloadActions(s, false)).toEqual([{ label: 'Download ready files', set: 'all', primary: true, testid: 'download-ready' }]);
  });

  it('offers "Download" once finished with nothing downloaded', () => {
    const s = summarize([file(), file()]);
    expect(downloadActions(s, true)).toEqual([{ label: 'Download', set: 'all', primary: true, testid: 'download-all' }]);
  });

  it('offers outline "Download all (3 files)" and primary "Download remaining (1 file)" after a partial download', () => {
    const s = summarize([file({ downloaded: true }), file({ downloaded: true }), file()]);
    expect(downloadActions(s, true)).toEqual([
      { label: 'Download all (3 files)', set: 'all', primary: false, testid: 'download-all' },
      { label: 'Download remaining (1 file)', set: 'remaining', primary: true, testid: 'download-remaining' },
    ]);
  });

  it('pluralizes "Download remaining" for several files', () => {
    const s = summarize([file({ downloaded: true }), file(), file()]);
    expect(downloadActions(s, false)[1].label).toBe('Download remaining (2 files)');
  });

  it('offers only "Download all (n files)" when everything ready was downloaded', () => {
    const s = summarize([file({ downloaded: true }), file({ downloaded: true }), file({ ready: false })]);
    expect(downloadActions(s, false)).toEqual([{ label: 'Download all (2 files)', set: 'all', primary: true, testid: 'download-all' }]);
  });

  it('offers nothing when no file is ready', () => {
    expect(downloadActions(summarize([file({ ready: false })]), false)).toEqual([]);
    expect(downloadActions(summarize([]), true)).toEqual([]);
  });
});

describe('fileStatus', () => {
  it('labels files Downloaded, Ready, Rehydrating, Pending, Not prepared and Cleaned up', () => {
    expect(fileStatus(file({ downloaded: true }), 'completed', false)).toBe('Downloaded');
    expect(fileStatus(file(), 'running', false)).toBe('Ready');
    expect(fileStatus(file({ ready: false }), 'rehydrating', false)).toBe('Rehydrating');
    expect(fileStatus(file({ ready: false }), 'running', false)).toBe('Pending');
    expect(fileStatus(file({ ready: false }), 'awaiting-cost', false)).toBe('Pending');
    expect(fileStatus(file({ ready: false }), 'failed', false)).toBe('Not prepared');
    expect(fileStatus(file({ ready: false }), 'cancelled', false)).toBe('Not prepared');
    expect(fileStatus(file({ ready: false }), 'interrupted', false)).toBe('Not prepared');
    expect(fileStatus(file({ downloaded: true }), 'completed', true)).toBe('Cleaned up');
  });
});

describe('readyHeadline', () => {
  it('says "All 3 files ready · 1.40 GB" when finished and "2 of 3 files ready · 20.5 MB" otherwise', () => {
    const all = summarize([file({ size: 1e9 }), file({ size: 2e8 }), file({ size: 2e8 })]);
    expect(readyHeadline(all, true)).toBe('All 3 files ready · 1.40 GB');
    const some = summarize([file({ size: 18.4e6 }), file({ size: 2.1e6 }), file({ size: 1.2e9, ready: false })]);
    expect(readyHeadline(some, false)).toBe('2 of 3 files ready · 20.5 MB');
  });

  it('keeps the "n of m" wording for a finished download with files that never became ready', () => {
    const s = summarize([file({ size: 1e6 }), file({ ready: false })]);
    expect(readyHeadline(s, true)).toBe('1 of 2 files ready · 1.0 MB');
  });
});

describe('readySubline', () => {
  const s = summarize([file(), file({ ready: false }), file({ ready: false })]);
  const none = summarize([file()]);

  it('mentions the files still being rehydrated', () => {
    expect(readySubline(s, 'rehydrating', null)).toBe('2 files still being rehydrated');
    expect(readySubline(summarize([file(), file({ ready: false })]), 'rehydrating', null)).toBe('1 file still being rehydrated');
  });

  it('shows the clean-up date as d MMM', () => {
    expect(readySubline(none, 'completed', '2026-11-22T12:00:00Z')).toBe('Kept on the server until 22 Nov, then cleaned up');
  });

  it('says files are kept until cleaned up when no clean-up date is set', () => {
    expect(readySubline(none, 'completed', null)).toBe('Kept on the server until you clean them up');
  });

  it('is empty while the job runs without waiting on rehydration', () => {
    expect(readySubline(s, 'running', null)).toBe('');
    expect(readySubline(none, 'rehydrating', null)).toBe('');
  });
});
