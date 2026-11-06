import fs from 'node:fs/promises';
import { test as base, expect, type Page } from '@playwright/test';
import { authFile, type RoleName } from './state';

/*
 * Shared fixtures and assertions. `problems` records what a page reports as
 * broken while a test runs: console errors, uncaught exceptions, failed /api
 * responses and /api requests that never completed.
 */

export interface PageProblems {
  readonly list: string[];
  /** Fails when anything was recorded; `allow` drops entries a test expects. */
  expectNone(allow?: RegExp[]): void;
  clear(): void;
}

export function trackProblems(page: Page): PageProblems {
  const list: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error') list.push(`console: ${message.text()}`);
  });
  page.on('pageerror', (error) => list.push(`pageerror: ${error.message}`));
  page.on('response', (response) => {
    const url = new URL(response.url());
    if (url.pathname.startsWith('/api/') && response.status() >= 400) {
      list.push(`api: ${response.request().method()} ${url.pathname}${url.search} -> ${response.status()}`);
    }
  });
  page.on('requestfailed', (request) => {
    const url = new URL(request.url());
    // Navigations and unmounts cancel in-flight requests; that is not a failure.
    if (url.pathname.startsWith('/api/') && request.failure()?.errorText !== 'net::ERR_ABORTED') {
      list.push(`api: ${request.method()} ${url.pathname} failed: ${request.failure()?.errorText}`);
    }
  });
  return {
    list,
    expectNone(allow = []) {
      const unexpected = list.filter((entry) => !allow.some((pattern) => pattern.test(entry)));
      expect(unexpected, 'console errors or failed /api requests').toEqual([]);
    },
    clear() {
      list.length = 0;
    },
  };
}

export const test = base.extend<{ problems: PageProblems }>({
  problems: async ({ page }, use) => {
    await use(trackProblems(page));
  },
});

export { expect };

/** Starts the file's tests signed in as the role (storage state from globalSetup). */
export function useRole(role: RoleName): void {
  test.use({ storageState: authFile(role) });
}

/** Neither the document nor the main column may scroll or clip sideways. */
export async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => {
    const doc = document.documentElement;
    const main = document.querySelector('main');
    return {
      document: doc.scrollWidth - doc.clientWidth,
      main: main ? main.scrollWidth - main.clientWidth : 0,
    };
  });
  expect(overflow, 'horizontal overflow in px').toEqual({ document: 0, main: 0 });
}

export interface DownloadedFile {
  filename: string;
  text: string;
}

/** Runs the action, waits for the browser download it starts and returns its content. */
export async function captureDownload(page: Page, action: () => Promise<unknown>): Promise<DownloadedFile> {
  const [download] = await Promise.all([page.waitForEvent('download'), action()]);
  const file = await download.path();
  return { filename: download.suggestedFilename(), text: await fs.readFile(file, 'utf8') };
}

/** Splits CSV text (RFC 4180 quoting, `,` or `;` separator) into rows of cells. */
export function parseCsv(text: string): string[][] {
  const body = text.replace(/^﻿/, '');
  const firstLine = body.split(/\r?\n/, 1)[0] ?? '';
  const separator = (firstLine.match(/;/g)?.length ?? 0) > (firstLine.match(/,/g)?.length ?? 0) ? ';' : ',';
  const rows: string[][] = [];
  let row: string[] = [];
  let cell = '';
  let quoted = false;
  for (let i = 0; i < body.length; i++) {
    const ch = body[i];
    if (quoted) {
      if (ch === '"' && body[i + 1] === '"') {
        cell += '"';
        i++;
      } else if (ch === '"') {
        quoted = false;
      } else {
        cell += ch;
      }
    } else if (ch === '"') {
      quoted = true;
    } else if (ch === separator) {
      row.push(cell);
      cell = '';
    } else if (ch === '\n' || ch === '\r') {
      if (ch === '\r' && body[i + 1] === '\n') i++;
      row.push(cell);
      rows.push(row);
      row = [];
      cell = '';
    } else {
      cell += ch;
    }
  }
  if (cell !== '' || row.length > 0) {
    row.push(cell);
    rows.push(row);
  }
  return rows;
}

/** No cell may start with a character a spreadsheet would evaluate as a formula. */
export function expectFormulaSafe(rows: string[][]): void {
  const unsafe = rows.flat().filter((cell) => /^[=+\-@\t\r]/.test(cell) && !/^-?\d/.test(cell));
  expect(unsafe, 'CSV cells that start a formula').toEqual([]);
}
