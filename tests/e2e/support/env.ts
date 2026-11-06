import path from 'node:path';
import { fileURLToPath } from 'node:url';

/*
 * Run configuration. The console and the API must share one origin
 * (the packaged server, or the Vite dev server proxying /api).
 */

const here = path.dirname(fileURLToPath(import.meta.url));

function required(name: string): string {
  const value = process.env[name]?.trim();
  if (!value) {
    throw new Error(`${name} is not set. See tests/e2e/README.md for the variables the suite needs.`);
  }
  return value;
}

export const BASE_URL = (process.env.BERMAP_E2E_BASE_URL?.trim() || 'http://localhost:3130').replace(/\/+$/, '');

export function adminPassword(): string {
  return required('BERMAP_E2E_ADMIN_PASSWORD');
}

export function enrollmentToken(): string {
  return required('BERMAP_E2E_ENROLLMENT_TOKEN');
}

/** Written by globalSetup: one storage state per role plus the seeded ids. */
export const AUTH_DIR = path.resolve(here, '..', '.auth');

export const ADMIN_USERNAME = 'admin';
