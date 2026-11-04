import React, { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Card } from '@/components/ui/Card';
import { Button } from '@/components/ui/Button';
import { StatusIndicator } from '@/components/ui/StatusIndicator';
import {
  AlertTriangle,
  Cpu,
  HardDrive,
  Loader2,
  Radio,
  RefreshCw,
  ShieldCheck,
  Terminal,
  Trash2,
  X,
} from 'lucide-react';
import { DialogShell, MessageBanner } from '@/components/users/DialogShell';
import { api } from '@/lib/api';
import {
  formatDateTime,
  formatNumber,
  formatPercent,
  formatRelativeTime,
  getErrorMessage,
  toNodeStatus,
} from '@/lib/format';
import { useLocale } from '@/lib/i18n';
import { useT } from '@/lib/i18n-runtime';
import { useCanManageIntegrations } from '@/stores/authStore';
import type { Collector } from '@/types';

/** A metric the collector never reported is shown as a dash, not as 0. */
function metricText(value: number | null | undefined, format: (value: number) => string): string {
  return value == null ? '—' : format(value);
}

/* Sample requests of the collector protocol (see CollectorsController). */
const REGISTER_SAMPLE = `POST /api/collectors/register
X-BerMap-Enrollment-Token: <server-token>
Content-Type: application/json

{
  "name": "Collector-DC-Istanbul",
  "hostname": "col-dc01.enterprise.local",
  "ipAddress": "10.20.0.50",
  "version": "1.0.0",
  "supportedAdapters": ["ActiveDirectory", "VMware"]
}

→ 200 { "registered": true, "collectorId": "<collector-id>" }`;

const HEARTBEAT_SAMPLE = `POST /api/collectors/heartbeat
X-BerMap-Enrollment-Token: <server-token>
Content-Type: application/json

{
  "collectorId": "<collector-id>",
  "hostname": "col-dc01.enterprise.local",
  "version": "1.0.0",
  "cpuUsagePercent": 8.4,
  "memoryUsagePercent": 42.0,
  "queuedMessagesCount": 0
}`;

const BATCH_SAMPLE = `POST /api/collectors/submit-batch?final=true
X-BerMap-Enrollment-Token: <server-token>
Content-Type: application/json

{
  "integrationId": "<integration-id>",
  "success": true,
  "nodes": [{
    "externalId": "vm-101", "nodeType": "VirtualMachine",
    "name": "app01", "fqdn": "app01.corp.local",
    "sourceSystem": "VMware", "properties": {}
  }],
  "relationships": [], "errors": [], "warnings": []
}

→ 200 { "jobId": "<job-id>", "jobCompleted": true, ... }

# Large snapshots in several batches:
#   1st batch:   POST /submit-batch            → jobId
#   next ones:   POST /submit-batch?jobId=<job-id>
#   last batch:  POST /submit-batch?jobId=<job-id>&final=true`;

function collectorStatusLabel(status: string, isTr: boolean): string {
  switch (status) {
    case 'Online':
      return isTr ? 'Çevrimiçi' : 'Online';
    case 'Offline':
      return isTr ? 'Çevrimdışı' : 'Offline';
    case 'Error':
      return isTr ? 'Hata' : 'Error';
    default:
      return status;
  }
}

function collectorStatusClass(status: string): string {
  switch (status) {
    case 'Online':
      return 'bg-emerald-50 dark:bg-emerald-950/40 text-emerald-700 dark:text-emerald-400 border-emerald-200 dark:border-emerald-800';
    case 'Error':
      return 'bg-rose-50 dark:bg-rose-950/40 text-rose-700 dark:text-rose-400 border-rose-200 dark:border-rose-800';
    default:
      return 'bg-neutral-100 dark:bg-dark-border text-neutral-600 dark:text-neutral-400 border-neutral-200 dark:border-dark-border';
  }
}

