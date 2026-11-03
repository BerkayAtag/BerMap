import React from 'react';
import {
  Server,
  CheckCircle2,
  PlugZap,
  GitFork,
  Play,
  RotateCw,
  Cpu,
  AlertTriangle,
  GitMerge,
} from 'lucide-react';
import { KPICard } from '@/components/dashboard/KPICard';
import { SystemHealthChart } from '@/components/dashboard/SystemHealthChart';
import { RecentChangesTable } from '@/components/dashboard/RecentChangesTable';
import { Button } from '@/components/ui/Button';
import { ButtonLink } from '@/components/ui/ButtonLink';
import { useDashboard } from '@/hooks/useDashboard';
import { useLocale } from '@/lib/i18n';

export const DashboardPage: React.FC = () => {
  const { data, isLoading, isError, error, refetch, isFetching } = useDashboard();
  const locale = useLocale();
  const isTr = locale === 'tr';

  const total = data?.totalEntities ?? 0;
  const healthy = data?.healthyEntities ?? 0;
  const healthyPercent = total > 0 ? ((healthy / total) * 100).toFixed(1) : '0.0';

  const labels = {
    title: isTr ? 'Altyapı İstihbarat Paneli' : 'Infrastructure Intelligence Dashboard',
    subtitle: isTr ? 'Kurumsal IT altyapısı, bileşen bağımlılıkları ve simülasyon özeti' : 'Enterprise IT infrastructure, component dependencies and simulation summary',
    refresh: isTr ? 'Yenile' : 'Refresh',
    startSimulation: isTr ? 'Simülasyon Başlat' : 'Start Simulation',
    errorFetch: isTr ? 'Dashboard verisi alınamadı.' : 'Failed to fetch dashboard data.',
    kpiTotal: isTr ? 'Toplam Keşfedilen Varlık' : 'Total Discovered Entities',
    kpiTotalChange: isTr ? 'son 24 saatte' : 'in last 24h',
    kpiTotalSub: isTr ? 'AD, vCenter ve collector kaynakları' : 'AD, vCenter and collector sources',
    kpiHealthy: isTr ? 'Sağlıklı Bileşenler' : 'Healthy Components',
    kpiHealthyChange: isTr ? 'sağlıklı' : 'healthy',
    kpiHealthySub: isTr ? 'Normal çalışan servisler' : 'Normal running services',
    kpiActive: isTr ? 'Aktif Entegrasyonlar' : 'Active Integrations',
    kpiActiveOk: isTr ? 'Keşif kaynakları hazır' : 'Discovery sources ready',
    kpiActiveWait: isTr ? 'Entegrasyon bekleniyor' : 'Awaiting integration',
    kpiActiveSub: isTr ? 'Salt-okunur bağlantılar' : 'Read-only connections',
    kpiCritical: isTr ? 'Kritik Bağımlılık Yolları' : 'Critical Dependency Paths',
    kpiCriticalChange: isTr ? 'uzlaştırma incelemesi' : 'reconciliation reviews',
    kpiCriticalSub: isTr ? 'Yüksek etki potansiyeli' : 'High impact potential',
    staleAlert: isTr ? 'varlık artık hiçbir keşif kaynağından görülmüyor. Envanter sayfasından inceleyebilirsiniz.' : 'entities are no longer seen by any discovery source. Check Inventory page.',
    simTitle: isTr ? 'BerSim — Simülasyon Stüdyosu' : 'BerSim — Simulation Studio',
    simSub: isTr ? 'Üretim ortamına dokunmadan host kesintilerini, VM taşımalarını veya ağ bağlantı arızalarını deterministik olarak simüle edin.' : 'Simulate host outages, VM migrations, or network failures deterministically without touching production.',
    simButton: isTr ? 'Simülasyon Stüdyosuna Git' : 'Go to Simulation Studio',
  };

  const errorMessage = isError
    ? (error as { message?: string } | null)?.message ?? labels.errorFetch
    : null;

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
          <ButtonLink to="/simulation" variant="primary" size="sm" className="gap-2 bg-brand-primary">
            <Play className="w-4 h-4 fill-white" />
            {labels.startSimulation}
          </ButtonLink>
        </div>
      </div>

      {errorMessage && (
        <div className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300 flex items-center gap-2">
          <AlertTriangle className="w-4 h-4" />
          {errorMessage}
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
            value={total.toLocaleString(isTr ? 'tr-TR' : 'en-US')}
            change={`${data?.recentDiscoveries ?? 0} ${labels.kpiTotalChange}`}
            trend="neutral"
            icon={Server}
            accent="primary"
            subtitle={labels.kpiTotalSub}
          />
          <KPICard
            title={labels.kpiHealthy}
            value={healthy.toLocaleString(isTr ? 'tr-TR' : 'en-US')}
            change={`${healthyPercent}% ${labels.kpiHealthyChange}`}
            trend="up"
            icon={CheckCircle2}
            accent="healthy"
            subtitle={labels.kpiHealthySub}
          />
          <KPICard
            title={labels.kpiActive}
            value={`${data?.activeIntegrations ?? 0} / ${data?.totalIntegrations ?? 0}`}
            change={(data?.activeIntegrations ?? 0) > 0 ? labels.kpiActiveOk : labels.kpiActiveWait}
            trend="neutral"
            icon={PlugZap}
            accent="info"
            subtitle={labels.kpiActiveSub}
          />
          <KPICard
            title={labels.kpiCritical}
            value={(data?.criticalDependenciesCount ?? 0).toLocaleString(isTr ? 'tr-TR' : 'en-US')}
            change={`${data?.pendingReconciliationReviews ?? 0} ${labels.kpiCriticalChange}`}
            trend="down"
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
            error={errorMessage}
          />
        </div>
      </div>

      {(data?.staleEntities ?? 0) > 0 && (
        <div className="p-4 rounded-xl border border-amber-200 dark:border-amber-900 bg-amber-50 dark:bg-amber-950/30 flex items-center gap-3">
          <GitMerge className="w-5 h-5 text-amber-600 dark:text-amber-400" />
          <p className="text-sm text-amber-800 dark:text-amber-300">
            {data?.staleEntities} {labels.staleAlert}
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