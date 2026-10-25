/**
 * A colour for the source an entry comes from in a view of several sources. The same name always gets the same hue,
 * so the replicas of a service can be told apart at a glance, also after reloading.
 */
export function memberColor(name: string, isDark: boolean): string {
  let hash = 0;
  for (let i = 0; i < name.length; i++) hash = (hash * 31 + name.charCodeAt(i)) | 0;
  const hue = ((hash % 360) + 360) % 360;
  return isDark ? `hsl(${hue}, 55%, 68%)` : `hsl(${hue}, 60%, 36%)`;
}

/** Keeps the end of a long source name, where replicas differ (`shop-api-1`, `shop-api-2`). */
export function shortMember(name: string, max = 18): string {
  return name.length <= max ? name : `…${name.slice(name.length - max + 1)}`;
}
