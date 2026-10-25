const pad = (n: number, width = 2) => String(n).padStart(width, '0');

/** `MM-dd HH:mm:ss.SSS` in local time: compact, fixed width, still unambiguous across days. */
export function formatRowTime(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  return `${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

export function formatFullTime(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  return `${d.getFullYear()}-${formatRowTime(iso)}`;
}

export function formatDuration(seconds: number): string {
  if (seconds < 60) return `${Math.round(seconds)} s`;
  const minutes = seconds / 60;
  if (minutes < 60) return `${Math.round(minutes)} min`;
  const hours = minutes / 60;
  if (hours < 48) return `${Math.round(hours * 10) / 10} h`;
  return `${Math.round((hours / 24) * 10) / 10} d`;
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

export const formatCount = (n: number) => n.toLocaleString();

/** First line of a message; the list shows one line per entry. */
export const firstLine = (text: string) => {
  const end = text.indexOf('\n');
  return end < 0 ? text : `${text.slice(0, end)} …`;
};
