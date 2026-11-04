import React, { useId, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  AlertTriangle,
  KeyRound,
  Loader2,
  Pencil,
  RefreshCw,
  ShieldAlert,
  UserPlus,
  Users,
  X,
} from 'lucide-react';
import { Card } from '@/components/ui/Card';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { DialogShell, MessageBanner } from '@/components/users/DialogShell';
import {
  PASSWORD_MIN_LENGTH,
  ROLE_KEYS,
  ROLE_OPTIONS,
  userErrorMessage,
  validatePassword,
} from '@/components/users/userText';
import { api } from '@/lib/api';
import { useLocale } from '@/lib/i18n';
import { useT } from '@/lib/i18n-runtime';
import { formatDateTime, formatRelativeTime, getErrorMessage } from '@/lib/format';
import { ROLES, useAuthStore } from '@/stores/authStore';
import type { CreateUserRequest, UpdateUserRequest, UserAccount } from '@/types';

const USERS_QUERY_KEY = ['users', 'list'] as const;

const selectClass =
  'h-10 w-full rounded-md border px-3 text-sm bg-white text-neutral-900 border-neutral-300 ' +
  'focus:border-brand-primary focus:ring-2 focus:ring-brand-primary/20 outline-none ' +
  'dark:bg-dark-card dark:text-dark-text dark:border-dark-border dark:focus:border-brand-primary-dark';

const labelClass = 'block text-xs font-semibold text-neutral-600 dark:text-neutral-400 mb-1';

type DialogState =
  | { kind: 'create' }
  | { kind: 'edit'; user: UserAccount }
  | { kind: 'reset'; user: UserAccount };

