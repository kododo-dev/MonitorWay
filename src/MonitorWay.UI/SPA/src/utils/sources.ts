import type { Source } from '../api/api.model';

/**
 * The sources with each group's members together, as a grouped list needs them: sources without a group first,
 * then the groups in the order they first appear. Within a group the server's order is kept.
 */
export function groupSources(sources: Source[]): Source[] {
  const order = new Map<string, number>();
  for (const source of sources) {
    const group = source.group ?? '';
    if (!order.has(group)) order.set(group, group === '' ? -1 : order.size);
  }
  return sources
    .map((source, index) => ({ source, index }))
    .sort((a, b) => order.get(a.source.group ?? '')! - order.get(b.source.group ?? '')! || a.index - b.index)
    .map(x => x.source);
}

/** Separates the ids of sources shown together; the server merges them by time. */
export const SOURCE_SEPARATOR = ',';

/** The ids in a selection such as `file-a,file-b` that still exist, or the first source when none does. */
export function parseSelection(value: string | null, sources: Source[]): string[] {
  const known = new Set(sources.map(s => s.id));
  const ids = [...new Set((value ?? '').split(SOURCE_SEPARATOR).filter(id => known.has(id)))];
  return ids.length ? ids : sources.length ? [sources[0].id] : [];
}

/** Adds or removes a source; the last one stays. */
export function toggleSource(selection: string[], id: string): string[] {
  if (!selection.includes(id)) return [...selection, id];
  return selection.length > 1 ? selection.filter(s => s !== id) : selection;
}

/** Adds the whole group, or, when all of it is already shown, removes it unless nothing would be left. */
export function toggleGroup(selection: string[], group: string[]): string[] {
  if (!group.every(id => selection.includes(id))) return [...selection, ...group.filter(id => !selection.includes(id))];
  const rest = selection.filter(id => !group.includes(id));
  return rest.length ? rest : selection;
}

/** How a selection is named: the source, the group when it is a whole group, or the names joined. */
export function selectionLabel(selection: string[], sources: Source[]): string {
  const chosen = sources.filter(s => selection.includes(s.id));
  if (chosen.length === 1) return chosen[0].name;
  const groups = new Set(chosen.map(s => s.group));
  const [group] = groups;
  if (groups.size === 1 && group && sources.filter(s => s.group === group).length === chosen.length)
    return `${group} (${chosen.length})`;
  return chosen.map(s => s.name).join(' + ');
}
