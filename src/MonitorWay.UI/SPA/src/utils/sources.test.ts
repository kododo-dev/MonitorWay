import { describe, expect, it } from 'vitest';
import type { Source } from '../api/api.model';
import { groupSources, parseSelection, selectionLabel, toggleGroup, toggleSource } from './sources';

const source = (id: string, group?: string): Source => ({ id, name: id, kind: 'file', group: group ?? null });

describe('groupSources', () => {
  it('puts ungrouped sources first and keeps members of a group together', () => {
    const sources = [source('web-1', 'web'), source('memory'), source('db-1', 'db'), source('web-2', 'web')];

    expect(groupSources(sources).map(s => s.id)).toEqual(['memory', 'web-1', 'web-2', 'db-1']);
  });

  it('keeps the order when nothing is grouped', () => {
    const sources = [source('b'), source('a')];

    expect(groupSources(sources).map(s => s.id)).toEqual(['b', 'a']);
  });
});

describe('source selection', () => {
  const sources = [source('memory'), source('web-1', 'shop / web'), source('web-2', 'shop / web'), source('db-1', 'shop / db')];

  it('keeps known ids and falls back to the first source', () => {
    expect(parseSelection('web-2,gone,web-1', sources)).toEqual(['web-2', 'web-1']);
    expect(parseSelection('gone', sources)).toEqual(['memory']);
    expect(parseSelection(null, [])).toEqual([]);
  });

  it('adds and removes sources but keeps the last one', () => {
    expect(toggleSource(['web-1'], 'db-1')).toEqual(['web-1', 'db-1']);
    expect(toggleSource(['web-1', 'db-1'], 'web-1')).toEqual(['db-1']);
    expect(toggleSource(['web-1'], 'web-1')).toEqual(['web-1']);
  });

  it('adds a whole group and removes it when all of it is shown', () => {
    expect(toggleGroup(['db-1'], ['web-1', 'web-2'])).toEqual(['db-1', 'web-1', 'web-2']);
    expect(toggleGroup(['db-1', 'web-1', 'web-2'], ['web-1', 'web-2'])).toEqual(['db-1']);
    expect(toggleGroup(['web-1', 'web-2'], ['web-1', 'web-2'])).toEqual(['web-1', 'web-2']);
  });

  it('names a whole group after it', () => {
    expect(selectionLabel(['web-1'], sources)).toBe('web-1');
    expect(selectionLabel(['web-1', 'web-2'], sources)).toBe('shop / web (2)');
    expect(selectionLabel(['web-1', 'db-1'], sources)).toBe('web-1 + db-1');
  });
});
