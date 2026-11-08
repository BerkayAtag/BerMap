import React, { useCallback, useEffect, useRef, useState } from 'react';
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query';
import { Card } from '@/components/ui/Card';
import { Button } from '@/components/ui/Button';
import { StatusIndicator } from '@/components/ui/StatusIndicator';
import {
  AlertTriangle,
  ChevronLeft,
  ChevronRight,
  Download,
  Filter,
  GitMerge,
  Info,
  Loader2,
  Play,
  RefreshCw,
  Search,
  Server,
  X,
} from 'lucide-react';
import { api } from '@/lib/api';
import {
  formatDateTime,
  formatNumber,
  formatProperty,
  formatRelativeTime,
  getErrorMessage,
  getErrorStatus,
  shortId,
  toNodeStatus,
} from '@/lib/format';
import { downloadCsv } from '@/lib/csv';
import { useLocale } from '@/lib/i18n';
import { useT } from '@/lib/i18n-runtime';
import { useEscapeKey } from '@/hooks/useEscapeKey';
import { useDialogFocus } from '@/hooks/useDialogFocus';
import { useCanOperate } from '@/stores/authStore';
import { canSimulate } from './simulation/scenarios';
import { Link, useSearchParams } from 'react-router-dom';
import type {
  InventoryNode,
  InventoryNodeDetail,
  InventoryPage as InventoryPageData,
  ReconciliationReview,
  ResolveReconciliationReviewRequest,
} from '@/types';

const PAGE_SIZE = 25;

interface NodeTypeOption {
  value: string;
  tr: string;
  en: string;
}

const NODE_TYPE_OPTIONS: NodeTypeOption[] = [
  { value: '', tr: 'Tüm varlık türleri', en: 'All entity types' },
  { value: 'Datacenter', tr: 'Datacenter', en: 'Datacenter' },
  { value: 'Site', tr: 'AD Site', en: 'AD Site' },
  { value: 'Forest', tr: 'AD Forest', en: 'AD Forest' },
  { value: 'Domain', tr: 'AD Domain', en: 'AD Domain' },
  { value: 'OrganizationalUnit', tr: 'Organizational Unit', en: 'Organizational Unit' },
  { value: 'Cluster', tr: 'Cluster', en: 'Cluster' },
  { value: 'Hypervisor', tr: 'ESXi Hypervisor', en: 'ESXi Hypervisor' },
  { value: 'VirtualMachine', tr: 'Virtual Machine', en: 'Virtual Machine' },
  { value: 'PhysicalServer', tr: 'Physical Server', en: 'Physical Server' },
  { value: 'Computer', tr: 'Computer', en: 'Computer' },
  { value: 'DomainController', tr: 'Domain Controller', en: 'Domain Controller' },
  { value: 'Storage', tr: 'Storage', en: 'Storage' },
  { value: 'Datastore', tr: 'Datastore', en: 'Datastore' },
  { value: 'NetworkDevice', tr: 'Network Device', en: 'Network Device' },
  { value: 'Network', tr: 'Network', en: 'Network' },
  { value: 'ResourcePool', tr: 'Resource Pool', en: 'Resource Pool' },
  { value: 'Group', tr: 'AD Group', en: 'AD Group' },
  { value: 'Application', tr: 'Application', en: 'Application' },
  { value: 'Service', tr: 'Service', en: 'Service' },
  { value: 'Database', tr: 'Database', en: 'Database' },
  { value: 'Endpoint', tr: 'Endpoint', en: 'Endpoint' },
];

const STATUS_LABELS: Record<string, { tr: string; en: string }> = {
  healthy: { tr: 'Sağlıklı', en: 'Healthy' },
  warning: { tr: 'Uyarı', en: 'Warning' },
  critical: { tr: 'Kritik', en: 'Critical' },
  unknown: { tr: 'Bilinmiyor', en: 'Unknown' },
};

const REVIEW_STATUS_LABELS: Record<string, { tr: string; en: string }> = {
  Pending: { tr: 'İnceleme Bekliyor', en: 'Pending Review' },
  Merged: { tr: 'Birleştirildi', en: 'Merged' },
  Dismissed: { tr: 'Yok Sayıldı', en: 'Dismissed' },
};

type StaleFilter = 'all' | 'current' | 'stale';

function findProperty(node: InventoryNode, keys: string[]): string | null {
  const lowered = keys.map((k) => k.toLowerCase());
  const entry = Object.entries(node.properties ?? {}).find(([key]) =>
    lowered.includes(key.toLowerCase()),
  );
  return entry ? formatProperty(entry[1]) : null;
}

interface FieldDisplayProps {
  label: string;
  value?: string | null;
  mono?: boolean;
  /** Raw English keys (source properties): uppercase them with English casing rules. */
  englishLabel?: boolean;
}

function FieldDisplay({ label, value, mono = false, englishLabel = false }: FieldDisplayProps) {
  return (
    <div>
      <dt lang={englishLabel ? 'en' : undefined} className="text-[11px] uppercase tracking-wide text-neutral-500">
        {label}
      </dt>
      <dd className={`mt-0.5 text-sm text-neutral-900 dark:text-neutral-100 break-words ${mono ? 'font-mono text-xs' : ''}`}>
        {value || '—'}
      </dd>
    </div>
  );
}

interface ReviewNodeLinkProps {
  id?: string | null;
  name: string;
  currentId: string;
  onOpen: (id: string) => void;
}

/**
 * A node named in a reconciliation review. After a merge or dismissal the other
 * record (and its PossibleDuplicate edge) may be gone; opening it then shows the
 * modal's not-found state instead of failing.
 */
function ReviewNodeLink({ id, name, currentId, onOpen }: ReviewNodeLinkProps) {
  const label = name || (id ? shortId(id) : '—');
  if (!id || id === currentId) {
    return <div className="font-medium text-neutral-800 dark:text-neutral-200">{label}</div>;
  }
  return (
    <button
      type="button"
      onClick={() => onOpen(id)}
      className="font-medium text-left text-brand-primary dark:text-brand-primary-dark hover:underline"
    >
      {label}
    </button>
  );
}

