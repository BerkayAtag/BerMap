import React from 'react';
import { NavLink } from 'react-router-dom';
import {
  LayoutDashboard,
  Network,
  Server,
  AlertTriangle,
  Cpu,
  BarChart3,
  Bell,
  FileText,
  PlugZap,
  Radio,
  History,
  Camera,
  ChevronLeft,
  ChevronRight,
} from 'lucide-react';
import { useSidebarStore } from '@/stores/sidebarStore';
import { useI18n, type TranslationKey } from '@/lib/i18n-runtime';

interface NavItem {
  key: TranslationKey;
  path: string;
  icon: React.ComponentType<{ className?: string }>;
  badge?: TranslationKey;
}

const NAV_ITEMS: NavItem[] = [
  { key: 'dashboard', path: '/', icon: LayoutDashboard },
  { key: 'infrastructureMap', path: '/map', icon: Network },
  { key: 'inventory', path: '/inventory', icon: Server },
  { key: 'impactAnalysis', path: '/impact', icon: AlertTriangle },
  { key: 'berSim', path: '/simulation', icon: Cpu, badge: 'berSimBadge' },
  { key: 'snapshots', path: '/snapshots', icon: Camera },
  { key: 'capacityIntelligence', path: '/capacity', icon: BarChart3 },
  { key: 'eventsAlerts', path: '/events', icon: Bell },
  { key: 'reports', path: '/reports', icon: FileText },
  { key: 'integrations', path: '/integrations', icon: PlugZap },
  { key: 'collectors', path: '/collectors', icon: Radio },
  { key: 'auditLogs', path: '/audit', icon: History },
];

export const Sidebar: React.FC = () => {
  const { isCollapsed, toggle } = useSidebarStore();
  const { t } = useI18n();

  return (
    <aside
      className={`
        relative flex flex-col border-r transition-all duration-300 select-none z-40
        ${isCollapsed ? 'w-18' : 'w-64'}
        bg-white border-neutral-200 text-neutral-800
        dark:bg-dark-sidebar dark:border-dark-border dark:text-dark-text
      `}
    >
      {/* Brand Header */}
      <div className="flex items-center h-16 px-4 border-b border-neutral-200 dark:border-dark-border">
        <div className="flex items-center gap-3 overflow-hidden">
          <div className="flex items-center justify-center w-9 h-9 rounded-lg bg-brand-primary text-white font-black text-xl shadow-md flex-shrink-0">
            B
          </div>
          {!isCollapsed && (
            <div className="flex flex-col min-w-0">
              <span className="font-bold text-lg leading-tight tracking-tight text-neutral-900 dark:text-white">
                Ber<span className="text-brand-primary dark:text-brand-primary-dark">Map</span>
              </span>
              <span className="text-[10px] uppercase font-semibold tracking-wider text-neutral-500 dark:text-neutral-400 truncate">
                {t('enterprise')}
              </span>
            </div>
          )}
        </div>
      </div>

      {/* Navigation List */}
      <nav aria-label={t('mainNavigation')} className="flex-1 overflow-y-auto px-3 py-4 space-y-1">
        {NAV_ITEMS.map((item) => {
          const Icon = item.icon;
          const label = t(item.key);
          return (
            <NavLink
              key={item.path}
              to={item.path}
              end={item.path === '/'}
              title={isCollapsed ? label : undefined}
              className={({ isActive }) => `
                group flex items-center gap-3 px-3 py-2.5 rounded-lg text-sm font-medium transition-all duration-150
                ${
                  isActive
                    ? 'bg-brand-primary text-white shadow-sm'
                    : 'text-neutral-600 hover:text-neutral-900 hover:bg-neutral-100 dark:text-neutral-300 dark:hover:text-white dark:hover:bg-neutral-800/60'
                }
              `}
            >
              {({ isActive }) => (
                <>
                  <Icon
                    className={`w-5 h-5 flex-shrink-0 transition-colors ${
                      isActive ? 'text-white' : 'text-neutral-500 group-hover:text-brand-primary dark:text-neutral-400 dark:group-hover:text-brand-primary-dark'
                    }`}
                  />
                  {!isCollapsed && (
                    <span className="truncate flex-1">{label}</span>
                  )}
                  {!isCollapsed && item.badge && (
                    <span
                      className={`text-[10px] font-bold px-1.5 py-0.5 rounded uppercase tracking-wider ${
                        isActive
                          ? 'bg-white/20 text-white'
                          : 'bg-brand-primary/10 text-brand-primary dark:bg-brand-primary-dark/20 dark:text-brand-primary-dark'
                      }`}
                    >
                      {t(item.badge)}
                    </span>
                  )}
                </>
              )}
            </NavLink>
          );
        })}
      </nav>

      {/* Collapse Toggle Footer */}
      <div className="p-3 border-t border-neutral-200 dark:border-dark-border">
        <button
          type="button"
          onClick={toggle}
          aria-label={isCollapsed ? t('expandSidebar') : t('collapseSidebar')}
          className="flex items-center justify-center w-full h-9 rounded-lg border border-neutral-200 dark:border-dark-border text-neutral-600 dark:text-neutral-400 hover:bg-neutral-100 dark:hover:bg-neutral-800 transition-colors"
        >
          {isCollapsed ? <ChevronRight className="w-4 h-4" /> : <ChevronLeft className="w-4 h-4" />}
          {!isCollapsed && <span className="ml-2 text-xs font-medium">{t('collapseSidebar')}</span>}
        </button>
      </div>
    </aside>
  );
};