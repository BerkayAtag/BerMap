import { useLocaleStore } from '@/stores/localeStore';
import dictionaries, { type TranslationKey } from '@/lib/dictionaries';

export type { TranslationKey };

export function useI18n() {
  const locale = useLocaleStore((state) => state.locale);
  const dict = dictionaries[locale];

  function t(key: TranslationKey, fallback?: string): string {
    const value = dict[key];
    if (typeof value === 'string' && value.length > 0) return value;
    return fallback ?? key;
  }

  return { locale, t };
}

export function useT() {
  return useI18n().t;
}