/*
 * CSV helpers shared by every client-side export. Cells are quoted and
 * formula-safe (a leading =, +, -, @, tab or carriage return is prefixed with
 * an apostrophe so spreadsheet applications never evaluate them), and files are
 * written with a UTF-8 BOM so Excel reads Turkish characters correctly.
 */

import type { ApiError } from '@/types';
import { getStoredToken } from '@/lib/api';

const FORMULA_PREFIX = /^[=+\-@\t\r]/;

function stringify(value: unknown): string {
  if (value === null || value === undefined) return '';
  if (value instanceof Date) return Number.isNaN(value.getTime()) ? '' : value.toISOString();
  if (typeof value === 'object') {
    try {
      return JSON.stringify(value);
    } catch {
      return String(value);
    }
  }
  return String(value);
}

/** Returns one quoted, formula-safe CSV cell. Numbers and booleans are not prefixed. */
export function csvCell(value: unknown): string {
  let text = stringify(value);
  if (typeof value !== 'number' && typeof value !== 'bigint' && FORMULA_PREFIX.test(text)) {
    text = `'${text}`;
  }
  return `"${text.replace(/"/g, '""')}"`;
}

/** Serialises rows (first row is usually the header) into CSV text without a BOM. */
export function toCsv(rows: unknown[][]): string {
  return rows.map((row) => row.map(csvCell).join(',')).join('\r\n');
}

/** Saves a CSV text or blob that is already formatted (for example a server export). */
export function downloadBlob(filename: string, blob: Blob): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  anchor.rel = 'noopener';
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  // Revoke after the click has been handled so the download is not cancelled.
  window.setTimeout(() => URL.revokeObjectURL(url), 1_000);
}

/** Builds a UTF-8 (BOM) CSV file from rows and starts the browser download. */
export function downloadCsv(filename: string, rows: unknown[][]): void {
  const blob = new Blob(['﻿', toCsv(rows)], { type: 'text/csv;charset=utf-8' });
  downloadBlob(filename, blob);
}

function fileNameFromDisposition(header: string | null): string | null {
  if (!header) return null;
  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(header);
  if (encoded) {
    try {
      return decodeURIComponent(encoded[1].trim());
    } catch {
      // Fall through to the plain filename.
    }
  }
  const plain = /filename="?([^";]+)"?/i.exec(header);
  return plain ? plain[1].trim() : null;
}

/**
 * Downloads a CSV the server already formatted (for example GET /api/audit/export).
 * The request carries the bearer token, so it cannot be a plain link. Rejects
 * with an ApiError ({ status, message }) when the server refuses the export.
 */
export async function downloadServerCsv(path: string, fallbackFilename: string): Promise<void> {
  const token = getStoredToken();
  const response = await fetch(`/api${path}`, {
    headers: token ? { Authorization: `Bearer ${token}` } : undefined,
  });
  if (!response.ok) {
    const error: ApiError = { status: response.status, message: response.statusText };
    try {
      const body = JSON.parse(await response.text());
      error.message = body.message || body.title || error.message;
      error.code = body.code;
    } catch {
      // Not a JSON error body.
    }
    throw error;
  }
  const blob = await response.blob();
  downloadBlob(fileNameFromDisposition(response.headers.get('content-disposition')) ?? fallbackFilename, blob);
}
