import type { ApiError } from '@/types';
import type { TranslationKey } from '@/lib/dictionaries';
import { getErrorMessage } from '@/lib/format';
import { ROLES } from '@/stores/authStore';

/*
 * Shared helpers for the user-management page and the change-password dialog:
 * role labels, the client-side password policy (mirrors the API's
 * PasswordPolicy) and localised messages for the API's error codes.
 */

export const PASSWORD_MIN_LENGTH = 12;
export const PASSWORD_MAX_LENGTH = 256;

export const ROLE_OPTIONS: string[] = [
  ROLES.PlatformAdministrator,
  ROLES.IntegrationAdministrator,
  ROLES.InfrastructureOperator,
  ROLES.SecurityAuditor,
  ROLES.ReadOnlyViewer,
];

export const ROLE_KEYS: Record<string, TranslationKey> = {
  PlatformAdministrator: 'rolePlatformAdministrator',
  IntegrationAdministrator: 'roleIntegrationAdministrator',
  InfrastructureOperator: 'roleInfrastructureOperator',
  SecurityAuditor: 'roleSecurityAuditor',
  ReadOnlyViewer: 'roleReadOnlyViewer',
};

type Text = { tr: string; en: string };

const USER_ERROR_MESSAGES: Record<string, Text> = {
  last_admin: {
    tr: 'Son etkin platform yöneticisi devre dışı bırakılamaz veya rolü değiştirilemez. Önce başka bir yönetici atayın.',
    en: 'The last active platform administrator cannot be deactivated or lose the role. Assign another administrator first.',
  },
  self_deactivation: {
    tr: 'Kendi hesabınızı devre dışı bırakamazsınız.',
    en: 'You cannot deactivate your own account.',
  },
  username_taken: {
    tr: 'Bu kullanıcı adı zaten kullanılıyor.',
    en: 'This username is already in use.',
  },
  invalid_username: {
    tr: 'Kullanıcı adı 2-64 karakter olmalı ve boşluk içermemelidir.',
    en: 'The username must be 2-64 characters without spaces.',
  },
  invalid_role: { tr: 'Geçersiz rol.', en: 'Invalid role.' },
  weak_password: {
    tr: `Parola en az ${PASSWORD_MIN_LENGTH} karakter olmalı ve kullanıcı adını içermemelidir.`,
    en: `The password must be at least ${PASSWORD_MIN_LENGTH} characters and must not contain the username.`,
  },
  invalid_current_password: { tr: 'Mevcut parola hatalı.', en: 'The current password is incorrect.' },
  password_unchanged: {
    tr: 'Yeni parola mevcut paroladan farklı olmalıdır.',
    en: 'The new password must differ from the current password.',
  },
};

/** Localised message for a failed user/password request. */
export function userErrorMessage(error: unknown, isTr: boolean): string {
  const apiError = error as Partial<ApiError> | null;
  const code = apiError?.code;
  if (code === 'weak_password' && isTr && apiError?.message) {
    // The API explains the exact rule in Turkish.
    return apiError.message;
  }
  const entry = code ? USER_ERROR_MESSAGES[code] : undefined;
  if (entry) return isTr ? entry.tr : entry.en;
  if (apiError?.status === 403) {
    return isTr ? 'Bu işlem için yetkiniz yok.' : 'You are not allowed to do this.';
  }
  if (apiError?.status === 404) {
    return isTr ? 'Kullanıcı bulunamadı.' : 'User not found.';
  }
  return getErrorMessage(error);
}

/** Client-side password policy check; returns a localised error or null. */
export function validatePassword(password: string, confirm: string, username: string, isTr: boolean): string | null {
  if (password.length < PASSWORD_MIN_LENGTH) {
    return isTr
      ? `Parola en az ${PASSWORD_MIN_LENGTH} karakter olmalıdır.`
      : `The password must be at least ${PASSWORD_MIN_LENGTH} characters.`;
  }
  if (password.length > PASSWORD_MAX_LENGTH) {
    return isTr
      ? `Parola en fazla ${PASSWORD_MAX_LENGTH} karakter olabilir.`
      : `The password can be at most ${PASSWORD_MAX_LENGTH} characters.`;
  }
  const name = username.trim();
  if (name && password.toLocaleLowerCase().includes(name.toLocaleLowerCase())) {
    return isTr ? 'Parola kullanıcı adını içeremez.' : 'The password must not contain the username.';
  }
  if (password !== confirm) {
    return isTr ? 'Parolalar eşleşmiyor.' : 'The passwords do not match.';
  }
  return null;
}

/** Mirrors the API's ViewAudit policy (audit log list and export). */
export function canViewAudit(role?: string | null): boolean {
  return role === ROLES.PlatformAdministrator || role === ROLES.SecurityAuditor;
}