export const CollectorsPage: React.FC = () => {
  const [showInstallGuide, setShowInstallGuide] = useState(false);
  const locale = useLocale();
  const isTr = locale === 'tr';
  const t = useT();
  const queryClient = useQueryClient();
  const canManage = useCanManageIntegrations();
  const [pendingDelete, setPendingDelete] = useState<Collector | null>(null);
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const handleDelete = async () => {
    if (!pendingDelete) return;
    setDeleting(true);
    setDeleteError(null);
    try {
      await api.delete<void>(`/collectors/${pendingDelete.id}`);
      setNotice(isTr ? `${pendingDelete.name} kaldırıldı.` : `${pendingDelete.name} was removed.`);
      setPendingDelete(null);
      await queryClient.invalidateQueries({ queryKey: ['collectors', 'list'] });
    } catch (error) {
      const status = (error as { status?: number } | null)?.status;
      if (status === 404) {
        // Already removed elsewhere; refresh the list.
        setPendingDelete(null);
        await queryClient.invalidateQueries({ queryKey: ['collectors', 'list'] });
      } else {
        setDeleteError(
          status === 403
            ? (isTr ? 'Collector kaldırmak için yetkiniz yok.' : 'You are not allowed to remove collectors.')
            : getErrorMessage(error),
        );
      }
    } finally {
      setDeleting(false);
    }
  };

  const collectorsQuery = useQuery<Collector[]>({
    queryKey: ['collectors', 'list'],
    queryFn: () => api.get<Collector[]>('/collectors'),
    refetchInterval: 15_000,
  });

  const collectors = collectorsQuery.data ?? [];
  const onlineCount = collectors.filter((collector) => collector.status === 'Online').length;
  const offlineCount = collectors.filter((collector) => collector.status !== 'Online').length;
  const reportedQueues = collectors
    .map((collector) => collector.metrics?.queuedMessages)
    .filter((value): value is number => value != null);
  const queuedMessages = reportedQueues.length > 0
    ? reportedQueues.reduce((total, value) => total + value, 0)
    : null;
  const errorMessage = collectorsQuery.isError
    ? getErrorMessage(collectorsQuery.error)
    : null;

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      {pendingDelete && (
        <DialogShell
          title={isTr ? 'Collector kaldırılsın mı?' : 'Remove collector?'}
          subtitle={`${pendingDelete.name} · ${pendingDelete.hostname}`}
          closeLabel={isTr ? 'Kapat' : 'Close'}
          onClose={() => {
            setPendingDelete(null);
            setDeleteError(null);
          }}
          busy={deleting}
          footer={
            <>
              <Button
                variant="outline"
                size="sm"
                disabled={deleting}
                onClick={() => {
                  setPendingDelete(null);
                  setDeleteError(null);
                }}
              >
                {isTr ? 'Vazgeç' : 'Cancel'}
              </Button>
              <Button variant="danger" size="sm" disabled={deleting} onClick={() => void handleDelete()}>
                {deleting ? <Loader2 className="w-4 h-4 animate-spin" /> : <Trash2 className="w-4 h-4" />}
                {isTr ? 'Kaldır' : 'Remove'}
              </Button>
            </>
          }
        >
          {deleteError && <MessageBanner tone="error">{deleteError}</MessageBanner>}
          <p className="text-sm text-neutral-700 dark:text-neutral-300">
            {isTr
              ? 'Collector kaydı silinir ve listeden kalkar. Gönderdiği veriler, beslediği entegrasyonlarda kalır. Collector hâlâ çalışıyorsa heartbeat istekleri reddedilir; yeniden kayıt olması gerekir.'
              : 'The collector record is deleted and leaves the list. Data it sent stays with the integrations it fed. If the collector is still running, its heartbeats are rejected until it registers again.'}
          </p>
        </DialogShell>
      )}

      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
              {isTr ? 'Bağımsız Collector Yönetimi' : 'Independent Collector Management'}
            </h1>
            <span
              className={`text-xs px-2.5 py-0.5 rounded-full border font-bold ${
                onlineCount > 0
                  ? 'bg-emerald-50 dark:bg-emerald-950/40 text-emerald-700 dark:text-emerald-400 border-emerald-200 dark:border-emerald-800'
                  : 'bg-neutral-100 dark:bg-dark-border text-neutral-600 dark:text-neutral-400 border-neutral-200 dark:border-dark-border'
              }`}
            >
              {formatNumber(onlineCount)} {isTr ? 'Çevrimiçi' : 'Online'}
            </span>
          </div>
          <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1">
            {isTr
              ? 'Uzak datacenter ve izole ağ segmentlerinde çalışan, yalnızca merkeze outbound bağlantı kuran veri toplayıcılar'
              : 'Data collection agents running in remote datacenters and isolated segments, opening outbound-only connections to central server'}
          </p>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            className="gap-2"
            onClick={() => void collectorsQuery.refetch()}
            disabled={collectorsQuery.isFetching}
          >
            <RefreshCw
              className={`w-4 h-4 ${collectorsQuery.isFetching ? 'animate-spin' : ''}`}
            />
            {isTr ? 'Yenile' : 'Refresh'}
          </Button>
          <Button
            variant="primary"
            size="sm"
            onClick={() => setShowInstallGuide((current) => !current)}
            className="gap-2 bg-brand-primary"
          >
            <Terminal className="w-4 h-4" />
            {isTr ? 'Kayıt Bilgisi' : 'Enrollment Info'}
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

      {errorMessage && (
        <div
          role="alert"
          className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-800 dark:text-rose-300 flex items-start gap-2"
        >
          <AlertTriangle className="w-4 h-4 mt-0.5 shrink-0" />
          <span className="flex-1">{errorMessage}</span>
          <Button variant="outline" size="sm" onClick={() => void collectorsQuery.refetch()}>
            {isTr ? 'Tekrar Dene' : 'Retry'}
          </Button>
        </div>
      )}

      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Toplam Collector' : 'Total Collectors'}</div>
          <div className="mt-1 text-2xl font-bold text-neutral-900 dark:text-white">
            {formatNumber(collectors.length)}
          </div>
          <div className="text-[11px] text-neutral-500 mt-1">{isTr ? 'Kayıtlı ajan' : 'Registered agents'}</div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Çevrimiçi' : 'Online'}</div>
          <div className="mt-1 text-2xl font-bold text-emerald-600 dark:text-emerald-400">
            {formatNumber(onlineCount)}
          </div>
          <div className="text-[11px] text-neutral-500 mt-1">{isTr ? 'Son 90 saniye içinde heartbeat' : 'Heartbeat within last 90s'}</div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Çevrimdışı / Hatalı' : 'Offline / Error'}</div>
          <div className="mt-1 text-2xl font-bold text-amber-600 dark:text-amber-400">
            {formatNumber(offlineCount)}
          </div>
          <div className="text-[11px] text-neutral-500 mt-1">{isTr ? 'Heartbeat bekleniyor' : 'Awaiting heartbeat'}</div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Bekleyen Kuyruk' : 'Pending Queue'}</div>
          <div className="mt-1 text-2xl font-bold text-neutral-900 dark:text-white">
            {metricText(queuedMessages, formatNumber)}
          </div>
          <div className="text-[11px] text-neutral-500 mt-1">{isTr ? 'Collector başına raporlanan mesaj' : 'Reported messages per collector'}</div>
        </Card>
      </div>

      {showInstallGuide && (
        <Card className="p-5 border-brand-primary/40 bg-brand-primary/5 dark:bg-brand-primary-dark/10 space-y-4">
          <div className="flex items-center justify-between gap-3">
            <h3 className="font-bold text-sm text-neutral-900 dark:text-white flex items-center gap-2">
              <Terminal className="w-4 h-4 text-brand-primary" />
              {isTr ? 'Collector Enrollment ve Heartbeat Sözleşmesi' : 'Collector Enrollment & Heartbeat Protocol'}
            </h3>
            <button
              type="button"
              onClick={() => setShowInstallGuide(false)}
              className="text-neutral-400 hover:text-neutral-600 dark:hover:text-neutral-200"
              aria-label={isTr ? 'Kurulum bilgisini kapat' : 'Close enrollment info'}
            >
              <X className="w-4 h-4" />
            </button>
          </div>

          <div className="p-3 rounded-lg border border-amber-200 dark:border-amber-900 bg-amber-50/70 dark:bg-amber-950/20 text-xs text-amber-800 dark:text-amber-300 flex items-start gap-2">
            <ShieldCheck className="w-4 h-4 mt-0.5 shrink-0" />
            <div>
              {isTr ? (
                <>
                  Collector kaydı varsayılan olarak kapalıdır. Sunucuda
                  <code className="mx-1 px-1 rounded bg-white/60 dark:bg-dark-card font-mono break-all">
                    BerMap:Collectors:EnrollmentToken
                  </code>
                  yapılandırılmadan collector uç noktaları 503 döner. Üç çağrının hepsi (kayıt, heartbeat, batch)
                  <code className="mx-1 px-1 rounded bg-white/60 dark:bg-dark-card font-mono break-all">
                    X-BerMap-Enrollment-Token
                  </code>
                  başlığını taşımalıdır; yoksa 401 döner.
                </>
              ) : (
                <>
                  Collector enrollment is disabled by default. Unless
                  <code className="mx-1 px-1 rounded bg-white/60 dark:bg-dark-card font-mono break-all">
                    BerMap:Collectors:EnrollmentToken
                  </code>
                  is configured on the server, the collector endpoints return 503. All three calls (register, heartbeat, batch) must send the
                  <code className="mx-1 px-1 rounded bg-white/60 dark:bg-dark-card font-mono break-all">
                    X-BerMap-Enrollment-Token
                  </code>
                  header, otherwise they return 401.
                </>
              )}
            </div>
          </div>

          <div className="grid grid-cols-1 lg:grid-cols-3 gap-4">
            <div className="min-w-0">
              <p className="text-xs font-semibold text-neutral-700 dark:text-neutral-200 mb-2">
                1. {isTr ? 'Collector kaydı' : 'Collector registration'}
              </p>
              <pre className="p-3 rounded-lg bg-neutral-900 text-neutral-200 font-mono text-[11px] overflow-x-auto whitespace-pre-wrap break-words">
                {REGISTER_SAMPLE}
              </pre>
            </div>
            <div className="min-w-0">
              <p className="text-xs font-semibold text-neutral-700 dark:text-neutral-200 mb-2">
                2. {isTr ? 'Heartbeat (her 30 sn)' : 'Heartbeat (every 30 s)'}
              </p>
              <pre className="p-3 rounded-lg bg-neutral-900 text-neutral-200 font-mono text-[11px] overflow-x-auto whitespace-pre-wrap break-words">
                {HEARTBEAT_SAMPLE}
              </pre>
              <p className="mt-2 text-[11px] text-neutral-500 dark:text-neutral-400">
                {isTr
                  ? '90 saniye heartbeat gelmeyen collector çevrimdışı görünür.'
                  : 'A collector without a heartbeat for 90 seconds shows as offline.'}
              </p>
            </div>
            <div className="min-w-0">
              <p className="text-xs font-semibold text-neutral-700 dark:text-neutral-200 mb-2">
                3. {isTr ? 'Keşif batch gönderimi' : 'Discovery batch submission'}
              </p>
              <pre className="p-3 rounded-lg bg-neutral-900 text-neutral-200 font-mono text-[11px] overflow-x-auto whitespace-pre-wrap break-words">
                {BATCH_SAMPLE}
              </pre>
              <p className="mt-2 text-[11px] text-neutral-500 dark:text-neutral-400">
                {isTr
                  ? 'integrationId, Entegrasyonlar sayfasında "Veri bir collector üzerinden gelir" seçilerek oluşturulan entegrasyonun kimliğidir. final=true işi tamamlar ve tam anlık görüntüde artık bildirilmeyen varlıkları eski olarak işaretler.'
                  : 'integrationId is the id of an integration created with "Data arrives from a collector" on the Integrations page. final=true completes the job and, for a complete snapshot, marks entities no longer reported as stale.'}
              </p>
            </div>
          </div>

          <p className="text-[11px] text-neutral-500 dark:text-neutral-400">
            {isTr
              ? "Collector, merkezi BerMap sunucusuna doğru HTTPS bağlantısı kurar; sunucu collector'a doğrudan bağlanmaz. Ağ erişilemediğinde batch'ler yerel kuyrukta tutulur."
              : 'Collector establishes outbound HTTPS connections to the central BerMap server; server never connects in-bound to collectors. Local queue buffers batches when offline.'}
          </p>
        </Card>
      )}

      {collectorsQuery.isLoading ? (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          {[0, 1, 2].map((index) => (
            <div
              key={index}
              className="h-80 rounded-xl border border-neutral-200 dark:border-dark-border bg-neutral-100 dark:bg-dark-card animate-pulse"
            />
          ))}
        </div>
      ) : collectors.length === 0 ? (
        <Card className="p-10 text-center">
          <Radio className="w-10 h-10 mx-auto text-neutral-300 dark:text-neutral-600" />
          <h2 className="mt-3 font-bold text-neutral-900 dark:text-white">
            {isTr ? 'Kayıtlı collector bulunmuyor' : 'No registered collectors found'}
          </h2>
          <p className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">
            {isTr
              ? 'Sunucuda enrollment token yapılandırıldıktan sonra collector kayıt uç noktasına bağlanabilir.'
              : 'Once enrollment token is configured on the server, collectors can register.'}
          </p>
          <Button
            variant="outline"
            size="sm"
            className="mt-4 gap-2"
            onClick={() => setShowInstallGuide(true)}
          >
            <Terminal className="w-4 h-4" />
            {isTr ? 'Kayıt Sözleşmesini Gör' : 'View Enrollment Contract'}
          </Button>
        </Card>
      ) : (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          {collectors.map((collector) => (
            <Card key={collector.id} className="p-5 flex flex-col justify-between">
              <div>
                <div className="flex items-start justify-between gap-3 border-b border-neutral-200 dark:border-dark-border pb-3">
                  <div className="flex items-center gap-3 min-w-0">
                    <div className="p-2.5 rounded-lg bg-brand-primary/10 text-brand-primary dark:text-brand-primary-dark shrink-0">
                      <Radio className="w-5 h-5" />
                    </div>
                    <div className="min-w-0">
                      <h3 className="font-bold text-base text-neutral-900 dark:text-white truncate">
                        {collector.name}
                      </h3>
                      <span className="text-xs font-mono text-neutral-500 truncate block">
                        {collector.hostname}
                      </span>
                    </div>
                  </div>

                  <div
                    className={`flex items-center gap-1.5 px-2.5 py-1 rounded-full border text-xs font-semibold shrink-0 ${collectorStatusClass(
                      collector.status
                    )}`}
                  >
                    <StatusIndicator status={toNodeStatus(collector.status)} size="sm" />
                    <span>{collectorStatusLabel(collector.status, isTr)}</span>
                  </div>
                </div>

                <div className="mt-4 space-y-2 text-xs">
                  <div className="flex justify-between gap-3 py-1 border-b border-neutral-100 dark:border-dark-border">
                    <span className="text-neutral-500">{isTr ? 'IP Adresi' : 'IP Address'}</span>
                    <span className="font-mono text-neutral-800 dark:text-neutral-200">
                      {collector.ipAddress || '—'}
                    </span>
                  </div>
                  <div className="flex justify-between gap-3 py-1 border-b border-neutral-100 dark:border-dark-border">
                    <span className="text-neutral-500">{isTr ? 'Son Heartbeat' : 'Last Heartbeat'}</span>
                    <span
                      className="text-neutral-800 dark:text-neutral-200 text-right"
                      title={formatDateTime(collector.lastHeartbeatAt)}
                    >
                      {formatRelativeTime(collector.lastHeartbeatAt)}
                    </span>
                  </div>
                  <div className="flex justify-between gap-3 py-1 border-b border-neutral-100 dark:border-dark-border">
                    <span className="text-neutral-500">{isTr ? 'Sürüm' : 'Version'}</span>
                    <span className="font-mono text-neutral-800 dark:text-neutral-200">
                      v{collector.version || '—'}
                    </span>
                  </div>
                  <div className="flex justify-between gap-3 py-1 border-b border-neutral-100 dark:border-dark-border">
                    <span className="text-neutral-500">Collector ID</span>
                    <span
                      className="font-mono text-[10px] text-neutral-500 truncate max-w-36"
                      title={collector.id}
                    >
                      {collector.id}
                    </span>
                  </div>
                </div>

                <div className="mt-4 pt-3 border-t border-neutral-200 dark:border-dark-border">
                  <span className="text-[11px] uppercase font-bold text-neutral-400">
                    {isTr ? 'Son Raporlanan Metrikler' : 'Last Reported Metrics'}
                  </span>
                  <div className="grid grid-cols-3 gap-2 mt-2 text-center">
                    <div className="p-2 rounded-lg bg-neutral-100 dark:bg-dark-border">
                      <Cpu className="w-3.5 h-3.5 mx-auto text-neutral-500" />
                      <span className="text-[10px] text-neutral-500 block mt-1">CPU</span>
                      <span className="text-xs font-bold text-neutral-800 dark:text-neutral-200">
                        {metricText(collector.metrics?.cpuUsagePercent, formatPercent)}
                      </span>
                    </div>
                    <div className="p-2 rounded-lg bg-neutral-100 dark:bg-dark-border">
                      <HardDrive className="w-3.5 h-3.5 mx-auto text-neutral-500" />
                      <span className="text-[10px] text-neutral-500 block mt-1">{isTr ? 'Bellek' : 'RAM'}</span>
                      <span className="text-xs font-bold text-neutral-800 dark:text-neutral-200">
                        {metricText(collector.metrics?.memoryUsagePercent, formatPercent)}
                      </span>
                    </div>
                    <div className="p-2 rounded-lg bg-neutral-100 dark:bg-dark-border">
                      <Radio className="w-3.5 h-3.5 mx-auto text-neutral-500" />
                      <span className="text-[10px] text-neutral-500 block mt-1">{isTr ? 'Kuyruk' : 'Queue'}</span>
                      <span className="text-xs font-bold text-neutral-800 dark:text-neutral-200">
                        {metricText(collector.metrics?.queuedMessages, formatNumber)}
                      </span>
                    </div>
                  </div>
                </div>

                {collector.metrics == null && (
                  <p className="mt-2 text-[10px] text-neutral-500">
                    {isTr ? 'Collector henüz heartbeat göndermedi.' : 'The collector has not sent a heartbeat yet.'}
                  </p>
                )}

                <div className="mt-3 flex flex-wrap items-center gap-1">
                  {collector.supportedAdapters.length > 0 && (
                    <span className="text-[10px] text-neutral-500 mr-1">
                      {isTr ? 'Bildirilen adaptörler:' : 'Reported adapters:'}
                    </span>
                  )}
                  {collector.supportedAdapters.length > 0 ? (
                    collector.supportedAdapters.map((adapter) => (
                      <span
                        key={adapter}
                        className="text-[10px] px-2 py-0.5 rounded bg-neutral-100 dark:bg-dark-border text-neutral-600 dark:text-neutral-400 font-medium"
                      >
                        {adapter}
                      </span>
                    ))
                  ) : (
                    <span className="text-[10px] text-neutral-500">{isTr ? 'Adaptör bildirilmedi' : 'No adapters reported'}</span>
                  )}
                </div>
              </div>

              <div className="mt-5 pt-3 border-t border-neutral-200 dark:border-dark-border flex flex-wrap items-center justify-between gap-2">
                <span className="text-[11px] text-neutral-500 flex items-center gap-1">
                  <ShieldCheck className="w-3.5 h-3.5 text-emerald-600" />
                  {collector.status === 'Online'
                    ? (isTr ? 'Heartbeat aktif' : 'Heartbeat active')
                    : (isTr ? 'Heartbeat yok' : 'No heartbeat')}
                </span>
                <Button
                  variant="ghost"
                  size="sm"
                  className="gap-1.5 text-rose-600 hover:text-rose-700 dark:text-rose-400"
                  onClick={() => {
                    setDeleteError(null);
                    setPendingDelete(collector);
                  }}
                  disabled={!canManage}
                  title={canManage ? (isTr ? 'Collector kaydını kaldır' : 'Remove the collector registration') : t('noPermission')}
                >
                  <Trash2 className="w-3.5 h-3.5" />
                  {isTr ? 'Kaldır' : 'Remove'}
                </Button>
              </div>
            </Card>
          ))}
        </div>
      )}

      {collectorsQuery.isFetching && !collectorsQuery.isLoading && (
        <div className="text-[11px] text-neutral-500 flex items-center gap-1.5">
          <Loader2 className="w-3.5 h-3.5 animate-spin" />
          {isTr ? 'Collector durumları yenileniyor...' : 'Refreshing collector status...'}
        </div>
      )}
    </div>
  );
};