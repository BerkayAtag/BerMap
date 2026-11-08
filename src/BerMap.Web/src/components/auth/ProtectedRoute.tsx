import React, { useEffect, useState } from 'react';
import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { RotateCw } from 'lucide-react';
import { api } from '@/lib/api';
import { useAuthStore } from '@/stores/authStore';
import { useT } from '@/lib/i18n-runtime';
import type { ApiError, User } from '@/types';

/**
 * Guards authenticated routes. Without a token the user is sent to /login;
 * with a stored token but no profile yet, the session is restored via /auth/me.
 */
export const ProtectedRoute: React.FC = () => {
  const token = useAuthStore((state) => state.token);
  const user = useAuthStore((state) => state.user);
  const setUser = useAuthStore((state) => state.setUser);
  const logout = useAuthStore((state) => state.logout);
  const location = useLocation();
  const t = useT();
  const [failed, setFailed] = useState(false);
  const [unreachable, setUnreachable] = useState(false);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!token || user) return;
    let cancelled = false;
    setUnreachable(false);
    api
      .get<User>('/auth/me')
      .then((me) => {
        if (!cancelled) setUser(me);
      })
      .catch((err: ApiError) => {
        if (cancelled) return;
        // Only a rejected token ends the session. A restarting API, a proxy
        // 502 or a network error must not throw away a still-valid token.
        if (err?.status === 401 || err?.status === 403) {
          setFailed(true);
          logout('session_ended');
        } else {
          setUnreachable(true);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [token, user, setUser, logout, attempt]);

  if (!token || failed) {
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />;
  }

  if (!user) {
    return (
      <div
        className="min-h-screen w-full flex items-center justify-center gap-2 bg-neutral-100 dark:bg-dark-bg text-sm text-neutral-500 dark:text-dark-text-secondary"
        role="status"
      >
        {unreachable ? (
          <>
            {t('sessionRestoreFailed')}
            <button
              type="button"
              onClick={() => setAttempt((value) => value + 1)}
              className="ml-2 font-semibold text-brand-primary dark:text-brand-primary-dark underline"
            >
              {t('retry')}
            </button>
          </>
        ) : (
          <>
            <RotateCw className="w-4 h-4 animate-spin" />
            {t('restoringSession')}
          </>
        )}
      </div>
    );
  }

  return <Outlet />;
};
