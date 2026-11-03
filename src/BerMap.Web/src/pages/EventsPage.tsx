import React from 'react';
import { useQuery } from '@tanstack/react-query';
import { useLocale } from '@/lib/i18n';
import { api } from '@/lib/api';
import { formatDateTime } from '@/lib/format';
import { Card } from '@/components/ui/Card';
import { Loader2, AlertTriangle } from 'lucide-react';

interface DashboardSummary {
  totalEntities: number;
  healthyEntities: number;
  warningEntities: number;
  criticalEntities: number;
  unknownEntities: number;
  staleEntities: number;
  activeIntegrations: number;
  totalIntegrations: number;
  pendingReconciliationReviews: number;
  recentChanges: Array<{
    id: string;
    entityName: string;
    entityType: string;
    changeType: string;
    source: string;
    status: string;
    timestamp: string;
  }>;
}

export const EventsPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';

  const { data, isLoading, isError, refetch } = useQuery<DashboardSummary>({
    queryKey: ['events', 'dashboard'],
    queryFn: () => api.get<DashboardSummary>('/dashboard/summary'),
    refetchInterval: 30_000,
  });

  const statusColor = (status: string) => {
    const s = status.toLowerCase();
    if (s === 'healthy' || s === 'normal') return 'text-emerald-600 dark:text-emerald-400';
    if (s === 'warning') return 'text-amber-600 dark:text-amber-400';
    if (s === 'critical') return 'text-rose-600 dark:text-rose-400';
    return 'text-neutral-500';
  };

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
            {isTr ? 'Olaylar & Uyarılar' : 'Events & Alerts'}
          </h1>
          <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1">
            {isTr
              ? 'Son keşif değişiklikleri ve durum uyarıları (gerçek envanter verisi)'
              : 'Recent discovery changes and status alerts (real inventory data)'}
          </p>
        </div>
        <button
          type="button"
          onClick={() => void refetch()}
          className="inline-flex items-center justify-center rounded-md font-medium transition-colors duration-150 border border-neutral-300 text-neutral-700 hover:bg-neutral-100 dark:border-dark-border dark:text-dark-text dark:hover:bg-dark-card h-8 px-3 text-sm gap-2"
        >
          {isLoading ? <Loader2 className="w-4 h-4 animate-spin" /> : null}
          {isTr ? 'Yenile' : 'Refresh'}
        </button>
      </div>

      {isError && (
        <div className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300">
          {isTr ? 'Olay verisi yüklenemedi.' : 'Failed to load event data.'}
        </div>
      )}

      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Toplam' : 'Total'}</div>
          <div className="text-2xl font-bold mt-1">{data?.totalEntities ?? 0}</div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Sağlıklı' : 'Healthy'}</div>
          <div className="text-2xl font-bold mt-1 text-emerald-600">{data?.healthyEntities ?? 0}</div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Uyarı' : 'Warning'}</div>
          <div className="text-2xl font-bold mt-1 text-amber-600">{data?.warningEntities ?? 0}</div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Kritik' : 'Critical'}</div>
          <div className="text-2xl font-bold mt-1 text-rose-600">{data?.criticalEntities ?? 0}</div>
        </Card>
      </div>

      <Card className="p-4">
        <h2 className="text-sm font-semibold mb-3 flex items-center gap-2">
          <AlertTriangle className="w-4 h-4 text-amber-500" />
          {isTr ? 'Son Altyapı Değişiklikleri' : 'Recent Infrastructure Changes'}
        </h2>
        {isLoading && !data && (
          <div className="flex items-center gap-2 text-sm text-neutral-500">
            <Loader2 className="w-4 h-4 animate-spin" />
            {isTr ? 'Yükleniyor...' : 'Loading...'}
          </div>
        )}
        {!isLoading && (data?.recentChanges?.length ?? 0) === 0 && (
          <p className="text-sm text-neutral-500">
            {isTr ? 'Kayıt bulunamadı.' : 'No records found.'}
          </p>
        )}
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-xs text-neutral-500 border-b border-neutral-200 dark:border-dark-border">
                <th className="py-2 pr-3">{isTr ? 'Varlık' : 'Entity'}</th>
                <th className="py-2 pr-3">{isTr ? 'Tür' : 'Type'}</th>
                <th className="py-2 pr-3">{isTr ? 'Olay' : 'Event'}</th>
                <th className="py-2 pr-3">{isTr ? 'Kaynak' : 'Source'}</th>
                <th className="py-2 pr-3">{isTr ? 'Durum' : 'Status'}</th>
                <th className="py-2 pr-3">{isTr ? 'Zaman' : 'Time'}</th>
              </tr>
            </thead>
            <tbody>
              {(data?.recentChanges ?? []).map((c) => (
                <tr key={c.id} className="border-b border-neutral-100 dark:border-dark-border/40">
                  <td className="py-2 pr-3 font-medium">{c.entityName}</td>
                  <td className="py-2 pr-3">{c.entityType}</td>
                  <td className="py-2 pr-3">{c.changeType}</td>
                  <td className="py-2 pr-3">{c.source}</td>
                  <td className={`py-2 pr-3 ${statusColor(c.status)}`}>{c.status}</td>
                  <td className="py-2 pr-3 text-xs text-neutral-500">
                    {formatDateTime(c.timestamp)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      {data && data.pendingReconciliationReviews > 0 && (
        <div className="p-3 rounded-lg border border-amber-200 dark:border-amber-900 bg-amber-50 dark:bg-amber-950/30 text-sm text-amber-800 dark:text-amber-300">
          {isTr
            ? `${data.pendingReconciliationReviews} bekleyen uzlaştırma incelemesi var. Envanter sayfasından çözün.`
            : `${data.pendingReconciliationReviews} pending reconciliation review(s). Resolve them on the Inventory page.`}
        </div>
      )}
    </div>
  );
};
