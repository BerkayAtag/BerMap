import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { AlertTriangle, Camera, Download, ExternalLink, FileText, Loader2, ScrollText } from 'lucide-react';
import { Card } from '@/components/ui/Card';
import { Button } from '@/components/ui/Button';
import { buttonClassName } from '@/components/ui/buttonStyles';
import { canViewAudit } from '@/components/users/userText';
import { useLocale } from '@/lib/i18n';
import { api } from '@/lib/api';
import { downloadCsv, downloadServerCsv } from '@/lib/csv';
import { formatDateTime, formatNumber, getErrorMessage } from '@/lib/format';
import { useAuthStore } from '@/stores/authStore';
import type { InventoryPage as InventoryResponse, SnapshotGraph, SnapshotSummary } from '@/types';

/** Backend clamps pageSize to 200 for /inventory/nodes. */
const EXPORT_PAGE_SIZE = 200;

function today(): string {
  return new Date().toISOString().slice(0, 10);
}

/** File-name safe form of a snapshot name. */
function slug(value: string): string {
  return value
    .trim()
    .toLocaleLowerCase('tr')
    .replace(/ı/g, 'i')
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40) || 'snapshot';
}

type BusyKey = 'inventory' | 'audit' | `snapshot:${string}:${'nodes' | 'relationships'}`;

