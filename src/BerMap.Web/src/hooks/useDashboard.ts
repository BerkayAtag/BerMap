import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { DashboardSummary } from '@/types';

/**
 * Fetches the dashboard summary from the real API. The backend computes every
 * value from persisted discovery data; there is no client-side mock fallback.
 */
export function useDashboard() {
  return useQuery<DashboardSummary>({
    queryKey: ['dashboard', 'summary'],
    queryFn: () => api.get<DashboardSummary>('/dashboard/summary'),
    refetchInterval: 30_000,
    staleTime: 10_000,
  });
}
