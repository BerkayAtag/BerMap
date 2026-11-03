import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { InventoryPage } from '@/types';

export interface InventoryNodesParams {
  query?: string;
  nodeType?: string;
  page?: number;
  /** Backend clamps this to 1..200. */
  pageSize?: number;
}

export function buildInventoryNodesPath({ query, nodeType, page = 1, pageSize = 50 }: InventoryNodesParams): string {
  const parameters = new URLSearchParams({
    page: String(page),
    pageSize: String(Math.min(200, Math.max(1, pageSize))),
  });
  if (query) parameters.set('query', query);
  if (nodeType) parameters.set('nodeType', nodeType);
  return `/inventory/nodes?${parameters.toString()}`;
}

/** Paged inventory listing (GET /api/inventory/nodes). */
export function useInventoryNodes(params: InventoryNodesParams, options?: { enabled?: boolean; refetchInterval?: number }) {
  return useQuery<InventoryPage>({
    queryKey: ['inventory', 'nodes', params],
    queryFn: () => api.get<InventoryPage>(buildInventoryNodesPath(params)),
    placeholderData: keepPreviousData,
    enabled: options?.enabled ?? true,
    refetchInterval: options?.refetchInterval,
  });
}

/** Fetches every page of an inventory listing (pageSize 200 per request). */
export async function fetchAllInventoryNodes(params: Omit<InventoryNodesParams, 'page' | 'pageSize'>, maxPages = 50) {
  const items: InventoryPage['items'] = [];
  let page = 1;
  let totalPages = 1;
  do {
    const response = await api.get<InventoryPage>(buildInventoryNodesPath({ ...params, page, pageSize: 200 }));
    items.push(...(response.items ?? []));
    totalPages = Math.max(1, response.totalPages ?? 1);
    page += 1;
  } while (page <= totalPages && page <= maxPages);
  return { items, truncated: totalPages > maxPages };
}
