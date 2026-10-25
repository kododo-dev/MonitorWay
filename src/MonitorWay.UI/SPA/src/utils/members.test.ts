import { describe, expect, it } from 'vitest';
import { memberColor, shortMember } from './members';

describe('memberColor', () => {
  it('gives a name the same colour every time', () => {
    expect(memberColor('shop-api-1', false)).toBe(memberColor('shop-api-1', false));
  });

  it('tells replicas apart', () => {
    expect(memberColor('shop-api-1', false)).not.toBe(memberColor('shop-api-2', false));
  });

  it('is lighter in the dark theme', () => {
    expect(memberColor('worker', true)).toMatch(/68%\)$/);
    expect(memberColor('worker', false)).toMatch(/36%\)$/);
  });
});

describe('shortMember', () => {
  it('keeps short names', () => {
    expect(shortMember('shop-api-1')).toBe('shop-api-1');
  });

  it('keeps the end of long names, where replicas differ', () => {
    expect(shortMember('a-very-long-project_shop-api-12', 12)).toBe('…shop-api-12');
  });
});
