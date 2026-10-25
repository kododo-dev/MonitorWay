import { describe, expect, it } from 'vitest';
import { addToken, fieldToken, hasToken, highlightPattern, quote, removeToken, serviceOf, toggleToken, withService } from './search';

describe('service', () => {
  it('is read from the search and replaced', () => {
    expect(serviceOf('timeout service:shop status:error')).toBe('shop');
    expect(serviceOf('timeout')).toBeNull();
    expect(withService('timeout service:shop', 'warehouse')).toBe('timeout service:warehouse');
    expect(withService('timeout service:shop', null)).toBe('timeout');
    expect(withService('', 'my service')).toBe('service:"my service"');
  });
});

const marked = (pattern: RegExp | null, text: string) => (pattern ? [...text.matchAll(pattern)].map(m => m[0]) : []);

describe('tokens', () => {
  it('quotes values with spaces or quotes', () => {
    expect(quote('c-63')).toBe('c-63');
    expect(quote('Order Service')).toBe('"Order Service"');
    expect(fieldToken('category', 'Shop.Orders')).toBe('category:Shop.Orders');
    expect(fieldToken('category', 'Shop Orders', true)).toBe('-category:"Shop Orders"');
  });

  it('adds a token once and keeps quoted phrases', () => {
    expect(addToken('', 'has:exception')).toBe('has:exception');
    expect(addToken('"order 1008" timeout', 'OrderId:5')).toBe('"order 1008" timeout OrderId:5');
    expect(addToken('timeout OrderId:5', 'orderid:5')).toBe('timeout OrderId:5');
  });

  it('replaces the opposite of a token', () => {
    expect(addToken('timeout -category:Cache', 'category:Cache')).toBe('timeout category:Cache');
    expect(addToken('category:Cache', '-category:Cache')).toBe('-category:Cache');
  });

  it('removes and toggles tokens ignoring case', () => {
    expect(removeToken('a HAS:EXCEPTION b', 'has:exception')).toBe('a b');
    expect(toggleToken('a', 'has:exception')).toBe('a has:exception');
    expect(toggleToken('a has:exception', 'has:exception')).toBe('a');
    expect(hasToken('x Has:Exception', 'has:exception')).toBe(true);
    expect(hasToken('has:exceptions', 'has:exception')).toBe(false);
  });
});

describe('highlightPattern', () => {
  it('marks words and phrases, not field filters or exclusions', () => {
    const pattern = highlightPattern('payment "order 1008" -stripe category:Payments', false, false);

    // Words match inside longer words too, like the search itself (contains).
    expect(marked(pattern, 'Payment for order 1008 failed at Stripe in Payments')).toEqual(['Payment', 'order 1008', 'Payment']);
  });

  it('marks message: terms and URLs', () => {
    expect(marked(highlightPattern('message:failed', false, false), 'it failed')).toEqual(['failed']);
    expect(marked(highlightPattern('https://api.example.com', false, false), 'GET https://api.example.com/pay')).toEqual(['https://api.example.com']);
  });

  it('escapes regular expression characters in plain search', () => {
    expect(marked(highlightPattern('a.b (x)', false, false), 'a.b axb (x)')).toEqual(['a.b', '(x)']);
  });

  it('respects case sensitivity', () => {
    expect(marked(highlightPattern('error', false, true), 'Error error')).toEqual(['error']);
    expect(marked(highlightPattern('error', false, false), 'Error error')).toEqual(['Error', 'error']);
  });

  it('uses the text as an expression in regex mode', () => {
    expect(marked(highlightPattern('order \\d+', true, false), 'Order 42 and order x')).toEqual(['Order 42']);
  });

  it('marks nothing for empty, invalid or empty-matching expressions', () => {
    expect(highlightPattern('   ', false, false)).toBeNull();
    expect(highlightPattern('(', true, false)).toBeNull();
    expect(highlightPattern('a*', true, false)).toBeNull();
    expect(highlightPattern('-only -exclusions', false, false)).toBeNull();
  });
});
