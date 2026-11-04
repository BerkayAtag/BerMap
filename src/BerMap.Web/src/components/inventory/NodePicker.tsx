import React, { useEffect, useId, useState } from 'react';
import { keepPreviousData, useQueries } from '@tanstack/react-query';
import { Loader2, Search } from 'lucide-react';
import { useLocale } from '@/lib/i18n';
import { api } from '@/lib/api';
import { formatNumber, getErrorMessage } from '@/lib/format';
import { buildInventoryNodesPath } from '@/hooks/useInventoryNodes';
import type { InventoryNode, InventoryPage } from '@/types';

const PAGE_SIZE = 50;

interface NodePickerProps {
  selectedId: string | null;
  onSelect: (node: InventoryNode) => void;
  /**
   * Optional NodeType filter (e.g. ["Hypervisor", "PhysicalServer"]). With more
   * than one type a chip row narrows the list to a single type.
   */
  nodeTypes?: readonly string[];
  label?: string;
  listClassName?: string;
}

/**
 * Searchable entity picker backed by the paged inventory endpoint, so it works on
 * estates far larger than the topology view cap. Results are server-filtered; the
 * endpoint takes one nodeType, so several allowed types are queried in parallel.
 */
export const NodePicker: React.FC<NodePickerProps> = ({
  selectedId,
  onSelect,
  nodeTypes,
  label,
  listClassName = 'max-h-72',
}) => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const inputId = useId();
  const [input, setInput] = useState('');
  const [term, setTerm] = useState('');
  const [typeFilter, setTypeFilter] = useState<string | null>(null);
  const typesKey = nodeTypes?.join(',') ?? '';

  useEffect(() => {
    const timer = window.setTimeout(() => setTerm(input.trim()), 300);
    return () => window.clearTimeout(timer);
  }, [input]);

  // A different allowed-type set (another scenario) starts from "all types" again.
  useEffect(() => {
    setTypeFilter(null);
  }, [typesKey]);

  const queriedTypes: (string | undefined)[] =
    typeFilter && nodeTypes?.includes(typeFilter)
      ? [typeFilter]
      : nodeTypes && nodeTypes.length > 0
        ? [...nodeTypes]
        : [undefined];

  const queries = useQueries({
    queries: queriedTypes.map((nodeType) => {
      const params = { query: term || undefined, nodeType, page: 1, pageSize: PAGE_SIZE };
      return {
        queryKey: ['inventory', 'nodes', params],
        queryFn: () => api.get<InventoryPage>(buildInventoryNodesPath(params)),
        placeholderData: keepPreviousData,
      };
    }),
  });

  const failed = queries.find((query) => query.isError);
  const isLoading = queries.some((query) => query.isLoading);
  const isFetching = queries.some((query) => query.isFetching);
  const items = queries
    .flatMap((query) => query.data?.items ?? [])
    .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
  const total = queries.reduce((sum, query) => sum + (query.data?.totalCount ?? 0), 0);

  const chipClass = (active: boolean) =>
    `shrink-0 px-2 py-1 rounded-md border text-[11px] font-medium transition-colors ${
      active
        ? 'border-brand-primary bg-brand-primary/10 text-brand-primary dark:text-brand-primary-dark'
        : 'border-neutral-200 dark:border-dark-border text-neutral-600 dark:text-neutral-400 hover:border-neutral-300'
    }`;

  return (
    <div className="min-w-0">
      <label htmlFor={inputId} className="sr-only">
        {label ?? (isTr ? 'Bileşen ara' : 'Search components')}
      </label>
      <div className="flex items-center gap-2 px-3 py-2 rounded-lg bg-neutral-100 dark:bg-dark-border text-sm mb-2">
        <Search className="w-4 h-4 text-neutral-400 shrink-0" aria-hidden="true" />
        <input
          id={inputId}
          type="search"
          value={input}
          onChange={(event) => setInput(event.target.value)}
          placeholder={isTr ? 'Ad, FQDN, IP veya UUID ara...' : 'Search name, FQDN, IP or UUID...'}
          className="bg-transparent text-sm focus:outline-none w-full text-neutral-800 dark:text-neutral-200"
        />
        {isFetching && <Loader2 className="w-3.5 h-3.5 animate-spin text-neutral-400 shrink-0" aria-hidden="true" />}
      </div>

      {nodeTypes && nodeTypes.length > 1 && (
        <div className="flex flex-wrap gap-1.5 mb-2" role="group" aria-label={isTr ? 'Tür filtresi' : 'Type filter'}>
          <button
            type="button"
            aria-pressed={typeFilter === null}
            onClick={() => setTypeFilter(null)}
            className={chipClass(typeFilter === null)}
          >
            {isTr ? 'Tümü' : 'All'}
          </button>
          {nodeTypes.map((nodeType) => (
            <button
              key={nodeType}
              type="button"
              lang="en"
              aria-pressed={typeFilter === nodeType}
              onClick={() => setTypeFilter(nodeType)}
              className={chipClass(typeFilter === nodeType)}
            >
              {nodeType}
            </button>
          ))}
        </div>
      )}

      {failed ? (
        <p className="text-xs text-rose-600 dark:text-rose-400 py-4 text-center" role="alert">
          {getErrorMessage(failed.error)}
        </p>
      ) : isLoading ? (
        <p className="text-xs text-neutral-500 py-6 text-center">
          {isTr ? 'Envanter yükleniyor...' : 'Loading inventory...'}
        </p>
      ) : items.length === 0 ? (
        <p className="text-xs text-neutral-500 py-6 text-center">
          {term
            ? isTr ? 'Eşleşen bileşen yok.' : 'No matching components.'
            : nodeTypes && nodeTypes.length > 0
              ? isTr ? 'Bu senaryo için uygun türde bileşen yok.' : 'No components of a type this scenario can target.'
              : isTr ? 'Kullanılabilir bileşen yok.' : 'No components available.'}
        </p>
      ) : (
        <>
          <div className={`${listClassName} overflow-y-auto space-y-1.5`} role="listbox" aria-label={label}>
            {items.map((node) => (
              <button
                key={node.id}
                type="button"
                role="option"
                aria-selected={selectedId === node.id}
                onClick={() => onSelect(node)}
                className={`w-full text-left p-2.5 rounded-lg border text-xs transition-all ${
                  selectedId === node.id
                    ? 'border-brand-primary bg-brand-primary/5 dark:bg-brand-primary-dark/10 text-neutral-900 dark:text-white font-medium'
                    : 'border-neutral-200 dark:border-dark-border text-neutral-600 dark:text-neutral-400 hover:border-neutral-300'
                }`}
              >
                <div className="flex items-center justify-between gap-2">
                  <span className="font-semibold truncate">{node.name}</span>
                  <span lang="en" className="shrink-0 text-[10px] px-1.5 py-0.5 rounded bg-neutral-100 dark:bg-dark-border font-mono">
                    {node.nodeType}
                  </span>
                </div>
                {node.fqdn && (
                  <div className="text-[10px] text-neutral-500 font-mono truncate mt-0.5">{node.fqdn}</div>
                )}
              </button>
            ))}
          </div>
          {total > items.length && (
            <p className="mt-2 text-[10px] text-neutral-500 text-center">
              {isTr
                ? `${formatNumber(total)} kayıttan ${formatNumber(items.length)} tanesi gösteriliyor — daraltmak için arayın.`
                : `Showing ${formatNumber(items.length)} of ${formatNumber(total)} — search to narrow down.`}
            </p>
          )}
        </>
      )}
    </div>
  );
};
