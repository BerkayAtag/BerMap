import type { NodeStatus } from '@/types';
import { useLocaleStore, type Locale } from '@/stores/localeStore';

/*
 * Formatting helpers read the active locale from the locale store at call time.
 * Components that render formatted values subscribe to the locale (useLocale),
 * so they re-render — and re-format — when the language changes.
 */

const INTL_LOCALES: Record<Locale, string> = { tr: 'tr-TR', en: 'en-US' };

function currentLocale(): Locale {
  return useLocaleStore.getState().locale;
}

function intlLocale(): string {
  return INTL_LOCALES[currentLocale()];
}

const dateTimeFormatters = new Map<string, Intl.DateTimeFormat>();
const relativeTimeFormatters = new Map<string, Intl.RelativeTimeFormat>();

function getDateTimeFormatter(): Intl.DateTimeFormat {
  const key = intlLocale();
  let formatter = dateTimeFormatters.get(key);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat(key, { dateStyle: 'medium', timeStyle: 'short' });
    dateTimeFormatters.set(key, formatter);
  }
  return formatter;
}

function getRelativeTimeFormatter(): Intl.RelativeTimeFormat {
  const key = intlLocale();
  let formatter = relativeTimeFormatters.get(key);
  if (!formatter) {
    formatter = new Intl.RelativeTimeFormat(key, { numeric: 'auto' });
    relativeTimeFormatters.set(key, formatter);
  }
  return formatter;
}

export function formatDateTime(value?: string | null): string {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return getDateTimeFormatter().format(date);
}

export function formatRelativeTime(value?: string | null): string {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';

  const formatter = getRelativeTimeFormatter();
  const seconds = Math.round((date.getTime() - Date.now()) / 1000);
  const absolute = Math.abs(seconds);

  if (absolute < 60) return formatter.format(seconds, 'second');
  if (absolute < 3_600) return formatter.format(Math.round(seconds / 60), 'minute');
  if (absolute < 86_400) return formatter.format(Math.round(seconds / 3_600), 'hour');
  return formatter.format(Math.round(seconds / 86_400), 'day');
}

export function formatNumber(value?: number | null): string {
  return (value ?? 0).toLocaleString(intlLocale());
}

const DURATION_UNITS: Record<Locale, { ms: string; s: string; min: string }> = {
  tr: { ms: 'ms', s: 'sn', min: 'dk' },
  en: { ms: 'ms', s: 's', min: 'min' },
};

export function formatDuration(durationMs?: number | null): string {
  if (durationMs == null || durationMs < 0) return '—';
  const units = DURATION_UNITS[currentLocale()];
  if (durationMs < 1_000) return `${Math.round(durationMs)} ${units.ms}`;
  const seconds = Math.round(durationMs / 1_000);
  if (seconds < 60) return `${seconds} ${units.s}`;
  const minutes = Math.floor(seconds / 60);
  const remainingSeconds = seconds % 60;
  return `${minutes} ${units.min} ${remainingSeconds} ${units.s}`;
}

/**
 * Parses a .NET TimeSpan string ("00:00:00.1234567" or "1.02:03:04.5") into milliseconds.
 * Returns null when the value cannot be parsed.
 */
export function parseTimeSpanMs(value?: string | number | null): number | null {
  if (value == null || value === '') return null;
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  const match = /^(-)?(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d+))?$/.exec(value.trim());
  if (!match) {
    const numeric = Number(value);
    return Number.isFinite(numeric) ? numeric : null;
  }
  const [, negative, days, hours, minutes, secs, fraction] = match;
  const totalSeconds =
    Number(days ?? 0) * 86_400 + Number(hours) * 3_600 + Number(minutes) * 60 + Number(secs);
  const fractionMs = fraction ? Number(`0.${fraction}`) * 1_000 : 0;
  const total = totalSeconds * 1_000 + fractionMs;
  return negative ? -total : total;
}

/** Formats a TimeSpan latency string as milliseconds, e.g. "123 ms". */
export function formatLatency(value?: string | number | null): string {
  const ms = parseTimeSpanMs(value);
  if (ms == null || ms < 0) return '—';
  if (ms < 10_000) {
    return `${ms.toLocaleString(intlLocale(), { maximumFractionDigits: ms < 10 ? 1 : 0 })} ms`;
  }
  return formatDuration(ms);
}

export function formatPercent(value?: number | null): string {
  return `${(value ?? 0).toLocaleString(intlLocale(), { maximumFractionDigits: 1 })}%`;
}

export function getErrorMessage(error: unknown): string {
  if (typeof error === 'object' && error !== null && 'message' in error) {
    const message = (error as { message?: unknown }).message;
    if (typeof message === 'string' && message.trim().length > 0) {
      return message;
    }
  }
  return currentLocale() === 'tr'
    ? 'Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.'
    : 'An unexpected error occurred. Please try again.';
}

export function toNodeStatus(status: string): NodeStatus {
  switch (status.toLowerCase()) {
    case 'healthy':
    case 'online':
    case 'active':
    case 'completed':
      return 'healthy';
    case 'warning':
    case 'degraded':
    case 'partialsuccess':
      return 'warning';
    case 'critical':
    case 'error':
    case 'failed':
      return 'critical';
    default:
      return 'unknown';
  }
}

export function formatProperty(value: unknown): string {
  if (value == null) return '—';
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

export function shortId(value: string, length = 8): string {
  return value.length <= length ? value : value.slice(0, length);
}
