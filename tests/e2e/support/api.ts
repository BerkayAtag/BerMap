import { BASE_URL } from './env';

/*
 * A small client for the BerMap API, used by globalSetup and by tests that
 * prepare or check state outside the browser. Requests go to the same origin
 * the console is served from.
 */

export class ApiError extends Error {
  constructor(
    readonly method: string,
    readonly path: string,
    readonly status: number,
    readonly body: string,
  ) {
    super(`${method} ${path} -> ${status}: ${body.slice(0, 500)}`);
  }
}

export interface LoginResult {
  token: string;
  user: { id: string; username: string; role: string; displayName: string };
}

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

export async function apiRequest<T>(
  method: string,
  path: string,
  options: { token?: string; body?: unknown; headers?: Record<string, string> } = {},
): Promise<T> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json', ...options.headers };
  if (options.token) headers.Authorization = `Bearer ${options.token}`;
  const response = await fetch(`${BASE_URL}${path}`, {
    method,
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  });
  const text = await response.text();
  if (!response.ok) throw new ApiError(method, path, response.status, text);
  return (text ? JSON.parse(text) : undefined) as T;
}

/**
 * Signs in through POST /api/auth/login. The endpoint allows 10 attempts per
 * minute per client address; a 429 waits for the next window instead of failing.
 */
export async function login(username: string, password: string): Promise<LoginResult> {
  for (let attempt = 0; ; attempt++) {
    try {
      return await apiRequest<LoginResult>('POST', '/api/auth/login', { body: { username, password } });
    } catch (error) {
      if (error instanceof ApiError && error.status === 429 && attempt < 3) {
        await sleep(21_000);
        continue;
      }
      throw error;
    }
  }
}

/** Polls GET /healthz/ready until the server reports Healthy (migrations applied). */
export async function waitForReady(timeoutMs = 120_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let last = '';
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`${BASE_URL}/healthz/ready`);
      last = `${response.status} ${await response.text()}`;
      if (response.ok) return;
    } catch (error) {
      last = String(error);
    }
    await sleep(1000);
  }
  throw new Error(`${BASE_URL}/healthz/ready did not become ready within ${timeoutMs / 1000}s (last: ${last})`);
}

export interface UserDto {
  id: string;
  username: string;
  displayName: string;
  email: string;
  role: string;
  isActive: boolean;
}

export interface IntegrationDto {
  id: string;
  name: string;
  adapterType: string;
  isEnabled: boolean;
  collectorManaged: boolean;
  configuration: Record<string, unknown>;
}

export interface NodeDto {
  id: string;
  name: string;
  nodeType: string;
  fqdn?: string;
}

export interface SnapshotDto {
  id: string;
  name: string;
}

export interface CollectorDto {
  id: string;
  name: string;
}

/** Creates a user, or resets the password and role of an existing one, so reruns are idempotent. */
export async function ensureUser(
  token: string,
  user: { username: string; displayName: string; role: string; password: string },
): Promise<UserDto> {
  const users = await apiRequest<UserDto[]>('GET', '/api/users', { token });
  const existing = users.find((u) => u.username.toLowerCase() === user.username.toLowerCase());
  if (!existing) {
    return apiRequest<UserDto>('POST', '/api/users', { token, body: { ...user, email: '' } });
  }
  await apiRequest('PUT', `/api/users/${existing.id}`, {
    token,
    body: { displayName: user.displayName, role: user.role, isActive: true },
  });
  await apiRequest('POST', `/api/users/${existing.id}/reset-password`, { token, body: { password: user.password } });
  return { ...existing, role: user.role, displayName: user.displayName, isActive: true };
}

/** A name that does not collide with earlier runs against the same database. */
export function uniqueName(prefix: string): string {
  return `${prefix}-${Date.now().toString(36)}${Math.floor(Math.random() * 1296).toString(36)}`;
}
