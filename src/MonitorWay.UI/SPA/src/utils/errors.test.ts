import { describe, expect, it } from 'vitest';
import { isApplicationFrame, shortType } from './errors';

describe('shortType', () => {
  it('drops the namespace', () => {
    expect(shortType('System.TimeoutException')).toBe('TimeoutException');
    expect(shortType('Exception')).toBe('Exception');
  });
});

describe('isApplicationFrame', () => {
  const frames = ['Shop.Payments.Gateway.ChargeAsync'];
  it('finds the frames the group is recognized by, also as async state machines', () => {
    expect(isApplicationFrame('   at Shop.Payments.Gateway.ChargeAsync() in Gateway.cs:line 4', frames)).toBe(true);
    expect(isApplicationFrame('   at Shop.Payments.Gateway.<ChargeAsync>d__4.MoveNext()', frames)).toBe(true);
    expect(isApplicationFrame('   at System.Threading.Tasks.Task.Wait()', frames)).toBe(false);
  });
});
