import type { Query } from '../api/api.model';

/**
 * Which categories the list shows: only the named ones, or all but the named ones. `null` shows every category.
 * Hiding keeps categories that appear later visible; choosing "only" keeps them hidden.
 */
export type CategoryFilter = { mode: 'only' | 'hide'; names: string[] } | null;

const has = (names: string[], name: string) => names.includes(name);

export function isShown(filter: CategoryFilter, name: string): boolean {
  if (!filter) return true;
  return filter.mode === 'only' ? has(filter.names, name) : !has(filter.names, name);
}

/**
 * Checks or unchecks a category. A filter that would show every known category, or none of them, goes back to
 * showing all; `known` is the list the user sees, when there is one.
 */
export function toggleCategory(filter: CategoryFilter, name: string, known?: string[]): CategoryFilter {
  if (!filter) return { mode: 'hide', names: [name] };
  const names = has(filter.names, name) ? filter.names.filter(n => n !== name) : [...filter.names, name];
  if (names.length === 0) return null;
  if (known?.length && known.every(n => has(names, n))) return null;
  return { mode: filter.mode, names };
}

export const onlyCategory = (name: string): CategoryFilter => ({ mode: 'only', names: [name] });

export function hideCategory(filter: CategoryFilter, name: string): CategoryFilter {
  return isShown(filter, name) ? toggleCategory(filter, name) : filter;
}

export function categoryQuery(filter: CategoryFilter): Pick<Query, 'categories' | 'excludedCategories'> {
  if (!filter) return {};
  return filter.mode === 'only' ? { categories: filter.names } : { excludedCategories: filter.names };
}
