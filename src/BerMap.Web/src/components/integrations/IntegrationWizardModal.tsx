import React, { useEffect, useId, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Card } from '@/components/ui/Card';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import {
  X,
  CheckCircle2,
  ShieldCheck,
  RotateCw,
  AlertTriangle,
  Info,
  Server,
} from 'lucide-react';
import { api } from '@/lib/api';
import { useLocale } from '@/lib/i18n';
import {
  connectionErrorText,
  integrationErrorMessage,
  normalizeServerInput,
  validateCron,
  validateServer,
} from '@/components/integrations/integrationText';
import type {
  AdapterDescriptor,
  ApiError,
  ConnectionTestResult,
  CreateIntegrationRequest,
  Integration,
  UpdateIntegrationRequest,
} from '@/types';

export interface IntegrationWizardResult {
  mode: 'create' | 'edit';
  integration: Integration;
  /** True when a discovery job was queued right after saving. */
  discoveryStarted: boolean;
}

interface IntegrationWizardModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSuccess: (result: IntegrationWizardResult) => void;
  /** When set, the wizard edits this integration instead of creating one. */
  integration?: Integration | null;
}

interface FormState {
  adapterType: string;
  name: string;
  description: string;
  server: string;
  port: number;
  username: string;
  secret: string;
  baseDn: string;
  useSsl: boolean;
  useStartTls: boolean;
  allowUntrustedCertificate: boolean;
  allowPlaintext: boolean;
  discoverAllDomains: boolean;
  collectMetrics: boolean;
  includePoweredOffVms: boolean;
  scheduleCron: string;
  timeoutSeconds: number;
  isEnabled: boolean;
  collectorManaged: boolean;
}

type FieldKey = 'name' | 'server' | 'port' | 'username' | 'secret' | 'scheduleCron' | 'timeoutSeconds';
type FieldErrors = Partial<Record<FieldKey, string>>;

const FIELD_STEP: Record<FieldKey, number> = {
  name: 2,
  server: 2,
  port: 2,
  username: 3,
  secret: 3,
  scheduleCron: 3,
  timeoutSeconds: 3,
};

/** Backend validation codes and the field they belong to. */
const ERROR_CODE_FIELD: Record<string, FieldKey> = {
  NAME_REQUIRED: 'name',
  NAME_TOO_LONG: 'name',
  CONFIG_INVALID_SERVER: 'server',
  CONFIG_REQUIRED: 'server',
  SECRET_REQUIRED: 'secret',
  SCHEDULE_REQUIRED: 'scheduleCron',
  SCHEDULE_INVALID: 'scheduleCron',
};

const initialState: FormState = {
  adapterType: 'VMware',
  name: '',
  description: '',
  server: '',
  port: 443,
  username: '',
  secret: '',
  baseDn: '',
  useSsl: true,
  useStartTls: false,
  allowUntrustedCertificate: false,
  allowPlaintext: false,
  discoverAllDomains: true,
  collectMetrics: true,
  includePoweredOffVms: true,
  scheduleCron: '0 */6 * * *',
  timeoutSeconds: 1800,
  isEnabled: true,
  collectorManaged: false,
};

function defaultName(adapterType: string, isTr: boolean): string {
  return `${adapterType} ${isTr ? 'Entegrasyonu' : 'Integration'}`;
}

function readString(value: unknown, fallback: string): string {
  return typeof value === 'string' ? value : fallback;
}

function readBool(value: unknown, fallback: boolean): boolean {
  return typeof value === 'boolean' ? value : fallback;
}

function readNumber(value: unknown, fallback: number): number {
  const parsed = typeof value === 'number' ? value : typeof value === 'string' ? Number(value) : NaN;
  return Number.isFinite(parsed) ? parsed : fallback;
}

/** Prefills the form from a saved integration; the secret always starts empty (= keep). */
function formFromIntegration(integration: Integration): FormState {
  const config = integration.configuration ?? {};
  const isAd = integration.adapterType === 'ActiveDirectory';
  return {
    ...initialState,
    adapterType: integration.adapterType,
    name: integration.name,
    description: integration.description ?? '',
    server: readString(config.server, ''),
    port: readNumber(config.port, isAd ? 636 : 443),
    username: readString(config.username, ''),
    secret: '',
    baseDn: readString(config.baseDn, ''),
    useSsl: readBool(config.useSsl, true),
    useStartTls: readBool(config.useStartTls, false),
    allowUntrustedCertificate: readBool(config.allowUntrustedCertificate, false),
    allowPlaintext: readBool(config.allowPlaintext, false),
    discoverAllDomains: readBool(config.discoverAllDomains, true),
    collectMetrics: readBool(config.collectMetrics, true),
    includePoweredOffVms: readBool(config.includePoweredOffVms, true),
    scheduleCron: integration.scheduleCron || initialState.scheduleCron,
    timeoutSeconds: integration.timeoutSeconds || initialState.timeoutSeconds,
    isEnabled: integration.isEnabled,
    collectorManaged: integration.collectorManaged === true,
  };
}

