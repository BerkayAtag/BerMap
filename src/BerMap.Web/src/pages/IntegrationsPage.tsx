import React, { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Card } from '@/components/ui/Card';
import { Button } from '@/components/ui/Button';
import { StatusIndicator } from '@/components/ui/StatusIndicator';
import {
  Activity,
  AlertTriangle,
  CheckCircle2,
  Clock3,
  Loader2,
  Play,
  PlugZap,
  Plus,
  Power,
  PowerOff,
  RefreshCw,
  ShieldCheck,
  Trash2,
  X,
} from 'lucide-react';
import { IntegrationWizardModal } from '@/components/integrations/IntegrationWizardModal';
import { api } from '@/lib/api';
import { useLocale } from '@/lib/i18n';
import { useT } from '@/lib/i18n-runtime';
import { useCanManageIntegrations } from '@/stores/authStore';
import {
  formatDateTime,
  formatLatency,
  formatNumber,
  formatRelativeTime,
  getErrorMessage,
  toNodeStatus,
} from '@/lib/format';
import type {
  ConnectionTestResult,
  DiscoveryJob,
  Integration,
} from '@/types';

const INTEGRATION_STATUS_LABELS: Record<string, { tr: string; en: string }> = {
  Active: { tr: 'Aktif', en: 'Active' },
  Inactive: { tr: 'Pasif', en: 'Inactive' },
  Error: { tr: 'Hata', en: 'Error' },
  Testing: { tr: 'Test Ediliyor', en: 'Testing' },
  Degraded: { tr: 'Kısıtlı', en: 'Degraded' },
};

function integrationStatusLabel(isTr: boolean, status: string): string {
  const entry = INTEGRATION_STATUS_LABELS[status];
  return entry ? (isTr ? entry.tr : entry.en) : status;
}

const JOB_STATUS_LABELS: Record<string, { tr: string; en: string }> = {
  Queued: { tr: 'Kuyrukta', en: 'Queued' },
  Running: { tr: 'Çalışıyor', en: 'Running' },
  Completed: { tr: 'Tamamlandı', en: 'Completed' },
  PartialSuccess: { tr: 'Kısmi Başarı', en: 'Partial Success' },
  Failed: { tr: 'Başarısız', en: 'Failed' },
  Cancelled: { tr: 'İptal Edildi', en: 'Cancelled' },
};

function jobStatusLabel(isTr: boolean, status: string): string {
  const entry = JOB_STATUS_LABELS[status];
  return entry ? (isTr ? entry.tr : entry.en) : status;
}

function integrationStatusClass(status: string): string {
  switch (status) {
    case 'Active':
      return 'bg-emerald-50 dark:bg-emerald-950/40 text-emerald-700 dark:text-emerald-400 border-emerald-200 dark:border-emerald-800';
    case 'Error':
      return 'bg-rose-50 dark:bg-rose-950/40 text-rose-700 dark:text-rose-400 border-rose-200 dark:border-rose-800';
    case 'Testing':
    case 'Degraded':
      return 'bg-amber-50 dark:bg-amber-950/40 text-amber-700 dark:text-amber-400 border-amber-200 dark:border-amber-800';
    default:
      return 'bg-neutral-100 dark:bg-dark-border text-neutral-600 dark:text-neutral-400 border-neutral-200 dark:border-dark-border';
  }
}

function jobStatusClass(status: string): string {
  switch (status) {
    case 'Completed':
      return 'text-emerald-600 dark:text-emerald-400';
    case 'PartialSuccess':
      return 'text-amber-600 dark:text-amber-400';
    case 'Failed':
      return 'text-rose-600 dark:text-rose-400';
    case 'Running':
      return 'text-blue-600 dark:text-blue-400';
    default:
      return 'text-neutral-500 dark:text-neutral-400';
  }
}

