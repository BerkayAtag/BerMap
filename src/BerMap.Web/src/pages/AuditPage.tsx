import React from 'react';
import { useQuery } from '@tanstack/react-query';
import { useLocale } from '@/lib/i18n';
import { api } from '@/lib/api';
import { Card } from '@/components/ui/Card';
import { Loader2, ShieldCheck } from 'lucide-react';

interface AuditEntry {
  id: string;
  userId: string;
  username: string;
  action: string;
  entityType: string;
  entityId: string;
  detailsJson: string;
  ipAddress: string;
  success: boolean;
  createdAt: string;
}

interface AuditResponse {
  items: AuditEntry[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export const AuditPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';

  const { data, isLoading, isError, refetch } = useQuery<AuditResponse>({
    queryKey: ['audit', 'list'],
    queryFn: () => api.get<AuditResponse>('/audit?page=1&pageSize=100'),
    refetchInterval: 30_000,
  });

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
            {isTr ? 'Denetim Günlükleri' : 'Audit Logs'}
          </h1>
          <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1">
            {isTr
              ? 'Sistem üzerinde gerçekleştirilen işlemlerin denetim kaydı (gerçek veri)'
              : 'Audit trail of actions performed on the system (real data)'}
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
          {isTr ? 'Denetim günlükleri yüklenemedi.' : 'Failed to load audit logs.'}
        </div>
      )}

      <Card className="p-4">
        <div className="flex items-center gap-2 mb-3">
          <ShieldCheck className="w-4 h-4 text-emerald-600" />
          <span className="text-sm font-semibold">
            {isTr ? 'Toplam Kayıt' : 'Total Records'}: {data?.totalCount ?? 0}
          </span>
        </div>

        {isLoading && !data && (
          <div className="flex items-center gap-2 text-sm text-neutral-500">
            <Loader2 className="w-4 h-4 animate-spin" />
            {isTr ? 'Yükleniyor...' : 'Loading...'}
          </div>
        )}

        {!isLoading && (data?.items?.length ?? 0) === 0 && (
          <p className="text-sm text-neutral-500">
            {isTr ? 'Henüz denetim kaydı yok.' : 'No audit records yet.'}
          </p>
        )}

        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-xs text-neutral-500 border-b border-neutral-200 dark:border-dark-border">
                <th className="py-2 pr-3">{isTr ? 'Zaman' : 'Time'}</th>
                <th className="py-2 pr-3">{isTr ? 'Kullanıcı' : 'User'}</th>
                <th className="py-2 pr-3">{isTr ? 'İşlem' : 'Action'}</th>
                <th className="py-2 pr-3">{isTr ? 'Varlık Türü' : 'Entity Type'}</th>
                <th className="py-2 pr-3">{isTr ? 'Varlık ID' : 'Entity ID'}</th>
                <th className="py-2 pr-3">{isTr ? 'IP' : 'IP'}</th>
                <th className="py-2 pr-3">{isTr ? 'Sonuç' : 'Result'}</th>
              </tr>
            </thead>
            <tbody>
              {(data?.items ?? []).map((a) => (
                <tr key={a.id} className="border-b border-neutral-100 dark:border-dark-border/40">
                  <td className="py-2 pr-3 text-xs text-neutral-500">{new Date(a.createdAt).toLocaleString()}</td>
                  <td className="py-2 pr-3">{a.username || a.userId || '—'}</td>
                  <td className="py-2 pr-3 font-medium">{a.action}</td>
                  <td className="py-2 pr-3">{a.entityType}</td>
                  <td className="py-2 pr-3 text-xs font-mono">{a.entityId}</td>
                  <td className="py-2 pr-3 text-xs">{a.ipAddress || '—'}</td>
                  <td className={`py-2 pr-3 ${a.success ? 'text-emerald-600' : 'text-rose-600'}`}>
                    {a.success ? (isTr ? 'Başarılı' : 'Success') : (isTr ? 'Hata' : 'Failed')}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>
    </div>
  );
};
