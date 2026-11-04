import React, { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useLocale } from '@/lib/i18n';
import { Card } from '@/components/ui/Card';
import { StatusIndicator } from '@/components/ui/StatusIndicator';
import { Link, useNavigate } from 'react-router-dom';
import { AlertTriangle, Loader2, Cpu, MemoryStick, Server } from 'lucide-react';
import { fetchAllInventoryNodes } from '@/hooks/useInventoryNodes';
import { formatNumber, formatPercent, getErrorMessage, toNodeStatus } from '@/lib/format';
import type { InventoryNode, NodeStatus } from '@/types';

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

/** Utilisation thresholds: at or above WARNING is amber, at or above CRITICAL is rose. */
const CAPACITY_WARNING_PERCENT = 80;
const CAPACITY_CRITICAL_PERCENT = 90;

type CapacityLevel = 'ok' | 'warning' | 'critical' | 'unknown';

function capacityLevel(...values: (number | null)[]): CapacityLevel {
  const known = values.filter((value): value is number => value !== null);
  if (known.length === 0) return 'unknown';
  const peak = Math.max(...known);
  if (peak >= CAPACITY_CRITICAL_PERCENT) return 'critical';
  if (peak >= CAPACITY_WARNING_PERCENT) return 'warning';
  return 'ok';
}

const STATUS_RANK: Record<NodeStatus, number> = { unknown: 0, healthy: 1, warning: 2, critical: 3 };

/** The worse of the discovered status and the utilisation threshold status. */
function effectiveStatus(status: string, level: CapacityLevel): NodeStatus {
  const discovered = toNodeStatus(status);
  const fromCapacity: NodeStatus | null = level === 'critical' ? 'critical' : level === 'warning' ? 'warning' : null;
  if (!fromCapacity) return discovered;
  return STATUS_RANK[fromCapacity] > STATUS_RANK[discovered] ? fromCapacity : discovered;
}

const PeakCell: React.FC<{ value: number | null }> = ({ value }) => {
  const level = capacityLevel(value);
  const tone =
    level === 'critical'
      ? 'text-rose-600 dark:text-rose-400 font-semibold'
      : level === 'warning'
        ? 'text-amber-600 dark:text-amber-400 font-semibold'
        : '';
  const bar =
    level === 'critical' ? 'bg-rose-500' : level === 'warning' ? 'bg-amber-500' : 'bg-emerald-500';
  return (
    <div className="flex items-center gap-2 min-w-24">
      <span className={`tabular-nums w-14 ${tone}`}>{pct(value)}</span>
      {value !== null && (
        <span className="hidden sm:block h-1.5 w-16 rounded-full bg-neutral-200 dark:bg-dark-border overflow-hidden" aria-hidden="true">
          <span className={`block h-full ${bar}`} style={{ width: `${Math.min(100, Math.max(0, value))}%` }} />
        </span>
      )}
    </div>
  );
};

interface CapacityData {
  clusters: InventoryNode[];
  hosts: InventoryNode[];
  truncated: boolean;
}

const byPeakDescending = <T extends { cpuPct: number | null; memPct: number | null; name: string }>(a: T, b: T) =>
  Math.max(b.cpuPct ?? -1, b.memPct ?? -1) - Math.max(a.cpuPct ?? -1, a.memPct ?? -1) ||
  a.name.localeCompare(b.name, undefined, { sensitivity: 'base' });

