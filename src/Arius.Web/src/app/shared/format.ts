/** Bytes → human-readable size (mirrors the prototype's fmtSize). */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null) return '—';
  if (bytes >= 1e12) return (bytes / 1e12).toFixed(2) + ' TB';
  if (bytes >= 1e9) return (bytes / 1e9).toFixed(2) + ' GB';
  if (bytes >= 1e6) return (bytes / 1e6).toFixed(1) + ' MB';
  if (bytes >= 1e3) return (bytes / 1e3).toFixed(0) + ' KB';
  return bytes + ' B';
}

/** Tier name → colour (Hot/Cool/Cold/Archive). */
export function tierColor(tier: string | null | undefined): string {
  switch ((tier ?? '').toLowerCase()) {
    case 'hot': return '#d97706';
    case 'cool': return '#0ea5e9';
    case 'cold': return '#3b82f6';
    case 'archive': return '#8b5cf6';
    default: return '#a1a1aa';
  }
}

/** A small "N files" / count formatter with thousands separators. */
export function formatCount(n: number | null | undefined): string {
  return n == null ? '—' : n.toLocaleString('en-US');
}

const CURRENCY_SYMBOLS: Record<string, string> = { EUR: '€', USD: '$', GBP: '£' };

/**
 * Money → display string. Whole units at/above 10 (e.g. €182), two decimals below (e.g. €8.14),
 * matching the cost-breakdown design. Falls back to a "CODE " prefix for unknown currencies.
 */
export function formatCurrency(amount: number | null | undefined, currency: string | null | undefined = 'EUR'): string {
  if (amount == null) return '—';
  const code = currency || 'EUR';
  const symbol = CURRENCY_SYMBOLS[code] ?? `${code} `;
  const digits = amount >= 10 ? 0 : 2;
  return symbol + amount.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits });
}
