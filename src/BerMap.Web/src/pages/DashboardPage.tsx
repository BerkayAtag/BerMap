import React from 'react';
import {
  Server,
  CheckCircle2,
  PlugZap,
  GitFork,
  RotateCw,
  Cpu,
  AlertTriangle,
  GitMerge,
  ClipboardCheck,
} from 'lucide-react';
import { Link } from 'react-router-dom';
import { KPICard } from '@/components/dashboard/KPICard';
import { SystemHealthChart } from '@/components/dashboard/SystemHealthChart';
import { RecentChangesTable } from '@/components/dashboard/RecentChangesTable';
import { Button } from '@/components/ui/Button';
import { ButtonLink } from '@/components/ui/ButtonLink';
import { useDashboard } from '@/hooks/useDashboard';
import { useLocale } from '@/lib/i18n';
import { formatNumber, formatPercent, getErrorMessage } from '@/lib/format';

export const DashboardPage: React.FC = () => {
  const { data, isLoading, isError, error, refetch, isFetching } = useDashboard();
  const locale = useLocale();
  const isTr = locale === 'tr';

  const total = data?.totalEntities ?? 0;
  const healthy = data?.healthyEntities ?? 0;
  const healthyPercent = total > 0 ? (healthy / total) * 100 : 0;
  const activeIntegrations = data?.activeIntegrations ?? 0;
  const totalIntegrations = data?.totalIntegrations ?? 0;
  const pendingReviews = data?.pendingReconciliationReviews ?? 0;
  // Without data (first load failed) the cards show placeholders, never real-looking zeros.
  const noData = !data;

  const labels = {
    title: isTr ? 'Altyapı İstihbarat Paneli' : 'Infrastructure Intelligence Dashboard',
    subtitle: isTr ? 'Kurumsal IT altyapısı, bileşen bağımlılıkları ve simülasyon özeti' : 'Enterprise IT infrastructure, component dependencies and simulation summary',
    refresh: isTr ? 'Yenile' : 'Refresh',
    errorFetch: isTr ? 'Dashboard verisi alınamadı.' : 'Failed to fetch dashboard data.',
    kpiTotal: isTr ? 'Toplam Keşfedilen Varlık' : 'Total Discovered Entities',
    kpiTotalChange: isTr ? 'son 24 saatte' : 'in last 24h',
    kpiTotalSub: isTr ? 'AD, vCenter ve collector kaynakları' : 'AD, vCenter and collector sources',
    kpiHealthy: isTr ? 'Sağlıklı Bileşenler' : 'Healthy Components',
    kpiHealthyChange: isTr ? 'sağlıklı' : 'healthy',
    kpiHealthySub: isTr ? 'Normal çalışan servisler' : 'Normal running services',
    kpiActive: isTr ? 'Aktif Entegrasyonlar' : 'Active Integrations',
    kpiActiveOk: isTr ? 'Keşif kaynakları hazır' : 'Discovery sources ready',
    kpiActiveNone: isTr ? 'Henüz entegrasyon eklenmedi' : 'No integrations added yet',
    kpiActiveDown: isTr ? 'Hiçbiri aktif değil — kontrol edin' : 'None active — check them',
    kpiActiveSub: isTr ? 'Salt-okunur bağlantılar' : 'Read-only connections',
    kpiCritical: isTr ? 'Kritik Bağımlılık Yolları' : 'Critical Dependency Paths',
    kpiCriticalChange: isTr ? 'kritik bileşen' : 'critical components',
    kpiCriticalSub: isTr ? 'Kritik düğümlere bağlı ilişkiler' : 'Relationships touching critical nodes',
    reviewAlert: isTr
      ? 'uzlaştırma incelemesi bekliyor (olası yinelenen kayıtlar).'
      : 'reconciliation reviews are pending (possible duplicate records).',
    reviewLink: isTr ? 'İncele' : 'Review',
    staleAlert: isTr ? 'varlık artık hiçbir keşif kaynağından görülmüyor. Envanter sayfasından inceleyebilirsiniz.' : 'entities are no longer seen by any discovery source. Check Inventory page.',
    simTitle: isTr ? 'BerSim — Simülasyon' : 'BerSim — Simulation',
    simSub: isTr
      ? 'Üretim ortamına dokunmadan host, datastore, ağ veya iş yükü kesintilerinin etkisini deterministik olarak hesaplayın ve değişiklik risk raporu alın.'
      : 'Compute the impact of host, datastore, network or workload outages deterministically without touching production, and get a change risk report.',
    simButton: isTr ? 'Simülasyona Git' : 'Open Simulation',
  };

  const errorMessage = isError ? getErrorMessage(error) || labels.errorFetch : null;
  const placeholder = '—';

  const activeChange = noData
    ? undefined
    : totalIntegrations === 0
      ? labels.kpiActiveNone
      : activeIntegrations === 0
        ? labels.kpiActiveDown
        : labels.kpiActiveOk;

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
            {labels.title}
          </h1>
          <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1">
            {labels.subtitle}
          </p>
        </div>

        <div className="flex items-center gap-3">
          <Button
            variant="outline"
            size="sm"
            className="gap-2"
            onClick={() => void refetch()}
            disabled={isFetching}
          >
            <RotateCw className={`w-4 h-4 text-neutral-500 ${isFetching ? 'animate-spin' : ''}`} />
            {labels.refresh}
          </Button>
        </div>
      </div>

      {errorMessage && (
        <div
          role="alert"
          className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300 flex items-center gap-2"
        >
          <AlertTriangle className="w-4 h-4 shrink-0" />
          <span className="flex-1">
            {labels.errorFetch} {errorMessage !== labels.errorFetch ? errorMessage : ''}
            {data ? (isTr ? ' Son alınan veriler gösteriliyor.' : ' Showing the last data received.') : ''}
          </span>
        </div>
      )}

      {isLoading ? (
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
          {[0, 1, 2, 3].map((index) => (
            <div
              key={index}
              className="h-32 rounded-xl border border-neutral-200 dark:border-dark-border bg-neutral-100 dark:bg-dark-card animate-pulse"
            />
          ))}
        </div>
      ) : (
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
          <KPICard
            title={labels.kpiTotal}
            value={noData ? placeholder : formatNumber(total)}
            change={noData ? undefined : `${formatNumber(data.recentDiscoveries)} ${labels.kpiTotalChange}`}
            trend="neutral"
            icon={Server}
            accent="primary"
            subtitle={labels.kpiTotalSub}
          />
          <KPICard
            title={labels.kpiHealthy}
            value={noData ? placeholder : formatNumber(healthy)}
            change={noData ? undefined : `${formatPercent(healthyPercent)} ${labels.kpiHealthyChange}`}
            trend="neutral"
            icon={CheckCircle2}
            accent="healthy"
            subtitle={labels.kpiHealthySub}
          />
          <KPICard
            title={labels.kpiActive}
            value={noData ? placeholder : `${formatNumber(activeIntegrations)} / ${formatNumber(totalIntegrations)}`}
            change={activeChange}
            trend={!noData && totalIntegrations > 0 && activeIntegrations === 0 ? 'down' : 'neutral'}
            icon={PlugZap}
            accent="info"
            subtitle={labels.kpiActiveSub}
          />
          <KPICard
            title={labels.kpiCritical}
            value={noData ? placeholder : formatNumber(data.criticalDependenciesCount)}
            change={noData ? undefined : `${formatNumber(data.criticalEntities)} ${labels.kpiCriticalChange}`}
            trend="neutral"
            icon={GitFork}
            accent="warning"
            subtitle={labels.kpiCriticalSub}
          />
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6">
        <div className="lg:col-span-5">
          <SystemHealthChart
            data={{
              healthy: data?.healthyEntities ?? 0,
              warning: data?.warningEntities ?? 0,
              critical: data?.criticalEntities ?? 0,
              unknown: (data?.unknownEntities ?? 0) + (data?.staleEntities ?? 0),
            }}
          />
        </div>

        <div className="lg:col-span-7">
          <RecentChangesTable
            changes={data?.recentChanges ?? []}
            isLoading={isLoading}
            // The page banner already reports a failed refresh over existing rows.
            error={noData ? errorMessage : null}
          />
        </div>
      </div>

      {pendingReviews > 0 && (
        <div className="p-4 rounded-xl border border-blue-200 dark:border-blue-900 bg-blue-50 dark:bg-blue-950/30 flex flex-wrap items-center gap-3">
          <ClipboardCheck className="w-5 h-5 text-blue-600 dark:text-blue-400 shrink-0" />
          <p className="text-sm text-blue-800 dark:text-blue-300 flex-1 min-w-0">
            {formatNumber(pendingReviews)} {labels.reviewAlert}
          </p>
          <Link
            to="/inventory?review=1"
            className="text-sm font-semibold text-blue-700 dark:text-blue-300 hover:underline"
          >
            {labels.reviewLink}
          </Link>
        </div>
      )}

      {(data?.staleEntities ?? 0) > 0 && (
        <div className="p-4 rounded-xl border border-amber-200 dark:border-amber-900 bg-amber-50 dark:bg-amber-950/30 flex items-center gap-3">
          <GitMerge className="w-5 h-5 text-amber-600 dark:text-amber-400 shrink-0" />
          <p className="text-sm text-amber-800 dark:text-amber-300">
            {formatNumber(data?.staleEntities)} {labels.staleAlert}
          </p>
        </div>
      )}

      <div className="p-6 rounded-xl border border-neutral-200 dark:border-dark-border bg-gradient-to-r from-red-500/10 via-neutral-100 to-transparent dark:from-red-950/30 dark:via-dark-card dark:to-transparent flex flex-col md:flex-row items-start md:items-center justify-between gap-4">
        <div className="flex items-center gap-4">
          <div className="p-3.5 rounded-xl bg-brand-primary text-white shadow-md flex-shrink-0">
            <Cpu className="w-6 h-6" />
          </div>
          <div>
            <h3 className="font-bold text-lg text-neutral-900 dark:text-white flex items-center gap-2">
              {labels.simTitle}
              <span className="text-xs px-2 py-0.5 rounded-full bg-brand-primary text-white font-semibold">
                {isTr ? 'BerSim Motoru' : 'BerSim Engine'}
              </span>
            </h3>
            <p className="text-sm text-neutral-600 dark:text-neutral-400 mt-0.5 max-w-2xl">
              {labels.simSub}
            </p>
          </div>
        </div>

        <ButtonLink to="/simulation" variant="primary" className="whitespace-nowrap bg-brand-primary">
          {labels.simButton}
        </ButtonLink>
      </div>
    </div>
  );
};