import React, { useEffect, useId, useState } from 'react';
import { Loader2, Search } from 'lucide-react';
import { useLocale } from '@/lib/i18n';
import { formatNumber, getErrorMessage } from '@/lib/format';
import { useInventoryNodes } from '@/hooks/useInventoryNodes';
import type { InventoryNode } from '@/types';

const PAGE_SIZE = 50;

interface NodePickerProps {
  selectedId: string | null;
  onSelect: (node: InventoryNode) => void;
  /** Optional NodeType filter (e.g. "Hypervisor"). */
  nodeType?: string;
  label?: string;
  listClassName?: string;
}

/**
 * Searchable entity picker backed by the paged inventory endpoint, so it works on
 * estates far larger than the topology view cap. Results are server-filtered.
 */
export const NodePicker: React.FC<NodePickerProps> = ({
  selectedId,
  onSelect,
  nodeType,
  label,
  listClassName = 'max-h-72',
}) => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const inputId = useId();
  const [input, setInput] = useState('');
  const [term, setTerm] = useState('');

  useEffect(() => {
    const timer = window.setTimeout(() => setTerm(input.trim()), 300);
    return () => window.clearTimeout(timer);
  }, [input]);

  const nodesQuery = useInventoryNodes({ query: term || undefined, nodeType, page: 1, pageSize: PAGE_SIZE });
  const items = nodesQuery.data?.items ?? [];
  const total = nodesQuery.data?.totalCount ?? 0;

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
        {nodesQuery.isFetching && <Loader2 className="w-3.5 h-3.5 animate-spin text-neutral-400 shrink-0" aria-hidden="true" />}
      </div>

      {nodesQuery.isError ? (
        <p className="text-xs text-rose-600 dark:text-rose-400 py-4 text-center" role="alert">
          {getErrorMessage(nodesQuery.error)}
        </p>
      ) : nodesQuery.isLoading ? (
        <p className="text-xs text-neutral-500 py-6 text-center">
          {isTr ? 'Envanter yükleniyor...' : 'Loading inventory...'}
        </p>
      ) : items.length === 0 ? (
        <p className="text-xs text-neutral-500 py-6 text-center">
          {term
            ? isTr ? 'Eşleşen bileşen yok.' : 'No matching components.'
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
                  <span className="shrink-0 text-[10px] px-1.5 py-0.5 rounded bg-neutral-100 dark:bg-dark-border font-mono">
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
                ? `${formatNumber(total)} kayıttan ilk ${formatNumber(items.length)} gösteriliyor — daraltmak için arayın.`
                : `Showing first ${formatNumber(items.length)} of ${formatNumber(total)} — search to narrow down.`}
            </p>
          )}
        </>
      )}
    </div>
  );
};
