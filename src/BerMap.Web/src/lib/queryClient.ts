import { QueryClient } from '@tanstack/react-query';

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      retry: (failureCount, error) => {
        const status = (error as { status?: number } | null)?.status;
        // Authentication / authorization failures will not succeed on retry.
        if (status === 401 || status === 403) return false;
        return failureCount < 1;
      },
    },
  },
});
