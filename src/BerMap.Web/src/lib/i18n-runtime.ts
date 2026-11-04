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

/** Replaces {name} placeholders with values; a missing value leaves the placeholder out. */
export function interpolate(template: string, values: Record<string, string | number | null | undefined> = {}): string {
  return template.replace(/\{(\w+)\}/g, (_, name: string) => {
    const value = values[name];
    return value === null || value === undefined ? '' : String(value);
  });
}
