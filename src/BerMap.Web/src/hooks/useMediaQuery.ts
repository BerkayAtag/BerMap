import { useCallback, useSyncExternalStore } from 'react';

/** Tailwind's `md` breakpoint: below this width the sidebar becomes a drawer. */
export const MOBILE_QUERY = '(max-width: 767px)';

/** Subscribes to a CSS media query and re-renders when it starts or stops matching. */
export function useMediaQuery(query: string): boolean {
  const subscribe = useCallback(
    (onChange: () => void) => {
      if (typeof window === 'undefined' || !window.matchMedia) return () => {};
      const list = window.matchMedia(query);
      list.addEventListener('change', onChange);
      return () => list.removeEventListener('change', onChange);
    },
    [query],
  );
  const getSnapshot = () =>
    typeof window !== 'undefined' && !!window.matchMedia && window.matchMedia(query).matches;
  return useSyncExternalStore(subscribe, getSnapshot, () => false);
}

export function useIsMobile(): boolean {
  return useMediaQuery(MOBILE_QUERY);
}
