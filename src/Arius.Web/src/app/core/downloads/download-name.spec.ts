import { describe, expect, it } from 'vitest';
import { downloadFileName } from './download-name';

describe('downloadFileName', () => {
  it('one file keeps its own name', () => {
    expect(downloadFileName('Photos', new Date(Date.UTC(2026, 10, 15)), ['x/IMG 1.HEIC'])).toBe('IMG 1.HEIC');
  });

  it('several files become alias-date.zip', () => {
    expect(downloadFileName('Photos', new Date(Date.UTC(2026, 10, 15)), ['a', 'b'])).toBe('Photos-2026-11-15.zip');
  });

  it('uses the UTC date, which is the date the server puts in the name', () => {
    expect(downloadFileName('Photos', new Date('2026-11-15T23:30:00Z'), ['a', 'b'])).toBe('Photos-2026-11-15.zip');
    expect(downloadFileName('Photos', new Date('2026-11-16T00:30:00Z'), ['a', 'b'])).toBe('Photos-2026-11-16.zip');
  });
});
