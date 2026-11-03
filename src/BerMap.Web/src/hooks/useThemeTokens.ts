import { useMemo } from 'react';
import { useThemeStore } from '@/stores/themeStore';

export interface ThemeTokens {
  isDark: boolean;
  surface: string;
  surfaceAlt: string;
  border: string;
  text: string;
  textMuted: string;
  background: string;
  brand: string;
  healthy: string;
  warning: string;
  critical: string;
  unknown: string;
  info: string;
}

function readVar(styles: CSSStyleDeclaration, name: string, fallback: string): string {
  const value = styles.getPropertyValue(name).trim();
  return value || fallback;
}

/**
 * Resolves design tokens from src/index.css into concrete colours for canvas/SVG
 * renderers (ECharts, React Flow) that cannot consume Tailwind classes. The values
 * are re-read whenever the theme changes, so charts re-render with the new palette.
 */
export function useThemeTokens(): ThemeTokens {
  const theme = useThemeStore((state) => state.theme);

  return useMemo(() => {
    const isDark = theme === 'dark';
    const styles = getComputedStyle(document.documentElement);
    const v = (name: string, fallback: string) => readVar(styles, name, fallback);

    return {
      isDark,
      surface: isDark ? v('--color-dark-card', '#20242D') : v('--color-brand-white', '#FFFFFF'),
      surfaceAlt: isDark ? v('--color-dark-sidebar', '#181B22') : v('--color-neutral-50', '#FAFBFC'),
      border: isDark ? v('--color-dark-border', '#343945') : v('--color-neutral-200', '#E4E6EB'),
      text: isDark ? v('--color-dark-text', '#F5F6F8') : v('--color-neutral-900', '#17191F'),
      textMuted: isDark ? v('--color-dark-text-secondary', '#AEB4C0') : v('--color-neutral-600', '#4B5563'),
      background: isDark ? v('--color-dark-bg', '#111318') : v('--color-brand-white', '#FFFFFF'),
      brand: isDark ? v('--color-brand-primary-dark', '#FF3344') : v('--color-brand-primary', '#E30613'),
      healthy: v('--color-status-healthy', '#10B981'),
      warning: v('--color-status-warning', '#F59E0B'),
      critical: v('--color-status-critical', '#EF4444'),
      unknown: v('--color-status-unknown', '#6B7280'),
      info: v('--color-status-info', '#3B82F6'),
    };
  }, [theme]);
}
