import { useLocaleStore } from '@/stores/localeStore';
import type { Locale } from '@/stores/localeStore';

export type { Locale };

export const SUPPORTED_LOCALES: Locale[] = ['tr', 'en'];

export const LOCALE_LABELS: Record<Locale, string> = {
  tr: 'Türkçe',
  en: 'English',
};

export function useLocale(): Locale {
  return useLocaleStore((state) => state.locale);
}

export function useToggleLocale(): () => void {
  return useLocaleStore((state) => state.toggleLocale);
}

export function useSetLocale(): (locale: Locale) => void {
  return useLocaleStore((state) => state.setLocale);
}