import React, { useId, useState } from 'react';
import { CheckCircle2, Loader2 } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { api } from '@/lib/api';
import { useLocale } from '@/lib/i18n';
import { useAuthStore } from '@/stores/authStore';
import { DialogShell, MessageBanner } from '@/components/users/DialogShell';
import { PASSWORD_MIN_LENGTH, userErrorMessage, validatePassword } from '@/components/users/userText';
import type { ChangePasswordResponse } from '@/types';

interface ChangePasswordDialogProps {
  open: boolean;
  onClose: () => void;
}

/** Lets the signed-in user change their own password (POST /api/auth/change-password). */
export const ChangePasswordDialog: React.FC<ChangePasswordDialogProps> = ({ open, onClose }) => {
  if (!open) return null;
  // Mounted only while open, so every opening starts with empty fields.
  return <ChangePasswordForm onClose={onClose} />;
};

const ChangePasswordForm: React.FC<{ onClose: () => void }> = ({ onClose }) => {
  const isTr = useLocale() === 'tr';
  const username = useAuthStore((state) => state.user?.username ?? '');
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [done, setDone] = useState(false);
  const fieldId = useId();
  const formId = `${fieldId}-form`;

  const handleSubmit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!currentPassword) {
      setError(isTr ? 'Mevcut parolanızı girin.' : 'Enter your current password.');
      return;
    }
    const validation = validatePassword(newPassword, confirmPassword, username, isTr);
    if (validation) {
      setError(validation);
      return;
    }
    if (newPassword === currentPassword) {
      setError(isTr ? 'Yeni parola mevcut paroladan farklı olmalıdır.' : 'The new password must differ from the current password.');
      return;
    }

    setSaving(true);
    setError(null);
    try {
      // The change ends every other session; the response carries a new token for
      // this one. An older server answers 204 and the next request signs out.
      const result = await api.post<ChangePasswordResponse | undefined>('/auth/change-password', {
        currentPassword,
        newPassword,
      });
      if (result?.token) useAuthStore.getState().setToken(result.token);
      setDone(true);
      setCurrentPassword('');
      setNewPassword('');
      setConfirmPassword('');
    } catch (requestError) {
      setError(userErrorMessage(requestError, isTr));
    } finally {
      setSaving(false);
    }
  };

  const labelClass = 'block text-xs font-semibold text-neutral-600 dark:text-neutral-400 mb-1';

  return (
    <DialogShell
      title={isTr ? 'Parolayı Değiştir' : 'Change Password'}
      subtitle={username}
      closeLabel={isTr ? 'Kapat' : 'Close'}
      onClose={onClose}
      busy={saving}
      footer={
        done ? (
          <Button variant="primary" size="sm" onClick={onClose}>
            {isTr ? 'Kapat' : 'Close'}
          </Button>
        ) : (
          <>
            <Button variant="outline" size="sm" onClick={onClose} disabled={saving}>
              {isTr ? 'Vazgeç' : 'Cancel'}
            </Button>
            <Button variant="primary" size="sm" type="submit" form={formId} disabled={saving}>
              {saving && <Loader2 className="w-4 h-4 animate-spin" />}
              {isTr ? 'Parolayı Değiştir' : 'Change Password'}
            </Button>
          </>
        )
      }
    >
      {done ? (
        <MessageBanner tone="success">
          <span className="flex items-start gap-2">
            <CheckCircle2 className="w-4 h-4 shrink-0" />
            {isTr
              ? 'Parolanız değiştirildi; diğer oturumlarınız kapatıldı. Bir sonraki girişte yeni parolanızı kullanın.'
              : 'Your password was changed and your other sessions were signed out. Use the new password the next time you sign in.'}
          </span>
        </MessageBanner>
      ) : (
        <form id={formId} onSubmit={(event) => void handleSubmit(event)} className="space-y-4" noValidate>
          {error && <MessageBanner tone="error">{error}</MessageBanner>}
          <div>
            <label htmlFor={`${fieldId}-current`} className={labelClass}>
              {isTr ? 'Mevcut Parola' : 'Current Password'}
            </label>
            <Input
              id={`${fieldId}-current`}
              type="password"
              autoComplete="current-password"
              value={currentPassword}
              onChange={(e) => setCurrentPassword(e.target.value)}
              autoFocus
            />
          </div>
          <div>
            <label htmlFor={`${fieldId}-new`} className={labelClass}>
              {isTr ? 'Yeni Parola' : 'New Password'}
            </label>
            <Input
              id={`${fieldId}-new`}
              type="password"
              autoComplete="new-password"
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
            />
            <p className="text-[11px] text-neutral-500 mt-1">
              {isTr
                ? `En az ${PASSWORD_MIN_LENGTH} karakter; kullanıcı adınızı içermemelidir.`
                : `At least ${PASSWORD_MIN_LENGTH} characters; must not contain your username.`}
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
              value={confirmPassword}
              onChange={(e) => setConfirmPassword(e.target.value)}
            />
          </div>
        </form>
      )}
    </DialogShell>
  );
};
