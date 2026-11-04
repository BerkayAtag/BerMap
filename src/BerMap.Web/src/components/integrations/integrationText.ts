import type { ApiError } from '@/types';
import { getErrorMessage } from '@/lib/format';

/*
 * Client-side helpers shared by the Integrations page and the wizard: the
 * backend returns stable error codes and English phase names, which are mapped
 * to Turkish/English text here; cron and server input are checked before the
 * request so mistakes are reported next to the field instead of at save time.
 */

type Text = { tr: string; en: string };

const API_ERROR_MESSAGES: Record<string, Text> = {
  NAME_REQUIRED: { tr: 'Entegrasyon adı zorunludur.', en: 'Integration name is required.' },
  NAME_TOO_LONG: { tr: 'Entegrasyon adı en fazla 200 karakter olabilir.', en: 'Integration name must be 200 characters or fewer.' },
  SCHEDULE_REQUIRED: { tr: 'Keşif takvimi zorunludur.', en: 'A discovery schedule is required.' },
  SCHEDULE_INVALID: {
    tr: 'Keşif takvimi geçerli bir cron ifadesi değil.',
    en: 'The discovery schedule is not a valid cron expression.',
  },
  ADAPTER_NOT_FOUND: { tr: 'Seçilen adaptör sunucuda kayıtlı değil.', en: 'The selected adapter is not registered on the server.' },
  CONFIG_KEY: { tr: 'Konfigürasyon anahtarları boş olamaz.', en: 'Configuration keys must not be empty.' },
  CONFIG_REQUIRED: {
    tr: 'Adaptörün zorunlu bağlantı alanları eksik (ör. sunucu, kullanıcı adı).',
    en: 'Required connection fields of the adapter are missing (e.g. server, username).',
  },
  CONFIG_INVALID_SERVER: {
    tr: 'Sunucu alanına yalnızca ana bilgisayar adı veya IP yazın; https:// ve yol (/sdk) eklemeyin.',
    en: 'Enter only a host name or IP as the server; do not include https:// or a path (/sdk).',
  },
  SECRET_REQUIRED: { tr: 'Bu adaptör için parola / secret zorunludur.', en: 'A password / secret is required for this adapter.' },
  SECRET_IN_CONFIGURATION: {
    tr: 'Parolalar konfigürasyon alanlarına yazılamaz; parola alanını kullanın.',
    en: 'Secrets must not be placed in configuration fields; use the password field.',
  },
  INTEGRATION_DISABLED: {
    tr: 'Entegrasyon devre dışı; önce etkinleştirin.',
    en: 'The integration is disabled; enable it first.',
  },
  DISCOVERY_ALREADY_ACTIVE: {
    tr: 'Bu entegrasyon için zaten kuyrukta veya çalışan bir keşif işi var.',
    en: 'A discovery job is already queued or running for this integration.',
  },
  COLLECTOR_MANAGED: {
    tr: 'Bu entegrasyonun verisi collector üzerinden gelir; test ve keşif sunucuda çalıştırılmaz.',
    en: 'This integration is fed by a collector; tests and discovery do not run on the server.',
  },
};

