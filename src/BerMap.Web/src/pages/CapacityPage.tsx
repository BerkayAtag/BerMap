import React, { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useLocale } from '@/lib/i18n';
import { api } from '@/lib/api';
import { Card } from '@/components/ui/Card';
import { Loader2, Cpu, MemoryStick, Server } from 'lucide-react';

interface TopologyNode {
  id: string;
  nodeType: string;
  name: string;
  status: string;
  properties?: Record<string, string>;
}

interface TopologyGraph {
  nodes: TopologyNode[];
  relationships: unknown[];
}

const readNum = (props: Record<string, string> | undefined, key: string): number | null => {
  if (!props) return null;
  const raw = props[key];
  if (raw === undefined || raw === null || raw === '') return null;
  const n = Number(raw);
  return Number.isFinite(n) ? n : null;
};

export const CapacityPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';

  const { data, isLoading, isError, refetch } = useQuery<TopologyGraph>({
    queryKey: ['capacity', 'topology'],
    queryFn: () => api.get<TopologyGraph>('/graph/topology?depth=3'),
    refetchInterval: 60_000,
  });

  const clusters = useMemo(() => {
    const nodes = data?.nodes ?? [];
    return nodes
      .filter((n) => n.nodeType === 'Cluster')
      .map((c) => {
        const props = c.properties ?? {};
        const cpuCap = readNum(props, 'cpuCapacityMhz');
        const cpuUsed = readNum(props, 'cpuUsageMhz');
        const memCap = readNum(props, 'memoryCapacityMb');
        const memUsed = readNum(props, 'memoryUsageMb');
        const cpuPct = readNum(props, 'cpuUsagePercent');
        const memPct = readNum(props, 'memoryUsagePercent');
        const hostCount = readNum(props, 'hostCount');
        const vmCount = readNum(props, 'vmCount');
        return {
          id: c.id,
          name: c.name,
          status: c.status,
          cpuCap, cpuUsed, memCap, memUsed, cpuPct, memPct, hostCount, vmCount,
          haEnabled: props['haEnabled'] === 'true' || props['HaEnabled'] === 'true',
          drsEnabled: props['drsEnabled'] === 'true' || props['DrsEnabled'] === 'true',
        };
      });
  }, [data]);

  const hosts = useMemo(() => {
    const nodes = data?.nodes ?? [];
    return nodes.filter((n) => n.nodeType === 'Hypervisor');
  }, [data]);

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
            {isTr ? 'Kapasite İstihbaratı' : 'Capacity Intelligence'}
          </h1>
          <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1">
            {isTr
              ? 'Cluster ve host düzeyinde gerçek vCenter metriklerine dayalı kapasite görünümü'
              : 'Capacity view based on real vCenter metrics at cluster and host level'}
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
          {isTr ? 'Kapasite verisi yüklenemedi.' : 'Failed to load capacity data.'}
        </div>
      )}

      {isLoading && clusters.length === 0 && (
        <div className="flex items-center gap-2 text-sm text-neutral-500">
          <Loader2 className="w-4 h-4 animate-spin" />
          {isTr ? 'Yükleniyor...' : 'Loading...'}
        </div>
      )}

      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
        <Card className="p-4">
          <div className="flex items-center gap-2 text-xs text-neutral-500">
            <Server className="w-4 h-4" /> {isTr ? 'Cluster Sayısı' : 'Clusters'}
          </div>
          <div className="text-2xl font-bold mt-1">{clusters.length}</div>
        </Card>
        <Card className="p-4">
          <div className="flex items-center gap-2 text-xs text-neutral-500">
            <Cpu className="w-4 h-4" /> {isTr ? 'Host Sayısı' : 'Hosts'}
          </div>
          <div className="text-2xl font-bold mt-1">{hosts.length}</div>
        </Card>
        <Card className="p-4">
          <div className="flex items-center gap-2 text-xs text-neutral-500">
            <MemoryStick className="w-4 h-4" /> {isTr ? 'Toplam VM' : 'Total VMs'}
          </div>
          <div className="text-2xl font-bold mt-1">
            {clusters.reduce((a, c) => a + (c.vmCount ?? 0), 0)}
          </div>
        </Card>
      </div>

      <Card className="p-4 overflow-x-auto">
        <h2 className="text-sm font-semibold mb-3">
          {isTr ? 'Cluster Kapasite Tablosu' : 'Cluster Capacity Table'}
        </h2>
        {clusters.length === 0 ? (
          <p className="text-sm text-neutral-500">
            {isTr ? 'Cluster bulunamadı. Önce keşif çalıştırın.' : 'No clusters found. Run discovery first.'}
          </p>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-xs text-neutral-500 border-b border-neutral-200 dark:border-dark-border">
                <th className="py-2 pr-3">{isTr ? 'Cluster' : 'Cluster'}</th>
                <th className="py-2 pr-3">HA</th>
                <th className="py-2 pr-3">DRS</th>
                <th className="py-2 pr-3">{isTr ? 'Host' : 'Hosts'}</th>
                <th className="py-2 pr-3">VM</th>
                <th className="py-2 pr-3">CPU %</th>
                <th className="py-2 pr-3">RAM %</th>
                <th className="py-2 pr-3">{isTr ? 'Durum' : 'Status'}</th>
              </tr>
            </thead>
            <tbody>
              {clusters.map((c) => (
                <tr key={c.id} className="border-b border-neutral-100 dark:border-dark-border/40">
                  <td className="py-2 pr-3 font-medium">{c.name}</td>
                  <td className="py-2 pr-3">{c.haEnabled ? '✓' : '—'}</td>
                  <td className="py-2 pr-3">{c.drsEnabled ? '✓' : '—'}</td>
                  <td className="py-2 pr-3">{c.hostCount ?? '—'}</td>
                  <td className="py-2 pr-3">{c.vmCount ?? '—'}</td>
                  <td className="py-2 pr-3">{c.cpuPct !== null ? `${c.cpuPct.toFixed(1)}%` : '—'}</td>
                  <td className="py-2 pr-3">{c.memPct !== null ? `${c.memPct.toFixed(1)}%` : '—'}</td>
                  <td className="py-2 pr-3">{c.status}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>

      <Card className="p-4 overflow-x-auto">
        <h2 className="text-sm font-semibold mb-3">
          {isTr ? 'Host Kapasite Detayı' : 'Host Capacity Detail'}
        </h2>
        {hosts.length === 0 ? (
          <p className="text-sm text-neutral-500">
            {isTr ? 'Host bulunamadı.' : 'No hosts found.'}
          </p>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-xs text-neutral-500 border-b border-neutral-200 dark:border-dark-border">
                <th className="py-2 pr-3">{isTr ? 'Host' : 'Host'}</th>
                <th className="py-2 pr-3">CPU %</th>
                <th className="py-2 pr-3">RAM %</th>
                <th className="py-2 pr-3">{isTr ? 'Durum' : 'Status'}</th>
              </tr>
            </thead>
            <tbody>
              {hosts.map((h) => {
                const p = (h.properties ?? {}) as Record<string, string>;
                const cpu = readNum(p, 'cpuUsagePercent');
                const mem = readNum(p, 'memoryUsagePercent');
                return (
                  <tr key={h.id} className="border-b border-neutral-100 dark:border-dark-border/40">
                    <td className="py-2 pr-3 font-medium">{h.name}</td>
                    <td className="py-2 pr-3">{cpu !== null ? `${cpu.toFixed(1)}%` : '—'}</td>
                    <td className="py-2 pr-3">{mem !== null ? `${mem.toFixed(1)}%` : '—'}</td>
                    <td className="py-2 pr-3">{h.status}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </Card>
    </div>
  );
};