export const ReportsPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const role = useAuthStore((state) => state.user?.role);
  const mayViewAudit = canViewAudit(role);
  const [busy, setBusy] = useState<BusyKey | null>(null);
  const [progress, setProgress] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const snapshots = useQuery<SnapshotSummary[]>({
    queryKey: ['snapshots', 'list'],
    queryFn: () => api.get<SnapshotSummary[]>('/snapshots'),
  });

  const run = async (key: BusyKey, task: () => Promise<void>) => {
    setBusy(key);
    setError(null);
    setProgress(null);
    try {
      await task();
    } catch (requestError) {
      setError(`${isTr ? 'Dışa aktarım başarısız oldu:' : 'Export failed:'} ${getErrorMessage(requestError)}`);
    } finally {
      setBusy(null);
      setProgress(null);
    }
  };

  /** Every inventory page, not only the one the Inventory screen shows. */
  const exportInventory = () =>
    run('inventory', async () => {
      const items: InventoryResponse['items'] = [];
      let page = 1;
      let totalPages = 1;
      do {
        const response = await api.get<InventoryResponse>(`/inventory/nodes?page=${page}&pageSize=${EXPORT_PAGE_SIZE}`);
        items.push(...(response.items ?? []));
        totalPages = Math.max(1, response.totalPages ?? 1);
        setProgress(`${formatNumber(items.length)} / ${formatNumber(response.totalCount)}`);
        page += 1;
      } while (page <= totalPages);

      downloadCsv(`bermap-inventory-${today()}.csv`, [
        ['id', 'name', 'nodeType', 'fqdn', 'ipAddress', 'macAddress', 'status', 'isStale', 'discoverySource', 'sources', 'firstSeen', 'lastSeen'],
        ...items.map((node) => [
          node.id,
          node.name,
          node.nodeType,
          node.fqdn,
          node.ipAddress ?? '',
          node.macAddress ?? '',
          node.status,
          node.isStale,
          node.discoverySource,
          (node.sourceSystems ?? []).join(';'),
          node.firstSeen,
          node.lastSeen,
        ]),
      ]);
    });

  const exportAudit = () =>
    run('audit', () => downloadServerCsv('/audit/export', `bermap-audit-${today()}.csv`));

  const exportSnapshot = (snapshot: SnapshotSummary, part: 'nodes' | 'relationships') =>
    run(`snapshot:${snapshot.id}:${part}`, async () => {
      const graph = await api.get<SnapshotGraph>(`/snapshots/${snapshot.id}/graph`);
      const base = `bermap-snapshot-v${snapshot.snapshotVersion}-${slug(snapshot.name)}`;
      if (part === 'nodes') {
        downloadCsv(`${base}-nodes.csv`, [
          ['nodeId', 'name', 'nodeType', 'fqdn', 'ipAddress', 'macAddress', 'status', 'isStale', 'discoverySource', 'firstSeen', 'lastSeen'],
          ...graph.nodes.map((node) => [
            node.nodeId,
            node.name,
            node.nodeType,
            node.fqdn,
            node.ipAddress ?? '',
            node.macAddress ?? '',
            node.status,
            node.isStale,
            node.discoverySource,
            node.firstSeen,
            node.lastSeen,
          ]),
        ]);
      } else {
        downloadCsv(`${base}-relationships.csv`, [
          ['relationshipId', 'source', 'sourceNodeId', 'relationshipType', 'target', 'targetNodeId', 'confidence', 'sourceSystem', 'isStale', 'evidence'],
          ...graph.relationships.map((relationship) => [
            relationship.relationshipId,
            relationship.sourceNodeName,
            relationship.sourceNodeId,
            relationship.relationshipType,
            relationship.targetNodeName,
            relationship.targetNodeId,
            relationship.confidenceLevel,
            relationship.sourceSystem,
            relationship.isStale,
            relationship.evidence,
          ]),
        ]);
      }
    });

  const isBusy = busy !== null;

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      <div>
        <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
          {isTr ? 'Raporlar' : 'Reports'}
        </h1>
        <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1">
          {isTr
            ? 'Envanter, denetim kaydı ve snapshot verilerini CSV olarak dışa aktarın. Dosyalar UTF-8 (BOM) kodludur ve formül enjeksiyonuna karşı korunur.'
            : 'Export inventory, audit and snapshot data as CSV. Files are UTF-8 (BOM) encoded and protected against formula injection.'}
        </p>
      </div>

      {error && (
        <div
          role="alert"
          className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300 flex items-start gap-2"
        >
          <AlertTriangle className="w-4 h-4 mt-0.5 shrink-0" />
          <span className="break-words">{error}</span>
        </div>
      )}

      <div className={`grid grid-cols-1 ${mayViewAudit ? 'md:grid-cols-2' : ''} gap-4`}>
        <Card padding="sm" className="space-y-3">
          <h2 className="text-sm font-semibold flex items-center gap-2 text-neutral-900 dark:text-white">
            <FileText className="w-4 h-4 text-brand-primary dark:text-brand-primary-dark" />
            {isTr ? 'Envanter (tümü)' : 'Inventory (all)'}
          </h2>
          <p className="text-sm text-neutral-500 dark:text-neutral-400">
            {isTr
              ? 'Tüm sayfalardaki envanter kayıtlarını (eski kayıtlar dahil) tek bir CSV dosyasına yazar: tür, durum, FQDN, IP/MAC, kaynaklar, ilk ve son görülme.'
              : 'Writes inventory records from every page (stale ones included) to one CSV: type, status, FQDN, IP/MAC, sources, first and last seen.'}
          </p>
          <Button variant="primary" size="sm" className="gap-2" onClick={() => void exportInventory()} disabled={isBusy}>
            {busy === 'inventory' ? <Loader2 className="w-4 h-4 animate-spin" /> : <Download className="w-4 h-4" />}
            {isTr ? 'Envanter CSV' : 'Inventory CSV'}
            {busy === 'inventory' && progress && <span className="text-xs opacity-80">({progress})</span>}
          </Button>
        </Card>

        {mayViewAudit && (
          <Card padding="sm" className="space-y-3">
            <h2 className="text-sm font-semibold flex items-center gap-2 text-neutral-900 dark:text-white">
              <ScrollText className="w-4 h-4 text-brand-primary dark:text-brand-primary-dark" />
              {isTr ? 'Denetim Kaydı' : 'Audit Log'}
            </h2>
            <p className="text-sm text-neutral-500 dark:text-neutral-400">
              {isTr
                ? 'Son 50.000 denetim kaydını en yeniden eskiye CSV olarak indirir. Kullanıcı, işlem veya tarih filtresi için Denetim sayfasını kullanın.'
                : 'Downloads the latest 50,000 audit records, newest first, as CSV. Use the Audit page to filter by user, action or date.'}
            </p>
            <div className="flex flex-wrap items-center gap-2">
              <Button variant="primary" size="sm" className="gap-2" onClick={() => void exportAudit()} disabled={isBusy}>
                {busy === 'audit' ? <Loader2 className="w-4 h-4 animate-spin" /> : <Download className="w-4 h-4" />}
                {isTr ? 'Denetim CSV' : 'Audit CSV'}
              </Button>
              <Link to="/audit" className={buttonClassName('outline', 'sm', 'gap-1.5')}>
                <ExternalLink className="w-3.5 h-3.5" />
                {isTr ? 'Filtreli dışa aktarım' : 'Filtered export'}
              </Link>
            </div>
          </Card>
        )}
      </div>

      <Card padding="sm" className="space-y-3">
        <div>
          <h2 className="text-sm font-semibold flex items-center gap-2 text-neutral-900 dark:text-white">
            <Camera className="w-4 h-4 text-brand-primary dark:text-brand-primary-dark" />
            {isTr ? 'Snapshot Dışa Aktarımları' : 'Snapshot Exports'}
          </h2>
          <p className="text-xs text-neutral-500 dark:text-neutral-400 mt-1">
            {isTr
              ? 'Her değiştirilemez snapshot’ın düğüm ve ilişki listesini indirin; karşılaştırma ve doğrulama için Snapshot sayfasını açın.'
              : 'Download the node and relationship lists of each immutable snapshot; open the Snapshots page to compare or verify.'}
          </p>
        </div>
        {snapshots.isLoading && (
          <div className="flex items-center gap-2 text-sm text-neutral-500">
            <Loader2 className="w-4 h-4 animate-spin" />
            {isTr ? 'Yükleniyor...' : 'Loading...'}
          </div>
        )}
        {snapshots.isError && (
          <p className="text-sm text-rose-600 dark:text-rose-400">{getErrorMessage(snapshots.error)}</p>
        )}
        {(snapshots.data?.length ?? 0) === 0 && !snapshots.isLoading && !snapshots.isError && (
          <p className="text-sm text-neutral-500">
            {isTr ? 'Henüz snapshot yok.' : 'No snapshots yet.'}
          </p>
        )}
        <ul className="space-y-2">
          {(snapshots.data ?? []).map((snapshot) => (
            <li
              key={snapshot.id}
              className="flex flex-col sm:flex-row sm:items-center justify-between gap-2 sm:gap-3 p-3 rounded-lg border border-neutral-200 dark:border-dark-border"
            >
              <div className="min-w-0">
                <div className="font-medium text-sm truncate text-neutral-900 dark:text-white">{snapshot.name}</div>
                <div className="text-xs text-neutral-500">
                  v{snapshot.snapshotVersion} · {formatNumber(snapshot.nodeCount)} {isTr ? 'düğüm' : 'nodes'} ·{' '}
                  {formatNumber(snapshot.relationshipCount)} {isTr ? 'ilişki' : 'relationships'} · {formatDateTime(snapshot.createdAt)}
                </div>
              </div>
              <div className="flex flex-wrap items-center gap-1.5 shrink-0">
                <Button
                  variant="outline"
                  size="sm"
                  className="gap-1.5"
                  onClick={() => void exportSnapshot(snapshot, 'nodes')}
                  disabled={isBusy}
                >
                  {busy === `snapshot:${snapshot.id}:nodes` ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Download className="w-3.5 h-3.5" />}
                  {isTr ? 'Düğümler' : 'Nodes'}
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  className="gap-1.5"
                  onClick={() => void exportSnapshot(snapshot, 'relationships')}
                  disabled={isBusy}
                >
                  {busy === `snapshot:${snapshot.id}:relationships` ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Download className="w-3.5 h-3.5" />}
                  {isTr ? 'İlişkiler' : 'Relationships'}
                </Button>
                <Link
                  to={`/snapshots?id=${encodeURIComponent(snapshot.id)}`}
                  className={buttonClassName('ghost', 'sm', 'text-brand-primary dark:text-brand-primary-dark')}
                >
                  {isTr ? 'Detay' : 'Details'}
                </Link>
              </div>
            </li>
          ))}
        </ul>
      </Card>
    </div>
  );
};