/** Connection-test error codes reported by the adapters. */
const CONNECTION_ERROR_MESSAGES: Record<string, Text> = {
  CONNECTION_TIMEOUT: { tr: 'Bağlantı zaman aşımına uğradı.', en: 'The connection timed out.' },
  ADAPTER_TEST_FAILED: { tr: 'Adaptör bağlantı testini tamamlayamadı.', en: 'The adapter could not complete the connection test.' },
  VMWARE_SERVER: { tr: 'vCenter sunucu adresi eksik veya geçersiz.', en: 'The vCenter server address is missing or invalid.' },
  VMWARE_PORT: { tr: 'vCenter portu geçersiz.', en: 'The vCenter port is invalid.' },
  VMWARE_USERNAME: { tr: 'vCenter kullanıcı adı eksik.', en: 'The vCenter username is missing.' },
  VMWARE_PASSWORD: { tr: 'vCenter parolası eksik.', en: 'The vCenter password is missing.' },
  VMWARE_AUTH: { tr: 'vCenter kimlik doğrulaması başarısız.', en: 'vCenter authentication failed.' },
  VMWARE_SESSION: { tr: 'vCenter oturumu açılamadı.', en: 'A vCenter session could not be opened.' },
  VMWARE_CONNECTION_ERROR: {
    tr: 'vCenter sunucusuna ulaşılamadı (DNS, ağ veya port).',
    en: 'The vCenter server could not be reached (DNS, network or port).',
  },
  VMWARE_CONFIG: { tr: 'vCenter konfigürasyonu geçersiz.', en: 'The vCenter configuration is invalid.' },
  VMWARE_CONFIG_JSON: { tr: 'vCenter konfigürasyonu okunamadı.', en: 'The vCenter configuration could not be read.' },
  AD_SERVER: { tr: 'Etki alanı denetleyicisi adresi eksik veya geçersiz.', en: 'The domain controller address is missing or invalid.' },
  AD_PORT: { tr: 'LDAP portu geçersiz.', en: 'The LDAP port is invalid.' },
  AD_USERNAME: { tr: 'LDAP servis hesabı eksik.', en: 'The LDAP service account is missing.' },
  AD_PASSWORD: { tr: 'LDAP parolası eksik.', en: 'The LDAP password is missing.' },
  AD_BASE_DN: { tr: 'Base DN geçersiz.', en: 'The base DN is invalid.' },
  AD_TLS_REQUIRED: { tr: 'LDAPS veya StartTLS gereklidir.', en: 'LDAPS or StartTLS is required.' },
  AD_TLS_MODE: { tr: 'LDAPS ve StartTLS birlikte seçilemez.', en: 'LDAPS and StartTLS cannot be combined.' },
  AD_TLS_CERTIFICATE: { tr: 'Sunucu sertifikası doğrulanamadı.', en: 'The server certificate could not be validated.' },
  AD_CONNECTION_ERROR: {
    tr: 'Etki alanı denetleyicisine ulaşılamadı (DNS, ağ veya port).',
    en: 'The domain controller could not be reached (DNS, network or port).',
  },
  AD_CONFIG: { tr: 'Active Directory konfigürasyonu geçersiz.', en: 'The Active Directory configuration is invalid.' },
  AD_CONFIG_JSON: { tr: 'Active Directory konfigürasyonu okunamadı.', en: 'The Active Directory configuration could not be read.' },
  LDAP_49: { tr: 'LDAP kimlik bilgileri geçersiz.', en: 'Invalid LDAP credentials.' },
  LDAP_81: { tr: 'LDAP sunucusuna ulaşılamadı.', en: 'The LDAP server is unavailable.' },
  LDAP_85: { tr: 'LDAP isteği zaman aşımına uğradı.', en: 'The LDAP request timed out.' },
};

function pick(text: Text, isTr: boolean): string {
  return isTr ? text.tr : text.en;
}

/** Localised text for an adapter/connection error code, or null when unknown. */
export function connectionErrorText(code: string | null | undefined, isTr: boolean): string | null {
  if (!code) return null;
  const entry = CONNECTION_ERROR_MESSAGES[code] ?? API_ERROR_MESSAGES[code];
  return entry ? pick(entry, isTr) : null;
}

/**
 * Message for a failed API call: a known backend code wins over the server's
 * (English) text, which is kept as a detail when it adds information.
 */
export function integrationErrorMessage(error: unknown, isTr: boolean): string {
  const code = (error as Partial<ApiError> | null)?.code;
  const entry = code ? API_ERROR_MESSAGES[code] : undefined;
  if (!entry) return getErrorMessage(error);
  if (code === 'SCHEDULE_INVALID') {
    // The server explains which field is wrong; keep that detail after the localised sentence.
    const detail = getErrorMessage(error).split(':').slice(1).join(':').trim();
    return detail ? `${pick(entry, isTr)} (${detail})` : pick(entry, isTr);
  }
  return pick(entry, isTr);
}

/**
 * Splits a stored "CODE: message" job error into a localised label and the
 * raw detail. Unknown codes are shown as they are.
 */
export function describeJobMessage(message: string, isTr: boolean): { label: string | null; detail: string } {
  const match = /^([A-Z][A-Z0-9_]{2,}):\s*(.*)$/s.exec(message);
  if (!match) return { label: null, detail: message };
  return { label: connectionErrorText(match[1], isTr) ?? match[1], detail: match[2] };
}

const PHASE_LABELS: Record<string, Text> = {
  Queued: { tr: 'Kuyrukta', en: 'Queued' },
  Starting: { tr: 'Başlatılıyor', en: 'Starting' },
  Connect: { tr: 'Bağlanılıyor', en: 'Connecting' },
  Forest: { tr: 'Orman', en: 'Forest' },
  Domain: { tr: 'Etki alanı', en: 'Domain' },
  Sites: { tr: 'Siteler', en: 'Sites' },
  Inventory: { tr: 'Envanter', en: 'Inventory' },
  Hosts: { tr: 'ESXi hostları', en: 'ESXi hosts' },
  VMs: { tr: "VM'ler", en: 'VMs' },
  Metrics: { tr: 'Metrikler', en: 'Metrics' },
  Topology: { tr: 'Topoloji', en: 'Topology' },
  Graph: { tr: 'Grafik', en: 'Graph' },
  Ingesting: { tr: 'Veri işleniyor', en: 'Ingesting' },
  Completed: { tr: 'Tamamlandı', en: 'Completed' },
  Failed: { tr: 'Başarısız', en: 'Failed' },
  Cancelled: { tr: 'İptal edildi', en: 'Cancelled' },
};

