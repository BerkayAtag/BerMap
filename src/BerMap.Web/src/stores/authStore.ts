import { create } from 'zustand';
import type { User } from '@/types';
import { TOKEN_STORAGE_KEY, getStoredToken, setUnauthorizedHandler } from '@/lib/api';
import { queryClient } from '@/lib/queryClient';

interface AuthState {
  user: User | null;
  token: string | null;
  isAuthenticated: boolean;
  login: (user: User, token: string) => void;
  setUser: (user: User) => void;
  logout: () => void;
}

function writeToken(token: string | null): void {
  try {
    if (token) localStorage.setItem(TOKEN_STORAGE_KEY, token);
    else localStorage.removeItem(TOKEN_STORAGE_KEY);
  } catch {
    // Storage unavailable (private mode); the session simply will not persist.
  }
}

const initialToken = getStoredToken();

/**
 * Only the bearer token is persisted. The user profile is restored on startup
 * from GET /api/auth/me (see ProtectedRoute).
 */
export const useAuthStore = create<AuthState>((set) => ({
  user: null,
  token: initialToken,
  isAuthenticated: false,
  login: (user, token) => {
    writeToken(token);
    set({ user, token, isAuthenticated: true });
  },
  setUser: (user) => set({ user, isAuthenticated: true }),
  logout: () => {
    writeToken(null);
    queryClient.clear();
    set({ user: null, token: null, isAuthenticated: false });
  },
}));

// Any 401 from an authenticated request drops the session; ProtectedRoute then
// redirects to /login without a full page reload, so no redirect loop can occur.
setUnauthorizedHandler(() => {
  if (useAuthStore.getState().token !== null || getStoredToken() !== null) {
    useAuthStore.getState().logout();
  }
});

export const ROLES = {
  PlatformAdministrator: 'PlatformAdministrator',
  IntegrationAdministrator: 'IntegrationAdministrator',
  InfrastructureOperator: 'InfrastructureOperator',
  SecurityAuditor: 'SecurityAuditor',
  ReadOnlyViewer: 'ReadOnlyViewer',
} as const;

/** Integration create/update/delete/test/discover/cancel. */
export function canManageIntegrations(role?: string | null): boolean {
  return role === ROLES.PlatformAdministrator || role === ROLES.IntegrationAdministrator;
}

/** Snapshot creation and reconciliation review resolution. */
export function canOperate(role?: string | null): boolean {
  return canManageIntegrations(role) || role === ROLES.InfrastructureOperator;
}

export function useCanManageIntegrations(): boolean {
  return useAuthStore((state) => canManageIntegrations(state.user?.role));
}

export function useCanOperate(): boolean {
  return useAuthStore((state) => canOperate(state.user?.role));
}
