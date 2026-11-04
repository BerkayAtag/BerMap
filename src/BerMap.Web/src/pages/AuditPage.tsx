import React, { Fragment, useId, useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import {
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  Download,
  Filter,
  Loader2,
  RefreshCw,
  ShieldAlert,
  ShieldCheck,
  X,
} from 'lucide-react';
import { Card } from '@/components/ui/Card';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { canViewAudit } from '@/components/users/userText';
import { useLocale } from '@/lib/i18n';
import { api } from '@/lib/api';
import { downloadServerCsv } from '@/lib/csv';
import { formatDateTime, formatNumber, getErrorMessage } from '@/lib/format';
import { useAuthStore } from '@/stores/authStore';
import type { ApiError, AuditEntry, AuditPage as AuditPageResult } from '@/types';

interface AuditFilters {
  action: string;
  entityType: string;
  username: string;
  /** '' = all, 'true' / 'false' = only successful / failed entries. */
  success: '' | 'true' | 'false';
  /** Local calendar dates (yyyy-mm-dd) from the date inputs. */
  from: string;
  to: string;
}

const EMPTY_FILTERS: AuditFilters = { action: '', entityType: '', username: '', success: '', from: '', to: '' };
const PAGE_SIZES = [25, 50, 100, 200];

/* Actions and entity types the API writes today; the inputs still accept any value. */
const KNOWN_ACTIONS = [
  'Auth.Login',
  'Auth.LoginFailed',
  'Auth.PasswordChanged',
  'User.Created',
  'User.Updated',
  'User.PasswordReset',
  'IntegrationCreated',
  'IntegrationUpdated',
  'IntegrationDeleted',
  'IntegrationConnectionTested',
  'DiscoveryQueued',
  'DiscoveryCancelled',
  'DiscoveryFailed',
  'ReconciliationReviewMerged',
  'ReconciliationReviewDismissed',
  'Snapshot.Created',
  'Collector.Deleted',
];
const KNOWN_ENTITY_TYPES = ['User', 'Integration', 'DiscoveryJob', 'ReconciliationReview', 'Snapshot', 'Collector'];

/** Query string for /audit and /audit/export. Dates are sent as the local day's bounds in UTC. */
function buildQuery(filters: AuditFilters, page?: number, pageSize?: number): string {
  const params = new URLSearchParams();
  if (page) params.set('page', String(page));
  if (pageSize) params.set('pageSize', String(pageSize));
  if (filters.action.trim()) params.set('action', filters.action.trim());
  if (filters.entityType.trim()) params.set('entityType', filters.entityType.trim());
  if (filters.username.trim()) params.set('username', filters.username.trim());
  if (filters.success) params.set('success', filters.success);
  if (filters.from) {
    const from = new Date(`${filters.from}T00:00:00`);
    if (!Number.isNaN(from.getTime())) params.set('from', from.toISOString());
  }
  if (filters.to) {
    const to = new Date(`${filters.to}T23:59:59.999`);
    if (!Number.isNaN(to.getTime())) params.set('to', to.toISOString());
  }
  const text = params.toString();
  return text ? `?${text}` : '';
}

function prettyDetails(json: string): string {
  if (!json || json === '{}') return '';
  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
}

function isForbidden(error: unknown): boolean {
  return (error as Partial<ApiError> | null)?.status === 403;
}

export const AuditPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const role = useAuthStore((state) => state.user?.role);
  const allowed = canViewAudit(role);
  const fieldId = useId();
  const [draft, setDraft] = useState<AuditFilters>(EMPTY_FILTERS);
  const [filters, setFilters] = useState<AuditFilters>(EMPTY_FILTERS);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState<string | null>(null);

  const query = useQuery<AuditPageResult>({
    queryKey: ['audit', 'list', filters, page, pageSize],
    queryFn: () => api.get<AuditPageResult>(`/audit${buildQuery(filters, page, pageSize)}`),
    enabled: allowed,
    placeholderData: keepPreviousData,
    refetchInterval: page === 1 ? 30_000 : false,
  });

  if (!allowed || isForbidden(query.error)) {
    return (
      <Card className="max-w-xl mx-auto text-center">
        <ShieldAlert className="w-10 h-10 mx-auto text-neutral-300 dark:text-neutral-600" />
        <h1 className="mt-3 font-bold text-neutral-900 dark:text-white">
          {isTr ? 'Denetim günlüklerini görme yetkiniz yok' : 'You cannot view the audit log'}
        </h1>
        <p className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">
          {isTr
            ? 'Denetim kayıtları yalnızca Platform Yöneticileri ve Güvenlik Denetçileri tarafından görüntülenebilir.'
            : 'Audit records are available to Platform Administrators and Security Auditors only.'}
        </p>
      </Card>
    );
  }

  const data = query.data;
  const items = data?.items ?? [];
  const totalCount = data?.totalCount ?? 0;
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));
  const hasFilters = Object.values(filters).some((value) => value !== '');
  const dateRangeInvalid = draft.from !== '' && draft.to !== '' && draft.from > draft.to;

  const applyFilters = (event?: React.FormEvent) => {
    event?.preventDefault();
    if (dateRangeInvalid) return;
    setFilters(draft);
    setPage(1);
    setExpandedId(null);
  };

  const clearFilters = () => {
    setDraft(EMPTY_FILTERS);
    setFilters(EMPTY_FILTERS);
    setPage(1);
    setExpandedId(null);
  };

  const handleExport = async () => {
    setExporting(true);
    setExportError(null);
    try {
      await downloadServerCsv(`/audit/export${buildQuery(filters)}`, `bermap-audit-${new Date().toISOString().slice(0, 10)}.csv`);
    } catch (error) {
      setExportError(
        isForbidden(error)
          ? (isTr ? 'Denetim kayıtlarını dışa aktarma yetkiniz yok.' : 'You are not allowed to export audit records.')
          : getErrorMessage(error),
      );
    } finally {
      setExporting(false);
    }
  };

  const updateDraft = <K extends keyof AuditFilters>(key: K, value: AuditFilters[K]) =>
    setDraft((current) => ({ ...current, [key]: value }));

  const labelClass = 'block text-[11px] font-semibold text-neutral-600 dark:text-neutral-400 mb-1';
  const selectClass =
    'h-10 w-full rounded-md border px-3 text-sm bg-white text-neutral-900 border-neutral-300 outline-none ' +
    'focus:border-brand-primary focus:ring-2 focus:ring-brand-primary/20 ' +
    'dark:bg-dark-card dark:text-dark-text dark:border-dark-border dark:focus:border-brand-primary-dark';

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
            {isTr ? 'Denetim Günlükleri' : 'Audit Logs'}
          </h1>
          <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1">
            {isTr
              ? 'Oturum açma, kullanıcı, entegrasyon, keşif ve snapshot işlemlerinin kaydı'
              : 'Record of sign-ins and user, integration, discovery and snapshot operations'}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            className="gap-2"
            onClick={() => void query.refetch()}
            disabled={query.isFetching}
          >
            <RefreshCw className={`w-4 h-4 ${query.isFetching ? 'animate-spin' : ''}`} />
            {isTr ? 'Yenile' : 'Refresh'}
          </Button>
          <Button
            variant="primary"
            size="sm"
            className="gap-2"
            onClick={() => void handleExport()}
            disabled={exporting || totalCount === 0}
            title={isTr ? 'Geçerli filtrelerle CSV olarak dışa aktar (en fazla 50.000 satır)' : 'Export with the current filters as CSV (max 50,000 rows)'}
          >
            {exporting ? <Loader2 className="w-4 h-4 animate-spin" /> : <Download className="w-4 h-4" />}
            {isTr ? 'CSV Dışa Aktar' : 'Export CSV'}
          </Button>
        </div>
      </div>

      {exportError && (
        <div role="alert" className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300 flex items-start gap-2">
          <span className="flex-1 break-words">{exportError}</span>
          <button type="button" onClick={() => setExportError(null)} aria-label={isTr ? 'Hatayı kapat' : 'Close error'}>
            <X className="w-4 h-4" />
          </button>
        </div>
      )}

      <Card padding="sm">
        <form onSubmit={applyFilters} className="space-y-3">
          <div className="flex items-center gap-2 text-sm font-semibold text-neutral-800 dark:text-neutral-200">
            <Filter className="w-4 h-4 text-brand-primary dark:text-brand-primary-dark" />
            {isTr ? 'Filtreler' : 'Filters'}
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-6 gap-3">
            <div>
              <label htmlFor={`${fieldId}-action`} className={labelClass}>{isTr ? 'İşlem' : 'Action'}</label>
              <Input
                id={`${fieldId}-action`}
                list={`${fieldId}-actions`}
                value={draft.action}
                onChange={(e) => updateDraft('action', e.target.value)}
                placeholder={isTr ? 'Tümü' : 'All'}
              />
              <datalist id={`${fieldId}-actions`}>
                {KNOWN_ACTIONS.map((action) => <option key={action} value={action} />)}
              </datalist>
            </div>
            <div>
              <label htmlFor={`${fieldId}-entity`} className={labelClass}>{isTr ? 'Varlık Türü' : 'Entity Type'}</label>
              <Input
                id={`${fieldId}-entity`}
                list={`${fieldId}-entities`}
                value={draft.entityType}
                onChange={(e) => updateDraft('entityType', e.target.value)}
                placeholder={isTr ? 'Tümü' : 'All'}
              />
              <datalist id={`${fieldId}-entities`}>
                {KNOWN_ENTITY_TYPES.map((type) => <option key={type} value={type} />)}
              </datalist>
            </div>
            <div>
              <label htmlFor={`${fieldId}-user`} className={labelClass}>{isTr ? 'Kullanıcı' : 'User'}</label>
              <Input
                id={`${fieldId}-user`}
                value={draft.username}
                onChange={(e) => updateDraft('username', e.target.value)}
                placeholder={isTr ? 'İçerir…' : 'Contains…'}
              />
            </div>
            <div>
              <label htmlFor={`${fieldId}-success`} className={labelClass}>{isTr ? 'Sonuç' : 'Result'}</label>
              <select
                id={`${fieldId}-success`}
                value={draft.success}
                onChange={(e) => updateDraft('success', e.target.value as AuditFilters['success'])}
                className={selectClass}
              >
                <option value="">{isTr ? 'Tümü' : 'All'}</option>
                <option value="true">{isTr ? 'Başarılı' : 'Success'}</option>
                <option value="false">{isTr ? 'Başarısız' : 'Failed'}</option>
              </select>
            </div>
            <div>
              <label htmlFor={`${fieldId}-from`} className={labelClass}>{isTr ? 'Başlangıç' : 'From'}</label>
              <Input id={`${fieldId}-from`} type="date" value={draft.from} onChange={(e) => updateDraft('from', e.target.value)} />
            </div>
            <div>
              <label htmlFor={`${fieldId}-to`} className={labelClass}>{isTr ? 'Bitiş' : 'To'}</label>
              <Input
                id={`${fieldId}-to`}
                type="date"
                value={draft.to}
                onChange={(e) => updateDraft('to', e.target.value)}
                error={dateRangeInvalid ? (isTr ? 'Bitiş, başlangıçtan önce olamaz.' : 'To must not be before From.') : undefined}
              />
            </div>
          </div>
          <div className="flex flex-wrap items-center justify-end gap-2">
            {hasFilters && (
              <Button type="button" variant="ghost" size="sm" onClick={clearFilters}>
                {isTr ? 'Filtreleri Temizle' : 'Clear Filters'}
              </Button>
            )}
            <Button type="submit" variant="outline" size="sm" disabled={dateRangeInvalid}>
              {isTr ? 'Uygula' : 'Apply'}
            </Button>
          </div>
        </form>
      </Card>

      {query.isError && (
        <div role="alert" className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300">
          {isTr ? 'Denetim günlükleri yüklenemedi.' : 'Failed to load audit logs.'} {getErrorMessage(query.error)}
        </div>
      )}

      <Card padding="sm">
        <div className="flex flex-wrap items-center justify-between gap-2 mb-3">
          <div className="flex items-center gap-2">
            <ShieldCheck className="w-4 h-4 text-emerald-600" />
            <span className="text-sm font-semibold text-neutral-800 dark:text-neutral-200">
              {hasFilters ? (isTr ? 'Eşleşen Kayıt' : 'Matching Records') : (isTr ? 'Toplam Kayıt' : 'Total Records')}:{' '}
              {formatNumber(totalCount)}
            </span>
          </div>
          <span className="text-[11px] text-neutral-500">
            {isTr ? 'Ayrıntılar için satıra tıklayın' : 'Click a row for details'}
          </span>
        </div>

        {query.isLoading && !data && (
          <div className="flex items-center gap-2 text-sm text-neutral-500">
            <Loader2 className="w-4 h-4 animate-spin" />
            {isTr ? 'Yükleniyor...' : 'Loading...'}
          </div>
        )}

        {!query.isLoading && items.length === 0 && (
          <p className="text-sm text-neutral-500">
            {hasFilters
              ? (isTr ? 'Filtrelerle eşleşen kayıt yok.' : 'No records match the filters.')
              : (isTr ? 'Henüz denetim kaydı yok.' : 'No audit records yet.')}
          </p>
        )}

        {items.length > 0 && (
          <div className={`overflow-x-auto ${query.isFetching && query.isPlaceholderData ? 'opacity-60' : ''}`}>
            <table className="w-full text-sm">
              <thead>
                <tr className="text-left text-xs text-neutral-500 border-b border-neutral-200 dark:border-dark-border">
                  <th className="py-2 pr-3 whitespace-nowrap">{isTr ? 'Zaman' : 'Time'}</th>
                  <th className="py-2 pr-3">{isTr ? 'Kullanıcı' : 'User'}</th>
                  <th className="py-2 pr-3">{isTr ? 'İşlem' : 'Action'}</th>
                  <th className="py-2 pr-3 whitespace-nowrap">{isTr ? 'Varlık Türü' : 'Entity Type'}</th>
                  <th className="py-2 pr-3 whitespace-nowrap">{isTr ? 'Varlık ID' : 'Entity ID'}</th>
                  <th className="py-2 pr-3">IP</th>
                  <th className="py-2 pr-3">{isTr ? 'Sonuç' : 'Result'}</th>
                </tr>
              </thead>
              <tbody>
                {items.map((entry) => (
                  <AuditRow
                    key={entry.id}
                    entry={entry}
                    isTr={isTr}
                    expanded={expandedId === entry.id}
                    onToggle={() => setExpandedId(expandedId === entry.id ? null : entry.id)}
                  />
                ))}
              </tbody>
            </table>
          </div>
        )}

        {totalCount > 0 && (
          <div className="mt-4 flex flex-wrap items-center justify-between gap-3 text-xs text-neutral-600 dark:text-neutral-400">
            <label className="flex items-center gap-2">
              {isTr ? 'Sayfa başına' : 'Per page'}
              <select
                value={pageSize}
                onChange={(e) => {
                  setPageSize(Number(e.target.value));
                  setPage(1);
                }}
                className="h-8 rounded-md border px-2 text-xs bg-white text-neutral-900 border-neutral-300 dark:bg-dark-card dark:text-dark-text dark:border-dark-border"
              >
                {PAGE_SIZES.map((size) => (
                  <option key={size} value={size}>{size}</option>
                ))}
              </select>
            </label>
            <div className="flex items-center gap-2">
              <Button
                variant="outline"
                size="sm"
                className="px-2"
                onClick={() => setPage((current) => Math.max(1, current - 1))}
                disabled={page <= 1 || query.isFetching}
                aria-label={isTr ? 'Önceki sayfa' : 'Previous page'}
              >
                <ChevronLeft className="w-4 h-4" />
              </Button>
              <span className="tabular-nums">
                {isTr
                  ? `Sayfa ${formatNumber(page)} / ${formatNumber(totalPages)}`
                  : `Page ${formatNumber(page)} of ${formatNumber(totalPages)}`}
              </span>
              <Button
                variant="outline"
                size="sm"
                className="px-2"
                onClick={() => setPage((current) => Math.min(totalPages, current + 1))}
                disabled={page >= totalPages || query.isFetching}
                aria-label={isTr ? 'Sonraki sayfa' : 'Next page'}
              >
                <ChevronRight className="w-4 h-4" />
              </Button>
            </div>
          </div>
        )}
      </Card>
    </div>
  );
};

