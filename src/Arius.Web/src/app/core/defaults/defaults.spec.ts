import { ARCHIVE_OPTIONS, DefaultValues, INHERITED_FROM, RESTORE_OPTIONS, ResolvedDefaults, SET_AT, archiveFlags,
  fromFactory, levelPill, replaceNote, resolve, runPill, sameSet, valuesOf, withPick, without } from './defaults';

const FACTORY: DefaultValues = { tier: 'archive', onDisk: 'keep', fastHash: false, overwrite: false, noPointers: false };

describe('fromFactory', () => {
  it('marks every option as coming from the factory', () => {
    const r = fromFactory(FACTORY);
    for (const key of Object.keys(FACTORY) as (keyof DefaultValues)[]) {
      expect(r[key]).toEqual({ value: FACTORY[key], source: 'factory' });
    }
  });
});

describe('resolve', () => {
  const inherited = fromFactory(FACTORY);

  it('lets set values win and tags them with the given source', () => {
    const r = resolve({ tier: 'cool' }, inherited, 'settings');
    expect(r.tier).toEqual({ value: 'cool', source: 'settings' });
    expect(r.onDisk).toEqual({ value: 'keep', source: 'factory' });
  });

  it('treats null and absent as inherit', () => {
    const r = resolve({ tier: null }, inherited, 'repository');
    expect(r).toEqual(inherited);
  });

  it('lets an explicit false win over an inherited true', () => {
    const withFastHash: ResolvedDefaults = { ...inherited, fastHash: { value: true, source: 'settings' } };
    expect(resolve({ fastHash: false }, withFastHash, 'repository').fastHash).toEqual({ value: false, source: 'repository' });
  });
});

describe('valuesOf', () => {
  it('drops the sources', () => {
    expect(valuesOf(resolve({ tier: 'hot' }, fromFactory(FACTORY), 'settings'))).toEqual({ ...FACTORY, tier: 'hot' });
  });
});

describe('option groups', () => {
  it('splits archive and restore options', () => {
    expect(ARCHIVE_OPTIONS).toEqual(['tier', 'onDisk', 'fastHash']);
    expect(RESTORE_OPTIONS).toEqual(['overwrite', 'noPointers']);
  });
});

describe('levelPill', () => {
  const inherited = fromFactory(FACTORY);

  it('shows where an inherited value comes from, without reset', () => {
    expect(levelPill('repository', 'tier', {}, inherited)).toEqual({ text: INHERITED_FROM.factory, canReset: false });
    const fromSettings: ResolvedDefaults = { ...inherited, tier: { value: 'cool', source: 'settings' } };
    expect(levelPill('repository', 'tier', {}, fromSettings)).toEqual({ text: 'Inherited from global settings', canReset: false });
  });

  it('shows a set value as set at that level, even when equal to the inherited one', () => {
    expect(levelPill('repository', 'tier', { tier: 'archive' }, inherited)).toEqual({ text: 'Set for this repository', canReset: true });
    expect(levelPill('settings', 'fastHash', { fastHash: false }, inherited)).toEqual({ text: 'Set in global settings', canReset: true });
  });
});

describe('runPill', () => {
  const resolved = resolve({ tier: 'cool' }, fromFactory(FACTORY), 'repository');

  it('shows the inherited text while the run value equals the resolved one', () => {
    expect(runPill('tier', valuesOf(resolved), resolved)).toEqual({ text: INHERITED_FROM.repository, canReset: false });
  });

  it('flags a changed run value and offers reset', () => {
    expect(runPill('tier', { ...valuesOf(resolved), tier: 'hot' }, resolved)).toEqual({ text: SET_AT.run, canReset: true });
    expect(SET_AT.run).toBe('Changed for this run');
  });
});

describe('withPick / without / sameSet', () => {
  it('withPick sets the key even when it equals the inherited value, without mutating', () => {
    const original: { tier?: 'hot' } = {};
    const picked = withPick(original, 'tier', 'archive');
    expect(picked).toEqual({ tier: 'archive' });
    expect(original).toEqual({});
  });

  it('without removes the key', () => {
    expect(without({ tier: 'hot', fastHash: true }, 'tier')).toEqual({ fastHash: true });
    expect('tier' in without({ tier: 'hot' }, 'tier')).toBe(false);
  });

  it('sameSet treats null and absent alike', () => {
    expect(sameSet({ tier: null }, {})).toBe(true);
    expect(sameSet({ tier: 'hot' }, { tier: 'hot' })).toBe(true);
    expect(sameSet({ tier: 'hot' }, { tier: 'cool' })).toBe(false);
    expect(sameSet({ fastHash: false }, {})).toBe(false);
  });
});

describe('archiveFlags', () => {
  it('maps onDisk to the hub flags', () => {
    expect(archiveFlags('keep')).toEqual({ removeLocal: false, writePointers: false });
    expect(archiveFlags('keep-pointers')).toEqual({ removeLocal: false, writePointers: true });
    expect(archiveFlags('replace')).toEqual({ removeLocal: true, writePointers: true });
  });
});

describe('replaceNote', () => {
  it('describes the reach of a global setting', () => {
    expect(replaceNote('settings', ['music', 'photos'])).toBe('Reaches 2 repositories that inherit it (music, photos), scheduled runs included.');
    expect(replaceNote('settings', ['music'])).toBe('Reaches 1 repository that inherits it (music), scheduled runs included.');
    expect(replaceNote('settings', [])).toBe('No repository inherits it right now.');
  });

  it('warns about unattended scheduled runs at repository level', () => {
    expect(replaceNote('repository')).toBe('Scheduled runs of this repository will do this unattended.');
  });

  it('is empty for a single run', () => {
    expect(replaceNote('run')).toBe('');
  });
});
