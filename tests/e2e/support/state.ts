import fs from 'node:fs';
import fsp from 'node:fs/promises';
import path from 'node:path';
import { AUTH_DIR } from './env';
import type { SeedResult } from './seed';

export type RoleName = 'admin' | 'viewer' | 'operator' | 'auditor';

/**
 * Accounts globalSetup creates (or resets) for role-gating tests. They are only
 * read with; tests that change accounts create their own users.
 */
export const ROLE_USERS = {
  viewer: { username: 'e2e-viewer', displayName: 'E2E Görüntüleyici', role: 'ReadOnlyViewer', password: 'Viewer-Pass-2026!x' },
  operator: { username: 'e2e-operator', displayName: 'E2E Operatör', role: 'InfrastructureOperator', password: 'Operator-Pass-2026!x' },
  auditor: { username: 'e2e-auditor', displayName: 'E2E Denetçi', role: 'SecurityAuditor', password: 'Auditor-Pass-2026!x' },
} as const;

export interface SetupState extends SeedResult {
  tokens: Record<RoleName, string>;
  userIds: Record<RoleName, string>;
}

const STATE_FILE = path.join(AUTH_DIR, 'state.json');

export async function writeSetupState(state: SetupState): Promise<void> {
  await fsp.writeFile(STATE_FILE, JSON.stringify(state, null, 2));
}

let cached: SetupState | undefined;

export function setupState(): SetupState {
  cached ??= JSON.parse(fs.readFileSync(STATE_FILE, 'utf8')) as SetupState;
  return cached;
}

/** Bearer token of the seeded admin session, for API calls from tests. */
export function adminToken(): string {
  return setupState().tokens.admin;
}

/** Playwright storage state file that starts a test signed in as the role. */
export function authFile(role: RoleName): string {
  return path.join(AUTH_DIR, `${role}.json`);
}
