/**
 * Sign-in page texts. The server answers in Turkish whatever the console
 * language is, so known failures are shown by status and code in the UI
 * language; the server message is only a fallback for unknown errors. Kept
 * free of React and path aliases so `npm test` covers it.
 */

interface Text {
  tr: string;
  en: string;
}

const pick = (text: Text, isTr: boolean) => (isTr ? text.tr : text.en);

export interface LoginFailure {
  status?: number;
  code?: string;
  message?: string;
}

const REQUIRED: Text = { tr: 'Kullanıcı adı ve parola zorunludur.', en: 'Username and password are required.' };
const INVALID: Text = { tr: 'Kullanıcı adı veya parola hatalı.', en: 'Invalid username or password.' };
const DISABLED: Text = { tr: 'Kullanıcı hesabı devre dışı.', en: 'This user account is disabled.' };
const RATE_LIMITED: Text = {
  tr: 'Çok fazla giriş denemesi. Lütfen bir dakika sonra tekrar deneyin.',
  en: 'Too many sign-in attempts. Please try again in a minute.',
};
const FAILED: Text = { tr: 'Giriş başarısız.', en: 'Login failed.' };

export function requiredFieldsText(isTr: boolean): string {
  return pick(REQUIRED, isTr);
}

/** The message for a failed POST /api/auth/login, in the console language. */
export function loginErrorText(error: LoginFailure | null | undefined, isTr: boolean): string {
  const status = error?.status;
  const code = error?.code;
  if (status === 429) return pick(RATE_LIMITED, isTr);
  if (code === 'account_disabled') return pick(DISABLED, isTr);
  if (code === 'invalid_credentials') return pick(status === 400 ? REQUIRED : INVALID, isTr);
  if (status === 401) return pick(INVALID, isTr);
  // Unknown failures (proxy errors, a restarting server): the server's own
  // text says more than a generic sentence, but it is Turkish or HTTP jargon,
  // so the English console keeps it only as a detail.
  const detail = error?.message?.trim();
  if (!detail) return pick(FAILED, isTr);
  return isTr ? detail : `${FAILED.en} (${detail})`;
}

/** Why the previous session ended; LoginPage explains it above the form. */
export type SignOutReason = 'session_ended' | 'role_changed' | 'password_reset';

const SIGN_OUT_TEXT: Record<SignOutReason, Text> = {
  session_ended: {
    tr: 'Oturumunuz sona erdi. Devam etmek için yeniden giriş yapın.',
    en: 'Your session has ended. Please sign in again to continue.',
  },
  role_changed: {
    tr: 'Rolünüz değiştirildiği için oturumunuz kapatıldı. Yeni rolünüzle devam etmek için yeniden giriş yapın.',
    en: 'You were signed out because your role was changed. Sign in again to continue with your new role.',
  },
  password_reset: {
    tr: 'Parolanız sıfırlandığı için oturumunuz kapatıldı. Yeni parolanızla giriş yapın.',
    en: 'You were signed out because your password was reset. Sign in with your new password.',
  },
};

export function signOutReasonText(reason: SignOutReason, isTr: boolean): string {
  return pick(SIGN_OUT_TEXT[reason], isTr);
}
