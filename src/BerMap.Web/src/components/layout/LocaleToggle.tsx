import React, { useEffect, useId, useRef, useState } from 'react';
import { Globe, Languages } from 'lucide-react';
import { useLocale, useSetLocale, LOCALE_LABELS, SUPPORTED_LOCALES } from '@/lib/i18n';
import { useT } from '@/lib/i18n-runtime';
import type { Locale } from '@/lib/i18n';

export const LocaleToggle: React.FC = () => {
  const locale = useLocale();
  const setLocale = useSetLocale();
  const t = useT();
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const itemRefs = useRef<Array<HTMLButtonElement | null>>([]);
  const menuId = useId();

  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: PointerEvent) => {
      if (!containerRef.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener('pointerdown', onPointerDown);
    return () => document.removeEventListener('pointerdown', onPointerDown);
  }, [open]);

  useEffect(() => {
    if (open) {
      const activeIndex = Math.max(0, SUPPORTED_LOCALES.indexOf(locale));
      itemRefs.current[activeIndex]?.focus();
    }
  }, [open, locale]);

  const choose = (option: Locale) => {
    setLocale(option);
    setOpen(false);
    buttonRef.current?.focus();
  };

  const onMenuKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
    const items = itemRefs.current.filter((item): item is HTMLButtonElement => item !== null);
    const index = items.indexOf(document.activeElement as HTMLButtonElement);
    if (event.key === 'Escape') {
      event.preventDefault();
      setOpen(false);
      buttonRef.current?.focus();
    } else if (event.key === 'ArrowDown') {
      event.preventDefault();
      items[(index + 1) % items.length]?.focus();
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      items[(index - 1 + items.length) % items.length]?.focus();
    } else if (event.key === 'Tab') {
      setOpen(false);
    }
  };

  return (
    <div className="relative" ref={containerRef}>
      <button
        ref={buttonRef}
        type="button"
        onClick={() => setOpen((value) => !value)}
        onKeyDown={(event) => {
          if (event.key === 'ArrowDown') {
            event.preventDefault();
            setOpen(true);
          }
        }}
        aria-label={`${t('changeLanguage')} (${LOCALE_LABELS[locale]})`}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={menuId}
        title={`${LOCALE_LABELS[locale]} · ${t('changeLanguage')}`}
        className="
          relative inline-flex items-center justify-center w-9 h-9 rounded-lg
          border transition-colors duration-200 focus:outline-none focus:ring-2 focus:ring-brand-primary
          bg-white border-neutral-200 text-neutral-600 hover:bg-neutral-100
          dark:bg-dark-card dark:border-dark-border dark:text-brand-primary-dark dark:hover:bg-neutral-800
        "
      >
        <Globe className="w-4 h-4" aria-hidden="true" />
      </button>

      {open && (
        <div
          id={menuId}
          role="menu"
          aria-label={t('locale')}
          onKeyDown={onMenuKeyDown}
          className="absolute right-0 top-full mt-1 z-50 w-40 rounded-lg border shadow-xl bg-white dark:bg-dark-card border-neutral-200 dark:border-dark-border"
        >
          <div className="p-1">
            <span className="block px-3 py-1.5 text-[10px] uppercase font-bold tracking-wider text-neutral-400">
              {t('localeMenuTitle')}
            </span>
            {SUPPORTED_LOCALES.map((option, index) => (
              <button
                key={option}
                ref={(element) => {
                  itemRefs.current[index] = element;
                }}
                type="button"
                role="menuitemradio"
                aria-checked={locale === option}
                onClick={() => choose(option)}
                className={`
                  w-full flex items-center gap-2 px-3 py-2 rounded-md text-sm transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-brand-primary
                  ${locale === option
                    ? 'bg-brand-primary/10 text-brand-primary dark:text-brand-primary-dark font-semibold'
                    : 'text-neutral-600 dark:text-neutral-300 hover:bg-neutral-100 dark:hover:bg-neutral-800'}
                `}
              >
                <Languages className="w-4 h-4 opacity-70" aria-hidden="true" />
                <span>{LOCALE_LABELS[option]}</span>
                {locale === option && <span className="ml-auto text-[10px]" aria-hidden="true">✓</span>}
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
};
