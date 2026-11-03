import type { NodeStatus } from '@/types';

const dateTimeFormatter = new Intl.DateTimeFormat('tr-TR', {
  dateStyle: 'medium',
  timeStyle: 'short',
});

const relativeTimeFormatter = new Intl.RelativeTimeFormat('tr', {
  numeric: 'auto',
});

export function formatDateTime(value?: string | null): string {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return dateTimeFormatter.format(date);
}

export function formatRelativeTime(value?: string | null): string {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';

  const seconds = Math.round((date.getTime() - Date.now()) / 1000);
  const absolute = Math.abs(seconds);

  if (absolute < 60) return relativeTimeFormatter.format(seconds, 'second');
  if (absolute < 3_600) return relativeTimeFormatter.format(Math.round(seconds / 60), 'minute');
  if (absolute < 86_400) return relativeTimeFormatter.format(Math.round(seconds / 3_600), 'hour');
  return relativeTimeFormatter.format(Math.round(seconds / 86_400), 'day');
}

export function formatNumber(value?: number | null): string {
  return (value ?? 0).toLocaleString('tr-TR');
}

export function formatDuration(durationMs?: number | null): string {
  if (durationMs == null || durationMs < 0) return '—';
  if (durationMs < 1_000) return `${durationMs} ms`;
  const seconds = Math.round(durationMs / 1_000);
  if (seconds < 60) return `${seconds} sn`;
  const minutes = Math.floor(seconds / 60);
  const remainingSeconds = seconds % 60;
  return `${minutes} dk ${remainingSeconds} sn`;
}

export function formatPercent(value?: number | null): string {
  return `${(value ?? 0).toLocaleString('tr-TR', { maximumFractionDigits: 1 })}%`;
}

export function getErrorMessage(error: unknown): string {
  if (typeof error === 'object' && error !== null && 'message' in error) {
    const message = (error as { message?: unknown }).message;
    if (typeof message === 'string' && message.trim().length > 0) {
      return message;
    }
  }
  return 'Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.';
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