export function jobPhaseLabel(phase: string | null | undefined, isTr: boolean): string {
  if (!phase) return '—';
  const entry = PHASE_LABELS[phase];
  return entry ? pick(entry, isTr) : phase;
}

/* ===== Cron (mirrors BerMap.Application CronSchedule) ===== */

const MONTH_NAMES = ['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC'];
const DAY_NAMES = ['SUN', 'MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT'];
const CRON_MACROS = ['@hourly', '@daily', '@midnight', '@weekly', '@monthly', '@yearly', '@annually'];

interface CronField {
  min: number;
  max: number;
  names?: string[];
  tr: string;
  en: string;
}

const CRON_FIELDS: CronField[] = [
  { min: 0, max: 59, tr: 'dakika', en: 'minute' },
  { min: 0, max: 23, tr: 'saat', en: 'hour' },
  { min: 1, max: 31, tr: 'ayın günü', en: 'day-of-month' },
  { min: 1, max: 12, names: MONTH_NAMES, tr: 'ay', en: 'month' },
  { min: 0, max: 7, names: DAY_NAMES, tr: 'haftanın günü', en: 'day-of-week' },
];

function parseCronValue(text: string, field: CronField): number | null {
  if (/^\d+$/.test(text)) return Number(text);
  if (field.names) {
    const index = field.names.indexOf(text.toUpperCase());
    if (index >= 0) return field === CRON_FIELDS[3] ? index + 1 : index;
  }
  return null;
}

function isValidCronPart(part: string, field: CronField): boolean {
  const [range, step, ...rest] = part.split('/');
  if (rest.length > 0 || range === '') return false;
  if (step !== undefined && (!/^\d+$/.test(step) || Number(step) < 1)) return false;
  if (range === '*' || range === '?') return true;

  const bounds = range.split('-');
  if (bounds.length > 2) return false;
  const values = bounds.map((value) => parseCronValue(value, field));
  if (values.some((value) => value === null || value < field.min || value > field.max)) return false;
  return values.length === 1 || (values[0] as number) <= (values[1] as number);
}

/** Returns a localised error for an invalid 5-field cron expression, or null when it is valid. */
export function validateCron(expression: string, isTr: boolean): string | null {
  const text = expression.trim();
  if (!text) return isTr ? 'Keşif takvimi zorunludur.' : 'A discovery schedule is required.';
  if (CRON_MACROS.includes(text.toLowerCase())) return null;

  const fields = text.split(/\s+/);
  if (fields.length !== 5) {
    return isTr
      ? `Cron ifadesi 5 alandan oluşmalıdır (dakika saat gün ay haftanın-günü); ${fields.length} alan girildi.`
      : `A cron expression needs 5 fields (minute hour day month weekday); ${fields.length} given.`;
  }

  for (let index = 0; index < fields.length; index++) {
    const field = CRON_FIELDS[index];
    if (!fields[index].split(',').every((part) => isValidCronPart(part, field))) {
      return isTr
        ? `Cron ifadesinin ${field.tr} alanı geçersiz: "${fields[index]}" (izin verilen ${field.min}-${field.max}).`
        : `The ${field.en} field of the cron expression is invalid: "${fields[index]}" (allowed ${field.min}-${field.max}).`;
    }
  }
  return null;
}

/* ===== Server address ===== */

export interface NormalizedServer {
  host: string;
  port: number | null;
  /** True when a scheme, path, credentials or port were removed from the input. */
  changed: boolean;
}

/**
 * Accepts what people paste (https://vc.example/sdk, vc.example:443) and keeps
 * only the host; the adapters build the URL themselves. An explicit port is
 * returned separately so the port field can be updated.
 */
export function normalizeServerInput(value: string): NormalizedServer {
  const original = value.trim();
  let host = original.replace(/^[a-z][a-z0-9+.-]*:\/\//i, '');
  host = host.split(/[/?#]/)[0];
  host = host.includes('@') ? host.slice(host.lastIndexOf('@') + 1) : host;

  let port: number | null = null;
  // host:port, but leave bare IPv6 literals (several colons) alone.
  const portMatch = /^(\[[^\]]+\]|[^:]+):(\d{1,5})$/.exec(host);
  if (portMatch) {
    host = portMatch[1];
    const parsed = Number(portMatch[2]);
    if (parsed >= 1 && parsed <= 65535) port = parsed;
  }
  return { host, port, changed: host !== original };
}

/** Localised error when the server field is not a plain host name or IP address. */
export function validateServer(value: string, isTr: boolean): string | null {
  const text = value.trim();
  if (!text) return isTr ? 'Sunucu adresi zorunludur.' : 'Server address is required.';
  if (text.includes('://') || text.includes('/') || /\s/.test(text)) {
    return isTr
      ? 'Yalnızca ana bilgisayar adı veya IP girin (https:// ve /sdk gibi yollar olmadan).'
      : 'Enter only a host name or IP (without https:// or paths such as /sdk).';
  }
  return null;
}
