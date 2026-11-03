import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useLocale } from '@/lib/i18n';
import { api } from '@/lib/api';
import { Card } from '@/components/ui/Card';
import { Loader2, Download, FileText, Camera } from 'lucide-react';

interface InventoryNode {
  id: string;
  name: string;
  nodeType: string;
  fqdn: string;
  ipAddress: string | null;
  status: string;
  discoverySource: string;
  sourceSystems?: string[];
}

interface InventoryResponse {
  items: InventoryNode[];
  totalCount: number;
  page: number;
  pageSize: number;
}

interface SnapshotSummary {
  id: string;
  name: string;
  nodeCount: number;
  relationshipCount: number;
  hash: string;
  createdAt: string;
  snapshotVersion: number;
  isImmutable: boolean;
}

interface DashboardSummary {
  totalEntities: number;
  healthyEntities: number;
  warningEntities: number;
  criticalEntities: number;
}

export const ReportsPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const navigate = useNavigate();
  const [exporting, setExporting] = useState(false);

  const dashboard = useQuery<DashboardSummary>({
    queryKey: ['reports', 'dashboard'],
    queryFn: () => api.get<DashboardSummary>('/dashboard/summary'),
  });

  const snapshots = useQuery<SnapshotSummary[]>({
    queryKey: ['reports', 'snapshots'],
    queryFn: () => api.get<SnapshotSummary[]>('/snapshots'),
  });

  const handleExportCsv = async () => {
    setExporting(true);
    try {
      const response = await api.get<InventoryResponse>('/inventory/nodes?page=1&pageSize=1000');
      const items = response.items ?? [];
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
        .join('\n');
      const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `bermap-inventory-${new Date().toISOString().slice(0, 10)}.csv`;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);
    } finally {
      setExporting(false);
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
          <div className="text-2xl font-bold mt-1">{dashboard.data?.totalEntities ?? 0}</div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Sağlıklı' : 'Healthy'}</div>
          <div className="text-2xl font-bold mt-1 text-emerald-600">{dashboard.data?.healthyEntities ?? 0}</div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Uyarı' : 'Warning'}</div>
          <div className="text-2xl font-bold mt-1 text-amber-600">{dashboard.data?.warningEntities ?? 0}</div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Kritik' : 'Critical'}</div>
          <div className="text-2xl font-bold mt-1 text-rose-600">{dashboard.data?.criticalEntities ?? 0}</div>
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
          className="inline-flex items-center justify-center rounded-md font-medium transition-colors duration-150 bg-brand-primary text-white hover:bg-brand-primary-dark h-9 px-4 text-sm gap-2 disabled:opacity-50"
        >
          {exporting ? <Loader2 className="w-4 h-4 animate-spin" /> : <Download className="w-4 h-4" />}
          {isTr ? 'CSV Dışa Aktar' : 'Export CSV'}
        </button>
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
                  v{s.snapshotVersion} · {s.nodeCount} {isTr ? 'düğüm' : 'nodes'} · {s.relationshipCount} {isTr ? 'ilişki' : 'rels'} · {new Date(s.createdAt).toLocaleString()}
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