const AuditRow: React.FC<{ entry: AuditEntry; isTr: boolean; expanded: boolean; onToggle: () => void }> = ({
  entry,
  isTr,
  expanded,
  onToggle,
}) => {
  const details = prettyDetails(entry.detailsJson);
  return (
    <Fragment>
      <tr
        className={`border-b border-neutral-100 dark:border-dark-border/40 cursor-pointer hover:bg-neutral-50 dark:hover:bg-dark-bg/60 ${
          expanded ? 'bg-neutral-50 dark:bg-dark-bg/60' : ''
        }`}
        onClick={onToggle}
      >
        <td className="py-2 pr-3 text-xs text-neutral-500 whitespace-nowrap">
          <button
            type="button"
            className="inline-flex items-center gap-1"
            aria-expanded={expanded}
            onClick={(event) => {
              event.stopPropagation();
              onToggle();
            }}
          >
            {expanded ? <ChevronDown className="w-3.5 h-3.5" /> : <ChevronRight className="w-3.5 h-3.5" />}
            {formatDateTime(entry.createdAt)}
          </button>
        </td>
        <td className="py-2 pr-3 text-neutral-800 dark:text-neutral-200">{entry.username || entry.userId || '—'}</td>
        <td className="py-2 pr-3 font-medium text-neutral-900 dark:text-white">{entry.action}</td>
        <td className="py-2 pr-3 text-neutral-700 dark:text-neutral-300">{entry.entityType || '—'}</td>
        <td className="py-2 pr-3 text-xs font-mono text-neutral-600 dark:text-neutral-400 max-w-40 truncate" title={entry.entityId}>
          {entry.entityId || '—'}
        </td>
        <td className="py-2 pr-3 text-xs text-neutral-600 dark:text-neutral-400">{entry.ipAddress || '—'}</td>
        <td className={`py-2 pr-3 ${entry.success ? 'text-emerald-600 dark:text-emerald-400' : 'text-rose-600 dark:text-rose-400'}`}>
          {entry.success ? (isTr ? 'Başarılı' : 'Success') : (isTr ? 'Başarısız' : 'Failed')}
        </td>
      </tr>
      {expanded && (
        <tr className="bg-neutral-50 dark:bg-dark-bg/60 border-b border-neutral-100 dark:border-dark-border/40">
          <td colSpan={7} className="px-3 pb-3 pt-1">
            <div className="text-[11px] text-neutral-500 mb-1 break-all">
              {isTr ? 'Kayıt' : 'Record'} {entry.id}
              {entry.userId && ` · ${isTr ? 'Kullanıcı ID' : 'User ID'} ${entry.userId}`}
            </div>
            {details ? (
              <pre className="p-3 rounded-lg bg-neutral-900 text-neutral-200 font-mono text-[11px] overflow-x-auto whitespace-pre-wrap break-words max-h-72">
                {details}
              </pre>
            ) : (
              <p className="text-xs text-neutral-500">{isTr ? 'Ek ayrıntı kaydedilmedi.' : 'No additional details recorded.'}</p>
            )}
          </td>
        </tr>
      )}
    </Fragment>
  );
};
