import React, { useState } from 'react';
import { Card } from '@/components/ui/Card';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Lock, User, ShieldCheck } from 'lucide-react';
import { useNavigate } from 'react-router-dom';
import { useAuthStore } from '@/stores/authStore';
import { useLocale } from '@/lib/i18n';
import { api } from '@/lib/api';

export const LoginPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const navigate = useNavigate();
  const { login } = useAuthStore();

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
        user: { id: string; username: string; displayName: string; role: string; email: string };
      }>('/auth/login', { username: username.trim(), password });
      login(
        {
          id: response.user.id,
          username: response.user.username,
          displayName: response.user.displayName,
          email: response.user.email,
          role: response.user.role,
        },
        response.token,
      );
      navigate('/');
    } catch (err) {
      const apiError = err as { message?: string };
      setError(apiError?.message || (isTr ? 'Giriş başarısız.' : 'Login failed.'));
    } finally {
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
          <p className="text-xs uppercase tracking-widest font-semibold text-neutral-500 mt-1">
            Enterprise Infrastructure Intelligence · BerSim Ready
          </p>
        </div>

        <form onSubmit={handleLogin} className="space-y-4">
          <div>
            <label className="block text-xs font-semibold uppercase tracking-wider text-neutral-600 dark:text-neutral-400 mb-1.5">
              {isTr ? 'Kullanıcı Adı veya Kurumsal Kimlik' : 'Username or Corporate Identity'}
            </label>
            <div className="relative">
              <User className="w-4 h-4 absolute left-3 top-3 text-neutral-400" />
              <Input
                type="text"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                className="pl-9"
                required
              />
            </div>
          </div>

          <div>
            <label className="block text-xs font-semibold uppercase tracking-wider text-neutral-600 dark:text-neutral-400 mb-1.5">
              {isTr ? 'Parola' : 'Password'}
            </label>
            <div className="relative">
              <Lock className="w-4 h-4 absolute left-3 top-3 text-neutral-400" />
              <Input
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                className="pl-9"
                required
              />
            </div>
          </div>

          {error && (
            <div className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300">
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
