import React, { useId, useState } from 'react';
import { Card } from '@/components/ui/Card';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Lock, User as UserIcon, ShieldCheck } from 'lucide-react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { useAuthStore } from '@/stores/authStore';
import { useLocale } from '@/lib/i18n';
import { api } from '@/lib/api';
import type { ApiError, User } from '@/types';

function getRedirectTarget(state: unknown): string {
  const from = (state as { from?: unknown } | null)?.from;
  return typeof from === 'string' && from.startsWith('/') && !from.startsWith('/login') ? from : '/';
}

export const LoginPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const navigate = useNavigate();
  const location = useLocation();
  const login = useAuthStore((state) => state.login);
  const token = useAuthStore((state) => state.token);
  const usernameId = useId();
  const passwordId = useId();
  const errorId = useId();
  const redirectTo = getRedirectTarget(location.state);

  if (token && !submitting) {
    return <Navigate to={redirectTo} replace />;
  }

  const handleLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    if (!username.trim() || !password.trim()) {
      setError(isTr ? 'Kullanıcı adı ve parola zorunludur.' : 'Username and password are required.');
      return;
    }
    setSubmitting(true);
    try {
      const response = await api.post<{
        token: string;
        expiresAt: string;
        user: User;
      }>('/auth/login', { username: username.trim(), password });
      login(response.user, response.token);
      setSubmitting(false);
      navigate(redirectTo, { replace: true });
    } catch (err) {
      const apiError = err as Partial<ApiError>;
      if (apiError?.status === 429) {
        setError(
          apiError.message && apiError.message !== 'Too Many Requests'
            ? apiError.message
            : isTr
              ? 'Çok fazla giriş denemesi. Lütfen bir dakika sonra tekrar deneyin.'
              : 'Too many sign-in attempts. Please try again in a minute.',
        );
      } else if (apiError?.status === 401) {
        setError(
          apiError.message && apiError.message !== 'Unauthorized'
            ? apiError.message
            : isTr
              ? 'Kullanıcı adı veya parola hatalı.'
              : 'Invalid username or password.',
        );
      } else {
        setError(apiError?.message || (isTr ? 'Giriş başarısız.' : 'Login failed.'));
      }
      setSubmitting(false);
    }
  };

  return (
    <div className="min-h-screen w-full flex items-center justify-center p-4 bg-neutral-100 dark:bg-dark-bg transition-colors">
      <Card className="w-full max-w-md p-8 border-neutral-200 dark:border-dark-border shadow-xl">
        <div className="flex flex-col items-center text-center mb-8">
          {/* Brand logo badge */}
          <div className="w-14 h-14 rounded-2xl bg-brand-primary text-white font-black text-3xl shadow-lg flex items-center justify-center mb-4">
            B
          </div>
          <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
            Ber<span className="text-brand-primary dark:text-brand-primary-dark">Map</span> Web Console
          </h1>
          <p lang="en" className="text-xs uppercase tracking-widest font-semibold text-neutral-500 mt-1">
            Enterprise Infrastructure Intelligence · BerSim Ready
          </p>
        </div>

        <form onSubmit={handleLogin} className="space-y-4">
          <div>
            <label htmlFor={usernameId} className="block text-xs font-semibold uppercase tracking-wider text-neutral-600 dark:text-neutral-400 mb-1.5">
              {isTr ? 'Kullanıcı Adı veya Kurumsal Kimlik' : 'Username or Corporate Identity'}
            </label>
            <div className="relative">
              <UserIcon className="w-4 h-4 absolute left-3 top-3 text-neutral-400" aria-hidden="true" />
              <Input
                id={usernameId}
                type="text"
                autoComplete="username"
                autoFocus
                aria-describedby={error ? errorId : undefined}
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                className="pl-9"
                required
              />
            </div>
          </div>

          <div>
            <label htmlFor={passwordId} className="block text-xs font-semibold uppercase tracking-wider text-neutral-600 dark:text-neutral-400 mb-1.5">
              {isTr ? 'Parola' : 'Password'}
            </label>
            <div className="relative">
              <Lock className="w-4 h-4 absolute left-3 top-3 text-neutral-400" aria-hidden="true" />
              <Input
                id={passwordId}
                type="password"
                autoComplete="current-password"
                aria-describedby={error ? errorId : undefined}
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                className="pl-9"
                required
              />
            </div>
          </div>

          {error && (
            <div id={errorId} role="alert" className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300">
              {error}
            </div>
          )}

          <Button type="submit" variant="primary" className="w-full mt-2 bg-brand-primary" disabled={submitting}>
            {submitting
              ? (isTr ? 'Giriş yapılıyor...' : 'Signing in...')
              : (isTr ? 'Güvenli Giriş Yap' : 'Secure Sign In')}
          </Button>
        </form>

        <div className="mt-8 pt-6 border-t border-neutral-200 dark:border-dark-border text-center text-xs text-neutral-400 dark:text-neutral-500 flex items-center justify-center gap-1.5">
          <ShieldCheck className="w-4 h-4 text-emerald-600" />
          {isTr ? 'Air-Gapped & Kurum İçi Güvenli Oturum' : 'Air-Gapped & On-Premises Secure Session'}
        </div>
      </Card>
    </div>
  );
};