export const CapacityPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const navigate = useNavigate();

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
    return (data?.clusters ?? [])
      .map((c) => {
        const props = c.properties ?? {};
        const cpuPct = readNum(props, 'cpuUsagePercent');
        const memPct = readNum(props, 'memoryUsagePercent');
        const level = capacityLevel(cpuPct, memPct);
        return {
          id: c.id,
          name: c.name,
          status: effectiveStatus(c.status, level),
          level,
          cpuPct,
          memPct,
          hostCount: readNum(props, 'hostCount'),
          vmCount: readNum(props, 'vmCount'),
          haEnabled: readBool(props, 'haEnabled'),
          drsEnabled: readBool(props, 'drsEnabled'),
        };
      })
      .sort(byPeakDescending);
  }, [data]);

  const hosts = useMemo(() => {
    return (data?.hosts ?? [])
      .map((h) => {
        const props = h.properties ?? {};
        const cpuPct = readNum(props, 'cpuUsagePercent');
        const memPct = readNum(props, 'memoryUsagePercent');
        const level = capacityLevel(cpuPct, memPct);
        const cluster = readProp(props, 'cluster') ?? readProp(props, 'clusterName');
        return {
          id: h.id,
          name: h.name,
          cluster: cluster === undefined || cluster === null || cluster === '' ? null : String(cluster),
          status: effectiveStatus(h.status, level),
          level,
          cpuPct,
          memPct,
        };
      })
      .sort(byPeakDescending);
  }, [data]);

  const overThreshold = [...clusters, ...hosts].filter((row) => row.level === 'warning' || row.level === 'critical').length;
  const openInInventory = (id: string) => navigate(`/inventory?node=${encodeURIComponent(id)}`);
  const rowClass =
    'border-b border-neutral-100 dark:border-dark-border/40 cursor-pointer hover:bg-neutral-50 dark:hover:bg-neutral-800/40 transition-colors';
  const levelRowTint = (level: CapacityLevel) =>
    level === 'critical' ? 'bg-rose-50/60 dark:bg-rose-950/20' : level === 'warning' ? 'bg-amber-50/60 dark:bg-amber-950/20' : '';
  const nameLink = (id: string, name: string) => (
    <Link
      to={`/inventory?node=${encodeURIComponent(id)}`}
      onClick={(event) => event.stopPropagation()}
      className="font-medium hover:underline focus:outline-none focus-visible:ring-2 focus-visible:ring-brand-primary rounded"
    >
      {name}
    </Link>
  );

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

      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
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
        <Card className="p-4">
          <div className="flex items-center gap-2 text-xs text-neutral-500">
            <AlertTriangle className="w-4 h-4" /> {isTr ? 'Eşik Üstü' : 'Over Threshold'}
          </div>
          <div className={`text-2xl font-bold mt-1 ${overThreshold > 0 ? 'text-amber-600 dark:text-amber-400' : ''}`}>
            {formatNumber(overThreshold)}
          </div>
          <div className="text-[11px] text-neutral-500 mt-0.5">
            {isTr
              ? `CPU veya RAM ≥ %${CAPACITY_WARNING_PERCENT} uyarı, ≥ %${CAPACITY_CRITICAL_PERCENT} kritik`
              : `CPU or RAM ≥ ${CAPACITY_WARNING_PERCENT}% warning, ≥ ${CAPACITY_CRITICAL_PERCENT}% critical`}
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
                <tr key={c.id} onClick={() => openInInventory(c.id)} className={`${rowClass} ${levelRowTint(c.level)}`}>
                  <td className="py-2 pr-3">{nameLink(c.id, c.name)}</td>
                  <td className="py-2 pr-3">{c.haEnabled ? '✓' : '—'}</td>
                  <td className="py-2 pr-3">{c.drsEnabled ? '✓' : '—'}</td>
                  <td className="py-2 pr-3">{c.hostCount !== null ? formatNumber(c.hostCount) : '—'}</td>
                  <td className="py-2 pr-3">{c.vmCount !== null ? formatNumber(c.vmCount) : '—'}</td>
                  <td className="py-2 pr-3"><PeakCell value={c.cpuPct} /></td>
                  <td className="py-2 pr-3"><PeakCell value={c.memPct} /></td>
                  <td className="py-2 pr-3">
                    <StatusIndicator status={c.status} size="sm" showLabel />
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
                <th className="py-2 pr-3">Cluster</th>
                <th className="py-2 pr-3">CPU %</th>
                <th className="py-2 pr-3">RAM %</th>
                <th className="py-2 pr-3">{isTr ? 'Durum' : 'Status'}</th>
              </tr>
            </thead>
            <tbody>
              {hosts.map((h) => (
                <tr key={h.id} onClick={() => openInInventory(h.id)} className={`${rowClass} ${levelRowTint(h.level)}`}>
                  <td className="py-2 pr-3">{nameLink(h.id, h.name)}</td>
                  <td className="py-2 pr-3 text-neutral-600 dark:text-neutral-300">{h.cluster ?? '—'}</td>
                  <td className="py-2 pr-3"><PeakCell value={h.cpuPct} /></td>
                  <td className="py-2 pr-3"><PeakCell value={h.memPct} /></td>
                  <td className="py-2 pr-3">
                    <StatusIndicator status={h.status} size="sm" showLabel />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </div>
  );
};
