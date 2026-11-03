import React, { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useLocale } from '@/lib/i18n';
import { Card } from '@/components/ui/Card';
import { StatusIndicator } from '@/components/ui/StatusIndicator';
import { Loader2, Cpu, MemoryStick, Server } from 'lucide-react';
import { fetchAllInventoryNodes } from '@/hooks/useInventoryNodes';
import { formatNumber, formatPercent, getErrorMessage, toNodeStatus } from '@/lib/format';
import type { InventoryNode } from '@/types';

type Props = Record<string, unknown>;

/** Case-insensitive property lookup (the backend camelCases dictionary keys). */
function readProp(props: Props | undefined, key: string): unknown {
  if (!props) return undefined;
  if (key in props) return props[key];
  const lowered = key.toLowerCase();
  const match = Object.keys(props).find((candidate) => candidate.toLowerCase() === lowered);
  return match === undefined ? undefined : props[match];
}

const readNum = (props: Props | undefined, key: string): number | null => {
  const raw = readProp(props, key);
  if (raw === undefined || raw === null || raw === '') return null;
  const n = Number(raw);
  return Number.isFinite(n) ? n : null;
};

/** vCenter flags arrive as "True"/"False" strings (or booleans). */
const readBool = (props: Props | undefined, key: string): boolean =>
  String(readProp(props, key) ?? '').toLowerCase() === 'true';

const pct = (value: number | null): string => (value !== null ? formatPercent(value) : '—');

interface CapacityData {
  clusters: InventoryNode[];
  hosts: InventoryNode[];
  truncated: boolean;
}

export const CapacityPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';

  // Paged inventory (not the capped topology view) so every cluster/host is listed.
  const { data, isLoading, isFetching, isError, error, refetch } = useQuery<CapacityData>({
    queryKey: ['inventory', 'capacity'],
    queryFn: async () => {
      const [clusters, hosts] = await Promise.all([
        fetchAllInventoryNodes({ nodeType: 'Cluster' }),
        fetchAllInventoryNodes({ nodeType: 'Hypervisor' }),
      ]);
      return {
        clusters: clusters.items,
        hosts: hosts.items,
        truncated: clusters.truncated || hosts.truncated,
      };
    },
    refetchInterval: 60_000,
  });

  const clusters = useMemo(() => {
    return (data?.clusters ?? []).map((c) => {
      const props = c.properties ?? {};
      return {
        id: c.id,
        name: c.name,
        status: c.status,
        cpuPct: readNum(props, 'cpuUsagePercent'),
        memPct: readNum(props, 'memoryUsagePercent'),
        hostCount: readNum(props, 'hostCount'),
        vmCount: readNum(props, 'vmCount'),
        haEnabled: readBool(props, 'haEnabled'),
        drsEnabled: readBool(props, 'drsEnabled'),
      };
    });
  }, [data]);

  const hosts = data?.hosts ?? [];

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
          disabled={isFetching}
          className="inline-flex items-center justify-center rounded-md font-medium transition-colors duration-150 border border-neutral-300 text-neutral-700 hover:bg-neutral-100 dark:border-dark-border dark:text-dark-text dark:hover:bg-dark-card h-8 px-3 text-sm gap-2"
        >
          {isFetching ? <Loader2 className="w-4 h-4 animate-spin" /> : null}
          {isTr ? 'Yenile' : 'Refresh'}
        </button>
      </div>

      {isError && (
        <div role="alert" className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300">
          {isTr ? 'Kapasite verisi yüklenemedi.' : 'Failed to load capacity data.'} {getErrorMessage(error)}
        </div>
      )}

      {data?.truncated && (
        <div role="status" className="p-3 rounded-lg border border-amber-200 dark:border-amber-900 bg-amber-50 dark:bg-amber-950/30 text-sm text-amber-800 dark:text-amber-300">
          {isTr
            ? 'Kayıt sayısı çok yüksek; listeler kısaltıldı.'
            : 'Very large estate; lists have been truncated.'}
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
          <div className="text-2xl font-bold mt-1">{formatNumber(clusters.length)}</div>
        </Card>
        <Card className="p-4">
          <div className="flex items-center gap-2 text-xs text-neutral-500">
            <Cpu className="w-4 h-4" /> {isTr ? 'Host Sayısı' : 'Hosts'}
          </div>
          <div className="text-2xl font-bold mt-1">{formatNumber(hosts.length)}</div>
        </Card>
        <Card className="p-4">
          <div className="flex items-center gap-2 text-xs text-neutral-500">
            <MemoryStick className="w-4 h-4" /> {isTr ? 'Toplam VM' : 'Total VMs'}
          </div>
          <div className="text-2xl font-bold mt-1">
            {formatNumber(clusters.reduce((a, c) => a + (c.vmCount ?? 0), 0))}
          </div>
        </Card>
      </div>

      <Card className="p-4 overflow-x-auto">
        <h2 className="text-sm font-semibold mb-3">
          {isTr ? 'Cluster Kapasite Tablosu' : 'Cluster Capacity Table'}
        </h2>
        {isLoading ? null : clusters.length === 0 ? (
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
                  <td className="py-2 pr-3">{c.hostCount !== null ? formatNumber(c.hostCount) : '—'}</td>
                  <td className="py-2 pr-3">{c.vmCount !== null ? formatNumber(c.vmCount) : '—'}</td>
                  <td className="py-2 pr-3">{pct(c.cpuPct)}</td>
                  <td className="py-2 pr-3">{pct(c.memPct)}</td>
                  <td className="py-2 pr-3">
                    <StatusIndicator status={toNodeStatus(c.status)} size="sm" showLabel />
                  </td>
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
        {isLoading ? null : hosts.length === 0 ? (
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
                const p = h.properties ?? {};
                const cpu = readNum(p, 'cpuUsagePercent');
                const mem = readNum(p, 'memoryUsagePercent');
                return (
                  <tr key={h.id} className="border-b border-neutral-100 dark:border-dark-border/40">
                    <td className="py-2 pr-3 font-medium">{h.name}</td>
                    <td className="py-2 pr-3">{pct(cpu)}</td>
                    <td className="py-2 pr-3">{pct(mem)}</td>
                    <td className="py-2 pr-3">
                      <StatusIndicator status={toNodeStatus(h.status)} size="sm" showLabel />
                    </td>
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
