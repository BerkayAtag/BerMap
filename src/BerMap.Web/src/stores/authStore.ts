import { create } from 'zustand';
import type { User } from '@/types';
import { TOKEN_STORAGE_KEY, api, getStoredToken, setForbiddenHandler, setUnauthorizedHandler } from '@/lib/api';
import { queryClient } from '@/lib/queryClient';
import type { SignOutReason } from '@/components/auth/loginText';

interface AuthState {
  user: User | null;
  token: string | null;
  isAuthenticated: boolean;
  /** Why the last session ended without the user signing out; shown on /login. */
  signOutReason: SignOutReason | null;
  login: (user: User, token: string) => void;
  setUser: (user: User) => void;
  /** Replaces the bearer token (e.g. the one issued after a password change). */
  setToken: (token: string) => void;
  /** Re-reads the signed-in user's profile (role, name) from GET /api/auth/me. */
  refreshProfile: () => Promise<void>;
  /** Ends the session; `reason` explains a sign-out the user did not ask for. */
  logout: (reason?: SignOutReason | null) => void;
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

/** The /auth/me request of a running refreshProfile(), shared by concurrent callers. */
let profileRefresh: Promise<void> | null = null;

/**
 * Only the bearer token is persisted. The user profile is restored on startup
 * from GET /api/auth/me (see ProtectedRoute) and re-read by refreshProfile().
 */
export const useAuthStore = create<AuthState>((set, get) => ({
  user: null,
  token: initialToken,
  isAuthenticated: false,
  signOutReason: null,
  login: (user, token) => {
    writeToken(token);
    set({ user, token, isAuthenticated: true, signOutReason: null });
  },
  setUser: (user) => set({ user, isAuthenticated: true }),
  setToken: (token) => {
    writeToken(token);
    set({ token });
  },
  refreshProfile: () => {
    if (!profileRefresh) {
      profileRefresh = api
        .get<User>('/auth/me')
        .then((me) => {
          const previous = get().user;
          if (!get().token || !me) return;
          set({ user: me, isAuthenticated: true });
          // Lists fetched under the old role may now be allowed or forbidden.
          if (previous && previous.role !== me.role) void queryClient.invalidateQueries();
        })
        // A 401 already ended the session; other failures keep the current profile.
        .catch(() => undefined)
        .finally(() => {
          profileRefresh = null;
        });
    }
    return profileRefresh;
  },
  logout: (reason = null) => {
    writeToken(null);
    queryClient.clear();
    set({ user: null, token: null, isAuthenticated: false, signOutReason: reason });
  },
}));

// Any 401 from an authenticated request drops the session; ProtectedRoute then
// redirects to /login without a full page reload, so no redirect loop can occur.
// The token expired or was revoked (role change, password reset, deactivation):
// LoginPage says so instead of showing a bare form.
setUnauthorizedHandler(() => {
  if (useAuthStore.getState().token !== null || getStoredToken() !== null) {
    useAuthStore.getState().logout('session_ended');
  }
});

// The server applies role changes on the next request, so a 403 may mean the
// role shown in the console is out of date: re-read the profile (at most every
// few seconds; /auth/me itself never triggers it).
const FORBIDDEN_REFRESH_INTERVAL_MS = 5000;
let lastForbiddenRefresh = 0;
setForbiddenHandler((path) => {
  if (path.split('?')[0].toLowerCase() === '/auth/me') return;
  const state = useAuthStore.getState();
  if (!state.token || !state.user) return;
  const now = Date.now();
  if (now - lastForbiddenRefresh < FORBIDDEN_REFRESH_INTERVAL_MS) return;
  lastForbiddenRefresh = now;
  void state.refreshProfile();
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
