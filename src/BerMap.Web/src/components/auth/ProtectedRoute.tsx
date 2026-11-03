import React, { useEffect, useState } from 'react';
import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { RotateCw } from 'lucide-react';
import { api } from '@/lib/api';
import { useAuthStore } from '@/stores/authStore';
import { useT } from '@/lib/i18n-runtime';
import type { User } from '@/types';

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

  useEffect(() => {
    if (!token || user) return;
    let cancelled = false;
    api
      .get<User>('/auth/me')
      .then((me) => {
        if (!cancelled) setUser(me);
      })
      .catch(() => {
        if (!cancelled) {
          setFailed(true);
          logout();
        }
      });
    return () => {
      cancelled = true;
    };
  }, [token, user, setUser, logout]);

  if (!token || failed) {
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />;
  }

  if (!user) {
    return (
      <div
        className="min-h-screen w-full flex items-center justify-center gap-2 bg-neutral-100 dark:bg-dark-bg text-sm text-neutral-500 dark:text-dark-text-secondary"
        role="status"
      >
        <RotateCw className="w-4 h-4 animate-spin" />
        {t('restoringSession')}
      </div>
    );
  }

  return <Outlet />;
};
