import React from 'react';
import { Search, Shield } from 'lucide-react';
import { ThemeToggle } from './ThemeToggle';
import { LocaleToggle } from './LocaleToggle';
import { StatusIndicator } from '@/components/ui/StatusIndicator';
import { useLocale } from '@/lib/i18n';

export const TopNav: React.FC = () => {
  const locale = useLocale();

  return (
    <header className="h-16 px-6 flex items-center justify-between border-b border-neutral-200 dark:border-dark-border bg-white dark:bg-dark-sidebar z-30 transition-colors">
      {/* Search Bar */}
      <div className="flex items-center w-96 relative">
        <Search className="w-4 h-4 absolute left-3 text-neutral-400 dark:text-neutral-500 pointer-events-none" />
        <input
          type="text"
          placeholder={
            locale === 'tr'
              ? 'Sunucu, cluster, IP veya servis ara... (⌘K)'
              : 'Search server, cluster, IP or service... (⌘K)'
          }
          className="w-full h-9 pl-9 pr-4 text-sm rounded-lg bg-neutral-100 dark:bg-dark-card border border-neutral-200 dark:border-dark-border text-neutral-900 dark:text-dark-text placeholder-neutral-400 dark:placeholder-neutral-500 focus:outline-none focus:ring-2 focus:ring-brand-primary focus:border-transparent transition-all"
        />
      </div>

      {/* Right Controls */}
      <div className="flex items-center gap-3">
        {/* System Health Badge */}
        <div className="hidden md:flex items-center gap-2 px-3 py-1.5 rounded-full bg-emerald-50 dark:bg-emerald-950/40 border border-emerald-200 dark:border-emerald-800/50 text-emerald-700 dark:text-emerald-400 text-xs font-medium">
          <StatusIndicator status="healthy" size="sm" />
          <span>
            {locale === 'tr' ? 'Sistem Sağlığı: Normal' : 'System Health: Normal'}
          </span>
        </div>

        {/* Locale Toggle */}
        <LocaleToggle />

        {/* Theme Toggle */}
        <ThemeToggle />

        {/* Vertical Divider */}
        <div className="h-6 w-[1px] bg-neutral-200 dark:bg-dark-border" />

        {/* User Profile */}
        <div className="flex items-center gap-3 pl-1">
          <div className="w-8 h-8 rounded-full bg-brand-primary/10 dark:bg-brand-primary-dark/20 text-brand-primary dark:text-brand-primary-dark border border-brand-primary/20 flex items-center justify-center font-bold text-xs">
            SA
          </div>
          <div className="hidden lg:flex flex-col text-left">
            <span className="text-xs font-semibold text-neutral-900 dark:text-white leading-tight">
              {locale === 'tr' ? 'Sistem Yöneticisi' : 'System Administrator'}
            </span>
            <span className="text-[10px] text-neutral-500 dark:text-neutral-400 flex items-center gap-1">
              <Shield className="w-3 h-3 text-brand-primary inline" /> Platform Admin
            </span>
          </div>
        </div>
      </div>
    </header>
  );
};