export const InventoryPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const t = useT();
  const queryClient = useQueryClient();
  const canOperate = useCanOperate();
  const [searchParams, setSearchParams] = useSearchParams();

  // The search text (?q=), the open entity (?node=) and the review tab (?review=1)
  // live in the URL, so reloading or sharing the page keeps them.
  const query = searchParams.get('q')?.trim() ?? '';
  const selectedNodeId = searchParams.get('node') || null;
  const needsReview = searchParams.get('review') === '1';

  const [queryInput, setQueryInput] = useState(query);
  const [nodeType, setNodeType] = useState('');
  const [status, setStatus] = useState('');
  const [staleFilter, setStaleFilter] = useState<StaleFilter>('all');
  const [page, setPage] = useState(1);
  const [mergeTargets, setMergeTargets] = useState<Record<string, string>>({});
  const [resolutionNotes, setResolutionNotes] = useState<Record<string, string>>({});
  const [resolvingReviewId, setResolvingReviewId] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  // URL updates made by this page itself; any other change of the query string
  // is a deep link (global search, dashboard, map, back/forward).
  const ownSearch = useRef<string | null>(null);
  const updateParams = useCallback(
    (mutate: (params: URLSearchParams) => void) => {
      setSearchParams(
        (current) => {
          const next = new URLSearchParams(current);
          mutate(next);
          // An unchanged URL triggers no effect run, so only real changes are marked.
          if (next.toString() !== current.toString()) ownSearch.current = next.toString();
          return next;
        },
        { replace: true },
      );
    },
    [setSearchParams],
  );

  const searchKey = searchParams.toString();
  useEffect(() => {
    if (ownSearch.current === searchKey) {
      ownSearch.current = null;
      return;
    }
    // A deep link starts a fresh search: the link's own text, no leftover filters.
    const linked = new URLSearchParams(searchKey);
    setQueryInput(linked.get('q') ?? '');
    setNodeType('');
    setStatus('');
    setStaleFilter('all');
    setPage(1);
  }, [searchKey]);

  const setSelectedNodeId = useCallback(
    (id: string | null) =>
      updateParams((params) => {
        if (id) params.set('node', id);
        else params.delete('node');
      }),
    [updateParams],
  );

  // Debounce the search box; the page is reset in the same update so a filter
  // change triggers exactly one request.
  useEffect(() => {
    const timer = window.setTimeout(() => {
      const next = queryInput.trim();
      if (next !== query) {
        updateParams((params) => {
          if (next) params.set('q', next);
          else params.delete('q');
        });
        setPage(1);
      }
    }, 350);
    return () => window.clearTimeout(timer);
  }, [queryInput, query, updateParams]);

  const changeNodeType = (value: string) => {
    setNodeType(value);
    setPage(1);
  };
  const changeStatus = (value: string) => {
    setStatus(value);
    setPage(1);
  };
  const changeNeedsReview = (value: boolean) => {
    updateParams((params) => {
      if (value) params.set('review', '1');
      else params.delete('review');
    });
    setPage(1);
  };
  const changeStaleFilter = (value: StaleFilter) => {
    setStaleFilter(value);
    setPage(1);
  };

  useEscapeKey(selectedNodeId !== null, () => setSelectedNodeId(null));
  const detailDialogRef = useRef<HTMLDivElement>(null);
  useDialogFocus(detailDialogRef, selectedNodeId !== null, { initialFocus: 'container' });

  const parameters = new URLSearchParams({
    page: String(page),
    pageSize: String(PAGE_SIZE),
  });
  if (query) parameters.set('query', query);
  if (nodeType) parameters.set('nodeType', nodeType);
  if (status) parameters.set('status', status);
  if (needsReview) parameters.set('needsReview', 'true');
  if (staleFilter === 'stale') parameters.set('isStale', 'true');
  if (staleFilter === 'current') parameters.set('isStale', 'false');

  const nodesQuery = useQuery<InventoryPageData>({
    queryKey: ['inventory', 'nodes', { query, nodeType, status, needsReview, staleFilter, page }],
    queryFn: () => api.get<InventoryPageData>(`/inventory/nodes?${parameters.toString()}`),
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  });

  const nodeDetailQuery = useQuery<InventoryNodeDetail>({
    queryKey: ['inventory', 'node', selectedNodeId],
    queryFn: () => api.get<InventoryNodeDetail>(`/inventory/nodes/${encodeURIComponent(selectedNodeId ?? '')}`),
    enabled: !!selectedNodeId,
    staleTime: 5_000,
    // An unknown or malformed id will not appear by retrying.
    retry: (failureCount, error) => {
      const status = getErrorStatus(error);
      if (status !== undefined && status >= 400 && status < 500) return false;
      return failureCount < 2;
    },
  });

  const data = nodesQuery.data;
  const items = data?.items ?? [];
  const pendingReviewCount = data?.pendingReviewCount ?? 0;
  const hasFilters =
    query.length > 0 || nodeType.length > 0 || status.length > 0 || needsReview || staleFilter !== 'all';

  const handleRefresh = useCallback(async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['inventory', 'nodes'] }),
      selectedNodeId
        ? queryClient.invalidateQueries({ queryKey: ['inventory', 'node', selectedNodeId] })
        : Promise.resolve(),
    ]);
  }, [queryClient, selectedNodeId]);

  const handleClearFilters = () => {
    setQueryInput('');
    updateParams((params) => {
      params.delete('q');
      params.delete('review');
    });
    setNodeType('');
    setStatus('');
    setStaleFilter('all');
    setPage(1);
  };

  const handleExportCsv = () => {
    if (items.length === 0) return;
    const header = [
      'Name',
      'FQDN',
      'Node Type',
      'Operating System',
      'IP Address',
      'MAC Address',
      'Status',
      'Source Count',
      'Source Systems',
      'BIOS UUID',
      'VM Instance UUID',
      'Domain SID',
      'First Seen',
      'Last Seen',
      'Is Stale',
      'Needs Review',
    ];
    const rows = items.map((node) => [
      node.name,
      node.fqdn,
      node.nodeType,
      findProperty(node, ['OperatingSystem', 'OSName', 'GuestOS', 'os']) ?? '',
      node.ipAddress ?? '',
      node.macAddress ?? '',
      node.status,
      node.sourceCount,
      node.sourceSystems.join(' + '),
      node.biosUuid,
      node.vmInstanceUuid,
      node.domainSid,
      node.firstSeen,
      node.lastSeen,
      node.isStale ? 'yes' : 'no',
      node.needsReview ? 'yes' : 'no',
    ]);
    // Formula-safe cells and a UTF-8 BOM (discovery data may start with =, +, - or @).
    downloadCsv(`bermap-inventory-${new Date().toISOString().slice(0, 10)}.csv`, [header, ...rows]);
  };

  const handleResolveReview = async (
    review: ReconciliationReview,
    resolution: 'Merged' | 'Dismissed',
  ) => {
    const candidateIds = Array.from(
      new Set([review.candidateNodeId, ...review.candidateNodeIds].filter((id): id is string => !!id)),
    );
    const targetNodeId = mergeTargets[review.id] ?? review.candidateNodeId ?? candidateIds[0];

    if (resolution === 'Merged' && !targetNodeId) {
      setActionError(isTr ? 'Birleştirme için hedef düğüm bulunamadı.' : 'No target node found for merge.');
      return;
    }

    setResolvingReviewId(review.id);
    setActionError(null);
    setNotice(null);
    try {
      const payload: ResolveReconciliationReviewRequest = {
        resolution,
        notes: resolutionNotes[review.id]?.trim() || undefined,
      };
      if (resolution === 'Merged' && targetNodeId) {
        payload.targetNodeId = targetNodeId;
      }
      await api.post(`/inventory/reviews/${review.id}/resolve`, payload);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['inventory', 'nodes'] }),
        queryClient.invalidateQueries({ queryKey: ['inventory', 'node'] }),
        queryClient.invalidateQueries({ queryKey: ['inventory', 'reviews'] }),
        queryClient.invalidateQueries({ queryKey: ['dashboard'] }),
      ]);
      if (resolution === 'Merged') {
        setSelectedNodeId(null);
        setNotice(
          isTr
            ? 'Uzlaştırma incelemesi birleştirildi. Envanter yeniden yüklendi.'
            : 'Reconciliation review merged. Inventory reloaded.',
        );
      } else {
        setNotice(
          isTr
            ? 'Uzlaştırma incelemesi yok sayıldı; iki kayıt arasındaki olası yinelenen ilişkisi kaldırıldı.'
            : 'Reconciliation review dismissed; the possible-duplicate link between the two records was removed.',
        );
      }
    } catch (error) {
      setActionError(getErrorMessage(error));
    } finally {
      setResolvingReviewId(null);
    }
  };

  const nodesError = nodesQuery.isError ? getErrorMessage(nodesQuery.error) : null;
  const detailStatus = nodeDetailQuery.isError ? getErrorStatus(nodeDetailQuery.error) : undefined;
  // 404 (unknown id) and 400 (malformed id) both mean the linked entity does not exist.
  const detailNotFound = detailStatus === 404 || detailStatus === 400;
  const detailError = nodeDetailQuery.isError && !detailNotFound ? getErrorMessage(nodeDetailQuery.error) : null;
  // Keep showing the previous node only while it matches the URL.
  const detail = nodeDetailQuery.data?.node.id === selectedNodeId ? nodeDetailQuery.data : undefined;

  const firstRecordIndex = data && data.totalCount > 0 ? (data.page - 1) * data.pageSize + 1 : 0;
  const lastRecordIndex = data ? Math.min(data.page * data.pageSize, data.totalCount) : 0;

  const statusLabel = (value: string): string => {
    const labels = STATUS_LABELS[value];
    if (labels) return isTr ? labels.tr : labels.en;
    return value;
  };

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      {/* Page header */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
              {isTr ? 'Altyapı Envanteri & Uzlaştırma' : 'Infrastructure Inventory & Reconciliation'}
            </h1>
            <span className="text-xs px-2.5 py-0.5 rounded-full bg-brand-primary/10 text-brand-primary dark:text-brand-primary-dark font-bold">
              {t('multiSourceReconciled')}
            </span>
          </div>
          <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1">
            {isTr
              ? 'Active Directory ve vCenter keşiflerinin BIOS UUID, FQDN, domain SID ve IP/MAC kanıtlarıyla uzlaştırılmış kurumsal görünümü'
              : 'Enterprise view reconciled from Active Directory and vCenter discoveries using BIOS UUID, FQDN, domain SID and IP/MAC evidence'}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Button variant="outline" size="sm" className="gap-2" onClick={() => void handleRefresh()} disabled={nodesQuery.isFetching}>
            <RefreshCw className={`w-4 h-4 ${nodesQuery.isFetching ? 'animate-spin' : ''}`} />
            {isTr ? 'Yenile' : 'Refresh'}
          </Button>
          <Button variant="outline" size="sm" className="gap-2" onClick={handleExportCsv} disabled={items.length === 0}>
            <Download className="w-4 h-4" />
            {isTr ? 'Sayfayı CSV Dışa Aktar' : 'Export Page as CSV'}
          </Button>
        </div>
      </div>

      {/* Notices */}
      {notice && (
        <div
          role="status"
          className="p-3 rounded-lg border border-emerald-200 dark:border-emerald-900 bg-emerald-50 dark:bg-emerald-950/30 text-sm text-emerald-800 dark:text-emerald-300 flex items-start gap-2"
        >
          <Info className="w-4 h-4 mt-0.5 shrink-0" />
          <span className="flex-1">{notice}</span>
          <button
            type="button"
            onClick={() => setNotice(null)}
            aria-label={isTr ? 'Bildirimi kapat' : 'Close notification'}
          >
            <X className="w-4 h-4" />
          </button>
        </div>
      )}
      {actionError && (
        <div
          role="alert"
          className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-800 dark:text-rose-300 flex items-start gap-2"
        >
          <AlertTriangle className="w-4 h-4 mt-0.5 shrink-0" />
          <span className="flex-1">{actionError}</span>
          <button
            type="button"
            onClick={() => setActionError(null)}
            aria-label={isTr ? 'Hatayı kapat' : 'Close error'}
          >
            <X className="w-4 h-4" />
          </button>
        </div>
      )}

      {/* KPI cards */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Toplam Varlık' : 'Total Entities'}</div>
          <div className="mt-1 text-2xl font-bold text-neutral-900 dark:text-white">
            {formatNumber(data?.totalCount)}
          </div>
          <div className="text-[11px] text-neutral-500 mt-1">
            {isTr ? 'Aktif filtre sonucu' : 'Result of active filters'}
          </div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">
            {isTr ? 'Uzlaştırılmış Varlıklar' : 'Reconciled Entities'}
          </div>
          <div className="mt-1 text-2xl font-bold text-emerald-600 dark:text-emerald-400">
            {formatNumber(data?.reconciledCount)}
          </div>
          <div className="text-[11px] text-neutral-500 mt-1">
            {isTr ? 'Birden fazla doğrulanmış kaynak' : 'Multiple verified sources'}
          </div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Tek Kaynak' : 'Single Source'}</div>
          <div className="mt-1 text-2xl font-bold text-neutral-900 dark:text-white">
            {formatNumber(data?.singleSourceCount)}
          </div>
          <div className="text-[11px] text-neutral-500 mt-1">
            {isTr ? 'Yalnızca bir keşif kaynağı' : 'Only one discovery source'}
          </div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'İnceleme Gerekli' : 'Needs Review'}</div>
          <div className="mt-1 text-2xl font-bold text-amber-600 dark:text-amber-400">
            {formatNumber(pendingReviewCount)}
          </div>
          <div className="text-[11px] text-neutral-500 mt-1">
            {isTr ? 'Belirsiz veya çakışan eşleşme' : 'Ambiguous or conflicting match'}
          </div>
        </Card>
      </div>

      {/* Tabs */}
      <div className="flex flex-wrap items-center gap-2 border-b border-neutral-200 dark:border-dark-border pb-3">
        <button
          type="button"
          onClick={() => changeNeedsReview(false)}
          className={`px-3 py-1.5 rounded-lg text-xs font-semibold transition-all ${
            needsReview
              ? 'text-neutral-600 dark:text-neutral-400 hover:bg-neutral-100 dark:hover:bg-neutral-800'
              : 'bg-brand-primary text-white shadow-xs'
          }`}
        >
          {isTr ? 'Tüm Varlıklar' : 'All Entities'}
        </button>
        <button
          type="button"
          onClick={() => changeNeedsReview(true)}
          className={`px-3 py-1.5 rounded-lg text-xs font-semibold transition-all ${
            needsReview
              ? 'bg-brand-primary text-white shadow-xs'
              : 'text-neutral-600 dark:text-neutral-400 hover:bg-neutral-100 dark:hover:bg-neutral-800'
          }`}
        >
          {isTr ? `İnceleme Gerekli (${formatNumber(pendingReviewCount)})` : `Needs Review (${formatNumber(pendingReviewCount)})`}
        </button>
        {hasFilters && (
          <button
            type="button"
            onClick={handleClearFilters}
            className="ml-auto text-xs text-brand-primary dark:text-brand-primary-dark hover:underline"
          >
            {isTr ? 'Filtreleri Temizle' : 'Clear Filters'}
          </button>
        )}
      </div>

      {/* Filters + table */}
      <Card className="p-5">
        <div className="flex flex-wrap items-end gap-3 mb-4">
          <div className="flex items-center gap-2 px-3 py-2 rounded-lg bg-neutral-100 dark:bg-dark-border text-sm w-full sm:w-80">
            <Search className="w-4 h-4 text-neutral-400 shrink-0" />
            <input
              type="text"
              value={queryInput}
              onChange={(event) => setQueryInput(event.target.value)}
              placeholder={
                isTr
                  ? 'FQDN, IP, MAC, BIOS UUID veya domain SID ara...'
                  : 'Search FQDN, IP, MAC, BIOS UUID or domain SID...'
              }
              className="bg-transparent text-sm focus:outline-none w-full text-neutral-800 dark:text-neutral-200 placeholder:text-neutral-400"
            />
            {queryInput && (
              <button
                type="button"
                onClick={() => setQueryInput('')}
                className="text-neutral-400 hover:text-neutral-600 dark:hover:text-neutral-200"
                aria-label={isTr ? 'Aramayı temizle' : 'Clear search'}
              >
                <X className="w-4 h-4" />
              </button>
            )}
          </div>

          <label className="text-xs text-neutral-500">
            <span className="sr-only">{isTr ? 'Varlık türü' : 'Entity type'}</span>
            <select
              value={nodeType}
              onChange={(event) => changeNodeType(event.target.value)}
              className="h-9 rounded-lg border border-neutral-300 dark:border-dark-border bg-white dark:bg-dark-card px-3 text-sm text-neutral-800 dark:text-neutral-200 focus:outline-none focus:ring-2 focus:ring-brand-primary/20"
            >
              {NODE_TYPE_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {isTr ? option.tr : option.en}
                </option>
              ))}
            </select>
          </label>

          <label className="text-xs text-neutral-500">
            <span className="sr-only">{isTr ? 'Durum' : 'Status'}</span>
            <select
              value={status}
              onChange={(event) => changeStatus(event.target.value)}
              className="h-9 rounded-lg border border-neutral-300 dark:border-dark-border bg-white dark:bg-dark-card px-3 text-sm text-neutral-800 dark:text-neutral-200 focus:outline-none focus:ring-2 focus:ring-brand-primary/20"
            >
              <option value="">{isTr ? 'Tüm durumlar' : 'All statuses'}</option>
              <option value="healthy">{statusLabel('healthy')}</option>
              <option value="warning">{statusLabel('warning')}</option>
              <option value="critical">{statusLabel('critical')}</option>
              <option value="unknown">{statusLabel('unknown')}</option>
            </select>
          </label>

          <label className="text-xs text-neutral-500">
            <span className="sr-only">{isTr ? 'Bayatlık' : 'Staleness'}</span>
            <select
              value={staleFilter}
              onChange={(event) => changeStaleFilter(event.target.value as StaleFilter)}
              className="h-9 rounded-lg border border-neutral-300 dark:border-dark-border bg-white dark:bg-dark-card px-3 text-sm text-neutral-800 dark:text-neutral-200 focus:outline-none focus:ring-2 focus:ring-brand-primary/20"
            >
              <option value="all">{isTr ? 'Tüm kayıtlar' : 'All records'}</option>
              <option value="current">{isTr ? 'Güncel' : 'Current'}</option>
              <option value="stale">{isTr ? 'Bayat' : 'Stale'}</option>
            </select>
          </label>

          <span className="text-[11px] text-neutral-500 ml-auto flex items-center gap-1">
            <Filter className="w-3.5 h-3.5" />
            {data
              ? `${formatNumber(data.totalCount)} ${isTr ? 'kayıt' : 'records'}`
              : isTr
                ? 'Yükleniyor...'
                : 'Loading...'}
          </span>
        </div>

        {nodesError && (
          <div className="mb-4 p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300 flex items-center justify-between gap-3">
            <span className="flex items-center gap-2">
              <AlertTriangle className="w-4 h-4" />
              {nodesError}
            </span>
            <Button variant="outline" size="sm" onClick={() => void nodesQuery.refetch()}>
              {isTr ? 'Tekrar Dene' : 'Retry'}
            </Button>
          </div>
        )}

        <div className="overflow-x-auto">
          <table className="w-full text-left text-sm">
            <thead>
              <tr className="border-b border-neutral-200 dark:border-dark-border text-[11px] uppercase font-semibold text-neutral-400 dark:text-neutral-500">
                <th className="pb-3 pl-1">{isTr ? 'Ad / FQDN' : 'Name / FQDN'}</th>
                <th className="pb-3">{isTr ? 'Tür' : 'Type'}</th>
                <th className="pb-3">{isTr ? 'İşletim Sistemi' : 'Operating System'}</th>
                <th className="pb-3">IP</th>
                <th className="pb-3">{isTr ? 'Kaynaklar' : 'Sources'}</th>
                <th className="pb-3">{isTr ? 'Son Görülme' : 'Last Seen'}</th>
                <th className="pb-3">{isTr ? 'Uzlaştırma' : 'Reconciliation'}</th>
                <th className="pb-3 text-right pr-1">{isTr ? 'Durum' : 'Status'}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-neutral-100 dark:divide-dark-border">
              {nodesQuery.isLoading
                ? Array.from({ length: 6 }).map((_, index) => (
                    <tr key={index}>
                      <td colSpan={8} className="py-3">
                        <div className="h-8 rounded bg-neutral-100 dark:bg-dark-border animate-pulse" />
                      </td>
                    </tr>
                  ))
                : items.length === 0
                  ? (
                    <tr>
                      <td colSpan={8} className="py-12 text-center text-sm text-neutral-500 dark:text-neutral-400">
                        <Server className="w-8 h-8 mx-auto text-neutral-300 dark:text-neutral-600" />
                        <p className="mt-2 font-medium text-neutral-700 dark:text-neutral-300">
                          {isTr ? 'Eşleşen varlık bulunamadı' : 'No matching entities found'}
                        </p>
                        <p className="mt-1">
                          {isTr
                            ? 'Keşif tamamlandıysa filtreleri temizleyin veya entegrasyon sayfasından yeni bir keşif başlatın.'
                            : 'If discovery is complete, clear filters or start a new discovery from the Integrations page.'}
                        </p>
                        {hasFilters && (
                          <Button variant="outline" size="sm" className="mt-3" onClick={handleClearFilters}>
                            {isTr ? 'Filtreleri Temizle' : 'Clear Filters'}
                          </Button>
                        )}
                      </td>
                    </tr>
                  )
                  : items.map((node) => {
                      const operatingSystem = findProperty(node, ['OperatingSystem', 'OSName', 'GuestOS', 'os']);
                      return (
                        <tr
                          key={node.id}
                          onClick={() => {
                            setSelectedNodeId(node.id);
                            setActionError(null);
                          }}
                          onKeyDown={(event) => {
                            if (event.key === 'Enter' || event.key === ' ') {
                              event.preventDefault();
                              setSelectedNodeId(node.id);
                              setActionError(null);
                            }
                          }}
                          tabIndex={0}
                          className="hover:bg-neutral-50 dark:hover:bg-neutral-800/40 cursor-pointer transition-colors focus:outline-none focus:bg-neutral-50 dark:focus:bg-neutral-800/40"
                        >
                          <td className="py-3 pl-1">
                            <div className="flex items-center gap-2 min-w-0">
                              <Server className="w-4 h-4 text-neutral-500 shrink-0" />
                              <div className="min-w-0">
                                <div className="font-medium text-neutral-900 dark:text-white truncate max-w-64">
                                  {node.name}
                                </div>
                                <div className="text-[11px] font-mono text-neutral-500 truncate max-w-64">
                                  {node.fqdn || node.id}
                                </div>
                                {node.isStale && (
                                  <span className="inline-flex mt-1 text-[10px] px-1.5 py-0.5 rounded bg-amber-50 dark:bg-amber-950/30 text-amber-700 dark:text-amber-400 font-semibold">
                                    {isTr ? 'Bayat' : 'Stale'}
                                  </span>
                                )}
                              </div>
                            </div>
                          </td>
                          <td lang="en" className="py-3 text-neutral-600 dark:text-neutral-300 text-xs">{node.nodeType}</td>
                          <td className="py-3 text-neutral-600 dark:text-neutral-300 text-xs">{operatingSystem || '—'}</td>
                          <td className="py-3 font-mono text-xs text-neutral-600 dark:text-neutral-400">
                            {node.ipAddress || '—'}
                          </td>
                          <td className="py-3">
                            <div className="flex flex-wrap gap-1 max-w-48">
                              {node.sourceSystems.length > 0 ? (
                                <>
                                  {node.sourceSystems.slice(0, 2).map((system) => (
                                    <span
                                      key={system}
                                      className="text-[10px] font-semibold px-2 py-0.5 rounded bg-brand-primary/10 text-brand-primary dark:text-brand-primary-dark"
                                    >
                                      {system}
                                    </span>
                                  ))}
                                  {node.sourceSystems.length > 2 && (
                                    <span className="text-[10px] text-neutral-500">
                                      +{node.sourceSystems.length - 2}
                                    </span>
                                  )}
                                </>
                              ) : (
                                <span className="text-[10px] text-neutral-500">
                                  {node.discoverySource || (isTr ? 'Kaynak yok' : 'No source')}
                                </span>
                              )}
                            </div>
                          </td>
                          <td className="py-3 text-xs text-neutral-600 dark:text-neutral-300">
                            <span title={formatDateTime(node.lastSeen)}>{formatRelativeTime(node.lastSeen)}</span>
                          </td>
                          <td className="py-3">
                            {node.needsReview ? (
                              <span className="inline-flex items-center gap-1 text-[11px] font-bold text-amber-600 dark:text-amber-400">
                                <AlertTriangle className="w-3.5 h-3.5" />
                                {isTr ? ' İnceleme Gerekli' : ' Needs Review'}
                              </span>
                            ) : node.sourceCount > 1 ? (
                              <span className="inline-flex items-center gap-1 text-[11px] font-medium text-emerald-600 dark:text-emerald-400">
                                <GitMerge className="w-3.5 h-3.5" />
                                {isTr ? ' Uzlaştırıldı' : ' Reconciled'}
                              </span>
                            ) : (
                              <span className="text-[11px] text-neutral-500">
                                {isTr ? 'Tek Kaynak' : 'Single Source'}
                              </span>
                            )}
                          </td>
                          <td className="py-3 text-right pr-1">
                            <div className="inline-flex items-center gap-1.5">
                              <StatusIndicator status={toNodeStatus(node.status)} size="sm" />
                              <span className="text-xs text-neutral-600 dark:text-neutral-400">
                                {statusLabel(node.status)}
                              </span>
                            </div>
                          </td>
                        </tr>
                      );
                    })}
            </tbody>
          </table>
        </div>

        {data && data.totalPages > 1 && (
          <div className="mt-4 pt-4 border-t border-neutral-200 dark:border-dark-border flex flex-wrap items-center justify-between gap-3">
            <span className="text-xs text-neutral-500">
              {formatNumber(firstRecordIndex)}–{formatNumber(lastRecordIndex)} / {formatNumber(data.totalCount)}
            </span>
            <div className="flex items-center gap-2">
              <Button
                variant="outline"
                size="sm"
                className="gap-1"
                onClick={() => setPage((current) => Math.max(1, current - 1))}
                disabled={data.page <= 1 || nodesQuery.isFetching}
              >
                <ChevronLeft className="w-4 h-4" />
                {isTr ? 'Önceki' : 'Previous'}
              </Button>
              <span className="text-xs text-neutral-500">
                {isTr ? 'Sayfa' : 'Page'} {data.page} / {Math.max(1, data.totalPages)}
              </span>
              <Button
                variant="outline"
                size="sm"
                className="gap-1"
                onClick={() => setPage((current) => Math.min(data.totalPages, current + 1))}
                disabled={data.page >= data.totalPages || nodesQuery.isFetching}
              >
                {isTr ? 'Sonraki' : 'Next'}
                <ChevronRight className="w-4 h-4" />
              </Button>
            </div>
          </div>
        )}
      </Card>

      {/* Detail modal */}
      {selectedNodeId && (
        <div
          ref={detailDialogRef}
          className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60 backdrop-blur-xs outline-none"
          role="dialog"
          aria-modal="true"
          aria-label={isTr ? 'Varlık detayı' : 'Entity detail'}
          onClick={() => setSelectedNodeId(null)}
        >
          <Card
            className="w-full max-w-5xl max-h-[90vh] overflow-y-auto border-neutral-200 dark:border-dark-border shadow-2xl"
            padding="none"
            onClick={(event) => event.stopPropagation()}
          >
            <div className="sticky top-0 z-10 flex items-start justify-between gap-4 p-5 border-b border-neutral-200 dark:border-dark-border bg-white dark:bg-dark-card">
              <div className="flex items-start gap-3 min-w-0">
                <div className="p-2 rounded-lg bg-brand-primary/10 text-brand-primary dark:text-brand-primary-dark">
                  <Server className="w-5 h-5" />
                </div>
                <div className="min-w-0">
                  <h2 className="font-bold text-lg text-neutral-900 dark:text-white truncate">
                    {detail?.node.name ??
                      (detailNotFound
                        ? isTr ? 'Varlık bulunamadı' : 'Entity not found'
                        : detailError
                          ? isTr ? 'Varlık detayı alınamadı' : 'Entity detail unavailable'
                          : isTr ? 'Varlık detayı yükleniyor...' : 'Loading entity detail...')}
                  </h2>
                  <p className="text-xs text-neutral-500 font-mono truncate">
                    {detail?.node.fqdn || selectedNodeId}
                  </p>
                </div>
              </div>
              <button
                type="button"
                onClick={() => setSelectedNodeId(null)}
                className="p-1 rounded-lg text-neutral-400 hover:text-neutral-600 dark:hover:text-neutral-200"
                data-dialog-close
                aria-label={isTr ? 'Detayı kapat' : 'Close detail'}
              >
                <X className="w-5 h-5" />
              </button>
            </div>

            <div className="p-5 space-y-6">
              {actionError && (
                <div className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-xs text-rose-700 dark:text-rose-300 flex items-start gap-2">
                  <AlertTriangle className="w-4 h-4 mt-0.5 shrink-0" />
                  <span>{actionError}</span>
                </div>
              )}

              {nodeDetailQuery.isLoading ? (
                <div className="space-y-3">
                  <div className="h-24 rounded-lg bg-neutral-100 dark:bg-dark-border animate-pulse" />
                  <div className="h-32 rounded-lg bg-neutral-100 dark:bg-dark-border animate-pulse" />
                </div>
              ) : detailNotFound ? (
                <div className="p-6 rounded-lg border border-neutral-200 dark:border-dark-border bg-neutral-50 dark:bg-dark-bg text-center">
                  <Server className="w-8 h-8 mx-auto text-neutral-300 dark:text-neutral-600" aria-hidden="true" />
                  <p className="mt-2 text-sm font-medium text-neutral-800 dark:text-neutral-200">
                    {isTr ? 'Bu bağlantıdaki varlık envanterde yok.' : 'The entity in this link is not in the inventory.'}
                  </p>
                  <p className="mt-1 text-xs text-neutral-500">
                    {isTr
                      ? 'Kayıt birleştirilmiş veya silinmiş olabilir. Listeden arayarak bulabilirsiniz.'
                      : 'The record may have been merged or removed. Search the list to find it.'}
                  </p>
                  <Button variant="outline" size="sm" className="mt-3" onClick={() => setSelectedNodeId(null)}>
                    {isTr ? 'Listeye dön' : 'Back to the list'}
                  </Button>
                </div>
              ) : detailError ? (
                <div className="p-4 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300">
                  <div className="flex items-center gap-2">
                    <AlertTriangle className="w-4 h-4" />
                    {detailError}
                  </div>
                  <Button
                    variant="outline"
                    size="sm"
                    className="mt-3"
                    onClick={() => void nodeDetailQuery.refetch()}
                  >
                    {isTr ? 'Tekrar Dene' : 'Retry'}
                  </Button>
                </div>
              ) : detail ? (
                <>
                  <div className="flex flex-wrap items-center gap-2">
                    <span
                      className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-semibold ${
                        detail.node.isStale
                          ? 'bg-amber-50 dark:bg-amber-950/30 text-amber-700 dark:text-amber-400'
                          : 'bg-emerald-50 dark:bg-emerald-950/30 text-emerald-700 dark:text-emerald-400'
                      }`}
                    >
                      <StatusIndicator status={toNodeStatus(detail.node.status)} size="sm" />
                      {statusLabel(detail.node.status)}
                    </span>
                    <span lang="en" className="text-xs px-2.5 py-1 rounded-full bg-neutral-100 dark:bg-dark-border text-neutral-600 dark:text-neutral-400">
                      {detail.node.nodeType}
                    </span>
                    <span className="text-xs px-2.5 py-1 rounded-full bg-brand-primary/10 text-brand-primary dark:text-brand-primary-dark">
                      {detail.node.sourceCount}{' '}
                      {isTr ? 'doğrulanmış kaynak' : 'verified sources'}
                    </span>
                    {detail.node.needsReview && (
                      <span className="text-xs px-2.5 py-1 rounded-full bg-amber-50 dark:bg-amber-950/30 text-amber-700 dark:text-amber-400 font-semibold">
                        {isTr ? 'Uzlaştırma incelemesi bekliyor' : 'Awaiting reconciliation review'}
                      </span>
                    )}
                    {/* Only node types a scenario can fail; others would open an empty Studio. */}
                    {canSimulate(detail.node.nodeType) && (
                      <Link
                        to={`/simulation?target=${encodeURIComponent(detail.node.id)}`}
                        className="ml-auto inline-flex items-center gap-1.5 text-xs font-semibold text-brand-primary dark:text-brand-primary-dark hover:underline"
                      >
                        <Play className="w-3.5 h-3.5" aria-hidden="true" />
                        {isTr ? 'Simülasyon' : 'Simulation'}
                      </Link>
                    )}
                  </div>

                  <section>
                    <h3 className="text-xs font-bold uppercase tracking-wider text-neutral-500 mb-3">
                      {isTr ? 'Kimlik ve Keşif Bilgileri' : 'Identity & Discovery Info'}
                    </h3>
                    <dl className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4 p-4 rounded-lg border border-neutral-200 dark:border-dark-border bg-neutral-50 dark:bg-dark-bg">
                      <FieldDisplay label="FQDN" value={detail.node.fqdn} mono englishLabel />
                      <FieldDisplay label={isTr ? 'IP Adresi' : 'IP Address'} value={detail.node.ipAddress} mono />
                      <FieldDisplay label={isTr ? 'MAC Adresi' : 'MAC Address'} value={detail.node.macAddress} mono />
                      <FieldDisplay label="BIOS UUID" value={detail.node.biosUuid} mono englishLabel />
                      <FieldDisplay label="VM Instance UUID" value={detail.node.vmInstanceUuid} mono englishLabel />
                      <FieldDisplay label="Domain SID" value={detail.node.domainSid} mono englishLabel />
                      <FieldDisplay label={isTr ? 'İlk Görülme' : 'First Seen'} value={formatDateTime(detail.node.firstSeen)} />
                      <FieldDisplay label={isTr ? 'Son Görülme' : 'Last Seen'} value={formatDateTime(detail.node.lastSeen)} />
                      <FieldDisplay
                        label={isTr ? 'Bayatlık' : 'Staleness'}
                        value={
                          detail.node.isStale
                            ? isTr
                              ? `Evet · ${formatDateTime(detail.node.staleSince)}`
                              : `Yes · ${formatDateTime(detail.node.staleSince)}`
                            : isTr
                              ? 'Güncel'
                              : 'Current'
                        }
                      />
                    </dl>
                  </section>

                  {Object.keys(detail.node.properties ?? {})
                    .filter((key) => !key.startsWith('_')).length > 0 && (
                    <section>
                      <h3 className="text-xs font-bold uppercase tracking-wider text-neutral-500 mb-3">
                        {isTr ? 'Kaynak Özellikleri' : 'Source Properties'}
                      </h3>
                      <dl className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3 p-4 rounded-lg border border-neutral-200 dark:border-dark-border">
                        {Object.entries(detail.node.properties)
                          .filter(([key]) => !key.startsWith('_'))
                          .slice(0, 18)
                          .map(([key, value]) => (
                            <FieldDisplay key={key} label={key} value={formatProperty(value)} englishLabel />
                          ))}
                      </dl>
                    </section>
                  )}

                  <section>
                    <h3 className="text-xs font-bold uppercase tracking-wider text-neutral-500 mb-3">
                      {isTr ? `Kaynak Bağlantıları (${detail.sourceLinks.length})` : `Source Links (${detail.sourceLinks.length})`}
                    </h3>
                    {detail.sourceLinks.length === 0 ? (
                      <p className="text-xs text-neutral-500">
                        {isTr ? 'Bu düğüm için kalıcı kaynak bağlantısı bulunmuyor.' : 'No persistent source links for this node.'}
                      </p>
                    ) : (
                      <div className="overflow-x-auto rounded-lg border border-neutral-200 dark:border-dark-border">
                        <table className="w-full text-left text-xs">
                          <thead className="bg-neutral-50 dark:bg-dark-bg text-neutral-500">
                            <tr>
                              <th className="p-3">{isTr ? 'Entegrasyon' : 'Integration'}</th>
                              <th className="p-3">{isTr ? 'Kaynak' : 'Source'}</th>
                              <th className="p-3">{isTr ? 'Harici ID' : 'External ID'}</th>
                              <th className="p-3">{isTr ? 'İlk Görülme' : 'First Seen'}</th>
                              <th className="p-3">{isTr ? 'Son Görülme' : 'Last Seen'}</th>
                              <th className="p-3">{isTr ? 'Durum' : 'Status'}</th>
                            </tr>
                          </thead>
                          <tbody className="divide-y divide-neutral-100 dark:divide-dark-border">
                            {detail.sourceLinks.map((link) => (
                              <tr key={link.id}>
                                <td className="p-3 text-neutral-800 dark:text-neutral-200">
                                  {link.integrationName || shortId(link.integrationId)}
                                </td>
                                <td className="p-3 text-neutral-600 dark:text-neutral-300">{link.sourceSystem}</td>
                                <td className="p-3 font-mono text-neutral-600 dark:text-neutral-300 break-all">
                                  {link.externalId}
                                </td>
                                <td className="p-3 text-neutral-600 dark:text-neutral-300">
                                  {formatDateTime(link.firstSeenAt)}
                                </td>
                                <td className="p-3 text-neutral-600 dark:text-neutral-300">
                                  {formatDateTime(link.lastSeenAt)}
                                </td>
                                <td className="p-3">
                                  {link.isStale ? (
                                    <span className="text-amber-600 dark:text-amber-400">{isTr ? 'Bayat' : 'Stale'}</span>
                                  ) : (
                                    <span className="text-emerald-600 dark:text-emerald-400">{isTr ? 'Güncel' : 'Current'}</span>
                                  )}
                                </td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </div>
                    )}
                  </section>

                  <section>
                    <h3 className="text-xs font-bold uppercase tracking-wider text-neutral-500 mb-3">
                      {isTr ? `İlişkiler (${detail.relationships.length})` : `Relationships (${detail.relationships.length})`}
                    </h3>
                    {detail.relationships.length === 0 ? (
                      <p className="text-xs text-neutral-500">
                        {isTr ? 'Bu düğüm için kaydedilmiş ilişki bulunmuyor.' : 'No recorded relationships for this node.'}
                      </p>
                    ) : (
                      <div className="space-y-2">
                        {detail.relationships.slice(0, 25).map((relationship) => {
                          const isSource = relationship.sourceNodeId === detail.node.id;
                          const otherId = isSource ? relationship.targetNodeId : relationship.sourceNodeId;
                          const otherName =
                            (isSource ? relationship.targetNodeName : relationship.sourceNodeName) || shortId(otherId);
                          return (
                            <div
                              key={relationship.id}
                              className={`p-3 rounded-lg border bg-white dark:bg-dark-card text-xs ${
                                relationship.isStale
                                  ? 'border-dashed border-neutral-300 dark:border-dark-border opacity-70'
                                  : 'border-neutral-200 dark:border-dark-border'
                              }`}
                            >
                              <div className="flex flex-wrap items-center gap-2">
                                <span lang="en" className="font-mono text-[10px] px-1.5 py-0.5 rounded bg-neutral-100 dark:bg-dark-border text-neutral-600 dark:text-neutral-300">
                                  {relationship.relationshipType}
                                </span>
                                <span className="text-neutral-700 dark:text-neutral-200">
                                  {isSource ? '→' : '←'}{' '}
                                  <button
                                    type="button"
                                    onClick={() => setSelectedNodeId(otherId)}
                                    className="font-medium hover:underline text-brand-primary dark:text-brand-primary-dark"
                                  >
                                    {otherName}
                                  </button>
                                </span>
                                {relationship.isStale && (
                                  <span className="text-[10px] px-1.5 py-0.5 rounded bg-amber-50 dark:bg-amber-950/30 text-amber-700 dark:text-amber-400 font-semibold">
                                    {isTr ? 'Bayat' : 'Stale'}
                                  </span>
                                )}
                                <span lang="en" className="ml-auto text-[10px] text-neutral-500">
                                  {relationship.confidenceLevel}
                                </span>
                              </div>
                              {relationship.evidence && (
                                <p className="mt-1 text-[11px] text-neutral-500">
                                  {isTr ? 'Kanıt: ' : 'Evidence: '}
                                  {relationship.evidence}
                                </p>
                              )}
                            </div>
                          );
                        })}
                        {detail.relationships.length > 25 && (
                          <p className="text-[11px] text-neutral-500">
                            +{detail.relationships.length - 25}{' '}
                            {isTr ? 'ilişki daha var.' : 'more relationships.'}
                          </p>
                        )}
                      </div>
                    )}
                  </section>

                  <section>
                    <h3 className="text-xs font-bold uppercase tracking-wider text-neutral-500 mb-3">
                      {isTr ? `Uzlaştırma İncelemeleri (${detail.reviews.length})` : `Reconciliation Reviews (${detail.reviews.length})`}
                    </h3>
                    {detail.reviews.length === 0 ? (
                      <p className="text-xs text-neutral-500">
                        {isTr
                          ? 'Bu düğüm için uzlaştırma incelemesi bulunmuyor.'
                          : 'No reconciliation reviews for this node.'}
                      </p>
                    ) : (
                      <div className="space-y-3">
                        {detail.reviews.map((review) => {
                          const candidateIds = Array.from(
                            new Set([review.candidateNodeId, ...review.candidateNodeIds].filter((id): id is string => !!id)),
                          );
                          const selectedTarget =
                            mergeTargets[review.id] ?? review.candidateNodeId ?? candidateIds[0] ?? '';
                          const isResolving = resolvingReviewId === review.id;
                          return (
                            <div
                              key={review.id}
                              className={`p-4 rounded-lg border text-xs space-y-3 ${
                                review.status === 'Pending'
                                  ? 'border-amber-200 dark:border-amber-900 bg-amber-50/60 dark:bg-amber-950/20'
                                  : 'border-neutral-200 dark:border-dark-border bg-neutral-50 dark:bg-dark-bg'
                              }`}
                            >
                              <div className="flex flex-wrap items-center gap-2">
                                <span className="font-semibold text-neutral-900 dark:text-white">
                                  {REVIEW_STATUS_LABELS[review.status]
                                    ? isTr
                                      ? REVIEW_STATUS_LABELS[review.status].tr
                                      : REVIEW_STATUS_LABELS[review.status].en
                                    : review.status}
                                </span>
                                <span className="font-mono text-[10px] px-1.5 py-0.5 rounded bg-white/70 dark:bg-dark-border text-neutral-600 dark:text-neutral-300">
                                  {review.matchKey ||
                                    (isTr ? 'eşleşme anahtarı yok' : 'no match key')}
                                </span>
                                <span className="ml-auto text-[10px] text-neutral-500">
                                  {isTr ? 'Güven:' : 'Confidence:'} {review.confidence} ·{' '}
                                  {isTr ? 'Skor:' : 'Score:'} {review.matchScore}
                                </span>
                              </div>

                              <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                                <div>
                                  <div className="text-[10px] uppercase text-neutral-500">
                                    {isTr ? 'Keşfedilen Düğüm' : 'Discovered Node'}
                                  </div>
                                  <ReviewNodeLink
                                    id={review.createdNodeId}
                                    name={review.createdNodeName}
                                    currentId={detail.node.id}
                                    onOpen={setSelectedNodeId}
                                  />
                                </div>
                                <div>
                                  <div className="text-[10px] uppercase text-neutral-500">
                                    {isTr ? 'Aday Düğüm' : 'Candidate Node'}
                                  </div>
                                  <ReviewNodeLink
                                    id={review.candidateNodeId}
                                    name={review.candidateNodeName}
                                    currentId={detail.node.id}
                                    onOpen={setSelectedNodeId}
                                  />
                                </div>
                              </div>

                              <p className="text-neutral-600 dark:text-neutral-300">{review.reason}</p>
                              {review.candidateSummary && (
                                <p className="text-[11px] text-neutral-500">{review.candidateSummary}</p>
                              )}

                              {review.status === 'Pending' && !canOperate ? (
                                <div className="pt-2 border-t border-amber-200/70 dark:border-amber-900/60 text-[11px] text-neutral-500">
                                  {t('noPermission')}
                                </div>
                              ) : review.status === 'Pending' ? (
                                <div className="space-y-2 pt-2 border-t border-amber-200/70 dark:border-amber-900/60">
                                  {candidateIds.length > 0 && (
                                    <label className="block text-[11px] text-neutral-500">
                                      {isTr ? 'Birleştirme hedefi' : 'Merge target'}
                                      <select
                                        value={selectedTarget}
                                        onChange={(event) =>
                                          setMergeTargets((current) => ({
                                            ...current,
                                            [review.id]: event.target.value,
                                          }))
                                        }
                                        className="mt-1 h-9 w-full rounded-lg border border-neutral-300 dark:border-dark-border bg-white dark:bg-dark-card px-2 text-xs text-neutral-800 dark:text-neutral-200"
                                      >
                                        {candidateIds.map((candidateId) => (
                                          <option key={candidateId} value={candidateId}>
                                            {candidateId === review.candidateNodeId
                                              ? review.candidateNodeName || shortId(candidateId)
                                              : `${isTr ? 'Aday' : 'Candidate'} ${shortId(candidateId)}`}
                                          </option>
                                        ))}
                                      </select>
                                    </label>
                                  )}
                                  <label className="block text-[11px] text-neutral-500">
                                    {isTr ? 'İnceleme notu (opsiyonel)' : 'Review notes (optional)'}
                                    <input
                                      type="text"
                                      value={resolutionNotes[review.id] ?? ''}
                                      onChange={(event) =>
                                        setResolutionNotes((current) => ({
                                          ...current,
                                          [review.id]: event.target.value,
                                        }))
                                      }
                                      placeholder={
                                        isTr ? 'Kararın gerekçesi...' : 'Decision rationale...'
                                      }
                                      className="mt-1 h-9 w-full rounded-lg border border-neutral-300 dark:border-dark-border bg-white dark:bg-dark-card px-2 text-xs text-neutral-800 dark:text-neutral-200"
                                    />
                                  </label>
                                  <div className="flex flex-wrap justify-end gap-2">
                                    <Button
                                      variant="outline"
                                      size="sm"
                                      onClick={() => void handleResolveReview(review, 'Dismissed')}
                                      disabled={isResolving}
                                    >
                                      {isResolving && <Loader2 className="w-3.5 h-3.5 animate-spin" />}
                                      {isTr ? 'Yok Say' : 'Dismiss'}
                                    </Button>
                                    <Button
                                      variant="primary"
                                      size="sm"
                                      className="bg-brand-primary"
                                      onClick={() => void handleResolveReview(review, 'Merged')}
                                      disabled={isResolving || candidateIds.length === 0}
                                    >
                                      {isResolving && <Loader2 className="w-3.5 h-3.5 animate-spin" />}
                                      {isTr ? 'Adayla Birleştir' : 'Merge with Candidate'}
                                    </Button>
                                  </div>
                                </div>
                              ) : (
                                <div className="text-[11px] text-neutral-500">
                                  {review.reviewedAt
                                    ? isTr
                                      ? `${formatDateTime(review.reviewedAt)} tarihinde ${review.reviewedBy || 'sistem'} tarafından sonuçlandırıldı.`
                                      : `Concluded at ${formatDateTime(review.reviewedAt)} by ${review.reviewedBy || 'system'}.`
                                    : isTr
                                      ? 'Sonuçlandırıldı.'
                                      : 'Concluded.'}
                                  {review.resolutionNotes &&
                                    ` ${isTr ? 'Not:' : 'Note:'} ${review.resolutionNotes}`}
                                </div>
                              )}
                            </div>
                          );
                        })}
                      </div>
                    )}

                    <div className="mt-3 p-3 rounded-lg bg-blue-50 dark:bg-blue-950/30 border border-blue-200 dark:border-blue-900 text-blue-800 dark:text-blue-300 text-xs flex items-start gap-2">
                      <Info className="w-4 h-4 mt-0.5 shrink-0 text-blue-600" />
                      <span>
                        {isTr
                          ? 'BerMap hiçbir varlığı tek başına kısa makine adı (hostname) ile otomatik birleştirmez. Şüpheli eşleşmeler burada manuel incelemeye sunulur.'
                          : 'BerMap never auto-merges entities on short hostname alone. Ambiguous matches are queued for manual review here.'}
                      </span>
                    </div>
                  </section>
                </>
              ) : null}
            </div>
          </Card>
        </div>
      )}
    </div>
  );
};
