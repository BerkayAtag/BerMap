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
} from 'lucide-react';
import { api } from '@/lib/api';
import { useLocale } from '@/lib/i18n';
import type {
  AdapterDescriptor,
  ConnectionTestResult,
  CreateIntegrationRequest,
  Integration,
} from '@/types';

interface IntegrationWizardModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSuccess: () => void;
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
}

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
};

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
}) => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const [step, setStep] = useState(1);
  const [form, setForm] = useState<FormState>(initialState);
  const [isTesting, setIsTesting] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  const [testResult, setTestResult] = useState<ConnectionTestResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [startDiscovery, setStartDiscovery] = useState(true);
  const [created, setCreated] = useState<Integration | null>(null);
  const [discoveryError, setDiscoveryError] = useState<string | null>(null);
  const titleId = useId();
  const isActiveDirectory = form.adapterType === 'ActiveDirectory';

  const { data: adapters = [], isLoading: adaptersLoading } = useQuery<AdapterDescriptor[]>({
    queryKey: ['integrations', 'adapters'],
    queryFn: () => api.get<AdapterDescriptor[]>('/integrations/adapters'),
    enabled: isOpen,
    staleTime: 5 * 60_000,
  });

  useEffect(() => {
    if (!isOpen) {
      setStep(1);
      setForm(initialState);
      setTestResult(null);
      setError(null);
      setIsSaving(false);
      setIsTesting(false);
      setCreated(null);
      setDiscoveryError(null);
    }
  }, [isOpen]);

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

  const update = <K extends keyof FormState>(key: K, value: FormState[K]) => {
    setForm((current) => ({ ...current, [key]: value }));
    setTestResult(null);
    setError(null);
  };

  const selectAdapter = (adapterType: string) => {
    setForm((current) => ({
      ...current,
      adapterType,
      name: current.name || `${adapterType} ${isTr ? 'Entegrasyonu' : 'Integration'}`,
      port: adapterType === 'ActiveDirectory' ? 636 : 443,
      useSsl: true,
      useStartTls: false,
    }));
    setTestResult(null);
    setError(null);
  };

  const validateConnectionFields = (): string | null => {
    if (!form.name.trim()) return isTr ? 'Entegrasyon adı zorunludur.' : 'Integration name is required.';
    if (!form.server.trim()) return isTr ? 'Sunucu adresi zorunludur.' : 'Server address is required.';
    if (!form.username.trim()) return isTr ? 'Salt-okunur servis hesabı zorunludur.' : 'A read-only service account is required.';
    if (!form.secret.trim()) return isTr ? 'Parola / secret zorunludur.' : 'Password / secret is required.';
    if (form.adapterType === 'ActiveDirectory' && !form.useSsl && !form.useStartTls && !form.allowPlaintext) {
      return isTr
        ? 'LDAPS veya StartTLS seçin; düz LDAP yalnızca açık onayla kullanılabilir.'
        : 'Select LDAPS or StartTLS; plaintext LDAP requires an explicit confirmation.';
    }
    return null;
  };

  const handleTestConnection = async () => {
    const validation = validateConnectionFields();
    if (validation) {
      setError(validation);
      return;
    }

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
        setError(result.errorDetail || result.message || (isTr ? 'Bağlantı testi başarısız oldu.' : 'Connection test failed.'));
      }
    } catch (requestError) {
      setError((requestError as { message?: string }).message ?? (isTr ? 'Bağlantı testi başarısız oldu.' : 'Connection test failed.'));
    } finally {
      setIsTesting(false);
    }
  };

  const handleSave = async () => {
    const validation = validateConnectionFields();
    if (validation) {
      setError(validation);
      return;
    }

    if (created) return;

    setIsSaving(true);
    setError(null);

    let createdIntegration: Integration;
    try {
      const payload: CreateIntegrationRequest = {
        name: form.name.trim(),
        description: form.description.trim(),
        adapterType: form.adapterType,
        configuration: buildConfiguration(form),
        secret: form.secret,
        scheduleCron: form.scheduleCron.trim() || '0 */6 * * *',
        timeoutSeconds: form.timeoutSeconds,
        isEnabled: true,
      };

      createdIntegration = await api.post<Integration>('/integrations', payload);
    } catch (requestError) {
      setError((requestError as { message?: string }).message ?? (isTr ? 'Entegrasyon kaydedilemedi.' : 'Failed to save the integration.'));
      setIsSaving(false);
      return;
    }

    // The integration now exists server-side. From here on, never re-submit the create request.
    setCreated(createdIntegration);
    onSuccess();

    if (startDiscovery) {
      try {
        await api.post(`/integrations/${createdIntegration.id}/discover`, { jobType: 'Full' });
      } catch (requestError) {
        const message = (requestError as { message?: string }).message;
        setDiscoveryError(
          (isTr
            ? 'Entegrasyon oluşturuldu ancak keşif başlatılamadı. Keşfi Entegrasyonlar listesinden yeniden başlatabilirsiniz.'
            : 'The integration was created, but discovery could not be started. You can start it again from the Integrations list.') +
            (message ? ` (${message})` : ''),
        );
        setIsSaving(false);
        return;
      }
    }

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

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60 backdrop-blur-xs bm-fade-in"
      role="dialog"
      aria-modal="true"
      aria-labelledby={titleId}
    >
      <Card className="w-full max-w-2xl border-neutral-200 dark:border-dark-border shadow-2xl overflow-hidden flex flex-col max-h-[90vh]">
        <div className="flex items-center justify-between p-5 border-b border-neutral-200 dark:border-dark-border bg-neutral-50 dark:bg-dark-card">
          <div>
            <h3 id={titleId} className="font-bold text-lg text-neutral-900 dark:text-white flex items-center gap-2">
              {isTr ? 'Yeni Altyapı Entegrasyonu Ekle' : 'Add New Infrastructure Integration'}
            </h3>
            <p className="text-xs text-neutral-500 mt-0.5">
              {isTr ? 'Gerçek, salt-okunur keşif bağlantısı tanımlayın ve doğrulayın' : 'Define and verify a real, read-only discovery connection'}
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label={isTr ? 'Kapat' : 'Close'}
            className="p-1 rounded-lg text-neutral-400 hover:text-neutral-600 dark:hover:text-neutral-200"
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        <div className="grid grid-cols-4 border-b border-neutral-200 dark:border-dark-border text-center text-xs font-semibold">
          {[
            { num: 1, label: isTr ? '1. Sistem Türü' : '1. System Type' },
            { num: 2, label: isTr ? '2. Bağlantı' : '2. Connection' },
            { num: 3, label: isTr ? '3. Kimlik' : '3. Credentials' },
            { num: 4, label: isTr ? '4. Doğrulama' : '4. Verification' },
          ].map((s) => (
            <div
              key={s.num}
              className={`py-3 border-b-2 transition-colors ${
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

        <div className="p-6 flex-1 overflow-y-auto space-y-4">
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
            <div className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-xs text-rose-700 dark:text-rose-300 flex items-start gap-2">
              <AlertTriangle className="w-4 h-4 mt-0.5 shrink-0" />
              <span>{error}</span>
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
                      className={`p-4 rounded-xl border text-left transition-all ${
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

              {selectedAdapter?.configurationSchema.notes.map((note) => (
                <p key={note} className="text-[11px] text-neutral-500">
                  {note}
                </p>
              ))}
            </div>
          )}

          {step === 2 && (
            <div className="space-y-4">
              <div>
                <label className="block text-xs font-semibold text-neutral-600 dark:text-neutral-400 mb-1">
                  {isTr ? 'Entegrasyon Adı' : 'Integration Name'}
                </label>
                <Input value={form.name} onChange={(e) => update('name', e.target.value)} />
              </div>

              <div>
                <label className="block text-xs font-semibold text-neutral-600 dark:text-neutral-400 mb-1">
                  {isTr ? 'Açıklama (Opsiyonel)' : 'Description (Optional)'}
                </label>
                <Input
                  value={form.description}
                  onChange={(e) => update('description', e.target.value)}
                  placeholder={isTr ? 'Örn: Üretim vCenter, salt-okunur keşif' : 'e.g. Production vCenter, read-only discovery'}
                />
              </div>

              <div className="grid grid-cols-3 gap-3">
                <div className="col-span-2">
                  <label className="block text-xs font-semibold text-neutral-600 dark:text-neutral-400 mb-1">
                    {isTr ? 'Sunucu Adresi (FQDN veya IP)' : 'Server Address (FQDN or IP)'}
                  </label>
                  <Input value={form.server} onChange={(e) => update('server', e.target.value)} />
                </div>
                <div>
                  <label className="block text-xs font-semibold text-neutral-600 dark:text-neutral-400 mb-1">
                    Port
                  </label>
                  <Input
                    type="number"
                    value={form.port}
                    onChange={(e) => update('port', Number(e.target.value))}
                  />
                </div>
              </div>

              {form.adapterType === 'ActiveDirectory' && (
                <div>
                  <label className="block text-xs font-semibold text-neutral-600 dark:text-neutral-400 mb-1">
                    {isTr ? 'Base DN (Opsiyonel)' : 'Base DN (Optional)'}
                  </label>
                  <Input
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
                <label className="block text-xs font-semibold text-neutral-600 dark:text-neutral-400 mb-1">
                  {isTr ? 'Kullanıcı Adı / Servis Hesabı' : 'Username / Service Account'}
                </label>
                <Input value={form.username} onChange={(e) => update('username', e.target.value)} />
              </div>

              <div>
                <label className="block text-xs font-semibold text-neutral-600 dark:text-neutral-400 mb-1">
                  {isTr ? 'Parola / Secret' : 'Password / Secret'}
                </label>
                <Input
                  type="password"
                  value={form.secret}
                  onChange={(e) => update('secret', e.target.value)}
                  autoComplete="new-password"
                />
                <p className="text-[11px] text-neutral-400 mt-1">
                  {isTr
                    ? 'Parola düz metin saklanmaz; AES-256-GCM ile şifrelenmiş yerel kasada tutulur.'
                    : 'The password is never stored in plain text; it is kept encrypted at rest in the local vault with AES-256-GCM.'}
                </p>
              </div>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-xs font-semibold text-neutral-600 dark:text-neutral-400 mb-1">
                    {isTr ? 'Keşif Takvimi (Cron)' : 'Discovery Schedule (Cron)'}
                  </label>
                  <Input
                    value={form.scheduleCron}
                    onChange={(e) => update('scheduleCron', e.target.value)}
                  />
                </div>
                <div>
                  <label className="block text-xs font-semibold text-neutral-600 dark:text-neutral-400 mb-1">
                    {isTr ? 'İşlem Zaman Aşımı (sn)' : 'Operation Timeout (s)'}
                  </label>
                  <Input
                    type="number"
                    value={form.timeoutSeconds}
                    onChange={(e) => update('timeoutSeconds', Number(e.target.value))}
                  />
                </div>
              </div>
            </div>
          )}

          {step === 4 && (
            <div className="space-y-5">
              <div className="p-4 rounded-xl border border-neutral-200 dark:border-dark-border bg-neutral-50 dark:bg-dark-bg space-y-2 text-xs">
                <div className="flex justify-between py-1 border-b border-neutral-200 dark:border-dark-border">
                  <span className="text-neutral-500">{isTr ? 'Entegrasyon:' : 'Integration:'}</span>
                  <span className="font-bold text-neutral-900 dark:text-white">
                    {form.name || (isTr ? '(adsız)' : '(unnamed)')} ({form.adapterType})
                  </span>
                </div>
                <div className="flex justify-between py-1 border-b border-neutral-200 dark:border-dark-border">
                  <span className="text-neutral-500">{isTr ? 'Hedef Uç Nokta:' : 'Target Endpoint:'}</span>
                  <span className="font-mono text-neutral-800 dark:text-neutral-200">
                    {form.server}:{form.port}
                  </span>
                </div>
                <div className="flex justify-between py-1 border-b border-neutral-200 dark:border-dark-border">
                  <span className="text-neutral-500">{isTr ? 'Kullanıcı:' : 'User:'}</span>
                  <span className="font-mono text-neutral-800 dark:text-neutral-200">{form.username}</span>
                </div>
                <div className="flex justify-between py-1">
                  <span className="text-neutral-500">{isTr ? 'TLS:' : 'TLS:'}</span>
                  <span className="font-medium text-neutral-800 dark:text-neutral-200">
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

              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => void handleTestConnection()}
                disabled={isTesting || isSaving}
                className="w-full gap-2 border-brand-primary text-brand-primary hover:bg-brand-primary/5"
              >
                <RotateCw className={`w-4 h-4 ${isTesting ? 'animate-spin' : ''}`} />
                {isTesting ? (isTr ? 'Bağlantı ve Yetkiler Test Ediliyor...' : 'Testing connection and permissions...') : (isTr ? 'Bağlantıyı Şimdi Test Et' : 'Test Connection Now')}
              </Button>

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
                  <div>
                    <div className="font-bold">
                      {testResult.success ? (isTr ? 'Bağlantı Başarılı' : 'Connection Successful') : (isTr ? 'Bağlantı Başarısız' : 'Connection Failed')}
                      {testResult.serverVersion ? ` — ${testResult.serverVersion}` : ''}
                    </div>
                    <div className="mt-0.5 text-[11px] opacity-90">
                      {testResult.message}
                      {testResult.errorCode ? ` (${testResult.errorCode})` : ''}
                    </div>
                    {testResult.warnings.length > 0 && (
                      <ul className="mt-1 list-disc list-inside text-[11px] opacity-90">
                        {testResult.warnings.map((warning) => (
                          <li key={warning}>{warning}</li>
                        ))}
                      </ul>
                    )}
                  </div>
                </div>
              )}

              <label className="flex items-center gap-2 text-xs text-neutral-600 dark:text-neutral-400">
                <input
                  type="checkbox"
                  checked={startDiscovery}
                  onChange={(e) => setStartDiscovery(e.target.checked)}
                />
                {isTr ? 'Kaydettikten sonra tam keşfi hemen başlat' : 'Start a full discovery immediately after saving'}
              </label>
            </div>
          )}
        </div>

        <div className="flex items-center justify-between p-4 border-t border-neutral-200 dark:border-dark-border bg-neutral-50 dark:bg-dark-card">
          {step > 1 ? (
            <Button variant="outline" size="sm" onClick={() => setStep((s) => s - 1)} disabled={isSaving || created !== null}>
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
              onClick={() => setStep((s) => s + 1)}
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
              {isSaving ? (isTr ? 'Kaydediliyor...' : 'Saving...') : (isTr ? 'Kaydet ve Keşfi Başlat' : 'Save & Start Discovery')}
            </Button>
          )}
        </div>
      </Card>
    </div>
  );
};
