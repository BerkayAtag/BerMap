import React from 'react';
import { Sun, Moon } from 'lucide-react';
import { useThemeStore } from '@/stores/themeStore';
import { useT } from '@/lib/i18n-runtime';

export const ThemeToggle: React.FC = () => {
  const { theme, toggleTheme } = useThemeStore();
  const isDark = theme === 'dark';
  const t = useT();
  const label = isDark ? t('switchToLight') : t('switchToDark');

  return (
    <button
      type="button"
      onClick={toggleTheme}
      aria-label={label}
      title={label}
      className={`
        relative inline-flex items-center justify-center w-9 h-9 rounded-lg
        border transition-colors duration-200 focus:outline-none focus:ring-2 focus:ring-brand-primary
        ${isDark
          ? 'bg-dark-card border-dark-border text-amber-400 hover:bg-neutral-800 hover:text-amber-300'
          : 'bg-white border-neutral-200 text-neutral-600 hover:bg-neutral-100 hover:text-neutral-900'}
      `}
    >
      <div className="relative w-5 h-5 flex items-center justify-center" aria-hidden="true">
        {isDark ? (
          <Moon className="w-4 h-4 transition-transform duration-200 transform rotate-0 scale-100" />
        ) : (
          <Sun className="w-4 h-4 transition-transform duration-200 transform rotate-0 scale-100 text-amber-500" />
        )}
      </div>
    </button>
  );
};
