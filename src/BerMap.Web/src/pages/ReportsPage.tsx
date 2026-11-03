import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useLocale } from '@/lib/i18n';
import { api } from '@/lib/api';
import { Card } from '@/components/ui/Card';
import { Loader2, Download, FileText, Camera, AlertTriangle } from 'lucide-react';
import { useDashboard } from '@/hooks/useDashboard';
import { formatDateTime, formatNumber, getErrorMessage } from '@/lib/format';
import type { InventoryPage as InventoryResponse, SnapshotSummary } from '@/types';

/** Backend clamps pageSize to 200 for /inventory/nodes. */
const EXPORT_PAGE_SIZE = 200;

export const ReportsPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const navigate = useNavigate();
  const [exporting, setExporting] = useState(false);
  const [exportProgress, setExportProgress] = useState<string | null>(null);
  const [exportError, setExportError] = useState<string | null>(null);

  const dashboard = useDashboard();

  const snapshots = useQuery<SnapshotSummary[]>({
    queryKey: ['snapshots', 'list'],
    queryFn: () => api.get<SnapshotSummary[]>('/snapshots'),
  });

  const handleExportCsv = async () => {
    setExporting(true);
    setExportError(null);
    setExportProgress(null);
    try {
      const items: InventoryResponse['items'] = [];
      let page = 1;
      let totalPages = 1;
      do {
        const response = await api.get<InventoryResponse>(
          `/inventory/nodes?page=${page}&pageSize=${EXPORT_PAGE_SIZE}`,
        );
        items.push(...(response.items ?? []));
        totalPages = Math.max(1, response.totalPages ?? 1);
        setExportProgress(`${formatNumber(items.length)} / ${formatNumber(response.totalCount)}`);
        page += 1;
      } while (page <= totalPages);

      const header = ['id', 'name', 'nodeType', 'fqdn', 'ipAddress', 'status', 'discoverySource', 'sources'];
      const rows = items.map((n) => [
        n.id,
        n.name,
        n.nodeType,
        n.fqdn,
        n.ipAddress ?? '',
        n.status,
        n.discoverySource,
        (n.sourceSystems ?? []).join(';'),
      ]);
      const csv = [header, ...rows]
        .map((row) => row.map((v) => `"${String(v).replace(/"/g, '""')}"`).join(','))
        .join('\r\n');
      // UTF-8 BOM so Excel opens Turkish characters correctly.
      const blob = new Blob([`\uFEFF${csv}`], { type: 'text/csv;charset=utf-8;' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `bermap-inventory-${new Date().toISOString().slice(0, 10)}.csv`;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);
    } catch (error) {
      setExportError(
        `${isTr ? 'CSV dışa aktarımı başarısız oldu:' : 'CSV export failed:'} ${getErrorMessage(error)}`,
      );
    } finally {
      setExporting(false);
      setExportProgress(null);
    }
  };

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      <div>
        <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
          {isTr ? 'Raporlar' : 'Reports'}
        </h1>
        <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1">
          {isTr
            ? 'Gerçek envanter ve snapshot verilerinden üretilen raporlar'
            : 'Reports generated from real inventory and snapshot data'}
        </p>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-4 gap-3">
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Toplam Varlık' : 'Total Entities'}</div>
          <div className="text-2xl font-bold mt-1">{formatNumber(dashboard.data?.totalEntities)}</div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Sağlıklı' : 'Healthy'}</div>
          <div className="text-2xl font-bold mt-1 text-emerald-600">{formatNumber(dashboard.data?.healthyEntities)}</div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Uyarı' : 'Warning'}</div>
          <div className="text-2xl font-bold mt-1 text-amber-600">{formatNumber(dashboard.data?.warningEntities)}</div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Kritik' : 'Critical'}</div>
          <div className="text-2xl font-bold mt-1 text-rose-600">{formatNumber(dashboard.data?.criticalEntities)}</div>
        </Card>
      </div>

      <Card className="p-4 space-y-3">
        <h2 className="text-sm font-semibold flex items-center gap-2">
          <FileText className="w-4 h-4" />
          {isTr ? 'Envanter Raporu' : 'Inventory Report'}
        </h2>
        <p className="text-sm text-neutral-500">
          {isTr
            ? 'Tüm envanter kayıtlarını CSV olarak dışa aktarır.'
            : 'Exports all inventory records as CSV.'}
        </p>
        <button
          type="button"
          onClick={() => void handleExportCsv()}
          disabled={exporting}
          className="inline-flex items-center justify-center rounded-md font-medium transition-colors duration-150 bg-brand-primary text-white hover:bg-brand-primary-hover h-9 px-4 text-sm gap-2 disabled:opacity-50"
        >
          {exporting ? <Loader2 className="w-4 h-4 animate-spin" /> : <Download className="w-4 h-4" />}
          {isTr ? 'CSV Dışa Aktar' : 'Export CSV'}
          {exportProgress && <span className="text-xs opacity-80">({exportProgress})</span>}
        </button>
        {exportError && (
          <div
            role="alert"
            className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300 flex items-start gap-2"
          >
            <AlertTriangle className="w-4 h-4 mt-0.5 shrink-0" />
            <span>{exportError}</span>
          </div>
        )}
      </Card>

      <Card className="p-4 space-y-3">
        <h2 className="text-sm font-semibold flex items-center gap-2">
          <Camera className="w-4 h-4" />
          {isTr ? 'Snapshot Raporları' : 'Snapshot Reports'}
        </h2>
        {snapshots.isLoading && (
          <div className="flex items-center gap-2 text-sm text-neutral-500">
            <Loader2 className="w-4 h-4 animate-spin" />
            {isTr ? 'Yükleniyor...' : 'Loading...'}
          </div>
        )}
        {(snapshots.data?.length ?? 0) === 0 && !snapshots.isLoading && (
          <p className="text-sm text-neutral-500">
            {isTr ? 'Henüz snapshot yok.' : 'No snapshots yet.'}
          </p>
        )}
        <div className="space-y-2">
          {(snapshots.data ?? []).map((s) => (
            <div
              key={s.id}
              className="flex items-center justify-between gap-3 p-2 rounded-lg border border-neutral-200 dark:border-dark-border"
            >
              <div className="min-w-0">
                <div className="font-medium text-sm truncate">{s.name}</div>
                <div className="text-xs text-neutral-500">
                  v{s.snapshotVersion} · {formatNumber(s.nodeCount)} {isTr ? 'düğüm' : 'nodes'} · {formatNumber(s.relationshipCount)} {isTr ? 'ilişki' : 'rels'} · {formatDateTime(s.createdAt)}
                </div>
              </div>
              <button
                type="button"
                onClick={() => navigate('/snapshots')}
                className="text-xs text-brand-primary hover:underline shrink-0"
              >
                {isTr ? 'Detay' : 'Details'}
              </button>
            </div>
          ))}
        </div>
      </Card>
    </div>
  );
};
