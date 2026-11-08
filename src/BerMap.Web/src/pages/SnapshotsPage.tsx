import React, { useCallback, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { Card } from '@/components/ui/Card';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { StatusIndicator } from '@/components/ui/StatusIndicator';
import {
  AlertTriangle,
  ArrowRight,
  Calendar,
  Camera,
  CheckCircle2,
  GitCompare,
  Hash,
  Loader2,
  Lock,
  Plus,
  RefreshCw,
  ShieldAlert,
  ShieldCheck,
  Workflow,
  X,
} from 'lucide-react';
import { api } from '@/lib/api';
import { useLocale } from '@/lib/i18n';
import { useT } from '@/lib/i18n-runtime';
import { useEscapeKey } from '@/hooks/useEscapeKey';
import { useDialogFocus } from '@/hooks/useDialogFocus';
import { useCanOperate } from '@/stores/authStore';
import {
  formatDateTime,
  formatNumber,
  getErrorMessage,
  shortId,
  toNodeStatus,
} from '@/lib/format';
import type {
  CreateSnapshotRequest,
  SnapshotDiff,
  SnapshotGraph,
  SnapshotNodeChange,
  SnapshotNodeFieldChange,
  SnapshotRelationshipChange,
  SnapshotSummary,
  SnapshotVerification,
} from '@/types';

function defaultSnapshotName(): string {
  return `snap-${new Date().toISOString().slice(0, 10)}`;
}

function NodeChangeSection({
  title,
  nodes,
  tone,
}: {
  title: string;
  nodes: SnapshotNodeChange[];
  tone: 'added' | 'removed';
}) {
  const locale = useLocale();
  const isTr = locale === 'tr';
  if (nodes.length === 0) return null;

  return (
    <div>
      <h4 className="text-xs font-bold uppercase tracking-wider text-neutral-500 mb-2">
        {title} ({nodes.length})
      </h4>
      <div className="space-y-1.5">
        {nodes.slice(0, 20).map((node) => (
          <div
            key={`${node.nodeId}-${tone}`}
            className={`p-2.5 rounded-lg border text-xs ${
              tone === 'added'
                ? 'border-emerald-200 dark:border-emerald-900 bg-emerald-50/60 dark:bg-emerald-950/20'
                : 'border-rose-200 dark:border-rose-900 bg-rose-50/60 dark:bg-rose-950/20'
            }`}
          >
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-semibold text-neutral-900 dark:text-white">{node.name}</span>
              <span className="text-[10px] px-1.5 py-0.5 rounded bg-white/70 dark:bg-dark-border text-neutral-600 dark:text-neutral-300">
                {node.nodeType}
              </span>
              <span className="ml-auto font-mono text-[10px] text-neutral-500">
                {node.ipAddress || (isTr ? 'IP yok' : 'No IP')}
              </span>
            </div>
            <div className="font-mono text-[10px] text-neutral-500 mt-0.5">
              {node.fqdn || shortId(node.nodeId)}
            </div>
          </div>
        ))}
        {nodes.length > 20 && (
          <p className="text-[11px] text-neutral-500">+{nodes.length - 20} {isTr ? 'düğüm daha var.' : 'more nodes.'}</p>
        )}
      </div>
    </div>
  );
}

function ChangedNodeSection({ nodes }: { nodes: SnapshotNodeFieldChange[] }) {
  const locale = useLocale();
  const isTr = locale === 'tr';
  if (nodes.length === 0) return null;

  return (
    <div>
      <h4 className="text-xs font-bold uppercase tracking-wider text-neutral-500 mb-2">
        {isTr ? `Değişen Düğümler` : `Changed Nodes`} ({nodes.length})
      </h4>
      <div className="space-y-2">
        {nodes.slice(0, 20).map((node) => (
          <div
            key={node.nodeId}
            className="p-3 rounded-lg border border-amber-200 dark:border-amber-900 bg-amber-50/60 dark:bg-amber-950/20 text-xs"
          >
            <div className="flex flex-wrap items-center gap-2 mb-1.5">
              <span className="font-semibold text-neutral-900 dark:text-white">{node.name}</span>
              <span className="text-[10px] px-1.5 py-0.5 rounded bg-white/70 dark:bg-dark-border text-neutral-600 dark:text-neutral-300">
                {node.nodeType}
              </span>
            </div>
            <div className="space-y-1">
              {node.changes.slice(0, 8).map((change) => (
                <div key={change.field} className="flex flex-wrap gap-1 text-[11px]">
                  <span className="font-semibold text-neutral-700 dark:text-neutral-200">
                    {change.field}:
                  </span>
                  <span className="text-rose-600 dark:text-rose-400 break-all">
                    {change.from || (isTr ? '(boş)' : '(empty)')}
                  </span>
                  <span className="text-neutral-400">→</span>
                  <span className="text-emerald-600 dark:text-emerald-400 break-all">
                    {change.to || (isTr ? '(boş)' : '(empty)')}
                  </span>
                </div>
              ))}
              {node.changes.length > 8 && (
                <div className="text-[10px] text-neutral-500">
                  +{node.changes.length - 8} {isTr ? 'alan değişikliği daha var.' : 'more field changes.'}
                </div>
              )}
            </div>
          </div>
        ))}
        {nodes.length > 20 && (
          <p className="text-[11px] text-neutral-500">+{nodes.length - 20} {isTr ? 'düğüm daha var.' : 'more nodes.'}</p>
        )}
      </div>
    </div>
  );
}

function RelationshipChangeSection({
  title,
  changes,
  tone,
}: {
  title: string;
  changes: SnapshotRelationshipChange[];
  tone: 'added' | 'removed' | 'changed';
}) {
  const locale = useLocale();
  const isTr = locale === 'tr';
  if (changes.length === 0) return null;

  const toneClass =
    tone === 'added'
      ? 'border-emerald-200 dark:border-emerald-900 bg-emerald-50/60 dark:bg-emerald-950/20'
      : tone === 'removed'
      ? 'border-rose-200 dark:border-rose-900 bg-rose-50/60 dark:bg-rose-950/20'
      : 'border-amber-200 dark:border-amber-900 bg-amber-50/60 dark:bg-amber-950/20';

  return (
    <div>
      <h4 className="text-xs font-bold uppercase tracking-wider text-neutral-500 mb-2">
        {title} ({changes.length})
      </h4>
      <div className="space-y-1.5">
        {changes.slice(0, 20).map((change) => (
          <div
            key={`${change.relationshipId}-${tone}`}
            className={`p-2.5 rounded-lg border text-xs ${toneClass}`}
          >
            <div className="flex flex-wrap items-center gap-1.5">
              <span className="font-medium text-neutral-900 dark:text-white">
                {change.sourceNodeName}
              </span>
              <span className="text-neutral-400">→</span>
              <span className="font-medium text-neutral-900 dark:text-white">
                {change.targetNodeName}
              </span>
              <span className="text-[10px] px-1.5 py-0.5 rounded bg-white/70 dark:bg-dark-border text-neutral-600 dark:text-neutral-300">
                {change.relationshipType}
              </span>
            </div>
            {change.changes.length > 0 && (
              <div className="mt-1 space-y-0.5">
                {change.changes.slice(0, 4).map((field) => (
                  <div key={field.field} className="text-[10px] text-neutral-600 dark:text-neutral-300">
                    <span className="font-semibold">{field.field}:</span> {field.from || (isTr ? '(boş)' : '(empty)')} →{' '}
                    {field.to || (isTr ? '(boş)' : '(empty)')}
                  </div>
                ))}
              </div>
            )}
          </div>
        ))}
        {changes.length > 20 && (
          <p className="text-[11px] text-neutral-500">+{changes.length - 20} {isTr ? 'ilişki daha var.' : 'more relationships.'}</p>
        )}
      </div>
    </div>
  );
}

export const SnapshotsPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const queryClient = useQueryClient();
  const [isCreateOpen, setIsCreateOpen] = useState(false);
  const [newName, setNewName] = useState(defaultSnapshotName);
  const [newDesc, setNewDesc] = useState('');
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  // The open snapshot lives in the URL (?id=) so other pages can deep-link to it.
  const [searchParams, setSearchParams] = useSearchParams();
  const detailSnapshotId = searchParams.get('id') || null;
  const setDetailSnapshotId = useCallback(
    (id: string | null) => {
      setSearchParams(
        (current) => {
          const next = new URLSearchParams(current);
          if (id) next.set('id', id);
          else next.delete('id');
          return next;
        },
        { replace: !id },
      );
    },
    [setSearchParams],
  );
  const [isDiffOpen, setIsDiffOpen] = useState(false);
  const navigate = useNavigate();
  const [compareA, setCompareA] = useState('');
  const [compareB, setCompareB] = useState('');
  const [diff, setDiff] = useState<SnapshotDiff | null>(null);
  const [diffLoading, setDiffLoading] = useState(false);
  const [diffError, setDiffError] = useState<string | null>(null);
  const [verification, setVerification] = useState<SnapshotVerification | null>(null);
  const [verifyingId, setVerifyingId] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  const t = useT();
  const canOperate = useCanOperate();

  const createDialogRef = useRef<HTMLDivElement>(null);
  const diffDialogRef = useRef<HTMLDivElement>(null);
  const detailDialogRef = useRef<HTMLDivElement>(null);
  const verificationDialogRef = useRef<HTMLDivElement>(null);
  useDialogFocus(createDialogRef, isCreateOpen);
  useDialogFocus(diffDialogRef, isDiffOpen);
  useDialogFocus(detailDialogRef, Boolean(detailSnapshotId), { initialFocus: 'container' });
  useDialogFocus(verificationDialogRef, verification !== null);

  // Escape closes the top-most open dialog.
  const anyDialogOpen = isCreateOpen || isDiffOpen || Boolean(detailSnapshotId) || verification !== null;
  useEscapeKey(anyDialogOpen, () => {
    if (verification) setVerification(null);
    else if (detailSnapshotId) setDetailSnapshotId(null);
    else if (isDiffOpen) setIsDiffOpen(false);
    else if (isCreateOpen && !creating) setIsCreateOpen(false);
  });

  const snapshotsQuery = useQuery<SnapshotSummary[]>({
    queryKey: ['snapshots', 'list'],
    queryFn: () => api.get<SnapshotSummary[]>('/snapshots'),
    refetchInterval: 30_000,
  });

  const graphQuery = useQuery<SnapshotGraph>({
    queryKey: ['snapshots', 'graph', detailSnapshotId],
    queryFn: () => api.get<SnapshotGraph>(`/snapshots/${detailSnapshotId}/graph`),
    enabled: Boolean(detailSnapshotId),
    staleTime: Infinity,
  });

  const snapshots = snapshotsQuery.data ?? [];
  const latestSnapshot = snapshots[0];
  const snapshotsError = snapshotsQuery.isError
    ? getErrorMessage(snapshotsQuery.error)
    : null;
  // A deep link (?id=) can point at a snapshot that does not exist (any more).
  const graphError = graphQuery.isError
    ? (graphQuery.error as { status?: number } | null)?.status === 404
      ? (isTr ? 'Bu snapshot bulunamadı. Bağlantı eski veya hatalı olabilir.' : 'This snapshot was not found. The link may be outdated or wrong.')
      : getErrorMessage(graphQuery.error)
    : null;

  const openCreate = () => {
    setNewName(defaultSnapshotName());
    setNewDesc('');
    setCreateError(null);
    setIsCreateOpen(true);
  };

  const handleCreate = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!newName.trim()) {
      setCreateError(isTr ? 'Snapshot adı zorunludur.' : 'Snapshot name is required.');
      return;
    }

    setCreating(true);
    setCreateError(null);
    setActionError(null);
    setNotice(null);

    try {
      const request: CreateSnapshotRequest = {
        name: newName.trim(),
        description: newDesc.trim(),
      };
      const created = await api.post<SnapshotSummary>('/snapshots', request);
      await queryClient.invalidateQueries({ queryKey: ['snapshots'] });
      setNotice(
        isTr
          ? `${created.name} snapshot'ı v${created.snapshotVersion} olarak mühürlendi (${formatNumber(
              created.nodeCount
            )} düğüm, ${formatNumber(created.relationshipCount)} ilişki).`
          : `${created.name} snapshot sealed as v${created.snapshotVersion} (${formatNumber(
              created.nodeCount
            )} nodes, ${formatNumber(created.relationshipCount)} relationships).`
      );
      setIsCreateOpen(false);
    } catch (error) {
      setCreateError(getErrorMessage(error));
    } finally {
      setCreating(false);
    }
  };

  const openDiff = () => {
    const list = snapshotsQuery.data ?? [];
    setCompareA(list[1]?.id ?? '');
    setCompareB(list[0]?.id ?? '');
    setDiff(null);
    setDiffError(null);
    setIsDiffOpen(true);
  };

  const handleCompare = async () => {
    if (!compareA || !compareB || compareA === compareB) {
      setDiffError(isTr ? 'Karşılaştırma için iki farklı snapshot seçin.' : 'Select two different snapshots to compare.');
      return;
    }

    setDiffLoading(true);
    setDiffError(null);
    setDiff(null);

    try {
      const result = await api.get<SnapshotDiff>(
        `/snapshots/compare?a=${encodeURIComponent(compareA)}&b=${encodeURIComponent(compareB)}`
      );
      setDiff(result);
    } catch (error) {
      setDiffError(getErrorMessage(error));
    } finally {
      setDiffLoading(false);
    }
  };

  const handleVerify = async (snapshot: SnapshotSummary) => {
    setVerifyingId(snapshot.id);
    setActionError(null);
    setNotice(null);

    try {
      const result = await api.get<SnapshotVerification>(`/snapshots/${snapshot.id}/verify`);
      setVerification(result);
    } catch (error) {
      setActionError(getErrorMessage(error));
    } finally {
      setVerifyingId(null);
    }
  };

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['snapshots', 'list'] });
    if (detailSnapshotId) {
      await queryClient.invalidateQueries({ queryKey: ['snapshots', 'graph', detailSnapshotId] });
    }
  };

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
              {isTr ? 'Altyapı Snapshot & Versiyonlama' : 'Infrastructure Snapshots & Versioning'}
            </h1>
            <span className="text-xs px-2.5 py-0.5 rounded-full bg-blue-50 dark:bg-blue-950/40 text-blue-700 dark:text-blue-400 border border-blue-200 dark:border-blue-800 font-bold">
              Immutable Snapshots
            </span>
          </div>
          <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1">
            {isTr
              ? 'Kalıcı envanter grafiğini değiştirilemez snapshot\'lar olarak dondurun, SHA-256 ile doğrulayın ve sürümler arasındaki farkları inceleyin'
              : 'Freeze the persistent inventory graph into immutable snapshots, verify with SHA-256 and inspect differences between versions'}
          </p>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            className="gap-2"
            onClick={() => void refresh()}
            disabled={snapshotsQuery.isFetching}
          >
            <RefreshCw className={`w-4 h-4 ${snapshotsQuery.isFetching ? 'animate-spin' : ''}`} />
            {isTr ? 'Yenile' : 'Refresh'}
          </Button>
          <Button
            variant="outline"
            size="sm"
            onClick={openDiff}
            className="gap-2"
            disabled={snapshots.length < 2}
          >
            <GitCompare className="w-4 h-4 text-brand-primary" />
            {isTr ? 'Snapshot Karşılaştır' : 'Compare Snapshots'}
          </Button>
          <Button
            variant="primary"
            size="sm"
            onClick={openCreate}
            disabled={!canOperate}
            title={canOperate ? undefined : t('noPermission')}
            className="gap-2 bg-brand-primary"
          >
            <Plus className="w-4 h-4" />
            {isTr ? 'Yeni Snapshot Al' : 'Take New Snapshot'}
          </Button>
        </div>
      </div>

      {notice && (
        <div
          role="status"
          className="p-3 rounded-lg border border-emerald-200 dark:border-emerald-900 bg-emerald-50 dark:bg-emerald-950/30 text-sm text-emerald-800 dark:text-emerald-300 flex items-start gap-2"
        >
          <CheckCircle2 className="w-4 h-4 mt-0.5 shrink-0" />
          <span className="flex-1">{notice}</span>
          <button type="button" onClick={() => setNotice(null)} aria-label={isTr ? 'Bildirimi kapat' : 'Close notification'}>
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
          <button type="button" onClick={() => setActionError(null)} aria-label={isTr ? 'Hatayı kapat' : 'Close error'}>
            <X className="w-4 h-4" />
          </button>
        </div>
      )}

      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Toplam Snapshot' : 'Total Snapshots'}</div>
          <div className="mt-1 text-2xl font-bold text-neutral-900 dark:text-white">
            {formatNumber(snapshots.length)}
          </div>
          <div className="text-[11px] text-neutral-500 mt-1">{isTr ? 'Değiştirilemez sürümler' : 'Immutable versions'}</div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Son Snapshot Düğümleri' : 'Latest Snapshot Nodes'}</div>
          <div className="mt-1 text-2xl font-bold text-neutral-900 dark:text-white">
            {formatNumber(latestSnapshot?.nodeCount)}
          </div>
          <div className="text-[11px] text-neutral-500 mt-1">
            {latestSnapshot ? formatDateTime(latestSnapshot.graphCapturedAt) : (isTr ? 'Henüz snapshot yok' : 'No snapshots yet')}
          </div>
        </Card>
        <Card className="p-4">
          <div className="text-xs text-neutral-500">{isTr ? 'Son Snapshot İlişkileri' : 'Latest Snapshot Relationships'}</div>
          <div className="mt-1 text-2xl font-bold text-neutral-900 dark:text-white">
            {formatNumber(latestSnapshot?.relationshipCount)}
          </div>
          <div className="text-[11px] text-neutral-500 mt-1">
            {latestSnapshot?.name ?? (isTr ? 'Henüz snapshot yok' : 'No snapshots yet')}
          </div>
        </Card>
      </div>

      {snapshotsQuery.isLoading ? (
        <div className="space-y-4">
          {[0, 1, 2].map((index) => (
            <div
              key={index}
              className="h-36 rounded-xl border border-neutral-200 dark:border-dark-border bg-neutral-100 dark:bg-dark-card animate-pulse"
            />
          ))}
        </div>
      ) : snapshotsError ? (
        <Card className="p-6 text-sm text-rose-700 dark:text-rose-300">
          <div className="flex items-center gap-2">
            <AlertTriangle className="w-4 h-4" />
            {snapshotsError}
          </div>
          <Button
            variant="outline"
            size="sm"
            className="mt-4"
            onClick={() => void snapshotsQuery.refetch()}
          >
            {isTr ? 'Tekrar Dene' : 'Retry'}
          </Button>
        </Card>
      ) : snapshots.length === 0 ? (
        <Card className="p-10 text-center">
          <Camera className="w-10 h-10 mx-auto text-neutral-300 dark:text-neutral-600" />
          <h2 className="mt-3 font-bold text-neutral-900 dark:text-white">
            {isTr ? 'Henüz snapshot alınmadı' : 'No snapshots taken yet'}
          </h2>
          <p className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">
            {isTr
              ? 'Envanter keşfi tamamlandıktan sonra mevcut grafiği değiştirilemez bir referans olarak dondurun.'
              : 'After inventory discovery completes, freeze the current graph as an immutable reference.'}
          </p>
          <Button
            variant="primary"
            size="sm"
            className="mt-4 gap-2 bg-brand-primary"
            onClick={openCreate}
            disabled={!canOperate}
            title={canOperate ? undefined : t('noPermission')}
          >
            <Plus className="w-4 h-4" />
            {isTr ? 'İlk Snapshot\'ı Al' : 'Take First Snapshot'}
          </Button>
        </Card>
      ) : (
        <div className="grid grid-cols-1 gap-4">
          {snapshots.map((snapshot) => (
            <Card
              key={snapshot.id}
              className="p-5 flex flex-col lg:flex-row lg:items-center justify-between gap-4"
            >
              <div className="space-y-2 flex-1 min-w-0">
                <div className="flex items-start gap-3">
                  <div className="p-2 rounded-lg bg-brand-primary/10 text-brand-primary dark:text-brand-primary-dark shrink-0">
                    <Camera className="w-5 h-5" />
                  </div>
                  <div className="min-w-0">
                    <h3 className="font-bold text-base text-neutral-900 dark:text-white flex flex-wrap items-center gap-2">
                      {snapshot.name}
                      <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-neutral-100 dark:bg-dark-border text-neutral-600 dark:text-neutral-300">
                        v{snapshot.snapshotVersion}
                      </span>
                      {snapshot.isImmutable && (
                        <span className="text-[10px] uppercase font-bold px-1.5 py-0.5 rounded bg-neutral-100 dark:bg-dark-border text-neutral-500 flex items-center gap-1">
                          <Lock className="w-3 h-3" /> {isTr ? 'Değiştirilemez' : 'Immutable'}
                        </span>
                      )}
                    </h3>
                    {snapshot.description && (
                      <p className="text-xs text-neutral-500 dark:text-neutral-400 mt-0.5">
                        {snapshot.description}
                      </p>
                    )}
                  </div>
                </div>

                <div className="flex flex-wrap items-center gap-x-4 gap-y-1.5 text-xs text-neutral-500 pt-1">
                  <span className="flex items-center gap-1">
                    <Calendar className="w-3.5 h-3.5" /> {formatDateTime(snapshot.createdAt)}
                  </span>
                  <span>{isTr ? 'Oluşturan:' : 'Created by:'} {snapshot.createdBy || '—'}</span>
                  <span className="font-semibold text-neutral-800 dark:text-neutral-200">
                    {formatNumber(snapshot.nodeCount)} {isTr ? 'düğüm' : 'nodes'}
                  </span>
                  <span className="font-semibold text-neutral-800 dark:text-neutral-200">
                    {formatNumber(snapshot.relationshipCount)} {isTr ? 'ilişki' : 'relationships'}
                  </span>
                </div>

                <div className="flex items-center gap-1.5 text-[11px] text-neutral-500 font-mono">
                  <Hash className="w-3.5 h-3.5 shrink-0" />
                  <span className="truncate" title={snapshot.hash}>
                    {snapshot.hash}
                  </span>
                </div>

                {snapshot.warnings.length > 0 && (
                  <div className="p-2.5 rounded-lg border border-amber-200 dark:border-amber-900 bg-amber-50/60 dark:bg-amber-950/20 text-[11px] text-amber-800 dark:text-amber-300 space-y-1">
                    {snapshot.warnings.map((warning) => (
                      <div key={warning} className="flex items-start gap-1.5">
                        <AlertTriangle className="w-3.5 h-3.5 mt-0.5 shrink-0" />
                        <span>{warning}</span>
                      </div>
                    ))}
                  </div>
                )}
              </div>

              <div className="flex flex-wrap items-center gap-2 self-end lg:self-center">
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => setDetailSnapshotId(snapshot.id)}
                >
                  {isTr ? 'Detay' : 'Detail'}
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  className="gap-1.5"
                  onClick={() => void handleVerify(snapshot)}
                  disabled={verifyingId !== null}
                >
                  {verifyingId === snapshot.id ? (
                    <Loader2 className="w-3.5 h-3.5 animate-spin" />
                  ) : (
                    <ShieldCheck className="w-3.5 h-3.5" />
                  )}
                  {isTr ? 'Doğrula' : 'Verify'}
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  className="gap-1.5"
                  onClick={() => navigate(`/simulation?snapshot=${encodeURIComponent(snapshot.id)}`)}
                  title={isTr ? 'Bu snapshot üzerinde simülasyon çalıştır' : 'Run a simulation against this snapshot'}
                >
                  <Workflow className="w-3.5 h-3.5" />
                  {isTr ? 'Simüle et' : 'Simulate'}
                </Button>
              </div>
            </Card>
          ))}
        </div>
      )}

      {isCreateOpen && (
        <div
          ref={createDialogRef}
          className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60 backdrop-blur-xs"
          role="dialog"
          aria-modal="true"
          aria-label={isTr ? 'Yeni snapshot' : 'New snapshot'}
          onClick={() => setIsCreateOpen(false)}
        >
          <Card
            className="w-full max-w-md p-6 border-neutral-200 dark:border-dark-border shadow-2xl space-y-4"
            onClick={(event) => event.stopPropagation()}
          >
            <div className="flex items-center justify-between border-b border-neutral-200 dark:border-dark-border pb-3">
              <h3 className="font-bold text-base text-neutral-900 dark:text-white">
                {isTr ? 'Yeni Altyapı Snapshot\'ı Al' : 'Take New Infrastructure Snapshot'}
              </h3>
              <button
                type="button"
                onClick={() => setIsCreateOpen(false)}
                className="p-1 rounded text-neutral-400 hover:text-neutral-600 dark:hover:text-neutral-200"
                data-dialog-close
                aria-label={isTr ? 'Pencereyi kapat' : 'Close window'}
              >
                <X className="w-5 h-5" />
              </button>
            </div>

            {createError && (
              <div className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-xs text-rose-700 dark:text-rose-300 flex items-start gap-2">
                <AlertTriangle className="w-4 h-4 mt-0.5 shrink-0" />
                <span>{createError}</span>
              </div>
            )}

            <form onSubmit={(event) => void handleCreate(event)} className="space-y-4 text-sm">
              <Input
                label={isTr ? 'Snapshot Adı / Etiketi' : 'Snapshot Name / Label'}
                value={newName}
                onChange={(event) => setNewName(event.target.value)}
                maxLength={200}
                required
              />
              <Input
                label={isTr ? 'Açıklama (Opsiyonel)' : 'Description (Optional)'}
                value={newDesc}
                onChange={(event) => setNewDesc(event.target.value)}
                placeholder={isTr ? 'Örn: Patch öncesi güvenlik baseline\'ı' : 'e.g. Security baseline before patching'}
              />
              <div className="p-3 rounded-lg bg-neutral-100 dark:bg-dark-border text-xs text-neutral-600 dark:text-neutral-400">
                {isTr
                  ? 'Snapshot alındıktan sonra salt-okunur olarak mühürlenir ve SHA-256 graf özeti hesaplanır. Boş bir grafik için snapshot yine oluşturulur ve uyarı kaydedilir.'
                  : 'After capture the snapshot is sealed read-only and a SHA-256 graph digest is computed. Snapshots of empty graphs are still created and a warning is recorded.'}
              </div>
              <div className="flex justify-end gap-2 pt-2">
                <Button
                  variant="outline"
                  size="sm"
                  type="button"
                  onClick={() => setIsCreateOpen(false)}
                  disabled={creating}
                >
                  {isTr ? 'İptal' : 'Cancel'}
                </Button>
                <Button
                  variant="primary"
                  size="sm"
                  type="submit"
                  className="bg-brand-primary gap-1.5"
                  disabled={creating}
                >
                  {creating && <Loader2 className="w-4 h-4 animate-spin" />}
                  {isTr ? 'Snapshot Oluştur' : 'Create Snapshot'}
                </Button>
              </div>
            </form>
          </Card>
        </div>
      )}

      {isDiffOpen && (
        <div
          ref={diffDialogRef}
          className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60 backdrop-blur-xs"
          role="dialog"
          aria-modal="true"
          aria-label={isTr ? 'Snapshot karşılaştırması' : 'Snapshot comparison'}
          onClick={() => setIsDiffOpen(false)}
        >
          <Card
            className="w-full max-w-4xl max-h-[90vh] overflow-y-auto border-neutral-200 dark:border-dark-border shadow-2xl"
            padding="none"
            onClick={(event) => event.stopPropagation()}
          >
            <div className="sticky top-0 z-10 flex items-center justify-between p-5 border-b border-neutral-200 dark:border-dark-border bg-white dark:bg-dark-card">
              <div className="flex items-center gap-2">
                <GitCompare className="w-5 h-5 text-brand-primary" />
                <h3 className="font-bold text-base text-neutral-900 dark:text-white">
                  {isTr ? 'Snapshot Değişiklik Karşılaştırması' : 'Snapshot Change Comparison'}
                </h3>
              </div>
              <button
                type="button"
                onClick={() => setIsDiffOpen(false)}
                className="p-1 rounded text-neutral-400 hover:text-neutral-600 dark:hover:text-neutral-200"
                data-dialog-close
                aria-label={isTr ? 'Karşılaştırmayı kapat' : 'Close comparison'}
              >
                <X className="w-5 h-5" />
              </button>
            </div>

            <div className="p-5 space-y-5">
              <div className="grid grid-cols-1 md:grid-cols-[1fr_auto_1fr_auto] gap-3 items-end">
                <label className="text-xs text-neutral-500">
                  {isTr ? 'Kaynak A (eski)' : 'Source A (older)'}
                  <select
                    value={compareA}
                    onChange={(event) => {
                      setCompareA(event.target.value);
                      setDiff(null);
                    }}
                    className="mt-1 h-10 w-full rounded-lg border border-neutral-300 dark:border-dark-border bg-white dark:bg-dark-card px-3 text-sm text-neutral-800 dark:text-neutral-200"
                  >
                    <option value="">{isTr ? 'Snapshot seçin' : 'Select snapshot'}</option>
                    {snapshots.map((snapshot) => (
                      <option key={snapshot.id} value={snapshot.id}>
                        v{snapshot.snapshotVersion} · {snapshot.name}
                      </option>
                    ))}
                  </select>
                </label>
                <ArrowRight className="hidden md:block w-4 h-4 text-neutral-400 mb-3" />
                <label className="text-xs text-neutral-500">
                  {isTr ? 'Hedef B (yeni)' : 'Target B (newer)'}
                  <select
                    value={compareB}
                    onChange={(event) => {
                      setCompareB(event.target.value);
                      setDiff(null);
                    }}
                    className="mt-1 h-10 w-full rounded-lg border border-neutral-300 dark:border-dark-border bg-white dark:bg-dark-card px-3 text-sm text-neutral-800 dark:text-neutral-200"
                  >
                    <option value="">{isTr ? 'Snapshot seçin' : 'Select snapshot'}</option>
                    {snapshots.map((snapshot) => (
                      <option key={snapshot.id} value={snapshot.id}>
                        v{snapshot.snapshotVersion} · {snapshot.name}
                      </option>
                    ))}
                  </select>
                </label>
                <Button
                  variant="primary"
                  size="sm"
                  className="bg-brand-primary h-10"
                  onClick={() => void handleCompare()}
                  disabled={diffLoading || !compareA || !compareB || compareA === compareB}
                >
                  {diffLoading ? <Loader2 className="w-4 h-4 animate-spin" /> : <GitCompare className="w-4 h-4" />}
                  {isTr ? 'Karşılaştır' : 'Compare'}
                </Button>
              </div>

              {diffError && (
                <div className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-xs text-rose-700 dark:text-rose-300 flex items-start gap-2">
                  <AlertTriangle className="w-4 h-4 mt-0.5 shrink-0" />
                  <span>{diffError}</span>
                </div>
              )}

              {diffLoading ? (
                <div className="space-y-2">
                  {[0, 1, 2, 3].map((index) => (
                    <div
                      key={index}
                      className="h-16 rounded-lg bg-neutral-100 dark:bg-dark-border animate-pulse"
                    />
                  ))}
                </div>
              ) : diff ? (
                <div className="space-y-5">
                  <div className="p-3 rounded-lg bg-neutral-100 dark:bg-dark-card border border-neutral-200 dark:border-dark-border text-xs flex flex-wrap items-center justify-between gap-2">
                    <div>
                      <span className="text-neutral-400 block text-[10px] uppercase font-bold">A</span>
                      <span className="font-semibold text-neutral-800 dark:text-neutral-200">
                        {diff.snapshotAName}
                      </span>
                    </div>
                    <ArrowRight className="w-4 h-4 text-neutral-400" />
                    <div>
                      <span className="text-neutral-400 block text-[10px] uppercase font-bold">B</span>
                      <span className="font-semibold text-brand-primary dark:text-brand-primary-dark">
                        {diff.snapshotBName}
                      </span>
                    </div>
                  </div>

                  <div className="grid grid-cols-2 sm:grid-cols-3 gap-3">
                    <DiffCount label={isTr ? 'Yeni Düğüm' : 'Added Nodes'} value={diff.addedNodesCount} tone="added" />
                    <DiffCount label={isTr ? 'Silinen Düğüm' : 'Removed Nodes'} value={diff.removedNodesCount} tone="removed" />
                    <DiffCount label={isTr ? 'Değişen Düğüm' : 'Changed Nodes'} value={diff.changedNodesCount} tone="changed" />
                    <DiffCount
                      label={isTr ? 'Yeni İlişki' : 'Added Relationships'}
                      value={diff.addedRelationshipsCount}
                      tone="added"
                    />
                    <DiffCount
                      label={isTr ? 'Silinen İlişki' : 'Removed Relationships'}
                      value={diff.removedRelationshipsCount}
                      tone="removed"
                    />
                    <DiffCount
                      label={isTr ? 'Değişen İlişki' : 'Changed Relationships'}
                      value={diff.changedRelationshipsCount}
                      tone="changed"
                    />
                  </div>

                  <div className="p-3 rounded-lg border border-neutral-200 dark:border-dark-border">
                    <h4 className="text-xs font-bold uppercase tracking-wider text-neutral-500 mb-2">
                      {isTr ? 'Öne Çıkanlar' : 'Highlights'}
                    </h4>
                    <ul className="space-y-1 text-xs text-neutral-600 dark:text-neutral-300 list-disc list-inside">
                      {diff.highlights.map((highlight) => (
                        <li key={highlight}>{highlight}</li>
                      ))}
                    </ul>
                  </div>

                  <NodeChangeSection title={isTr ? 'Eklenen Düğümler' : 'Added Nodes'} nodes={diff.addedNodes} tone="added" />
                  <NodeChangeSection title={isTr ? 'Silinen Düğümler' : 'Removed Nodes'} nodes={diff.removedNodes} tone="removed" />
                  <ChangedNodeSection nodes={diff.changedNodes} />
                  <RelationshipChangeSection
                    title={isTr ? 'Eklenen İlişkiler' : 'Added Relationships'}
                    changes={diff.addedRelationships}
                    tone="added"
                  />
                  <RelationshipChangeSection
                    title={isTr ? 'Silinen İlişkiler' : 'Removed Relationships'}
                    changes={diff.removedRelationships}
                    tone="removed"
                  />
                  <RelationshipChangeSection
                    title={isTr ? 'Değişen İlişkiler' : 'Changed Relationships'}
                    changes={diff.changedRelationships}
                    tone="changed"
                  />
                </div>
              ) : (
                <div className="py-8 text-center text-sm text-neutral-500">
                  {isTr ? 'İki snapshot seçip “Karşılaştır” düğmesine basın.' : 'Select two snapshots and press “Compare”.'}
                </div>
              )}
            </div>
          </Card>
        </div>
      )}

      {detailSnapshotId && (
        <div
          ref={detailDialogRef}
          className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60 backdrop-blur-xs outline-none"
          role="dialog"
          aria-modal="true"
          aria-label={isTr ? 'Snapshot detayı' : 'Snapshot detail'}
          onClick={() => setDetailSnapshotId(null)}
        >
          <Card
            className="w-full max-w-5xl max-h-[90vh] overflow-y-auto border-neutral-200 dark:border-dark-border shadow-2xl"
            padding="none"
            onClick={(event) => event.stopPropagation()}
          >
            <div className="sticky top-0 z-10 flex items-center justify-between gap-3 p-5 border-b border-neutral-200 dark:border-dark-border bg-white dark:bg-dark-card">
              <div className="flex items-center gap-2 min-w-0">
                <Camera className="w-5 h-5 text-brand-primary shrink-0" />
                <div className="min-w-0">
                  <h3 className="font-bold text-base text-neutral-900 dark:text-white truncate">
                    {graphQuery.data?.snapshot.name ?? (isTr ? 'Snapshot detayı' : 'Snapshot detail')}
                  </h3>
                  <p className="text-[11px] text-neutral-500 font-mono truncate">
                    {detailSnapshotId}
                  </p>
                </div>
              </div>
              <button
                type="button"
                onClick={() => setDetailSnapshotId(null)}
                className="p-1 rounded text-neutral-400 hover:text-neutral-600 dark:hover:text-neutral-200"
                data-dialog-close
                aria-label={isTr ? 'Detayı kapat' : 'Close detail'}
              >
                <X className="w-5 h-5" />
              </button>
            </div>

            <div className="p-5 space-y-5">
              {graphQuery.isLoading ? (
                <div className="space-y-3">
                  <div className="h-24 rounded-lg bg-neutral-100 dark:bg-dark-border animate-pulse" />
                  <div className="h-48 rounded-lg bg-neutral-100 dark:bg-dark-border animate-pulse" />
                </div>
              ) : graphError ? (
                <div className="p-4 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300">
                  <div className="flex items-center gap-2">
                    <AlertTriangle className="w-4 h-4" />
                    {graphError}
                  </div>
                  <Button
                    variant="outline"
                    size="sm"
                    className="mt-3"
                    onClick={() => void graphQuery.refetch()}
                  >
                    {isTr ? 'Tekrar Dene' : 'Retry'}
                  </Button>
                </div>
              ) : graphQuery.data ? (
                <>
                  <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3">
                    <SnapshotMeta
                      label={isTr ? 'Sürüm' : 'Version'}
                      value={`v${graphQuery.data.snapshot.snapshotVersion}`}
                    />
                    <SnapshotMeta
                      label={isTr ? 'Oluşturulma' : 'Created At'}
                      value={formatDateTime(graphQuery.data.snapshot.createdAt)}
                    />
                    <SnapshotMeta
                      label={isTr ? 'Oluşturan' : 'Created By'}
                      value={graphQuery.data.snapshot.createdBy || '—'}
                    />
                    <SnapshotMeta
                      label={isTr ? 'Yakalama Zamanı' : 'Graph Captured At'}
                      value={formatDateTime(graphQuery.data.snapshot.graphCapturedAt)}
                    />
                    <SnapshotMeta
                      label={isTr ? 'Düğüm Sayısı' : 'Node Count'}
                      value={formatNumber(graphQuery.data.snapshot.nodeCount)}
                    />
                    <SnapshotMeta
                      label={isTr ? 'İlişki Sayısı' : 'Relationship Count'}
                      value={formatNumber(graphQuery.data.snapshot.relationshipCount)}
                    />
                    <SnapshotMeta
                      label={isTr ? 'Değiştirilemez' : 'Immutable'}
                      value={graphQuery.data.snapshot.isImmutable ? (isTr ? 'Evet' : 'Yes') : (isTr ? 'Hayır' : 'No')}
                    />
                    <SnapshotMeta label={isTr ? 'Graf Hash' : 'Graph Hash'} value={graphQuery.data.snapshot.hash} mono />
                  </div>

                  {graphQuery.data.snapshot.warnings.length > 0 && (
                    <div className="p-3 rounded-lg border border-amber-200 dark:border-amber-900 bg-amber-50/60 dark:bg-amber-950/20 text-xs text-amber-800 dark:text-amber-300 space-y-1">
                      {graphQuery.data.snapshot.warnings.map((warning) => (
                        <div key={warning} className="flex items-start gap-1.5">
                          <AlertTriangle className="w-4 h-4 mt-0.5 shrink-0" />
                          <span>{warning}</span>
                        </div>
                      ))}
                    </div>
                  )}

                  <section>
                    <h4 className="text-xs font-bold uppercase tracking-wider text-neutral-500 mb-2">
                      {isTr ? 'Snapshot Düğümleri' : 'Snapshot Nodes'} ({formatNumber(graphQuery.data.nodes.length)})
                    </h4>
                    <div className="overflow-x-auto rounded-lg border border-neutral-200 dark:border-dark-border max-h-80 overflow-y-auto">
                      <table className="w-full text-left text-xs">
                        <thead className="sticky top-0 bg-neutral-50 dark:bg-dark-bg text-neutral-500">
                          <tr>
                            <th className="p-2.5">{isTr ? 'Ad / FQDN' : 'Name / FQDN'}</th>
                            <th className="p-2.5">{isTr ? 'Tür' : 'Type'}</th>
                            <th className="p-2.5">IP</th>
                            <th className="p-2.5">{isTr ? 'Kaynak' : 'Source'}</th>
                            <th className="p-2.5 text-right">{isTr ? 'Durum' : 'Status'}</th>
                          </tr>
                        </thead>
                        <tbody className="divide-y divide-neutral-100 dark:divide-dark-border">
                          {graphQuery.data.nodes.slice(0, 200).map((node) => (
                            <tr key={node.id}>
                              <td className="p-2.5">
                                <div className="font-medium text-neutral-900 dark:text-white">
                                  {node.name}
                                </div>
                                <div className="font-mono text-[10px] text-neutral-500">
                                  {node.fqdn || shortId(node.nodeId)}
                                </div>
                              </td>
                              <td className="p-2.5 text-neutral-600 dark:text-neutral-300">
                                {node.nodeType}
                              </td>
                              <td className="p-2.5 font-mono text-neutral-600 dark:text-neutral-300">
                                {node.ipAddress || '—'}
                              </td>
                              <td className="p-2.5 text-neutral-600 dark:text-neutral-300">
                                {node.discoverySource || '—'}
                              </td>
                              <td className="p-2.5 text-right">
                                <span className="inline-flex items-center gap-1.5">
                                  <StatusIndicator status={toNodeStatus(node.status)} size="sm" />
                                  <span className="text-neutral-600 dark:text-neutral-300">
                                    {node.status}
                                  </span>
                                </span>
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                    {graphQuery.data.nodes.length > 200 && (
                      <p className="mt-1 text-[11px] text-neutral-500">
                        {isTr
              ? `İlk 200 düğüm gösteriliyor; toplam ${formatNumber(graphQuery.data.nodes.length)}.`
              : `Showing first 200 nodes of ${formatNumber(graphQuery.data.nodes.length)} total.`}
                      </p>
                    )}
                  </section>

                  <section>
                    <h4 className="text-xs font-bold uppercase tracking-wider text-neutral-500 mb-2">
                      {isTr ? 'Snapshot İlişkileri' : 'Snapshot Relationships'} ({formatNumber(graphQuery.data.relationships.length)})
                    </h4>
                    <div className="overflow-x-auto rounded-lg border border-neutral-200 dark:border-dark-border max-h-80 overflow-y-auto">
                      <table className="w-full text-left text-xs">
                        <thead className="sticky top-0 bg-neutral-50 dark:bg-dark-bg text-neutral-500">
                          <tr>
                            <th className="p-2.5">{isTr ? 'Kaynak' : 'Source'}</th>
                            <th className="p-2.5">{isTr ? 'İlişki' : 'Relationship'}</th>
                            <th className="p-2.5">{isTr ? 'Hedef' : 'Target'}</th>
                            <th className="p-2.5">{isTr ? 'Güven' : 'Confidence'}</th>
                            <th className="p-2.5">{isTr ? 'Kaynak Sistem' : 'Source System'}</th>
                          </tr>
                        </thead>
                        <tbody className="divide-y divide-neutral-100 dark:divide-dark-border">
                          {graphQuery.data.relationships.slice(0, 200).map((relationship) => (
                            <tr key={relationship.id}>
                              <td className="p-2.5 text-neutral-800 dark:text-neutral-200">
                                {relationship.sourceNodeName}
                              </td>
                              <td className="p-2.5 font-mono text-[10px] text-neutral-600 dark:text-neutral-300">
                                {relationship.relationshipType}
                              </td>
                              <td className="p-2.5 text-neutral-800 dark:text-neutral-200">
                                {relationship.targetNodeName}
                              </td>
                              <td className="p-2.5 text-neutral-600 dark:text-neutral-300">
                                {relationship.confidenceLevel}
                              </td>
                              <td className="p-2.5 text-neutral-600 dark:text-neutral-300">
                                {relationship.sourceSystem}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                    {graphQuery.data.relationships.length > 200 && (
                      <p className="mt-1 text-[11px] text-neutral-500">
                        {isTr
              ? `İlk 200 ilişki gösteriliyor; toplam ${formatNumber(graphQuery.data.relationships.length)}.`
              : `Showing first 200 relationships of ${formatNumber(graphQuery.data.relationships.length)} total.`}
                      </p>
                    )}
                  </section>
                </>
              ) : null}
            </div>
          </Card>
        </div>
      )}

      {verification && (
        <div
          ref={verificationDialogRef}
          className="fixed inset-0 z-[60] flex items-center justify-center p-4 bg-black/60 backdrop-blur-xs"
          role="dialog"
          aria-modal="true"
          aria-label={isTr ? 'Snapshot doğrulama sonucu' : 'Snapshot verification result'}
          onClick={() => setVerification(null)}
        >
          <Card
            className="w-full max-w-2xl p-6 border-neutral-200 dark:border-dark-border shadow-2xl space-y-4"
            onClick={(event) => event.stopPropagation()}
          >
            <div className="flex items-start justify-between gap-3 border-b border-neutral-200 dark:border-dark-border pb-3">
              <div className="flex items-center gap-2">
                {verification.isValid ? (
                  <ShieldCheck className="w-5 h-5 text-emerald-600" />
                ) : (
                  <ShieldAlert className="w-5 h-5 text-rose-600" />
                )}
                <div>
                  <h3 className="font-bold text-base text-neutral-900 dark:text-white">
                    {isTr ? 'Snapshot Bütünlük Doğrulaması' : 'Snapshot Integrity Verification'}
                  </h3>
                  <p className="text-xs text-neutral-500">{verification.snapshotName}</p>
                </div>
              </div>
              <button
                type="button"
                onClick={() => setVerification(null)}
                className="p-1 rounded text-neutral-400 hover:text-neutral-600 dark:hover:text-neutral-200"
                data-dialog-close
                aria-label={isTr ? 'Doğrulama sonucunu kapat' : 'Close verification result'}
              >
                <X className="w-5 h-5" />
              </button>
            </div>

            <div
              className={`p-3 rounded-lg border text-sm font-semibold ${
                verification.isValid
                  ? 'border-emerald-200 dark:border-emerald-900 bg-emerald-50 dark:bg-emerald-950/30 text-emerald-800 dark:text-emerald-300'
                  : 'border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-rose-800 dark:text-rose-300'
              }`}
            >
              {verification.isValid
                ? (isTr
                  ? 'Graf bütünlüğü doğrulandı: saklanan SHA-256 özeti ve satır sayıları eşleşiyor.'
                  : 'Graph integrity verified: stored SHA-256 digest and row counts match.')
                : (isTr
                  ? 'Graf bütünlüğü doğrulanamadı. Farklar aşağıda listelenmiştir.'
                  : 'Graph integrity could not be verified. Differences are listed below.')}
            </div>

            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-xs">
              <SnapshotMeta label={isTr ? 'Saklanan Düğüm / Gerçek' : 'Stored / Actual Nodes'} value={`${verification.storedNodeCount} / ${verification.actualNodeCount}`} />
              <SnapshotMeta
                label={isTr ? 'Saklanan İlişki / Gerçek' : 'Stored / Actual Relationships'}
                value={`${verification.storedRelationshipCount} / ${verification.actualRelationshipCount}`}
              />
              <SnapshotMeta label={isTr ? 'Saklanan Hash' : 'Stored Hash'} value={verification.storedHash} mono />
              <SnapshotMeta label={isTr ? 'Hesaplanan Hash' : 'Computed Hash'} value={verification.computedHash} mono />
            </div>

            {verification.differences.length > 0 && (
              <div>
                <h4 className="text-xs font-bold uppercase tracking-wider text-neutral-500 mb-2">
                  {isTr ? 'Farklar' : 'Differences'}
                </h4>
                <ul className="space-y-1 text-xs text-rose-700 dark:text-rose-300 list-disc list-inside">
                  {verification.differences.map((difference) => (
                    <li key={difference}>{difference}</li>
                  ))}
                </ul>
              </div>
            )}

            <div className="flex justify-between items-center pt-2 text-[11px] text-neutral-500">
              <span>{isTr ? 'Doğrulama:' : 'Verified:'} {formatDateTime(verification.verifiedAt)}</span>
              <Button variant="outline" size="sm" onClick={() => setVerification(null)}>
                {isTr ? 'Kapat' : 'Close'}
              </Button>
            </div>
          </Card>
        </div>
      )}
    </div>
  );
};

function DiffCount({
  label,
  value,
  tone,
}: {
  label: string;
  value: number;
  tone: 'added' | 'removed' | 'changed';
}) {
  const toneClass =
    tone === 'added'
      ? 'bg-emerald-50 dark:bg-emerald-950/30 border-emerald-200 dark:border-emerald-800 text-emerald-700 dark:text-emerald-400'
      : tone === 'removed'
      ? 'bg-rose-50 dark:bg-rose-950/30 border-rose-200 dark:border-rose-800 text-rose-700 dark:text-rose-400'
      : 'bg-amber-50 dark:bg-amber-950/30 border-amber-200 dark:border-amber-800 text-amber-700 dark:text-amber-400';

  return (
    <div className={`p-3 rounded-lg border text-center ${toneClass}`}>
      <div className="text-[10px] uppercase font-bold opacity-80">{label}</div>
      <div className="text-lg font-bold">{formatNumber(value)}</div>
    </div>
  );
}

function SnapshotMeta({
  label,
  value,
  mono = false,
}: {
  label: string;
  value: string;
  mono?: boolean;
}) {
  return (
    <div className="p-3 rounded-lg border border-neutral-200 dark:border-dark-border bg-neutral-50 dark:bg-dark-bg min-w-0">
      <div className="text-[10px] uppercase tracking-wide text-neutral-500">{label}</div>
      <div
        className={`mt-0.5 text-xs text-neutral-900 dark:text-neutral-100 break-all ${
          mono ? 'font-mono' : ''
        }`}
        title={value}
      >
        {value}
      </div>
    </div>
  );
}
