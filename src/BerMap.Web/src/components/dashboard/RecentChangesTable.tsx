import React from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Card } from '@/components/ui/Card';
import { StatusIndicator } from '@/components/ui/StatusIndicator';
import { Server, Cpu, Database, Network } from 'lucide-react';
import { toNodeStatus } from '@/lib/format';
import { useLocale } from '@/lib/i18n';
import type { RecentChange } from '@/types';

interface RecentChangesTableProps {
  changes: RecentChange[];
  isLoading?: boolean;
  error?: string | null;
}

function getIcon(entityType: string) {
  const value = entityType.toLowerCase();
  if (value.includes('database')) return <Database className="w-4 h-4 text-blue-500" />;
  if (value.includes('network') || value.includes('switch')) {
    return <Network className="w-4 h-4 text-emerald-500" />;
  }
  if (value.includes('virtual') || value.includes('vm') || value.includes('hypervisor')) {
    return <Cpu className="w-4 h-4 text-purple-500" />;
  }
  return <Server className="w-4 h-4 text-neutral-500" />;
}

function getChangeBadge(changeType: string, isTr: boolean) {
  switch (changeType) {
    case 'Discovered':
      return (
        <span className="text-[11px] px-2 py-0.5 rounded-full font-medium bg-emerald-50 dark:bg-emerald-950/40 text-emerald-700 dark:text-emerald-400 border border-emerald-200 dark:border-emerald-800">
          {isTr ? 'Yeni Keşif' : 'New Discovery'}
        </span>
      );
    case 'StatusChanged':
    case 'Status Changed':
      return (
        <span className="text-[11px] px-2 py-0.5 rounded-full font-medium bg-amber-50 dark:bg-amber-950/40 text-amber-700 dark:text-amber-400 border border-amber-200 dark:border-amber-800">
          {isTr ? 'Durum Değişti' : 'Status Changed'}
        </span>
      );
    default:
      return (
        <span className="text-[11px] px-2 py-0.5 rounded-full font-medium bg-neutral-100 dark:bg-neutral-800 text-neutral-700 dark:text-neutral-300 border border-neutral-200 dark:border-neutral-700">
          {changeType || (isTr ? 'Güncelleme' : 'Update')}
        </span>
      );
  }
}

function formatTimestamp(value: string, isTr: boolean): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;

  const seconds = Math.max(0, Math.round((Date.now() - date.getTime()) / 1000));
  if (seconds < 60) return isTr ? `${seconds} sn önce` : `${seconds}s ago`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return isTr ? `${minutes} dk önce` : `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return isTr ? `${hours} saat önce` : `${hours}h ago`;
  return date.toLocaleString(isTr ? 'tr-TR' : 'en-US');
}

export const RecentChangesTable: React.FC<RecentChangesTableProps> = ({
  changes,
  isLoading = false,
  error = null,
}) => {
  const navigate = useNavigate();
  const locale = useLocale();
  const isTr = locale === 'tr';

  const labels = {
    title: isTr ? 'Son Altyapı Değişiklikleri & Keşifler' : 'Recent Infrastructure Changes & Discoveries',
    subtitle: isTr
      ? 'İlk kez keşfedilen ve durumu değişen bileşenler, en yeniden eskiye'
      : 'Newly discovered components and real status changes, newest first',
    open: isTr ? 'Envanterde aç' : 'Open in inventory',
    loading: isTr ? 'Yükleniyor...' : 'Loading...',
    emptyTitle: isTr ? 'Henüz keşif kaydı yok' : 'No discovery records yet',
    emptyBody: isTr ? 'Bir entegrasyon ekleyip keşif başlattığınızda değişiklikler burada görünür.' : 'Add an integration and start a discovery to see changes here.',
    entity: isTr ? 'Varlık / Bileşen' : 'Entity / Component',
    eventType: isTr ? 'Olay Türü' : 'Event Type',
    source: isTr ? 'Kaynak' : 'Source',
    status: isTr ? 'Durum' : 'Status',
    time: isTr ? 'Zaman' : 'Time',
  };

  return (
    <Card className="p-5 flex flex-col h-full">
      <div className="flex items-center justify-between mb-4">
        <div>
          <h3 className="font-semibold text-base text-neutral-900 dark:text-white">
            {labels.title}
          </h3>
          <p className="text-xs text-neutral-500 dark:text-neutral-400 mt-0.5">
            {labels.subtitle}
          </p>
        </div>
      </div>

      {isLoading && <p className="text-sm text-neutral-500">{labels.loading}</p>}
      {error && <p className="text-sm text-rose-600 dark:text-rose-400">{error}</p>}

      {!isLoading && !error && changes.length === 0 && (
        <div className="flex-1 flex items-center justify-center py-10 text-center">
          <div>
            <p className="text-sm font-medium text-neutral-600 dark:text-neutral-300">
              {labels.emptyTitle}
            </p>
            <p className="text-xs text-neutral-500 mt-1">
              {labels.emptyBody}
            </p>
          </div>
        </div>
      )}

      {changes.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full text-left text-sm">
            <thead>
              <tr className="border-b border-neutral-200 dark:border-dark-border text-xs uppercase font-semibold text-neutral-400 dark:text-neutral-500">
                <th className="pb-3 pl-1">{labels.entity}</th>
                <th className="pb-3">{labels.eventType}</th>
                <th className="pb-3">{labels.source}</th>
                <th className="pb-3">{labels.status}</th>
                <th className="pb-3 text-right pr-1">{labels.time}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-neutral-100 dark:divide-dark-border">
              {changes.map((change) => (
                <tr
                  key={`${change.id}-${change.changeType}-${change.timestamp}`}
                  onClick={() => navigate(`/inventory?node=${encodeURIComponent(change.id)}`)}
                  className="cursor-pointer hover:bg-neutral-50 dark:hover:bg-neutral-800/40 transition-colors"
                >
                  <td className="py-3 pl-1">
                    <div className="flex items-center gap-2.5">
                      {getIcon(change.entityType)}
                      {/* The row is clickable; the link keeps it reachable by keyboard. */}
                      <Link
                        to={`/inventory?node=${encodeURIComponent(change.id)}`}
                        onClick={(event) => event.stopPropagation()}
                        title={labels.open}
                        className="font-medium text-neutral-900 dark:text-white truncate max-w-[220px] hover:underline focus:outline-none focus-visible:ring-2 focus-visible:ring-brand-primary rounded"
                      >
                        {change.entityName}
                      </Link>
                    </div>
                  </td>
                  <td className="py-3">{getChangeBadge(change.changeType, isTr)}</td>
                  <td className="py-3">
                    <span className="text-xs font-medium text-neutral-600 dark:text-neutral-400">
                      {change.source || '—'}
                    </span>
                  </td>
                  <td className="py-3">
                    <div className="flex items-center gap-1.5">
                      <StatusIndicator status={toNodeStatus(change.status)} size="sm" showLabel />
                    </div>
                  </td>
                  <td className="py-3 text-right pr-1 text-xs text-neutral-400 dark:text-neutral-500 font-mono">
                    {formatTimestamp(change.timestamp, isTr)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
};