function buildConfiguration(form: FormState): Record<string, unknown> {
  if (form.adapterType === 'ActiveDirectory') {
    return {
      server: form.server.trim(),
      port: form.port,
      useSsl: form.useSsl,
      useStartTls: form.useStartTls,
      username: form.username.trim(),
      baseDn: form.baseDn.trim() || null,
      allowUntrustedCertificate: form.allowUntrustedCertificate,
      allowPlaintext: form.allowPlaintext,
      discoverAllDomains: form.discoverAllDomains,
      includeGroups: true,
      includeComputers: true,
      includeOus: true,
      includeSites: true,
      includeTrusts: true,
      pageSize: 500,
      timeoutSeconds: 30,
    };
  }

  return {
    server: form.server.trim(),
    port: form.port,
    username: form.username.trim(),
    allowUntrustedCertificate: form.allowUntrustedCertificate,
    ignoreSslErrors: false,
    collectMetrics: form.collectMetrics,
    useSoapMetrics: true,
    includePoweredOffVms: form.includePoweredOffVms,
    timeoutSeconds: 30,
    maxConcurrency: 8,
    retryCount: 3,
  };
}

export const IntegrationWizardModal: React.FC<IntegrationWizardModalProps> = ({
  isOpen,
  onClose,
  onSuccess,
  integration = null,
}) => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const isEdit = integration !== null;
  const [step, setStep] = useState(isEdit ? 2 : 1);
  const [form, setForm] = useState<FormState>(() => (integration ? formFromIntegration(integration) : initialState));
  const [isTesting, setIsTesting] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  const [testResult, setTestResult] = useState<ConnectionTestResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [serverHint, setServerHint] = useState<string | null>(null);
  const [startDiscovery, setStartDiscovery] = useState(true);
  const [created, setCreated] = useState<Integration | null>(null);
  const [discoveryError, setDiscoveryError] = useState<string | null>(null);
  const titleId = useId();
  const fieldId = useId();
  const isActiveDirectory = form.adapterType === 'ActiveDirectory';
  const collectorManaged = form.collectorManaged;

  const { data: adapters = [], isLoading: adaptersLoading } = useQuery<AdapterDescriptor[]>({
    queryKey: ['integrations', 'adapters'],
    queryFn: () => api.get<AdapterDescriptor[]>('/integrations/adapters'),
    enabled: isOpen,
    staleTime: 5 * 60_000,
  });

  useEffect(() => {
    if (!isOpen) {
      setStep(integration ? 2 : 1);
      setForm(integration ? formFromIntegration(integration) : initialState);
      setTestResult(null);
      setError(null);
      setFieldErrors({});
      setServerHint(null);
      setIsSaving(false);
      setIsTesting(false);
      setStartDiscovery(true);
      setCreated(null);
      setDiscoveryError(null);
    }
  }, [isOpen, integration]);

  useEffect(() => {
    if (!isOpen) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !isSaving) onClose();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [isOpen, isSaving, onClose]);

  const selectedAdapter = useMemo(
    () => adapters.find((a) => a.adapterType === form.adapterType),
    [adapters, form.adapterType]
  );

  if (!isOpen) return null;

  const id = (key: string) => `${fieldId}-${key}`;

  const update = <K extends keyof FormState>(key: K, value: FormState[K]) => {
    setForm((current) => ({ ...current, [key]: value }));
    setTestResult(null);
    setError(null);
    if (key in FIELD_STEP) {
      setFieldErrors((current) => ({ ...current, [key as FieldKey]: undefined }));
    }
  };

  const selectAdapter = (adapterType: string) => {
    setForm((current) => {
      // Replace the name while it is still the generated default of the previous adapter.
      const keepName = current.name.trim() !== '' && current.name !== defaultName(current.adapterType, isTr);
      return {
        ...current,
        adapterType,
        name: keepName ? current.name : defaultName(adapterType, isTr),
        port: adapterType === 'ActiveDirectory' ? 636 : 443,
        useSsl: true,
        useStartTls: false,
      };
    });
    setTestResult(null);
    setError(null);
    setFieldErrors({});
  };

  /** Strips a pasted scheme/path from the server field; returns the cleaned form. */
  const normalizeServer = (current: FormState): FormState => {
    const normalized = normalizeServerInput(current.server);
    if (!normalized.changed || !normalized.host) return current;
    setServerHint(
      isTr
        ? `Adres "${normalized.host}" olarak düzeltildi; https:// ve yol (/sdk) bağdaştırıcı tarafından eklenir.`
        : `The address was trimmed to "${normalized.host}"; the adapter adds https:// and the path (/sdk) itself.`,
    );
    const next = { ...current, server: normalized.host, port: normalized.port ?? current.port };
    setForm(next);
    return next;
  };

  const validateStep = (target: number, current: FormState): FieldErrors => {
    const errors: FieldErrors = {};
    if (target === 2) {
      if (!current.name.trim()) errors.name = isTr ? 'Entegrasyon adı zorunludur.' : 'Integration name is required.';
      const serverText = current.server.trim();
      if (!current.collectorManaged || serverText) {
        const serverError = validateServer(serverText, isTr);
        if (serverError) errors.server = serverError;
      }
      if (!Number.isInteger(current.port) || current.port < 1 || current.port > 65535) {
        errors.port = isTr ? 'Port 1-65535 arasında olmalıdır.' : 'Port must be between 1 and 65535.';
      }
    }
    if (target === 3) {
      if (!current.collectorManaged) {
        if (!current.username.trim()) {
          errors.username = isTr ? 'Salt-okunur servis hesabı zorunludur.' : 'A read-only service account is required.';
        }
        if (!isEdit && !current.secret.trim()) {
          errors.secret = isTr ? 'Parola / secret zorunludur.' : 'Password / secret is required.';
        }
      }
      const cronError = validateCron(current.scheduleCron, isTr);
      if (cronError) errors.scheduleCron = cronError;
      if (!Number.isFinite(current.timeoutSeconds) || current.timeoutSeconds < 30 || current.timeoutSeconds > 86_400) {
        errors.timeoutSeconds = isTr ? 'Zaman aşımı 30-86400 saniye olmalıdır.' : 'Timeout must be 30-86400 seconds.';
      }
    }
    return errors;
  };

  /** Step-level rule that is not tied to a single input. */
  const stepRuleError = (target: number, current: FormState): string | null => {
    if (
      target === 2 &&
      !current.collectorManaged &&
      current.adapterType === 'ActiveDirectory' &&
      !current.useSsl &&
      !current.useStartTls &&
      !current.allowPlaintext
    ) {
      return isTr
        ? 'LDAPS veya StartTLS seçin; düz LDAP yalnızca açık onayla kullanılabilir.'
        : 'Select LDAPS or StartTLS; plaintext LDAP requires an explicit confirmation.';
    }
    return null;
  };

  /** Validates steps up to `last`; on failure jumps to the first invalid step. */
  const validateThrough = (last: number): boolean => {
    const current = normalizeServer(form);
    for (let target = 2; target <= last; target++) {
      const errors = validateStep(target, current);
      const rule = stepRuleError(target, current);
      if (Object.keys(errors).length > 0 || rule) {
        setFieldErrors(errors);
        setError(rule);
        setStep(target);
        return false;
      }
    }
    setFieldErrors({});
    return true;
  };

  const goNext = () => {
    if (step >= 2 && !validateThrough(step)) return;
    setError(null);
    setStep((s) => s + 1);
  };

  /** Puts a backend validation error next to its field when the code is known. */
  const showRequestError = (requestError: unknown, fallback: string) => {
    const code = (requestError as Partial<ApiError> | null)?.code;
    const message = integrationErrorMessage(requestError, isTr) || fallback;
    const field = code ? ERROR_CODE_FIELD[code] : undefined;
    if (field) {
      setFieldErrors({ [field]: message });
      setStep(FIELD_STEP[field]);
      setError(null);
    } else {
      setError(message);
    }
  };

  const canTestDraft = !collectorManaged && (!isEdit || form.secret.trim() !== '');

  const handleTestConnection = async () => {
    if (!validateThrough(3)) return;

    setIsTesting(true);
    setError(null);
    setTestResult(null);

    try {
      const result = await api.post<ConnectionTestResult>('/integrations/test-connection', {
        adapterType: form.adapterType,
        configuration: buildConfiguration(form),
        secret: form.secret,
        timeoutSeconds: 30,
      });
      setTestResult(result);
      if (!result.success) {
        setError(
          connectionErrorText(result.errorCode, isTr) ??
            (result.errorDetail || result.message || (isTr ? 'Bağlantı testi başarısız oldu.' : 'Connection test failed.')),
        );
      }
    } catch (requestError) {
      showRequestError(requestError, isTr ? 'Bağlantı testi başarısız oldu.' : 'Connection test failed.');
    } finally {
      setIsTesting(false);
    }
  };

  const willStartDiscovery = !isEdit && startDiscovery && !collectorManaged && form.isEnabled;

  const handleSave = async () => {
    if (!validateThrough(3)) return;
    if (created) return;

    setIsSaving(true);
    setError(null);

    if (isEdit && integration) {
      try {
        const payload: UpdateIntegrationRequest = {
          name: form.name.trim(),
          description: form.description.trim(),
          // Keep keys the wizard does not edit (tuning values set through the API).
          configuration: { ...(integration.configuration ?? {}), ...buildConfiguration(form) },
          scheduleCron: form.scheduleCron.trim(),
          timeoutSeconds: form.timeoutSeconds,
          isEnabled: form.isEnabled,
          collectorManaged: form.collectorManaged,
        };
        if (form.secret.trim()) payload.secret = form.secret;
        const updated = await api.put<Integration>(`/integrations/${integration.id}`, payload);
        setIsSaving(false);
        onSuccess({ mode: 'edit', integration: updated, discoveryStarted: false });
        onClose();
      } catch (requestError) {
        showRequestError(requestError, isTr ? 'Entegrasyon güncellenemedi.' : 'Failed to update the integration.');
        setIsSaving(false);
      }
      return;
    }

    let createdIntegration: Integration;
    try {
      const payload: CreateIntegrationRequest = {
        name: form.name.trim(),
        description: form.description.trim(),
        adapterType: form.adapterType,
        configuration: buildConfiguration(form),
        scheduleCron: form.scheduleCron.trim() || '0 */6 * * *',
        timeoutSeconds: form.timeoutSeconds,
        isEnabled: form.isEnabled,
        collectorManaged: form.collectorManaged,
      };
      if (form.secret.trim()) payload.secret = form.secret;

      createdIntegration = await api.post<Integration>('/integrations', payload);
    } catch (requestError) {
      showRequestError(requestError, isTr ? 'Entegrasyon kaydedilemedi.' : 'Failed to save the integration.');
      setIsSaving(false);
      return;
    }

    // The integration now exists server-side. From here on, never re-submit the create request.
    setCreated(createdIntegration);

    if (willStartDiscovery) {
      try {
        await api.post(`/integrations/${createdIntegration.id}/discover`, { jobType: 'Full' });
      } catch (requestError) {
        onSuccess({ mode: 'create', integration: createdIntegration, discoveryStarted: false });
        setDiscoveryError(
          (isTr
            ? 'Entegrasyon oluşturuldu ancak keşif başlatılamadı. Keşfi Entegrasyonlar listesinden yeniden başlatabilirsiniz.'
            : 'The integration was created, but discovery could not be started. You can start it again from the Integrations list.') +
            ` (${integrationErrorMessage(requestError, isTr)})`,
        );
        setIsSaving(false);
        return;
      }
    }

    onSuccess({ mode: 'create', integration: createdIntegration, discoveryStarted: willStartDiscovery });
    setIsSaving(false);
    onClose();
  };

  const adapterOptions = adapters.length > 0
    ? adapters
    : [
        {
          adapterType: 'ActiveDirectory',
          displayName: 'Active Directory Domain Services (LDAP/LDAPS)',
          version: '2.0.0',
        } as AdapterDescriptor,
        {
          adapterType: 'VMware',
          displayName: 'VMware vCenter Server (vSphere REST + SOAP)',
          version: '2.0.0',
        } as AdapterDescriptor,
      ];

  const saveLabel = isSaving
    ? (isTr ? 'Kaydediliyor...' : 'Saving...')
    : isEdit
      ? (isTr ? 'Değişiklikleri Kaydet' : 'Save Changes')
      : willStartDiscovery
        ? (isTr ? 'Kaydet ve Keşfi Başlat' : 'Save & Start Discovery')
        : (isTr ? 'Kaydet' : 'Save');

  const labelClass = 'block text-xs font-semibold text-neutral-600 dark:text-neutral-400 mb-1';

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center p-2 sm:p-4 bg-black/60 backdrop-blur-xs bm-fade-in"
      role="dialog"
      aria-modal="true"
      aria-labelledby={titleId}
    >
      <Card className="w-full max-w-2xl border-neutral-200 dark:border-dark-border shadow-2xl overflow-hidden flex flex-col max-h-[92vh]">
        <div className="flex items-center justify-between gap-3 p-4 sm:p-5 border-b border-neutral-200 dark:border-dark-border bg-neutral-50 dark:bg-dark-card">
          <div className="min-w-0">
            <h3 id={titleId} className="font-bold text-lg text-neutral-900 dark:text-white flex items-center gap-2">
              {isEdit
                ? (isTr ? 'Entegrasyonu Düzenle' : 'Edit Integration')
                : (isTr ? 'Yeni Altyapı Entegrasyonu Ekle' : 'Add New Infrastructure Integration')}
            </h3>
            <p className="text-xs text-neutral-500 mt-0.5 truncate">
              {isEdit
                ? integration?.name
                : (isTr ? 'Gerçek, salt-okunur keşif bağlantısı tanımlayın ve doğrulayın' : 'Define and verify a real, read-only discovery connection')}
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label={isTr ? 'Kapat' : 'Close'}
            className="p-1 rounded-lg text-neutral-400 hover:text-neutral-600 dark:hover:text-neutral-200 shrink-0"
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        <div className="grid grid-cols-4 border-b border-neutral-200 dark:border-dark-border text-center text-[11px] sm:text-xs font-semibold">
          {[
            { num: 1, label: isTr ? '1. Sistem Türü' : '1. System Type' },
            { num: 2, label: isTr ? '2. Bağlantı' : '2. Connection' },
            { num: 3, label: isTr ? '3. Kimlik' : '3. Credentials' },
            { num: 4, label: isTr ? '4. Doğrulama' : '4. Verification' },
          ].map((s) => (
            <div
              key={s.num}
              className={`py-3 px-1 border-b-2 transition-colors ${
                step === s.num
                  ? 'border-brand-primary text-brand-primary dark:text-brand-primary-dark font-bold bg-brand-primary/5'
                  : step > s.num
                  ? 'border-emerald-500 text-emerald-600 dark:text-emerald-400'
                  : 'border-transparent text-neutral-400'
              }`}
            >
              {s.label}
            </div>
          ))}
        </div>

        <div className="p-4 sm:p-6 flex-1 overflow-y-auto space-y-4">
          {created && (
            <div className="p-3 rounded-lg border border-emerald-200 dark:border-emerald-800 bg-emerald-50 dark:bg-emerald-950/30 text-xs text-emerald-800 dark:text-emerald-300 flex items-start gap-2" role="status">
              <CheckCircle2 className="w-4 h-4 mt-0.5 shrink-0" />
              <span>
                {isTr
                  ? `"${created.name}" entegrasyonu oluşturuldu.`
                  : `Integration "${created.name}" was created.`}
              </span>
            </div>
          )}

          {discoveryError && (
            <div className="p-3 rounded-lg border border-amber-200 dark:border-amber-900 bg-amber-50 dark:bg-amber-950/30 text-xs text-amber-800 dark:text-amber-300 flex items-start gap-2" role="alert">
              <AlertTriangle className="w-4 h-4 mt-0.5 shrink-0" />
              <span>{discoveryError}</span>
            </div>
          )}

          {error && (
            <div className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-xs text-rose-700 dark:text-rose-300 flex items-start gap-2" role="alert">
              <AlertTriangle className="w-4 h-4 mt-0.5 shrink-0" />
              <span className="break-words">{error}</span>
            </div>
          )}

          {step === 1 && (
            <div className="space-y-3">
              <label className="block text-xs font-semibold uppercase tracking-wider text-neutral-500">
                {isTr ? 'Hedef Altyapı Bileşeni Seçin' : 'Select Target Infrastructure Component'}
              </label>
              {adaptersLoading ? (
                <p className="text-xs text-neutral-500">{isTr ? 'Adaptörler yükleniyor...' : 'Loading adapters...'}</p>
              ) : (
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                  {adapterOptions.map((adapter) => (
                    <button
                      key={adapter.adapterType}
                      type="button"
                      onClick={() => selectAdapter(adapter.adapterType)}
                      // The adapter of an existing integration cannot be changed.
                      disabled={isEdit && adapter.adapterType !== form.adapterType}
                      aria-pressed={form.adapterType === adapter.adapterType}
                      className={`p-4 rounded-xl border text-left transition-all disabled:opacity-40 disabled:cursor-not-allowed ${
                        form.adapterType === adapter.adapterType
                          ? 'border-brand-primary bg-brand-primary/5 dark:bg-brand-primary-dark/10 ring-2 ring-brand-primary/20'
                          : 'border-neutral-200 dark:border-dark-border hover:border-neutral-300'
                      }`}
                    >
                      <div className="flex items-center justify-between gap-2">
                        <span className="font-bold text-sm text-neutral-900 dark:text-white">
                          {adapter.displayName}
                        </span>
                        <span className="text-[10px] font-semibold px-2 py-0.5 rounded bg-neutral-100 dark:bg-dark-border text-neutral-600 dark:text-neutral-400">
                          v{adapter.version}
                        </span>
                      </div>
                      <p className="text-xs text-neutral-500 mt-1">
                        {adapter.adapterType === 'ActiveDirectory'
                          ? isTr
                            ? 'Forest, domain, OU, bilgisayar, grup ve güven ilişkileri'
                            : 'Forest, domain, OU, computer, group and trust relationships'
                          : isTr
                            ? 'Datacenter, cluster, ESXi, VM, datastore, ağ ve kaynak havuzları'
                            : 'Datacenter, cluster, ESXi, VM, datastore, network and resource pools'}
                      </p>
                    </button>
                  ))}
                </div>
              )}

              {isEdit && (
                <p className="text-[11px] text-neutral-500">
                  {isTr
                    ? 'Mevcut bir entegrasyonun adaptör türü değiştirilemez.'
                    : 'The adapter type of an existing integration cannot be changed.'}
                </p>
              )}

              {/* Adapter notes come from the server in English; show them only in the English UI. */}
              {!isTr && selectedAdapter?.configurationSchema.notes.map((note) => (
                <p key={note} className="text-[11px] text-neutral-500">
                  {note}
                </p>
              ))}
              {isTr && selectedAdapter && (
                <p className="text-[11px] text-neutral-500">
                  {isActiveDirectory
                    ? 'Salt-okunur bir LDAP servis hesabı kullanın; LDAPS (636) veya StartTLS önerilir.'
                    : "vCenter'da Read-Only role sahip bir servis hesabı kullanın; BerMap yalnızca okuma yapar."}
                </p>
              )}

              <label className="flex items-start gap-2 p-3 rounded-lg border border-neutral-200 dark:border-dark-border text-xs text-neutral-700 dark:text-neutral-300">
                <input
                  type="checkbox"
                  className="mt-0.5"
                  checked={form.collectorManaged}
                  onChange={(e) => update('collectorManaged', e.target.checked)}
                />
                <span>
                  <span className="font-semibold flex items-center gap-1.5">
                    <Server className="w-3.5 h-3.5" />
                    {isTr ? 'Veri bir collector üzerinden gelir' : 'Data arrives from a collector'}
                  </span>
                  <span className="block mt-0.5 text-[11px] text-neutral-500">
                    {isTr
                      ? 'Sunucu bu kaynağa bağlanmaz; bağlantı testi ve keşif collector tarafında yapılır. Sunucu, kullanıcı ve parola alanları opsiyoneldir.'
                      : 'The server never connects to this source; tests and discovery run on the collector. Server, user and password are optional.'}
                  </span>
                </span>
              </label>
            </div>
          )}

          {step === 2 && (
            <div className="space-y-4">
              <div>
                <label htmlFor={id('name')} className={labelClass}>
                  {isTr ? 'Entegrasyon Adı' : 'Integration Name'}
                </label>
                <Input id={id('name')} value={form.name} error={fieldErrors.name} onChange={(e) => update('name', e.target.value)} />
              </div>

              <div>
                <label htmlFor={id('description')} className={labelClass}>
                  {isTr ? 'Açıklama (Opsiyonel)' : 'Description (Optional)'}
                </label>
                <Input
                  id={id('description')}
                  value={form.description}
                  onChange={(e) => update('description', e.target.value)}
                  placeholder={isTr ? 'Örn: Üretim vCenter, salt-okunur keşif' : 'e.g. Production vCenter, read-only discovery'}
                />
              </div>

              <div className="grid grid-cols-3 gap-3">
                <div className="col-span-2">
                  <label htmlFor={id('server')} className={labelClass}>
                    {isTr ? 'Sunucu Adresi (FQDN veya IP)' : 'Server Address (FQDN or IP)'}
                    {collectorManaged && (isTr ? ' — opsiyonel' : ' — optional')}
                  </label>
                  <Input
                    id={id('server')}
                    value={form.server}
                    error={fieldErrors.server}
                    placeholder={isActiveDirectory ? 'dc01.corp.local' : 'vcenter.corp.local'}
                    onChange={(e) => {
                      setServerHint(null);
                      update('server', e.target.value);
                    }}
                    onBlur={() => normalizeServer(form)}
                  />
                </div>
                <div>
                  <label htmlFor={id('port')} className={labelClass}>
                    Port
                  </label>
                  <Input
                    id={id('port')}
                    type="number"
                    min={1}
                    max={65535}
                    value={form.port}
                    error={fieldErrors.port}
                    onChange={(e) => update('port', Number(e.target.value))}
                  />
                </div>
              </div>
              {serverHint && (
                <p className="-mt-2 text-[11px] text-blue-700 dark:text-blue-300 flex items-start gap-1.5" role="status">
                  <Info className="w-3.5 h-3.5 mt-0.5 shrink-0" />
                  {serverHint}
                </p>
              )}

              {form.adapterType === 'ActiveDirectory' && (
                <div>
                  <label htmlFor={id('baseDn')} className={labelClass}>
                    {isTr ? 'Base DN (Opsiyonel)' : 'Base DN (Optional)'}
                  </label>
                  <Input
                    id={id('baseDn')}
                    value={form.baseDn}
                    onChange={(e) => update('baseDn', e.target.value)}
                    placeholder="DC=corp,DC=local"
                  />
                </div>
              )}

              <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-xs">
                {isActiveDirectory && (
                  <label className="flex items-center gap-2 p-2.5 rounded-lg border border-neutral-200 dark:border-dark-border">
                    <input
                      type="checkbox"
                      checked={form.useSsl}
                      onChange={(e) => {
                        update('useSsl', e.target.checked);
                        if (e.target.checked) update('useStartTls', false);
                      }}
                    />
                    {isTr ? 'LDAPS (TLS) kullan' : 'Use LDAPS (TLS)'}
                  </label>
                )}
                {isActiveDirectory && (
                  <label className="flex items-center gap-2 p-2.5 rounded-lg border border-neutral-200 dark:border-dark-border">
                    <input
                      type="checkbox"
                      checked={form.useStartTls}
                      onChange={(e) => {
                        update('useStartTls', e.target.checked);
                        if (e.target.checked) update('useSsl', false);
                      }}
                    />
                    {isTr ? 'StartTLS kullan' : 'Use StartTLS'}
                  </label>
                )}
                <label className="flex items-center gap-2 p-2.5 rounded-lg border border-amber-200 dark:border-amber-900 bg-amber-50/50 dark:bg-amber-950/20">
                  <input
                    type="checkbox"
                    checked={form.allowUntrustedCertificate}
                    onChange={(e) => update('allowUntrustedCertificate', e.target.checked)}
                  />
                  {isTr ? 'Sertifika doğrulamasını devre dışı bırak' : 'Disable certificate validation'}
                </label>
                {isActiveDirectory && (
                  <label className="flex items-center gap-2 p-2.5 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50/50 dark:bg-rose-950/20">
                    <input
                      type="checkbox"
                      checked={form.allowPlaintext}
                      onChange={(e) => update('allowPlaintext', e.target.checked)}
                    />
                    {isTr ? 'Düz LDAP\'a izin ver (önerilmez)' : 'Allow plaintext LDAP (not recommended)'}
                  </label>
                )}
              </div>

              {form.adapterType === 'ActiveDirectory' ? (
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-xs">
                  <label className="flex items-center gap-2">
                    <input
                      type="checkbox"
                      checked={form.discoverAllDomains}
                      onChange={(e) => update('discoverAllDomains', e.target.checked)}
                    />
                    {isTr ? 'Tüm orman/domainleri keşfet' : 'Discover all forests/domains'}
                  </label>
                </div>
              ) : (
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-xs">
                  <label className="flex items-center gap-2">
                    <input
                      type="checkbox"
                      checked={form.collectMetrics}
                      onChange={(e) => update('collectMetrics', e.target.checked)}
                    />
                    {isTr ? 'CPU / RAM / datastore metriklerini topla' : 'Collect CPU / RAM / datastore metrics'}
                  </label>
                  <label className="flex items-center gap-2">
                    <input
                      type="checkbox"
                      checked={form.includePoweredOffVms}
                      onChange={(e) => update('includePoweredOffVms', e.target.checked)}
                    />
                    {isTr ? 'Kapalı VM\'leri dahil et' : 'Include powered-off VMs'}
                  </label>
                </div>
              )}
            </div>
          )}

          {step === 3 && (
            <div className="space-y-4">
              <div className="p-3.5 rounded-lg bg-blue-50 dark:bg-blue-950/30 border border-blue-200 dark:border-blue-900 text-xs text-blue-800 dark:text-blue-300 flex items-start gap-2">
                <ShieldCheck className="w-4 h-4 mt-0.5 text-blue-600 shrink-0" />
                <div>
                  <strong>{isTr ? 'Salt-Okunur Güvenlik İlkesi:' : 'Read-Only Security Policy:'}</strong>{' '}
                  {isTr
                    ? 'BerMap hedef sistemlerde hiçbir değişiklik yapmaz. Yalnızca okuma yetkisine sahip bir servis hesabı kullanın.'
                    : 'BerMap makes no changes on target systems. Use a service account with read-only permissions.'}
                </div>
              </div>

              <div>
                <label htmlFor={id('username')} className={labelClass}>
                  {isTr ? 'Kullanıcı Adı / Servis Hesabı' : 'Username / Service Account'}
                  {collectorManaged && (isTr ? ' — opsiyonel' : ' — optional')}
                </label>
                <Input
                  id={id('username')}
                  value={form.username}
                  error={fieldErrors.username}
                  onChange={(e) => update('username', e.target.value)}
                />
              </div>

              <div>
                <label htmlFor={id('secret')} className={labelClass}>
                  {isTr ? 'Parola / Secret' : 'Password / Secret'}
                  {(collectorManaged || isEdit) && (isTr ? ' — opsiyonel' : ' — optional')}
                </label>
                <Input
                  id={id('secret')}
                  type="password"
                  value={form.secret}
                  error={fieldErrors.secret}
                  onChange={(e) => update('secret', e.target.value)}
                  autoComplete="new-password"
                  placeholder={isEdit ? (isTr ? '••••••• (değiştirilmedi)' : '••••••• (unchanged)') : undefined}
                />
                <p className="text-[11px] text-neutral-400 mt-1">
                  {isEdit
                    ? (isTr
                      ? 'Boş bırakırsanız mevcut parola korunur. Yeni bir değer girerseniz şifreli kasadaki parola değiştirilir.'
                      : 'Leave empty to keep the current password. A new value replaces the password in the encrypted vault.')
                    : (isTr
                      ? 'Parola düz metin saklanmaz; AES-256-GCM ile şifrelenmiş yerel kasada tutulur.'
                      : 'The password is never stored in plain text; it is kept encrypted at rest in the local vault with AES-256-GCM.')}
                </p>
              </div>

              <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div>
                  <label htmlFor={id('cron')} className={labelClass}>
                    {isTr ? 'Keşif Takvimi (Cron)' : 'Discovery Schedule (Cron)'}
                  </label>
                  <Input
                    id={id('cron')}
                    value={form.scheduleCron}
                    error={fieldErrors.scheduleCron}
                    onChange={(e) => update('scheduleCron', e.target.value)}
                    className="font-mono"
                  />
                  <p className="text-[11px] text-neutral-400 mt-1">
                    {collectorManaged
                      ? (isTr
                        ? 'Collector yönetimli entegrasyonlarda sunucu bu takvimle keşif çalıştırmaz.'
                        : 'The server does not run discovery on this schedule for collector-managed integrations.')
                      : (isTr
                        ? 'Dakika saat gün ay haftanın-günü, sunucu saatiyle. Örn. 0 2 * * * her gün 02:00, 0 */6 * * * altı saatte bir.'
                        : 'Minute hour day month weekday, in server time. E.g. 0 2 * * * daily at 02:00, 0 */6 * * * every six hours.')}
                  </p>
                </div>
                <div>
                  <label htmlFor={id('timeout')} className={labelClass}>
                    {isTr ? 'İşlem Zaman Aşımı (sn)' : 'Operation Timeout (s)'}
                  </label>
                  <Input
                    id={id('timeout')}
                    type="number"
                    min={30}
                    max={86400}
                    value={form.timeoutSeconds}
                    error={fieldErrors.timeoutSeconds}
                    onChange={(e) => update('timeoutSeconds', Number(e.target.value))}
                  />
                </div>
              </div>
            </div>
          )}

          {step === 4 && (
            <div className="space-y-5">
              <div className="p-4 rounded-xl border border-neutral-200 dark:border-dark-border bg-neutral-50 dark:bg-dark-bg space-y-2 text-xs">
                <div className="flex justify-between gap-3 py-1 border-b border-neutral-200 dark:border-dark-border">
                  <span className="text-neutral-500 shrink-0">{isTr ? 'Entegrasyon:' : 'Integration:'}</span>
                  <span className="font-bold text-neutral-900 dark:text-white text-right break-all">
                    {form.name || (isTr ? '(adsız)' : '(unnamed)')} ({form.adapterType})
                  </span>
                </div>
                <div className="flex justify-between gap-3 py-1 border-b border-neutral-200 dark:border-dark-border">
                  <span className="text-neutral-500 shrink-0">{isTr ? 'Hedef Uç Nokta:' : 'Target Endpoint:'}</span>
                  <span className="font-mono text-neutral-800 dark:text-neutral-200 text-right break-all">
                    {form.server.trim() ? `${form.server.trim()}:${form.port}` : '—'}
                  </span>
                </div>
                <div className="flex justify-between gap-3 py-1 border-b border-neutral-200 dark:border-dark-border">
                  <span className="text-neutral-500 shrink-0">{isTr ? 'Kullanıcı:' : 'User:'}</span>
                  <span className="font-mono text-neutral-800 dark:text-neutral-200 text-right break-all">{form.username || '—'}</span>
                </div>
                <div className="flex justify-between gap-3 py-1 border-b border-neutral-200 dark:border-dark-border">
                  <span className="text-neutral-500 shrink-0">{isTr ? 'Toplama:' : 'Collection:'}</span>
                  <span className="font-medium text-neutral-800 dark:text-neutral-200 text-right">
                    {collectorManaged
                      ? (isTr ? 'Collector yönetimli' : 'Collector-managed')
                      : (isTr ? 'Sunucu tarafı keşif' : 'Server-side discovery')}
                  </span>
                </div>
                <div className="flex justify-between gap-3 py-1">
                  <span className="text-neutral-500 shrink-0">TLS:</span>
                  <span className="font-medium text-neutral-800 dark:text-neutral-200 text-right">
                    {form.allowUntrustedCertificate
                      ? (isTr ? 'Sertifika doğrulaması kapalı' : 'Certificate validation off')
                      : !isActiveDirectory
                      ? (isTr ? 'HTTPS / sıkı doğrulama' : 'HTTPS / strict validation')
                      : form.useSsl
                      ? (isTr ? 'LDAPS / sıkı doğrulama' : 'LDAPS / strict validation')
                      : form.useStartTls
                      ? (isTr ? 'StartTLS / sıkı doğrulama' : 'StartTLS / strict validation')
                      : (isTr ? 'Düz LDAP' : 'Plaintext LDAP')}
                  </span>
                </div>
              </div>

              {collectorManaged ? (
                <p className="p-3 rounded-lg border border-blue-200 dark:border-blue-900 bg-blue-50 dark:bg-blue-950/30 text-xs text-blue-800 dark:text-blue-300 flex items-start gap-2">
                  <Info className="w-4 h-4 mt-0.5 shrink-0" />
                  {isTr
                    ? 'Collector yönetimli entegrasyonlar sunucudan test edilmez ve keşfedilmez; durum collector batch’lerinden izlenir.'
                    : 'Collector-managed integrations are not tested or discovered from the server; health follows the collector batches.'}
                </p>
              ) : (
                <>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    onClick={() => void handleTestConnection()}
                    disabled={isTesting || isSaving || !canTestDraft}
                    className="w-full gap-2 border-brand-primary text-brand-primary hover:bg-brand-primary/5"
                  >
                    <RotateCw className={`w-4 h-4 ${isTesting ? 'animate-spin' : ''}`} />
                    {isTesting ? (isTr ? 'Bağlantı ve Yetkiler Test Ediliyor...' : 'Testing connection and permissions...') : (isTr ? 'Bağlantıyı Şimdi Test Et' : 'Test Connection Now')}
                  </Button>
                  {!canTestDraft && (
                    <p className="-mt-3 text-[11px] text-neutral-500">
                      {isTr
                        ? 'Değişiklikleri kayıtlı parolayla test etmek için kaydedin ve karttaki “Test Et” düğmesini kullanın; ya da parolayı yeniden girin.'
                        : 'To test with the stored password, save and use “Test” on the card, or enter the password again.'}
                    </p>
                  )}
                </>
              )}

              {testResult && (
                <div
                  className={`p-3.5 rounded-lg border text-xs flex items-start gap-2.5 ${
                    testResult.success
                      ? 'bg-emerald-50 dark:bg-emerald-950/40 border-emerald-200 dark:border-emerald-800 text-emerald-800 dark:text-emerald-300'
                      : 'bg-rose-50 dark:bg-rose-950/40 border-rose-200 dark:border-rose-800 text-rose-800 dark:text-rose-300'
                  }`}
                >
                  {testResult.success ? (
                    <CheckCircle2 className="w-4 h-4 mt-0.5 text-emerald-600 shrink-0" />
                  ) : (
                    <AlertTriangle className="w-4 h-4 mt-0.5 text-rose-600 shrink-0" />
                  )}
                  <div className="min-w-0">
                    <div className="font-bold">
                      {testResult.success ? (isTr ? 'Bağlantı Başarılı' : 'Connection Successful') : (isTr ? 'Bağlantı Başarısız' : 'Connection Failed')}
                      {testResult.serverVersion ? ` — ${testResult.serverVersion}` : ''}
                    </div>
                    <div className="mt-0.5 text-[11px] opacity-90 break-words">
                      {connectionErrorText(testResult.errorCode, isTr) ?? testResult.message}
                      {testResult.errorCode ? ` (${testResult.errorCode})` : ''}
                    </div>
                    {testResult.warnings.length > 0 && (
                      <ul className="mt-1 list-disc list-inside text-[11px] opacity-90">
                        {testResult.warnings.map((warning) => (
                          <li key={warning} className="break-words">{warning}</li>
                        ))}
                      </ul>
                    )}
                  </div>
                </div>
              )}

              <div className="space-y-2">
                <label className="flex items-center gap-2 text-xs text-neutral-600 dark:text-neutral-400">
                  <input
                    type="checkbox"
                    checked={form.isEnabled}
                    onChange={(e) => update('isEnabled', e.target.checked)}
                  />
                  {isTr ? 'Entegrasyon etkin (zamanlanmış keşif çalışır)' : 'Integration enabled (scheduled discovery runs)'}
                </label>
                {!isEdit && !collectorManaged && (
                  <label className="flex items-center gap-2 text-xs text-neutral-600 dark:text-neutral-400">
                    <input
                      type="checkbox"
                      checked={startDiscovery}
                      disabled={!form.isEnabled}
                      onChange={(e) => setStartDiscovery(e.target.checked)}
                    />
                    {isTr ? 'Kaydettikten sonra tam keşfi hemen başlat' : 'Start a full discovery immediately after saving'}
                  </label>
                )}
              </div>
            </div>
          )}
        </div>

        <div className="flex items-center justify-between gap-2 p-4 border-t border-neutral-200 dark:border-dark-border bg-neutral-50 dark:bg-dark-card">
          {step > 1 ? (
            <Button
              variant="outline"
              size="sm"
              onClick={() => {
                setError(null);
                setStep((s) => s - 1);
              }}
              disabled={isSaving || created !== null}
            >
              {isTr ? 'Geri' : 'Back'}
            </Button>
          ) : (
            <div />
          )}

          {created ? (
            <Button variant="primary" size="sm" onClick={onClose} disabled={isSaving} className="bg-brand-primary">
              {isTr ? 'Kapat' : 'Close'}
            </Button>
          ) : step < 4 ? (
            <Button
              variant="primary"
              size="sm"
              onClick={goNext}
              className="bg-brand-primary"
            >
              {isTr ? 'İlerle' : 'Next'}
            </Button>
          ) : (
            <Button
              variant="primary"
              size="sm"
              onClick={() => void handleSave()}
              disabled={isSaving || isTesting}
              className="bg-brand-primary gap-1.5"
            >
              {isSaving ? (
                <RotateCw className="w-4 h-4 animate-spin" />
              ) : (
                <CheckCircle2 className="w-4 h-4" />
              )}
              {saveLabel}
            </Button>
          )}
        </div>
      </Card>
    </div>
  );
};