export const UsersPage: React.FC = () => {
  const isTr = useLocale() === 'tr';
  const t = useT();
  const queryClient = useQueryClient();
  const currentUser = useAuthStore((state) => state.user);
  const isAdmin = currentUser?.role === ROLES.PlatformAdministrator;
  const [dialog, setDialog] = useState<DialogState | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const usersQuery = useQuery<UserAccount[]>({
    queryKey: USERS_QUERY_KEY,
    queryFn: () => api.get<UserAccount[]>('/users'),
    enabled: isAdmin,
  });

  const roleLabel = (role: string) => (ROLE_KEYS[role] ? t(ROLE_KEYS[role]) : role);

  const handleSaved = (message: string) => {
    setDialog(null);
    setNotice(message);
    void queryClient.invalidateQueries({ queryKey: USERS_QUERY_KEY });
  };

  if (!isAdmin) {
    return (
      <Card className="max-w-xl mx-auto text-center">
        <ShieldAlert className="w-10 h-10 mx-auto text-neutral-300 dark:text-neutral-600" />
        <h1 className="mt-3 font-bold text-neutral-900 dark:text-white">
          {isTr ? 'Kullanıcı yönetimi yetkisi gerekli' : 'User management permission required'}
        </h1>
        <p className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">
          {isTr
            ? 'Bu sayfayı yalnızca Platform Yöneticileri görebilir.'
            : 'Only Platform Administrators can open this page.'}
        </p>
      </Card>
    );
  }

  const users = usersQuery.data ?? [];
  const activeCount = users.filter((user) => user.isActive).length;

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      {dialog?.kind === 'create' && (
        <CreateUserDialog
          isTr={isTr}
          roleLabel={roleLabel}
          onClose={() => setDialog(null)}
          onSaved={(user) =>
            handleSaved(isTr ? `${user.username} kullanıcısı oluşturuldu.` : `User ${user.username} was created.`)
          }
        />
      )}
      {dialog?.kind === 'edit' && (
        <EditUserDialog
          isTr={isTr}
          user={dialog.user}
          isSelf={dialog.user.id === currentUser?.id}
          roleLabel={roleLabel}
          onClose={() => setDialog(null)}
          onSaved={(user) =>
            handleSaved(isTr ? `${user.username} güncellendi.` : `${user.username} was updated.`)
          }
        />
      )}
      {dialog?.kind === 'reset' && (
        <ResetPasswordDialog
          isTr={isTr}
          user={dialog.user}
          onClose={() => setDialog(null)}
          onSaved={() =>
            handleSaved(
              isTr
                ? `${dialog.user.username} için parola sıfırlandı. Yeni parolayı kullanıcıya güvenli bir kanaldan iletin.`
                : `The password of ${dialog.user.username} was reset. Share it with the user over a secure channel.`,
            )
          }
        />
      )}

      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
            {isTr ? 'Kullanıcı Yönetimi' : 'User Management'}
          </h1>
          <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1">
            {isTr
              ? 'Yerel BerMap hesapları, rolleri ve erişim durumu. Hesaplar silinmez; denetim kayıtları korunsun diye devre dışı bırakılır.'
              : 'Local BerMap accounts, their roles and access. Accounts are deactivated rather than deleted so audit records keep their owner.'}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            className="gap-2"
            onClick={() => void usersQuery.refetch()}
            disabled={usersQuery.isFetching}
          >
            <RefreshCw className={`w-4 h-4 ${usersQuery.isFetching ? 'animate-spin' : ''}`} />
            {isTr ? 'Yenile' : 'Refresh'}
          </Button>
          <Button variant="primary" size="sm" className="gap-2" onClick={() => setDialog({ kind: 'create' })}>
            <UserPlus className="w-4 h-4" />
            {isTr ? 'Yeni Kullanıcı' : 'New User'}
          </Button>
        </div>
      </div>

      {notice && (
        <div
          role="status"
          className="p-3 rounded-lg border border-emerald-200 dark:border-emerald-900 bg-emerald-50 dark:bg-emerald-950/30 text-sm text-emerald-800 dark:text-emerald-300 flex items-start gap-2"
        >
          <span className="flex-1 break-words">{notice}</span>
          <button
            type="button"
            onClick={() => setNotice(null)}
            className="text-emerald-600 dark:text-emerald-400"
            aria-label={isTr ? 'Bildirimi kapat' : 'Close notification'}
          >
            <X className="w-4 h-4" />
          </button>
        </div>
      )}

      <Card padding="none">
        <div className="flex flex-wrap items-center justify-between gap-2 p-4 border-b border-neutral-200 dark:border-dark-border">
          <div className="flex items-center gap-2">
            <Users className="w-4 h-4 text-brand-primary dark:text-brand-primary-dark" />
            <h2 className="font-bold text-sm text-neutral-900 dark:text-white">{isTr ? 'Hesaplar' : 'Accounts'}</h2>
          </div>
          {!usersQuery.isLoading && !usersQuery.isError && (
            <span className="text-xs text-neutral-500">
              {isTr
                ? `${users.length} hesap · ${activeCount} etkin`
                : `${users.length} accounts · ${activeCount} active`}
            </span>
          )}
        </div>

        {usersQuery.isLoading ? (
          <div className="p-4 space-y-2">
            {[0, 1, 2].map((index) => (
              <div key={index} className="h-12 rounded bg-neutral-100 dark:bg-dark-border animate-pulse" />
            ))}
          </div>
        ) : usersQuery.isError ? (
          <div className="p-6 text-sm text-rose-700 dark:text-rose-300 flex flex-col items-start gap-3">
            <span className="flex items-center gap-2">
              <AlertTriangle className="w-4 h-4" />
              {userErrorMessage(usersQuery.error, isTr) || getErrorMessage(usersQuery.error)}
            </span>
            <Button variant="outline" size="sm" onClick={() => void usersQuery.refetch()}>
              {isTr ? 'Tekrar Dene' : 'Retry'}
            </Button>
          </div>
        ) : users.length === 0 ? (
          <p className="p-6 text-sm text-neutral-500">{isTr ? 'Kullanıcı bulunamadı.' : 'No users found.'}</p>
        ) : (
          <>
            {/* Phones: one card per account. */}
            <ul className="md:hidden divide-y divide-neutral-100 dark:divide-dark-border">
              {users.map((user) => (
                <li key={user.id} className="p-4 space-y-2">
                  <div className="flex items-start justify-between gap-3">
                    <div className="min-w-0">
                      <div className="font-semibold text-sm text-neutral-900 dark:text-white truncate">
                        {user.displayName}
                        {user.id === currentUser?.id && <SelfTag isTr={isTr} />}
                      </div>
                      <div className="text-xs font-mono text-neutral-500 truncate">{user.username}</div>
                      {user.email && <div className="text-xs text-neutral-500 truncate">{user.email}</div>}
                    </div>
                    <ActiveBadge isTr={isTr} active={user.isActive} />
                  </div>
                  <div className="text-xs text-neutral-600 dark:text-neutral-300">
                    {roleLabel(user.role)} · {isTr ? 'Son giriş' : 'Last sign-in'}:{' '}
                    <span title={formatDateTime(user.lastLogin)}>
                      {user.lastLogin ? formatRelativeTime(user.lastLogin) : (isTr ? 'hiç' : 'never')}
                    </span>
                  </div>
                  <RowActions isTr={isTr} onEdit={() => setDialog({ kind: 'edit', user })} onReset={() => setDialog({ kind: 'reset', user })} />
                </li>
              ))}
            </ul>

            <div className="hidden md:block overflow-x-auto">
              <table className="w-full text-left text-sm">
                <thead>
                  <tr className="border-b border-neutral-200 dark:border-dark-border text-xs uppercase font-semibold text-neutral-400 dark:text-neutral-500">
                    <th className="py-2.5 pl-4">{isTr ? 'Kullanıcı' : 'User'}</th>
                    <th className="py-2.5">{isTr ? 'E-posta' : 'Email'}</th>
                    <th className="py-2.5">{isTr ? 'Rol' : 'Role'}</th>
                    <th className="py-2.5">{isTr ? 'Durum' : 'Status'}</th>
                    <th className="py-2.5">{isTr ? 'Son Giriş' : 'Last Sign-in'}</th>
                    <th className="py-2.5 pr-4 text-right">{isTr ? 'İşlem' : 'Actions'}</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-neutral-100 dark:divide-dark-border">
                  {users.map((user) => (
                    <tr key={user.id} className={user.isActive ? undefined : 'opacity-70'}>
                      <td className="py-3 pl-4">
                        <div className="font-semibold text-neutral-900 dark:text-white">
                          {user.displayName}
                          {user.id === currentUser?.id && <SelfTag isTr={isTr} />}
                        </div>
                        <div className="text-xs font-mono text-neutral-500">{user.username}</div>
                      </td>
                      <td className="py-3 text-neutral-600 dark:text-neutral-300">{user.email || '—'}</td>
                      <td className="py-3 text-neutral-700 dark:text-neutral-200">{roleLabel(user.role)}</td>
                      <td className="py-3">
                        <ActiveBadge isTr={isTr} active={user.isActive} />
                      </td>
                      <td className="py-3 text-neutral-600 dark:text-neutral-300 whitespace-nowrap" title={formatDateTime(user.lastLogin)}>
                        {user.lastLogin ? formatRelativeTime(user.lastLogin) : (isTr ? 'Hiç giriş yapmadı' : 'Never')}
                      </td>
                      <td className="py-3 pr-4">
                        <RowActions isTr={isTr} onEdit={() => setDialog({ kind: 'edit', user })} onReset={() => setDialog({ kind: 'reset', user })} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
      </Card>
    </div>
  );
};

const SelfTag: React.FC<{ isTr: boolean }> = ({ isTr }) => (
  <span className="ml-1.5 align-middle text-[10px] font-semibold px-1.5 py-0.5 rounded bg-brand-primary/10 text-brand-primary dark:text-brand-primary-dark">
    {isTr ? 'siz' : 'you'}
  </span>
);

const ActiveBadge: React.FC<{ isTr: boolean; active: boolean }> = ({ isTr, active }) => (
  <span
    className={`inline-flex items-center px-2 py-0.5 rounded-full border text-[11px] font-semibold shrink-0 ${
      active
        ? 'bg-emerald-50 dark:bg-emerald-950/40 text-emerald-700 dark:text-emerald-400 border-emerald-200 dark:border-emerald-800'
        : 'bg-neutral-100 dark:bg-dark-border text-neutral-600 dark:text-neutral-400 border-neutral-200 dark:border-dark-border'
    }`}
  >
    {active ? (isTr ? 'Etkin' : 'Active') : (isTr ? 'Devre dışı' : 'Disabled')}
  </span>
);

const RowActions: React.FC<{ isTr: boolean; onEdit: () => void; onReset: () => void }> = ({ isTr, onEdit, onReset }) => (
  <div className="flex flex-wrap items-center justify-end gap-1.5">
    <Button variant="outline" size="sm" className="gap-1.5" onClick={onEdit}>
      <Pencil className="w-3.5 h-3.5" />
      {isTr ? 'Düzenle' : 'Edit'}
    </Button>
    <Button variant="outline" size="sm" className="gap-1.5" onClick={onReset}>
      <KeyRound className="w-3.5 h-3.5" />
      {isTr ? 'Parola Sıfırla' : 'Reset Password'}
    </Button>
  </div>
);

interface CreateUserDialogProps {
  isTr: boolean;
  roleLabel: (role: string) => string;
  onClose: () => void;
  onSaved: (user: UserAccount) => void;
}

const CreateUserDialog: React.FC<CreateUserDialogProps> = ({ isTr, roleLabel, onClose, onSaved }) => {
  const fieldId = useId();
  const formId = `${fieldId}-form`;
  const [username, setUsername] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<string>(ROLES.ReadOnlyViewer);
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const handleSubmit = async (event: React.FormEvent) => {
    event.preventDefault();
    const name = username.trim();
    if (name.length < 2 || name.length > 64 || /\s/.test(name)) {
      setError(isTr ? 'Kullanıcı adı 2-64 karakter olmalı ve boşluk içermemelidir.' : 'The username must be 2-64 characters without spaces.');
      return;
    }
    const passwordError = validatePassword(password, confirm, name, isTr);
    if (passwordError) {
      setError(passwordError);
      return;
    }

    setSaving(true);
    setError(null);
    try {
      const payload: CreateUserRequest = {
        username: name,
        displayName: displayName.trim() || undefined,
        email: email.trim() || undefined,
        role,
        password,
      };
      const user = await api.post<UserAccount>('/users', payload);
      onSaved(user);
    } catch (requestError) {
      setError(userErrorMessage(requestError, isTr));
      setSaving(false);
    }
  };

  return (
    <DialogShell
      title={isTr ? 'Yeni Kullanıcı' : 'New User'}
      subtitle={isTr ? 'Yerel BerMap hesabı oluşturun.' : 'Create a local BerMap account.'}
      closeLabel={isTr ? 'Kapat' : 'Close'}
      onClose={onClose}
      busy={saving}
      footer={
        <>
          <Button variant="outline" size="sm" onClick={onClose} disabled={saving}>
            {isTr ? 'Vazgeç' : 'Cancel'}
          </Button>
          <Button variant="primary" size="sm" type="submit" form={formId} disabled={saving}>
            {saving && <Loader2 className="w-4 h-4 animate-spin" />}
            {isTr ? 'Oluştur' : 'Create'}
          </Button>
        </>
      }
    >
      <form id={formId} onSubmit={(event) => void handleSubmit(event)} className="space-y-4" noValidate>
        {error && <MessageBanner tone="error">{error}</MessageBanner>}
        <div>
          <label htmlFor={`${fieldId}-username`} className={labelClass}>
            {isTr ? 'Kullanıcı Adı' : 'Username'}
          </label>
          <Input
            id={`${fieldId}-username`}
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            autoComplete="off"
            autoFocus
          />
        </div>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <div>
            <label htmlFor={`${fieldId}-display`} className={labelClass}>
              {isTr ? 'Görünen Ad (Opsiyonel)' : 'Display Name (Optional)'}
            </label>
            <Input id={`${fieldId}-display`} value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
          </div>
          <div>
            <label htmlFor={`${fieldId}-email`} className={labelClass}>
              {isTr ? 'E-posta (Opsiyonel)' : 'Email (Optional)'}
            </label>
            <Input id={`${fieldId}-email`} type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
          </div>
        </div>
        <div>
          <label htmlFor={`${fieldId}-role`} className={labelClass}>
            {isTr ? 'Rol' : 'Role'}
          </label>
          <select id={`${fieldId}-role`} value={role} onChange={(e) => setRole(e.target.value)} className={selectClass}>
            {ROLE_OPTIONS.map((option) => (
              <option key={option} value={option}>
                {roleLabel(option)}
              </option>
            ))}
          </select>
        </div>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <div>
            <label htmlFor={`${fieldId}-password`} className={labelClass}>
              {isTr ? 'Parola' : 'Password'}
            </label>
            <Input
              id={`${fieldId}-password`}
              type="password"
              autoComplete="new-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
          </div>
          <div>
            <label htmlFor={`${fieldId}-confirm`} className={labelClass}>
              {isTr ? 'Parola (Tekrar)' : 'Confirm Password'}
            </label>
            <Input
              id={`${fieldId}-confirm`}
              type="password"
              autoComplete="new-password"
              value={confirm}
              onChange={(e) => setConfirm(e.target.value)}
            />
          </div>
        </div>
        <p className="text-[11px] text-neutral-500 -mt-2">
          {isTr
            ? `En az ${PASSWORD_MIN_LENGTH} karakter; kullanıcı adını içermemelidir.`
            : `At least ${PASSWORD_MIN_LENGTH} characters; must not contain the username.`}
        </p>
      </form>
    </DialogShell>
  );
};

interface EditUserDialogProps {
  isTr: boolean;
  user: UserAccount;
  isSelf: boolean;
  roleLabel: (role: string) => string;
  onClose: () => void;
  onSaved: (user: UserAccount) => void;
}

const EditUserDialog: React.FC<EditUserDialogProps> = ({ isTr, user, isSelf, roleLabel, onClose, onSaved }) => {
  const fieldId = useId();
  const formId = `${fieldId}-form`;
  const [displayName, setDisplayName] = useState(user.displayName);
  const [email, setEmail] = useState(user.email ?? '');
  const [role, setRole] = useState(user.role);
  const [isActive, setIsActive] = useState(user.isActive);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const handleSubmit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (isSelf && !isActive) {
      setError(isTr ? 'Kendi hesabınızı devre dışı bırakamazsınız.' : 'You cannot deactivate your own account.');
      return;
    }
    // Only send what changed so the audit record lists the real changes.
    const payload: UpdateUserRequest = {};
    if (displayName.trim() !== user.displayName) payload.displayName = displayName.trim();
    if (email.trim() !== (user.email ?? '')) payload.email = email.trim();
    if (role !== user.role) payload.role = role;
    if (isActive !== user.isActive) payload.isActive = isActive;
    if (Object.keys(payload).length === 0) {
      onClose();
      return;
    }

    setSaving(true);
    setError(null);
    try {
      const updated = await api.put<UserAccount>(`/users/${user.id}`, payload);
      onSaved(updated);
    } catch (requestError) {
      setError(userErrorMessage(requestError, isTr));
      setSaving(false);
    }
  };

  return (
    <DialogShell
      title={isTr ? 'Kullanıcıyı Düzenle' : 'Edit User'}
      subtitle={user.username}
      closeLabel={isTr ? 'Kapat' : 'Close'}
      onClose={onClose}
      busy={saving}
      footer={
        <>
          <Button variant="outline" size="sm" onClick={onClose} disabled={saving}>
            {isTr ? 'Vazgeç' : 'Cancel'}
          </Button>
          <Button variant="primary" size="sm" type="submit" form={formId} disabled={saving}>
            {saving && <Loader2 className="w-4 h-4 animate-spin" />}
            {isTr ? 'Kaydet' : 'Save'}
          </Button>
        </>
      }
    >
      <form id={formId} onSubmit={(event) => void handleSubmit(event)} className="space-y-4" noValidate>
        {error && <MessageBanner tone="error">{error}</MessageBanner>}
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <div>
            <label htmlFor={`${fieldId}-display`} className={labelClass}>
              {isTr ? 'Görünen Ad' : 'Display Name'}
            </label>
            <Input id={`${fieldId}-display`} value={displayName} onChange={(e) => setDisplayName(e.target.value)} autoFocus />
          </div>
          <div>
            <label htmlFor={`${fieldId}-email`} className={labelClass}>
              {isTr ? 'E-posta' : 'Email'}
            </label>
            <Input id={`${fieldId}-email`} type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
          </div>
        </div>
        <div>
          <label htmlFor={`${fieldId}-role`} className={labelClass}>
            {isTr ? 'Rol' : 'Role'}
          </label>
          <select id={`${fieldId}-role`} value={role} onChange={(e) => setRole(e.target.value)} className={selectClass}>
            {ROLE_OPTIONS.map((option) => (
              <option key={option} value={option}>
                {roleLabel(option)}
              </option>
            ))}
          </select>
          {isSelf && role !== ROLES.PlatformAdministrator && user.role === ROLES.PlatformAdministrator && (
            <p className="mt-1 text-[11px] text-amber-700 dark:text-amber-300">
              {isTr
                ? 'Kendi yönetici rolünüzü kaldırırsanız bu sayfaya erişiminizi kaybedersiniz.'
                : 'Removing your own administrator role takes away your access to this page.'}
            </p>
          )}
        </div>
        <label className="flex items-start gap-2 text-sm text-neutral-700 dark:text-neutral-300">
          <input
            type="checkbox"
            className="mt-1"
            checked={isActive}
            disabled={isSelf}
            onChange={(e) => setIsActive(e.target.checked)}
          />
          <span>
            {isTr ? 'Hesap etkin' : 'Account active'}
            <span className="block text-[11px] text-neutral-500">
              {isSelf
                ? (isTr ? 'Kendi hesabınızı devre dışı bırakamazsınız.' : 'You cannot deactivate your own account.')
                : (isTr
                  ? 'Devre dışı hesaplar oturum açamaz; açık oturumları bir sonraki istekte sonlanır.'
                  : 'Disabled accounts cannot sign in; their open sessions end on the next request.')}
            </span>
          </span>
        </label>
      </form>
    </DialogShell>
  );
};

interface ResetPasswordDialogProps {
  isTr: boolean;
  user: UserAccount;
  onClose: () => void;
  onSaved: () => void;
}

const ResetPasswordDialog: React.FC<ResetPasswordDialogProps> = ({ isTr, user, onClose, onSaved }) => {
  const fieldId = useId();
  const formId = `${fieldId}-form`;
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const handleSubmit = async (event: React.FormEvent) => {
    event.preventDefault();
    const passwordError = validatePassword(password, confirm, user.username, isTr);
    if (passwordError) {
      setError(passwordError);
      return;
    }
    setSaving(true);
    setError(null);
    try {
      await api.post<void>(`/users/${user.id}/reset-password`, { password });
      onSaved();
    } catch (requestError) {
      setError(userErrorMessage(requestError, isTr));
      setSaving(false);
    }
  };

  return (
    <DialogShell
      title={isTr ? 'Parola Sıfırla' : 'Reset Password'}
      subtitle={user.username}
      closeLabel={isTr ? 'Kapat' : 'Close'}
      onClose={onClose}
      busy={saving}
      footer={
        <>
          <Button variant="outline" size="sm" onClick={onClose} disabled={saving}>
            {isTr ? 'Vazgeç' : 'Cancel'}
          </Button>
          <Button variant="primary" size="sm" type="submit" form={formId} disabled={saving}>
            {saving && <Loader2 className="w-4 h-4 animate-spin" />}
            {isTr ? 'Parolayı Sıfırla' : 'Reset Password'}
          </Button>
        </>
      }
    >
      <form id={formId} onSubmit={(event) => void handleSubmit(event)} className="space-y-4" noValidate>
        {error && <MessageBanner tone="error">{error}</MessageBanner>}
        <div>
          <label htmlFor={`${fieldId}-password`} className={labelClass}>
            {isTr ? 'Yeni Parola' : 'New Password'}
          </label>
          <Input
            id={`${fieldId}-password`}
            type="password"
            autoComplete="new-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoFocus
          />
          <p className="text-[11px] text-neutral-500 mt-1">
            {isTr
              ? `En az ${PASSWORD_MIN_LENGTH} karakter; kullanıcı adını içermemelidir.`
              : `At least ${PASSWORD_MIN_LENGTH} characters; must not contain the username.`}
          </p>
        </div>
        <div>
          <label htmlFor={`${fieldId}-confirm`} className={labelClass}>
            {isTr ? 'Yeni Parola (Tekrar)' : 'Confirm New Password'}
          </label>
          <Input
            id={`${fieldId}-confirm`}
            type="password"
            autoComplete="new-password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
          />
        </div>
      </form>
    </DialogShell>
  );
};
