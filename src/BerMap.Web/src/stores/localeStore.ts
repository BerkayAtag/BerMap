import { create } from 'zustand';

export type Locale = 'tr' | 'en';

interface LocaleState {
  locale: Locale;
  setLocale: (locale: Locale) => void;
  toggleLocale: () => void;
}

function getInitialLocale(): Locale {
  if (typeof window === 'undefined') return 'tr';
  const stored = localStorage.getItem('bermap-locale');
  if (stored === 'tr' || stored === 'en') return stored;
  return 'tr';
}

function applyLocale(locale: Locale): void {
  if (typeof document !== 'undefined') {
    document.documentElement.setAttribute('data-locale', locale);
    document.documentElement.lang = locale;
  }
  if (typeof window !== 'undefined') {
    localStorage.setItem('bermap-locale', locale);
  }
}

export const useLocaleStore = create<LocaleState>((set) => {
  const initial = getInitialLocale();
  applyLocale(initial);

  return {
    locale: initial,
    setLocale: (locale) => {
      applyLocale(locale);
      set({ locale });
    },
    toggleLocale: () => {
      set((state) => {
        const next: Locale = state.locale === 'tr' ? 'en' : 'tr';
        applyLocale(next);
        return { locale: next };
      });
    },
  };
});

export function useLocale(): Locale {
  return useLocaleStore((state) => state.locale);
}

export function useToggleLocale(): () => void {
  return useLocaleStore((state) => state.toggleLocale);
}