function integrationWarnings(integration: Integration, isTr: boolean): string[] {
  const warnings: string[] = [];
  const configuration = integration.configuration ?? {};

  if (configuration.allowUntrustedCertificate === true) {
    warnings.push(isTr ? 'TLS sertifika doğrulaması devre dışı.' : 'TLS certificate validation is disabled.');
  }
  if (configuration.allowPlaintext === true) {
    warnings.push(isTr ? 'Düz LDAP bağlantısına izin verilmiş.' : 'Plaintext LDAP connection is allowed.');
  }
  if (!integration.isEnabled) {
    warnings.push(isTr ? 'Entegrasyon devre dışı; yeni keşif başlatılamaz.' : 'Integration is disabled; a new discovery cannot be started.');
  }
  if (integration.lastError) {
    warnings.push(integration.lastError);
  }

  return warnings;
}

export const IntegrationsPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const queryClient = useQueryClient();
  const t = useT();
  const canManage = useCanManageIntegrations();
  const [isWizardOpen, setIsWizardOpen] = useState(false);
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  const integrationsQuery = useQuery<Integration[]>({
    queryKey: ['integrations', 'list'],
    queryFn: () => api.get<Integration[]>('/integrations'),
    refetchInterval: 20_000,
  });

  const jobsQuery = useQuery<DiscoveryJob[]>({
    queryKey: ['integrations', 'jobs'],
    queryFn: () => api.get<DiscoveryJob[]>('/integrations/jobs?limit=12'),
    refetchInterval: 8_000,
  });

  const integrations = integrationsQuery.data ?? [];
  const jobs = jobsQuery.data ?? [];
  const activeIntegrations = integrations.filter((item) => item.status === 'Active').length;
  const errorIntegrations = integrations.filter((item) => item.status === 'Error').length;

  const refreshAll = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['integrations', 'list'] }),
      queryClient.invalidateQueries({ queryKey: ['integrations', 'jobs'] }),
    ]);
  };

  const handleTest = async (integration: Integration) => {
    setBusyAction(`test:${integration.id}`);
    setActionError(null);
    setNotice(null);

    try {
      const result = await api.post<ConnectionTestResult>(
        `/integrations/${integration.id}/test`,
        {}
      );
      await refreshAll();

      if (result.success) {
        const warningText = result.warnings.length > 0
          ? (isTr ? ` Uyarılar: ${result.warnings.join(' ')}` : ` Warnings: ${result.warnings.join(' ')}`)
          : '';
        setNotice(
          isTr
            ? `${integration.name}: bağlantı başarılı (${formatLatency(result.latency)}).${warningText}`
            : `${integration.name}: connection successful (${formatLatency(result.latency)}).${warningText}`
        );
      } else {
        setActionError(
          `${integration.name}: ${result.errorDetail || result.message}` +
            (result.errorCode ? ` (${result.errorCode})` : '')
        );
      }
    } catch (error) {
      setActionError(`${integration.name}: ${getErrorMessage(error)}`);
    } finally {
      setBusyAction(null);
    }
  };

  const handleDiscover = async (integration: Integration) => {
    setBusyAction(`discover:${integration.id}`);
    setActionError(null);
    setNotice(null);

    try {
      const job = await api.post<DiscoveryJob>(
        `/integrations/${integration.id}/discover`,
        { jobType: 'Full' }
      );
      await refreshAll();
      setNotice(
        isTr
          ? `${integration.name}: keşif işi kuyruğa alındı (${jobStatusLabel(isTr, job.status)}).`
          : `${integration.name}: discovery job queued (${jobStatusLabel(isTr, job.status)}).`
      );
    } catch (error) {
      setActionError(`${integration.name}: ${getErrorMessage(error)}`);
    } finally {
      setBusyAction(null);
    }
  };

  const handleBulkDiscover = async () => {
    const enabled = integrations.filter((item) => item.isEnabled);
    if (enabled.length === 0) {
      setActionError(isTr ? 'Keşif başlatılabilecek etkin entegrasyon bulunmuyor.' : 'No enabled integration available to start discovery.');
      return;
    }

    setBusyAction('bulk');
    setActionError(null);
    setNotice(null);

    const results = await Promise.allSettled(
      enabled.map((integration) =>
        api.post<DiscoveryJob>(`/integrations/${integration.id}/discover`, {
          jobType: 'Full',
        })
      )
    );

    const failures = results
      .map((result, index) => ({ result, integration: enabled[index] }))
      .filter(({ result }) => result.status === 'rejected');

    await refreshAll();

    if (failures.length > 0) {
      const details = failures
        .slice(0, 3)
        .map(({ result, integration }) =>
          `${integration.name}: ${
            result.status === 'rejected' ? getErrorMessage(result.reason) : ''
          }`
        )
        .join(' ');
      setActionError(
        isTr
          ? `${failures.length} entegrasyon için keşif başlatılamadı. ${details}`
          : `Failed to start discovery for ${failures.length} integrations. ${details}`
      );
    }

    if (failures.length < enabled.length) {
      setNotice(
        isTr
          ? `${enabled.length - failures.length} entegrasyon için keşif işi kuyruğa alındı.`
          : `Discovery jobs queued for ${enabled.length - failures.length} integrations.`
      );
    }

    setBusyAction(null);
  };

  const handleToggleEnabled = async (integration: Integration) => {
    setBusyAction(`toggle:${integration.id}`);
    setActionError(null);
    setNotice(null);

    try {
      await api.put<Integration>(`/integrations/${integration.id}`, {
        isEnabled: !integration.isEnabled,
      });
      await refreshAll();
      setNotice(
        isTr
          ? `${integration.name} ${integration.isEnabled ? 'devre dışı bırakıldı' : 'etkinleştirildi'}.`
          : `${integration.name} ${integration.isEnabled ? 'disabled' : 'enabled'}.`
      );
    } catch (error) {
      setActionError(`${integration.name}: ${getErrorMessage(error)}`);
    } finally {
      setBusyAction(null);
    }
  };

  const handleDelete = async (integration: Integration) => {
    const confirmed = window.confirm(
      isTr
        ? `${integration.name} entegrasyonu silinsin mi? Bu işlem geçmiş keşif verilerini silmez, ancak entegrasyonu ve şifreli kimlik bilgisini kaldırır.`
        : `Delete integration ${integration.name}? This does not delete past discovery data, but removes the integration and its encrypted credential.`
    );
    if (!confirmed) return;

    setBusyAction(`delete:${integration.id}`);
    setActionError(null);
    setNotice(null);

    try {
      await api.delete<void>(`/integrations/${integration.id}`);
      await refreshAll();
      setNotice(isTr ? `${integration.name} silindi.` : `${integration.name} deleted.`);
    } catch (error) {
      setActionError(`${integration.name}: ${getErrorMessage(error)}`);
    } finally {
      setBusyAction(null);
    }
  };

  const handleCancelJob = async (job: DiscoveryJob) => {
    setBusyAction(`cancel:${job.id}`);
    setActionError(null);
    setNotice(null);

    try {
      await api.post<void>(`/integrations/jobs/${job.id}/cancel`);
      await refreshAll();
      setNotice(isTr ? 'Keşif işi için iptal talebi kaydedildi.' : 'Cancel request recorded for the discovery job.');
    } catch (error) {
      setActionError(getErrorMessage(error));
    } finally {
      setBusyAction(null);
    }
  };

  const isBusy = busyAction !== null;
  const integrationsError = integrationsQuery.isError
    ? getErrorMessage(integrationsQuery.error)
    : null;
  const jobsError = jobsQuery.isError ? getErrorMessage(jobsQuery.error) : null;

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      <IntegrationWizardModal
        isOpen={isWizardOpen}
        onClose={() => setIsWizardOpen(false)}
        onSuccess={() => {
          void refreshAll();
          setNotice(isTr
            ? 'Entegrasyon oluşturuldu. Bağlantı durumu ve keşif işi aşağıda görüntülenebilir.'
            : 'Integration created. Its connection status and discovery job are shown below.');
        }}
      />

      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
            {isTr ? 'Entegrasyon Yönetimi' : 'Integration Management'}
          </h1>
          <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1">
            {isTr
              ? 'Active Directory ve VMware vCenter için salt-okunur bağlantılar, bağlantı testleri ve kalıcı keşif kuyruğu'
              : 'Read-only connections, connection tests and a persistent discovery queue for Active Directory and VMware vCenter'}
          </p>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            className="gap-2"
            onClick={() => void refreshAll()}
            disabled={integrationsQuery.isFetching || jobsQuery.isFetching}
          >
            <RefreshCw
              className={`w-4 h-4 ${integrationsQuery.isFetching ? 'animate-spin' : ''}`}
            />
            {isTr ? 'Yenile' : 'Refresh'}
          </Button>
          <Button
            variant="outline"
            size="sm"
            className="gap-2"
            onClick={() => void handleBulkDiscover()}
            disabled={isBusy || integrations.length === 0 || !canManage}
          >
            {busyAction === 'bulk' ? (
              <Loader2 className="w-4 h-4 animate-spin" />
            ) : (
              <Play className="w-4 h-4" />
            )}
            {isTr ? 'Tümünü Keşfet' : 'Discover All'}
          </Button>
          <Button
            variant="primary"
            size="sm"
            onClick={() => setIsWizardOpen(true)}
            disabled={!canManage}
            title={canManage ? undefined : t('noPermission')}
            className="gap-2 bg-brand-primary"
          >
            <Plus className="w-4 h-4" />
            {isTr ? 'Yeni Entegrasyon Ekle' : 'Add New Integration'}
          </Button>
        </div>
      </div>

      {notice && (
        <div
          role="status"
          className="p-3 rounded-lg border border-emerald-200 dark:border-emerald-900 bg-emerald-50 dark:bg-emerald-950/30 text-sm text-emerald-800 dark:text-emerald-300 flex items-start gap-2"
        >
          <CheckCircle2 className="w-4 h-4 mt-0.5 shrink-0" />
          <span className="flex-1">{notice}</span>
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

      {actionError && (
        <div
          role="alert"
          className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-800 dark:text-rose-300 flex items-start gap-2"
        >
          <AlertTriangle className="w-4 h-4 mt-0.5 shrink-0" />
          <span className="flex-1">{actionError}</span>
          <button
            type="button"
            onClick={() => setActionError(null)}
            className="text-rose-600 dark:text-rose-400"
            aria-label={isTr ? 'Hatayı kapat' : 'Close error'}
          >
            <X className="w-4 h-4" />
          </button>
        </div>
      )}

      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
        <Card className="p-4 flex items-center gap-3">
          <div className="p-2 rounded-lg bg-brand-primary/10 text-brand-primary dark:text-brand-primary-dark">
            <PlugZap className="w-5 h-5" />
          </div>
          <div>
            <div className="text-xs text-neutral-500">{isTr ? 'Toplam Entegrasyon' : 'Total Integrations'}</div>
            <div className="text-xl font-bold text-neutral-900 dark:text-white">
              {formatNumber(integrations.length)}
            </div>
          </div>
        </Card>
        <Card className="p-4 flex items-center gap-3">
          <div className="p-2 rounded-lg bg-emerald-50 dark:bg-emerald-950/30 text-emerald-600 dark:text-emerald-400">
            <ShieldCheck className="w-5 h-5" />
          </div>
          <div>
            <div className="text-xs text-neutral-500">{isTr ? 'Aktif Bağlantı' : 'Active Connections'}</div>
            <div className="text-xl font-bold text-neutral-900 dark:text-white">
              {formatNumber(activeIntegrations)}
            </div>
          </div>
        </Card>
        <Card className="p-4 flex items-center gap-3">
          <div className="p-2 rounded-lg bg-rose-50 dark:bg-rose-950/30 text-rose-600 dark:text-rose-400">
            <AlertTriangle className="w-5 h-5" />
          </div>
          <div>
            <div className="text-xs text-neutral-500">{isTr ? 'Hatalı Bağlantı' : 'Failed Connections'}</div>
            <div className="text-xl font-bold text-neutral-900 dark:text-white">
              {formatNumber(errorIntegrations)}
            </div>
          </div>
        </Card>
      </div>

      {integrationsQuery.isLoading ? (
        <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
          {[0, 1, 2, 3].map((index) => (
            <div
              key={index}
              className="h-72 rounded-xl border border-neutral-200 dark:border-dark-border bg-neutral-100 dark:bg-dark-card animate-pulse"
            />
          ))}
        </div>
      ) : integrationsError ? (
        <Card className="p-6 text-sm text-rose-700 dark:text-rose-300">
          <div className="flex items-center gap-2">
            <AlertTriangle className="w-4 h-4" />
            {integrationsError}
          </div>
          <Button
            variant="outline"
            size="sm"
            className="mt-4"
            onClick={() => void integrationsQuery.refetch()}
          >
            {isTr ? 'Tekrar Dene' : 'Retry'}
          </Button>
        </Card>
      ) : integrations.length === 0 ? (
        <Card className="p-10 text-center">
          <PlugZap className="w-10 h-10 mx-auto text-neutral-300 dark:text-neutral-600" />
          <h2 className="mt-3 font-bold text-neutral-900 dark:text-white">
            {isTr ? 'Henüz entegrasyon tanımlanmadı' : 'No integrations defined yet'}
          </h2>
          <p className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">
            {isTr ? 'Gerçek Active Directory veya vCenter keşfi için salt-okunur bir bağlantı ekleyin.' : 'Add a read-only connection for real Active Directory or vCenter discovery.'}
          </p>
          <Button
            variant="primary"
            size="sm"
            className="mt-4 gap-2 bg-brand-primary"
            onClick={() => setIsWizardOpen(true)}
            disabled={!canManage}
          >
            <Plus className="w-4 h-4" />
            {isTr ? 'İlk Entegrasyonu Ekle' : 'Add First Integration'}
          </Button>
        </Card>
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
          {integrations.map((integration) => {
            const warnings = integrationWarnings(integration, isTr);
            const isTestBusy = busyAction === `test:${integration.id}`;
            const isDiscoverBusy = busyAction === `discover:${integration.id}`;
            const isToggleBusy = busyAction === `toggle:${integration.id}`;
            const isDeleteBusy = busyAction === `delete:${integration.id}`;

            return (
              <Card key={integration.id} className="p-5 flex flex-col justify-between">
                <div>
                  <div className="flex items-start justify-between gap-3 border-b border-neutral-200 dark:border-dark-border pb-3">
                    <div className="flex items-center gap-3 min-w-0">
                      <div className="p-2.5 rounded-lg bg-brand-primary/10 text-brand-primary dark:text-brand-primary-dark shrink-0">
                        <PlugZap className="w-5 h-5" />
                      </div>
                      <div className="min-w-0">
                        <h3 className="font-bold text-base text-neutral-900 dark:text-white truncate">
                          {integration.name}
                        </h3>
                        <span className="text-xs text-neutral-500">
                          {integration.adapter?.displayName ?? integration.adapterType}
                        </span>
                      </div>
                    </div>

                    <div
                      className={`flex items-center gap-1.5 px-2.5 py-1 rounded-full border text-xs font-semibold shrink-0 ${integrationStatusClass(
                        integration.status
                      )}`}
                    >
                      <StatusIndicator
                        status={
                          integration.status === 'Testing'
                            ? 'warning'
                            : toNodeStatus(integration.status)
                        }
                        size="sm"
                      />
                      <span>
                        {integrationStatusLabel(isTr, integration.status)}
                      </span>
                    </div>
                  </div>

                  {integration.description && (
                    <p className="mt-3 text-xs text-neutral-500 dark:text-neutral-400">
                      {integration.description}
                    </p>
                  )}

                  <dl className="mt-4 grid grid-cols-1 sm:grid-cols-2 gap-x-4 gap-y-2.5 text-xs">
                    <div className="flex justify-between gap-2 py-1 border-b border-neutral-100 dark:border-dark-border">
                      <dt className="text-neutral-500">{isTr ? 'Uç Nokta' : 'Endpoint'}</dt>
                      <dd className="font-mono text-neutral-800 dark:text-neutral-200 truncate text-right">
                        {integration.endpoint || '—'}
                      </dd>
                    </div>
                    <div className="flex justify-between gap-2 py-1 border-b border-neutral-100 dark:border-dark-border">
                      <dt className="text-neutral-500">{isTr ? 'Keşfedilen Varlık' : 'Discovered Entities'}</dt>
                      <dd className="font-bold text-neutral-900 dark:text-white">
                        {formatNumber(integration.discoveredEntitiesCount)}
                      </dd>
                    </div>
                    <div className="flex justify-between gap-2 py-1 border-b border-neutral-100 dark:border-dark-border">
                      <dt className="text-neutral-500">{isTr ? 'Son Senkronizasyon' : 'Last Sync'}</dt>
                      <dd
                        className="text-neutral-700 dark:text-neutral-300 text-right"
                        title={formatDateTime(integration.lastSyncAt)}
                      >
                        {formatRelativeTime(integration.lastSyncAt)}
                      </dd>
                    </div>
                    <div className="flex justify-between gap-2 py-1 border-b border-neutral-100 dark:border-dark-border">
                      <dt className="text-neutral-500">{isTr ? 'Takvim' : 'Schedule'}</dt>
                      <dd className="font-mono text-neutral-700 dark:text-neutral-300 text-right">
                        {integration.scheduleCron}
                      </dd>
                    </div>
                    <div className="flex justify-between gap-2 py-1 border-b border-neutral-100 dark:border-dark-border">
                      <dt className="text-neutral-500">{isTr ? 'Sonraki Keşif' : 'Next Discovery'}</dt>
                      <dd
                        className="text-neutral-700 dark:text-neutral-300 text-right"
                        title={
                          integration.nextScheduledRunAt
                            ? `${formatDateTime(integration.nextScheduledRunAt)} · ${integration.scheduleTimeZone ?? ''}`
                            : undefined
                        }
                      >
                        {!integration.nextScheduledRunAt
                          ? '—'
                          : new Date(integration.nextScheduledRunAt).getTime() <= Date.now()
                            ? isTr ? 'Sırada' : 'Due now'
                            : formatRelativeTime(integration.nextScheduledRunAt)}
                      </dd>
                    </div>
                    <div className="flex justify-between gap-2 py-1 border-b border-neutral-100 dark:border-dark-border">
                      <dt className="text-neutral-500">{isTr ? 'Son Test' : 'Last Test'}</dt>
                      <dd className="text-right">
                        {integration.lastTestSuccess == null ? (
                          <span className="text-neutral-500">{isTr ? 'Test edilmedi' : 'Not tested'}</span>
                        ) : integration.lastTestSuccess ? (
                          <span className="text-emerald-600 dark:text-emerald-400">
                            {isTr ? 'Başarılı' : 'Success'} · {formatRelativeTime(integration.lastTestAt)}
                          </span>
                        ) : (
                          <span className="text-rose-600 dark:text-rose-400">
                            {isTr ? 'Başarısız' : 'Failed'} · {formatRelativeTime(integration.lastTestAt)}
                          </span>
                        )}
                      </dd>
                    </div>
                    <div className="flex justify-between gap-2 py-1 border-b border-neutral-100 dark:border-dark-border">
                      <dt className="text-neutral-500">{isTr ? 'Konfigürasyon' : 'Configuration'}</dt>
                      <dd className="text-neutral-700 dark:text-neutral-300">
                        v{integration.configurationVersion}
                      </dd>
                    </div>
                  </dl>

                  {warnings.length > 0 && (
                    <div className="mt-3 p-3 rounded-lg border border-amber-200 dark:border-amber-900 bg-amber-50/70 dark:bg-amber-950/20 text-[11px] text-amber-800 dark:text-amber-300 space-y-1">
                      {warnings.map((warning) => (
                        <div key={warning} className="flex items-start gap-1.5">
                          <AlertTriangle className="w-3.5 h-3.5 mt-0.5 shrink-0" />
                          <span className="break-words">{warning}</span>
                        </div>
                      ))}
                    </div>
                  )}
                </div>

                <div className="mt-5 pt-4 border-t border-neutral-200 dark:border-dark-border flex flex-wrap items-center justify-between gap-2">
                  <span
                    className={`text-[11px] flex items-center gap-1 ${
                      integration.lastTestSuccess
                        ? 'text-emerald-600 dark:text-emerald-400'
                        : 'text-neutral-500'
                    }`}
                  >
                    <ShieldCheck className="w-3.5 h-3.5" />
                    {integration.lastTestSuccess
                      ? (isTr ? 'Salt-okunur kimlik doğrulandı' : 'Read-only credential verified')
                      : (isTr ? 'Bağlantı testi bekleniyor' : 'Awaiting connection test')}
                  </span>

                  <div className="flex flex-wrap items-center gap-2">
                    <Button
                      variant="ghost"
                      size="sm"
                      className="px-2"
                      onClick={() => void handleToggleEnabled(integration)}
                      disabled={isBusy || !canManage}
                      title={
                        integration.isEnabled
                          ? (isTr ? 'Entegrasyonu devre dışı bırak' : 'Disable integration')
                          : (isTr ? 'Entegrasyonu etkinleştir' : 'Enable integration')
                      }
                    >
                      {isToggleBusy ? (
                        <Loader2 className="w-4 h-4 animate-spin" />
                      ) : integration.isEnabled ? (
                        <Power className="w-4 h-4" />
                      ) : (
                        <PowerOff className="w-4 h-4" />
                      )}
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      className="px-2 text-rose-600 hover:text-rose-700 dark:text-rose-400"
                      onClick={() => void handleDelete(integration)}
                      disabled={isBusy || !canManage}
                      title={isTr ? 'Entegrasyonu sil' : 'Delete integration'}
                    >
                      {isDeleteBusy ? (
                        <Loader2 className="w-4 h-4 animate-spin" />
                      ) : (
                        <Trash2 className="w-4 h-4" />
                      )}
                    </Button>
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => void handleTest(integration)}
                      disabled={isBusy || !canManage}
                    >
                      {isTestBusy && <Loader2 className="w-3.5 h-3.5 animate-spin" />}
                      {isTr ? 'Test Et' : 'Test'}
                    </Button>
                    <Button
                      variant="primary"
                      size="sm"
                      className="bg-brand-primary"
                      onClick={() => void handleDiscover(integration)}
                      disabled={isBusy || !integration.isEnabled || !canManage}
                    >
                      {isDiscoverBusy ? (
                        <Loader2 className="w-3.5 h-3.5 animate-spin" />
                      ) : (
                        <Play className="w-3.5 h-3.5" />
                      )}
                      {isTr ? 'Keşfi Başlat' : 'Start Discovery'}
                    </Button>
                  </div>
                </div>
              </Card>
            );
          })}
        </div>
      )}

      <Card className="p-5">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-neutral-200 dark:border-dark-border pb-3">
          <div className="flex items-center gap-2">
            <Activity className="w-4 h-4 text-brand-primary" />
            <div>
              <h2 className="font-bold text-sm text-neutral-900 dark:text-white">
                {isTr ? 'Keşif İşleri' : 'Discovery Jobs'}
              </h2>
              <p className="text-[11px] text-neutral-500">
                {isTr
                  ? 'Kalıcı PostgreSQL kuyruğundan worker tarafından alınan gerçek keşif çalışmaları'
                  : 'Real discovery runs picked up by the background worker from the durable PostgreSQL queue'}
              </p>
            </div>
          </div>
          <Button
            variant="outline"
            size="sm"
            className="gap-2"
            onClick={() => void jobsQuery.refetch()}
            disabled={jobsQuery.isFetching}
          >
            <RefreshCw className={`w-3.5 h-3.5 ${jobsQuery.isFetching ? 'animate-spin' : ''}`} />
            {isTr ? 'İşleri Yenile' : 'Refresh Jobs'}
          </Button>
        </div>

        {jobsQuery.isLoading ? (
          <div className="mt-4 space-y-2">
            {[0, 1, 2].map((index) => (
              <div
                key={index}
                className="h-10 rounded bg-neutral-100 dark:bg-dark-border animate-pulse"
              />
            ))}
          </div>
        ) : jobsError ? (
          <div className="mt-4 text-xs text-rose-600 dark:text-rose-400 flex items-center gap-2">
            <AlertTriangle className="w-4 h-4" />
            {jobsError}
          </div>
        ) : jobs.length === 0 ? (
          <div className="mt-4 text-xs text-neutral-500 dark:text-neutral-400 flex items-center gap-2">
            <Clock3 className="w-4 h-4" />
            {isTr ? 'Henüz keşif işi yok. Bir entegrasyon için “Keşfi Başlat” seçeneğini kullanın.' : 'No discovery jobs yet. Use "Start Discovery" on an integration.'}
          </div>
        ) : (
          <div className="mt-4 overflow-x-auto">
            <table className="w-full text-left text-xs">
              <thead>
                <tr className="border-b border-neutral-200 dark:border-dark-border uppercase font-semibold text-neutral-400 dark:text-neutral-500">
                  <th className="pb-2 pl-1">{isTr ? 'Entegrasyon' : 'Integration'}</th>
                  <th className="pb-2">{isTr ? 'Durum' : 'Status'}</th>
                  <th className="pb-2">{isTr ? 'Aşama' : 'Phase'}</th>
                  <th className="pb-2 min-w-36">{isTr ? 'İlerleme' : 'Progress'}</th>
                  <th className="pb-2 pr-4 text-right">{isTr ? 'Varlık / İlişki' : 'Entities / Relationships'}</th>
                  <th className="pb-2 pl-4">{isTr ? 'Başlangıç' : 'Started'}</th>
                  <th className="pb-2 text-right pr-1">{isTr ? 'İşlem' : 'Action'}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-neutral-100 dark:divide-dark-border">
                {jobs.map((job) => (
                  <tr key={job.id}>
                    <td className="py-2.5 pl-1">
                      <div className="font-semibold text-neutral-900 dark:text-white">
                        {job.integrationName || '—'}
                      </div>
                      <div className="text-[10px] text-neutral-500">
                        {job.adapterType} · {job.jobType}
                      </div>
                    </td>
                    <td className={`py-2.5 font-semibold ${jobStatusClass(job.status)}`}>
                      {jobStatusLabel(isTr, job.status)}
                    </td>
                    <td className="py-2.5 text-neutral-600 dark:text-neutral-300">
                      <div>{job.currentPhase || '—'}</div>
                      {job.currentMessage && (
                        <div className="text-[10px] text-neutral-500 max-w-48 truncate" title={job.currentMessage}>
                          {job.currentMessage}
                        </div>
                      )}
                    </td>
                    <td className="py-2.5">
                      <div className="h-1.5 w-full rounded-full bg-neutral-100 dark:bg-dark-border overflow-hidden">
                        <div
                          className="h-full rounded-full bg-brand-primary transition-all"
                          style={{
                            width: `${Math.min(100, Math.max(0, job.progressPercent))}%`,
                          }}
                        />
                      </div>
                      <span className="text-[10px] text-neutral-500">
                        {Math.min(100, Math.max(0, job.progressPercent))}%
                      </span>
                    </td>
                    <td className="py-2.5 pr-4 text-right tabular-nums text-neutral-600 dark:text-neutral-300">
                      {formatNumber(job.entitiesDiscovered)} /{' '}
                      {formatNumber(job.relationshipsDiscovered)}
                    </td>
                    <td className="py-2.5 pl-4 whitespace-nowrap text-neutral-600 dark:text-neutral-300">
                      <span title={formatDateTime(job.startedAt)}>
                        {formatRelativeTime(job.startedAt)}
                      </span>
                    </td>
                    <td className="py-2.5 text-right pr-1">
                      {(job.status === 'Queued' || job.status === 'Running') && (
                        <Button
                          variant="ghost"
                          size="sm"
                          className="px-2 text-rose-600 hover:text-rose-700 dark:text-rose-400"
                          onClick={() => void handleCancelJob(job)}
                          disabled={isBusy || !canManage}
                          title={isTr ? 'Keşif işini iptal et' : 'Cancel discovery job'}
                        >
                          {busyAction === `cancel:${job.id}` ? (
                            <Loader2 className="w-4 h-4 animate-spin" />
                          ) : (
                            <X className="w-4 h-4" />
                          )}
                        </Button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {(jobStatusHasActive(jobs)) && (
          <p className="mt-3 text-[11px] text-neutral-500 dark:text-neutral-400">
            {isTr
              ? 'Kuyrukta veya çalışmakta olan işler, DiscoveryBackgroundService etkin olduğunda otomatik olarak ilerler. Worker kapalıysa işler kuyrukta bekler.'
              : 'Queued or running jobs progress automatically while DiscoveryBackgroundService is active. If the worker is stopped, jobs wait in the queue.'}
          </p>
        )}
      </Card>
    </div>
  );
};

function jobStatusHasActive(jobs: DiscoveryJob[]): boolean {
  return jobs.some((job) => job.status === 'Queued' || job.status === 'Running');
}
