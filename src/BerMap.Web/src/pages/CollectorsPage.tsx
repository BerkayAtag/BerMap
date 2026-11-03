import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
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
  X,
} from 'lucide-react';
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
import type { Collector } from '@/types';

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

  const collectorsQuery = useQuery<Collector[]>({
    queryKey: ['collectors', 'list'],
    queryFn: () => api.get<Collector[]>('/collectors'),
    refetchInterval: 15_000,
  });

  const collectors = collectorsQuery.data ?? [];
  const onlineCount = collectors.filter((collector) => collector.status === 'Online').length;
  const offlineCount = collectors.filter((collector) => collector.status !== 'Online').length;
  const queuedMessages = collectors.reduce(
    (total, collector) => total + (collector.metrics?.queuedMessages ?? 0),
    0
  );
  const errorMessage = collectorsQuery.isError
    ? getErrorMessage(collectorsQuery.error)
    : null;

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
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
            {formatNumber(queuedMessages)}
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
                  <code className="mx-1 px-1 rounded bg-white/60 dark:bg-dark-card font-mono">
                    BerMap:Collectors:EnrollmentToken
                  </code>
                  yapılandırılmadan kayıt uç noktası 503 döner. Kayıt isteği
                  <code className="mx-1 px-1 rounded bg-white/60 dark:bg-dark-card font-mono">
                    X-BerMap-Enrollment-Token
                  </code>
                  başlığını taşımalıdır.
                </>
              ) : (
                <>
                  Collector registration is disabled by default. Unless
                  <code className="mx-1 px-1 rounded bg-white/60 dark:bg-dark-card font-mono">
                    BerMap:Collectors:EnrollmentToken
                  </code>
                  is configured on the server, registration returns 503. The registration request must include
                  <code className="mx-1 px-1 rounded bg-white/60 dark:bg-dark-card font-mono">
                    X-BerMap-Enrollment-Token
                  </code>.
                </>
              )}
            </div>
          </div>

          <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
            <div>
              <p className="text-xs font-semibold text-neutral-700 dark:text-neutral-200 mb-2">
                1. {isTr ? 'Collector kaydı' : 'Collector registration'}
              </p>
              <pre className="p-3 rounded-lg bg-neutral-900 text-neutral-200 font-mono text-[11px] overflow-x-auto whitespace-pre-wrap">
{`POST /api/collectors/register
X-BerMap-Enrollment-Token: <server-token>
Content-Type: application/json

{
  "name": "Collector-DC-Istanbul",
  "hostname": "col-dc01.enterprise.local",
  "ipAddress": "10.20.0.50",
  "version": "1.0.0",
  "supportedAdaptersJson": "[]"
}`}
              </pre>
            </div>
            <div>
              <p className="text-xs font-semibold text-neutral-700 dark:text-neutral-200 mb-2">
                2. {isTr ? 'Heartbeat ve batch gönderimi' : 'Heartbeat & batch submission'}
              </p>
              <pre className="p-3 rounded-lg bg-neutral-900 text-neutral-200 font-mono text-[11px] overflow-x-auto whitespace-pre-wrap">
{`POST /api/collectors/heartbeat
{ "collectorId": "<register-response>",
  "hostname": "...", "version": "1.0.0",
  "cpuUsagePercent": 8.4,
  "memoryUsagePercent": 42.0,
  "queuedMessagesCount": 0 }

POST /api/collectors/submit-batch
{ "integrationId": "<integration-id>", ... }`}
              </pre>
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
                        {formatPercent(collector.metrics?.cpuUsagePercent)}
                      </span>
                    </div>
                    <div className="p-2 rounded-lg bg-neutral-100 dark:bg-dark-border">
                      <HardDrive className="w-3.5 h-3.5 mx-auto text-neutral-500" />
                      <span className="text-[10px] text-neutral-500 block mt-1">{isTr ? 'Bellek' : 'RAM'}</span>
                      <span className="text-xs font-bold text-neutral-800 dark:text-neutral-200">
                        {formatPercent(collector.metrics?.memoryUsagePercent)}
                      </span>
                    </div>
                    <div className="p-2 rounded-lg bg-neutral-100 dark:bg-dark-border">
                      <Radio className="w-3.5 h-3.5 mx-auto text-neutral-500" />
                      <span className="text-[10px] text-neutral-500 block mt-1">{isTr ? 'Kuyruk' : 'Queue'}</span>
                      <span className="text-xs font-bold text-neutral-800 dark:text-neutral-200">
                        {formatNumber(collector.metrics?.queuedMessages)}
                      </span>
                    </div>
                  </div>
                </div>

                <div className="mt-3 flex flex-wrap gap-1">
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

              <div className="mt-5 pt-3 border-t border-neutral-200 dark:border-dark-border flex items-center justify-between gap-2">
                <span className="text-[11px] text-neutral-500 flex items-center gap-1">
                  <ShieldCheck className="w-3.5 h-3.5 text-emerald-600" />
                  {isTr ? 'Kayıtlı collector' : 'Registered collector'}
                </span>
                <span className="text-[10px] text-neutral-400 font-mono">
                  {collector.status === 'Online'
                    ? (isTr ? 'heartbeat aktif' : 'heartbeat active')
                    : (isTr ? 'heartbeat yok' : 'no heartbeat')}
                </span>
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