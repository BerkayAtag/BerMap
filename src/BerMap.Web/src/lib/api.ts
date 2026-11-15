import type { ApiError } from '@/types';

const BASE_URL = '/api';
export const TOKEN_STORAGE_KEY = 'bermap-token';

type UnauthorizedHandler = () => void;
type ForbiddenHandler = (path: string) => void;

let unauthorizedHandler: UnauthorizedHandler | null = null;
let forbiddenHandler: ForbiddenHandler | null = null;
let sessionToken: string | null | undefined;

/** Keep the active session usable even when browser storage is disabled. */
export function setSessionToken(token: string | null): void {
  sessionToken = token;
  try {
    if (token) localStorage.setItem(TOKEN_STORAGE_KEY, token);
    else localStorage.removeItem(TOKEN_STORAGE_KEY);
  } catch { /* The session remains available in memory. */ }
}

/**
 * Registers the callback invoked when an authenticated request is rejected with 401.
 * The auth store registers itself here so api.ts stays free of store imports.
 */
export function setUnauthorizedHandler(handler: UnauthorizedHandler | null): void {
  unauthorizedHandler = handler;
}

/**
 * Registers the callback invoked when an authenticated request is rejected with 403.
 * The auth store uses it to re-read the profile: the role may have changed on the server.
 */
export function setForbiddenHandler(handler: ForbiddenHandler | null): void {
  forbiddenHandler = handler;
}

export function getStoredToken(): string | null {
  if (sessionToken !== undefined) return sessionToken;
  try {
    return localStorage.getItem(TOKEN_STORAGE_KEY);
  } catch {
    return null;
  }
}

function getAuthHeaders(token: string | null): HeadersInit {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
  };
  if (token) {
    headers['Authorization'] = `Bearer ${token}`;
  }
  return headers;
}

function isLoginRequest(path: string): boolean {
  return path.split('?')[0].toLowerCase() === '/auth/login';
}

function handleUnauthorized(): void {
  if (unauthorizedHandler) {
    unauthorizedHandler();
    return;
  }
  setSessionToken(null);
  if (window.location.pathname !== '/login') {
    window.location.assign('/login');
  }
}

async function handleResponse<T>(path: string, response: Response, token: string | null): Promise<T> {
  if (!response.ok) {
    if (response.status === 401 && token === getStoredToken() && !isLoginRequest(path)) {
      handleUnauthorized();
    }
    if (response.status === 403 && token === getStoredToken() && forbiddenHandler) {
      forbiddenHandler(path);
    }

    const error: ApiError = {
      status: response.status,
      message: response.statusText,
    };
    try {
      const text = await response.text();
      if (text) {
        const body = JSON.parse(text);
        error.message = body.message || body.title || error.message;
        error.code = body.code;
        error.details = body.errors || body.details;
      }
    } catch {
      // Response body is not JSON
    }
    throw error;
  }

  if (response.status === 204 || response.headers.get('content-length') === '0') {
    return undefined as T;
  }

  const text = await response.text();
  if (!text) {
    return undefined as T;
  }
  return JSON.parse(text) as T;
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const hasBody = body !== undefined && body !== null;
  const token = getStoredToken();
  const response = await fetch(`${BASE_URL}${path}`, {
    method,
    headers: getAuthHeaders(token),
    body: hasBody ? JSON.stringify(body) : undefined,
  });
  return handleResponse<T>(path, response, token);
}

export const api = {
  get<T>(path: string): Promise<T> {
    return request<T>('GET', path);
  },

  post<T>(path: string, body?: unknown): Promise<T> {
    return request<T>('POST', path, body);
  },

  put<T>(path: string, body?: unknown): Promise<T> {
    return request<T>('PUT', path, body);
  },

  delete<T>(path: string): Promise<T> {
    return request<T>('DELETE', path);
  },
};
