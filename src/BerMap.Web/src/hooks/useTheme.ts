import { useThemeStore } from '@/stores/themeStore';

/**
 * Custom hook for theme management.
 * Provides the current theme and controls to change it.
 */
export function useTheme() {
  const theme = useThemeStore((s) => s.theme);
  const setTheme = useThemeStore((s) => s.setTheme);
  const toggleTheme = useThemeStore((s) => s.toggleTheme);

  const isDark = theme === 'dark';

  return { theme, isDark, setTheme, toggleTheme };
}
