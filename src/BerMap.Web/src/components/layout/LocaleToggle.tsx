import React from 'react';
import { Globe, Languages } from 'lucide-react';
import { useLocale, useToggleLocale, useSetLocale, LOCALE_LABELS, SUPPORTED_LOCALES } from '@/lib/i18n';

export const LocaleToggle: React.FC = () => {
  const locale = useLocale();
  const toggleLocale = useToggleLocale();
  const setLocale = useSetLocale();

  return (
    <div className="relative group">
      <button
        type="button"
        onClick={toggleLocale}
        aria-label="Toggle language"
        title={`${LOCALE_LABELS[locale]} · Switch language`}
        className={`
          relative inline-flex items-center justify-center w-9 h-9 rounded-lg
          border transition-colors duration-200 focus:outline-none focus:ring-2 focus:ring-brand-primary
          ${locale === 'tr'
            ? 'bg-dark-card border-dark-border text-brand-primary-dark hover:bg-neutral-800'
            : 'bg-white border-neutral-200 text-neutral-600 hover:bg-neutral-100'}
        `}
      >
        <Globe className="w-4 h-4" />
      </button>

      <div
        className={`
          absolute right-0 top-full mt-1 z-50 w-40 rounded-lg border shadow-xl
          bg-white dark:bg-dark-card border-neutral-200 dark:border-dark-border
          opacity-0 invisible group-hover:opacity-100 group-hover:visible
          transition-all duration-150
        `}
      >
        <div className="p-1">
          <span className="px-3 py-1.5 text-[10px] uppercase font-bold tracking-wider text-neutral-400">
            Dil / Language
          </span>
          {SUPPORTED_LOCALES.map((option) => (
            <button
              key={option}
              type="button"
              onClick={() => setLocale(option)}
              className={`
                w-full flex items-center gap-2 px-3 py-2 rounded-md text-sm transition-colors
                ${locale === option
                  ? 'bg-brand-primary/10 text-brand-primary dark:text-brand-primary-dark font-semibold'
                  : 'text-neutral-600 dark:text-neutral-300 hover:bg-neutral-100 dark:hover:bg-neutral-800'}
              `}
            >
              <Languages className="w-4 h-4 opacity-70" />
              <span>{LOCALE_LABELS[option]}</span>
              {locale === option && <span className="ml-auto text-[10px]">✓</span>}
            </button>
          ))}
        </div>
      </div>
    </div>
  );
};