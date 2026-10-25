import { describe, expect, it } from 'vitest';
import { categoryQuery, hideCategory, isShown, onlyCategory, toggleCategory } from './categories';

const known = ['Shop.Orders', 'Shop.Payments', 'Shop.Cache'];

describe('category filter', () => {
  it('shows everything without a filter and hides an unchecked category', () => {
    const filter = toggleCategory(null, 'Shop.Cache', known);

    expect(isShown(null, 'Shop.Cache')).toBe(true);
    expect(isShown(filter, 'Shop.Cache')).toBe(false);
    expect(isShown(filter, 'Shop.Orders')).toBe(true);
    expect(categoryQuery(filter)).toEqual({ excludedCategories: ['Shop.Cache'] });
  });

  it('adds checked categories to "only"', () => {
    const filter = toggleCategory(onlyCategory('Shop.Orders'), 'Shop.Payments', known);

    expect(known.filter(n => isShown(filter, n))).toEqual(['Shop.Orders', 'Shop.Payments']);
    expect(categoryQuery(filter)).toEqual({ categories: ['Shop.Orders', 'Shop.Payments'] });
  });

  it('goes back to all when nothing or everything would be shown', () => {
    expect(toggleCategory(onlyCategory('Shop.Orders'), 'Shop.Orders', known)).toBeNull();
    expect(toggleCategory({ mode: 'hide', names: ['Shop.Cache'] }, 'Shop.Cache', known)).toBeNull();
    expect(toggleCategory({ mode: 'hide', names: ['Shop.Orders', 'Shop.Payments'] }, 'Shop.Cache', known)).toBeNull();
    expect(toggleCategory({ mode: 'only', names: ['Shop.Orders', 'Shop.Payments'] }, 'Shop.Cache', known)).toBeNull();
  });

  it('hides a category only when it is shown', () => {
    const filter = { mode: 'hide' as const, names: ['Shop.Cache'] };

    expect(hideCategory(filter, 'Shop.Cache')).toBe(filter);
    expect(hideCategory(onlyCategory('Shop.Orders'), 'Shop.Payments')).toEqual(onlyCategory('Shop.Orders'));
    expect(hideCategory(null, 'Shop.Cache')).toEqual(filter);
  });
});
