/**
 * Helpers for the search syntax evaluated by the server:
 * `timeout -health "order 1008" category:Payments OrderId:1008 has:exception trace:abc`.
 */

interface Token {
  raw: string;
  quoted: boolean;
}

function tokenize(text: string): Token[] {
  const tokens: Token[] = [];
  let current = '';
  let inQuotes = false;
  let quotedWhole = false;
  for (const c of text + ' ') {
    if (c === '"') {
      if (!inQuotes && current.length === 0) quotedWhole = true;
      inQuotes = !inQuotes;
      if (!quotedWhole) current += c;
      continue;
    }
    if (/\s/.test(c) && !inQuotes) {
      if (current) tokens.push({ raw: current, quoted: quotedWhole });
      current = '';
      quotedWhole = false;
      continue;
    }
    current += c;
  }
  return tokens;
}

/** Quotes a value when it would otherwise be split into several terms. */
export const quote = (value: string) => (/[\s"]/.test(value) ? `"${value.replace(/"/g, '')}"` : value);

/** The token for a field filter, e.g. `OrderId:1008` or `-category:Cache`. */
export const fieldToken = (field: string, value: string, negate = false) =>
  `${negate ? '-' : ''}${field}:${quote(value)}`;

const normalize = (token: string) => token.toLowerCase();

export function hasToken(text: string, token: string): boolean {
  return tokenize(text).some(t => normalize(t.raw) === normalize(token));
}

/** Adds a token unless it is already there; replaces its negation (`x` replaces `-x` and vice versa). */
export function addToken(text: string, token: string): string {
  const opposite = token.startsWith('-') ? token.slice(1) : `-${token}`;
  const kept = tokenize(text)
    .filter(t => normalize(t.raw) !== normalize(opposite))
    .map(t => (t.quoted ? `"${t.raw}"` : t.raw));
  if (kept.some(t => normalize(t) === normalize(token))) return kept.join(' ');
  return [...kept, token].join(' ');
}

export function removeToken(text: string, token: string): string {
  return tokenize(text)
    .filter(t => normalize(t.raw) !== normalize(token))
    .map(t => (t.quoted ? `"${t.raw}"` : t.raw))
    .join(' ');
}

export const toggleToken = (text: string, token: string) =>
  hasToken(text, token) ? removeToken(text, token) : addToken(text, token);

/** The service the search is narrowed to by a `service:` term, or `null`. */
export function serviceOf(text: string): string | null {
  const token = tokenize(text).find(t => !t.quoted && /^service:/i.test(t.raw));
  return token ? token.raw.slice('service:'.length).replace(/^"|"$/g, '') : null;
}

/** The search narrowed to one service, or to none: any `service:` term is replaced. */
export function withService(text: string, service: string | null): string {
  const kept = tokenize(text)
    .filter(t => t.quoted || !/^service:/i.test(t.raw))
    .map(t => (t.quoted ? `"${t.raw}"` : t.raw));
  return [...kept, ...(service ? [fieldToken('service', service)] : [])].join(' ');
}

/**
 * A regular expression that finds what the search looks for in message text: plain terms and phrases,
 * and `message:` terms. Field filters and negated terms are not highlighted.
 */
export function highlightPattern(text: string, regex: boolean, caseSensitive: boolean): RegExp | null {
  const flags = caseSensitive ? 'g' : 'gi';
  if (!text.trim()) return null;
  if (regex) {
    try {
      const pattern = new RegExp(text, flags);
      return pattern.test('') ? null : pattern; // A pattern matching empty text would mark nothing useful.
    } catch {
      return null;
    }
  }

  const words: string[] = [];
  for (const token of tokenize(text)) {
    if (token.quoted) {
      words.push(token.raw);
      continue;
    }
    if (token.raw.startsWith('-')) continue;
    const colon = token.raw.indexOf(':');
    const isField = colon > 0 && !token.raw.includes('://') && /^[A-Za-z_@][\w.@-]*$/.test(token.raw.slice(0, colon));
    if (!isField) words.push(token.raw);
    else if (/^(message|msg)$/i.test(token.raw.slice(0, colon))) words.push(token.raw.slice(colon + 1).replace(/^"|"$/g, ''));
  }
  if (!words.length) return null;
  const escaped = words
    .filter(Boolean)
    .sort((a, b) => b.length - a.length)
    .map(w => w.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
  return new RegExp(escaped.join('|'), flags);
}
